using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.GrabPolicy
{
    // The abridged policy that applies to one audiobook row, and where it came from.
    public sealed class AbridgedTarget
    {
        public AbridgedTarget(AbridgedAudiobookPolicy policy, string source)
        {
            Policy = policy;
            Source = source;
        }

        public AbridgedAudiobookPolicy Policy { get; }

        // book | global | recording (AbridgedPolicySources)
        public string Source { get; }

        // A per-book choice is strict: "unabridged only" set for a book does not let an abridged
        // release through because the monitored edition happens to be abridged.
        public bool IsStrict => Source == AbridgedPolicySources.Book;
    }

    public sealed class AbridgedJudgement
    {
        public static readonly AbridgedJudgement Accept = new AbridgedJudgement(true, null);

        public AbridgedJudgement(bool accepted, string reason)
        {
            Accepted = accepted;
            Reason = reason;
        }

        public bool Accepted { get; }
        public string Reason { get; }
    }

    // Talearr: the abridged grab rules shared by AbridgedReleaseSpecification (accept or reject),
    // DownloadDecisionComparer (ranking), the edition switch and the Audiobookshelf owned-copy check, so all use
    // the same effective policy per book:
    //
    //   - a per-book override (TalearrBookGrabPolicyService) wins over the global policy,
    //   - additional recordings (copy rows) take no override and keep their existing behaviour:
    //     an abridged copy searches abridged; under a global "abridged only" an unabridged copy
    //     searches unabridged and a copy of unknown abridgement takes either,
    //   - the global policy keeps its "the wanted edition is itself abridged" exception.
    public static class TalearrAbridgedRules
    {
        public static AbridgedTarget Target(Book row, AbridgedAudiobookPolicy global, ITalearrBookGrabPolicyService bookPolicies, ITalearrAbridgedResolver resolver)
        {
            if (row == null || row.MediaType != BookMediaType.Audiobook)
            {
                return new AbridgedTarget(global, AbridgedPolicySources.Global);
            }

            if (TalearrRecordingGroupService.IsCopySlug(row.TitleSlug))
            {
                if (global != AbridgedAudiobookPolicy.AbridgedOnly)
                {
                    return new AbridgedTarget(global, AbridgedPolicySources.Global);
                }

                switch (Marker(row, resolver))
                {
                    case AbridgedMarker.Abridged:
                        return new AbridgedTarget(AbridgedAudiobookPolicy.AbridgedOnly, AbridgedPolicySources.Recording);
                    case AbridgedMarker.Unabridged:
                        return new AbridgedTarget(AbridgedAudiobookPolicy.UnabridgedOnly, AbridgedPolicySources.Recording);
                    default:
                        return new AbridgedTarget(AbridgedAudiobookPolicy.Any, AbridgedPolicySources.Recording);
                }
            }

            var value = TalearrBookGrabPolicyService.ResolveOverride(bookPolicies, row);
            return value.HasValue
                ? new AbridgedTarget(value.Value, AbridgedPolicySources.Book)
                : new AbridgedTarget(global, AbridgedPolicySources.Global);
        }

        // The target of the first audiobook row a release is for.
        public static AbridgedTarget Target(RemoteBook subject, AbridgedAudiobookPolicy global, ITalearrBookGrabPolicyService bookPolicies, ITalearrAbridgedResolver resolver)
        {
            return Target(WantedRows(subject).FirstOrDefault(), global, bookPolicies, resolver);
        }

        public static AbridgedJudgement Judge(RemoteBook subject, AbridgedTarget target, ITalearrAbridgedResolver resolver)
        {
            if (subject == null || target == null || !AbridgedReleaseDetector.IsAudiobookRelease(subject))
            {
                return AbridgedJudgement.Accept;
            }

            var marker = AbridgedReleaseDetector.DetectRelease(subject.Release);
            var scope = Scope(target);

            // An unmarked release far too big for an abridged reading is the unabridged one.
            if (marker == AbridgedMarker.Unknown && WantsAbridgedReading(subject, target, resolver))
            {
                var unabridged = UnabridgedSizeCheck(subject, resolver);
                if (unabridged != null)
                {
                    return new AbridgedJudgement(false, $"Looks like the unabridged recording ({unabridged}){scope}");
                }
            }

            switch (target.Policy)
            {
                case AbridgedAudiobookPolicy.UnabridgedOnly:
                    if (marker != AbridgedMarker.Abridged)
                    {
                        return AbridgedJudgement.Accept;
                    }

                    if (!target.IsStrict && AbridgedReleaseDetector.WantsAbridged(subject, resolver))
                    {
                        return AbridgedJudgement.Accept;
                    }

                    return new AbridgedJudgement(false, $"Abridged audiobook (grab policy is unabridged only{scope})");

                case AbridgedAudiobookPolicy.AbridgedOnly:
                    if (marker == AbridgedMarker.Abridged)
                    {
                        return AbridgedJudgement.Accept;
                    }

                    if (marker == AbridgedMarker.Unabridged)
                    {
                        return new AbridgedJudgement(false, $"Unabridged audiobook (grab policy is abridged only{scope})");
                    }

                    // Most releases say neither. One is the abridged reading only when the wanted
                    // edition is (Talearr switches to an abridged edition before searching).
                    if (AbridgedReleaseDetector.WantsAbridged(subject, resolver))
                    {
                        return AbridgedJudgement.Accept;
                    }

                    return new AbridgedJudgement(false, $"Not marked abridged and the monitored edition is not abridged (grab policy is abridged only{scope})");

                default:
                    return AbridgedJudgement.Accept;
            }
        }

        // Ranking score, higher is better. 0 is neutral (ebooks, policy "any").
        public static int Rank(RemoteBook subject, AbridgedTarget target, ITalearrAbridgedResolver resolver)
        {
            if (subject == null || target == null || target.Policy == AbridgedAudiobookPolicy.Any ||
                !AbridgedReleaseDetector.IsAudiobookRelease(subject))
            {
                return 0;
            }

            var marker = AbridgedReleaseDetector.DetectRelease(subject.Release);

            if (target.Policy == AbridgedAudiobookPolicy.AbridgedOnly || target.Policy == AbridgedAudiobookPolicy.PreferAbridged)
            {
                return marker switch
                {
                    AbridgedMarker.Abridged => 1,
                    AbridgedMarker.Unabridged => -1,
                    _ => 0
                };
            }

            if (marker != AbridgedMarker.Abridged)
            {
                return 0;
            }

            // When the wanted recording is itself abridged (an abridged copy row), abridged
            // releases rank first instead of last. A strict per-book "unabridged only" never does.
            var flip = !(target.IsStrict && target.Policy == AbridgedAudiobookPolicy.UnabridgedOnly) &&
                       AbridgedReleaseDetector.WantsAbridged(subject, resolver);

            return flip ? 1 : -1;
        }

        // Bitrate an unabridged reading is assumed to be encoded at, at least. A release at least
        // this big for the whole unabridged runtime is not an abridged reading (those run about a
        // third of it).
        public const int UnabridgedMinimumKbps = 56;

        // Whether a release for this target has to be the abridged reading: "abridged only", or a
        // row that wants an abridged edition under a policy that does not insist on unabridged.
        private static bool WantsAbridgedReading(RemoteBook subject, AbridgedTarget target, ITalearrAbridgedResolver resolver)
        {
            switch (target.Policy)
            {
                case AbridgedAudiobookPolicy.AbridgedOnly:
                    return true;
                case AbridgedAudiobookPolicy.Any:
                case AbridgedAudiobookPolicy.PreferAbridged:
                    return false;
                default:
                    return !target.IsStrict && AbridgedReleaseDetector.WantsAbridged(subject, resolver);
            }
        }

        // "812 MB for a book whose unabridged reading runs 14 h 20 min" when the release size
        // fits the unabridged runtime, else null (also when the size or runtime is unknown).
        internal static string UnabridgedSizeCheck(RemoteBook subject, ITalearrAbridgedResolver resolver)
        {
            var size = subject?.Release?.Size ?? 0;
            var row = WantedRows(subject).FirstOrDefault();
            if (size <= 0 || row == null || resolver == null)
            {
                return null;
            }

            int? minutes;
            try
            {
                minutes = resolver.UnabridgedRuntimeMinutes(row);
            }
            catch (Exception)
            {
                return null;
            }

            if (!minutes.HasValue || minutes.Value <= 0)
            {
                return null;
            }

            var threshold = (long)minutes.Value * 60 * UnabridgedMinimumKbps * 1000 / 8;
            if (size < threshold)
            {
                return null;
            }

            return $"{size / 1_000_000} MB for a book whose unabridged reading runs {FormatRuntime(minutes.Value)}";
        }

        public static string FormatRuntime(int minutes)
        {
            var hours = minutes / 60;
            var rest = minutes % 60;
            return hours == 0 ? $"{rest} min" : rest == 0 ? $"{hours} h" : $"{hours} h {rest} min";
        }

        public static string Describe(AbridgedAudiobookPolicy policy)
        {
            return policy switch
            {
                AbridgedAudiobookPolicy.AbridgedOnly => "abridged only",
                AbridgedAudiobookPolicy.PreferUnabridged => "prefer unabridged",
                AbridgedAudiobookPolicy.PreferAbridged => "prefer abridged",
                AbridgedAudiobookPolicy.Any => "any",
                _ => "unabridged only"
            };
        }

        private static string Scope(AbridgedTarget target)
        {
            return target.Source switch
            {
                AbridgedPolicySources.Book => " for this book",
                AbridgedPolicySources.Recording => " for this recording",
                _ => string.Empty
            };
        }

        private static List<Book> WantedRows(RemoteBook subject)
        {
            List<Book> books;
            try
            {
                books = subject?.GetBooksMatchingReleaseMediaType() ?? new List<Book>();
            }
            catch (Exception)
            {
                books = subject?.Books ?? new List<Book>();
            }

            return books.Where(b => b != null && b.MediaType == BookMediaType.Audiobook).ToList();
        }

        private static AbridgedMarker Marker(Book row, ITalearrAbridgedResolver resolver)
        {
            if (resolver != null)
            {
                try
                {
                    return resolver.Resolve(row).Marker;
                }
                catch (Exception)
                {
                    // Fall through to the metadata rules.
                }
            }

            return AbridgedReleaseDetector.IsAbridgedBook(row) ? AbridgedMarker.Abridged : AbridgedMarker.Unknown;
        }
    }
}

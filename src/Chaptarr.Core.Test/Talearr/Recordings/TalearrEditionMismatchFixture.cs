using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    // Talearr: an import that matched another edition than the one grabbed names both editions
    // by what tells them apart, not just their (shared) title.
    [TestFixture]
    public class TalearrEditionMismatchFixture
    {
        private sealed class Resolver : ITalearrAbridgedResolver
        {
            public Dictionary<int, AbridgedMarker> Markers { get; } = new Dictionary<int, AbridgedMarker>();

            public AbridgedResolution Resolve(Book book) => AbridgedResolution.Unknown;

            public AbridgedResolution ResolveEdition(Edition edition, IReadOnlyCollection<Edition> siblings, IReadOnlyCollection<BookFile> files = null) =>
                Markers.TryGetValue(edition.Id, out var marker) ? new AbridgedResolution(marker, AbridgedSources.Audible, "test") : AbridgedResolution.Unknown;
        }

        // The Client tonight: grabbed for the 1993 abridged reading, the files are the 2024 unabridged one.
        private static Edition Abridged() => new Edition
        {
            Id = 27004,
            Title = "The Client",
            Publisher = "Random House Audio",
            ReleaseDate = new DateTime(1993, 3, 1),
            Isbn13 = "9780553712704"
        };

        private static Edition Unabridged() => new Edition
        {
            Id = 26999,
            Title = "The Client",
            Publisher = "Random House Audio",
            ReleaseDate = new DateTime(2024, 12, 10),
            AudibleASIN = "B0DP3KS9Y4"
        };

        private static Dictionary<int, EditionAudibleFacts> Facts() => new Dictionary<int, EditionAudibleFacts>
        {
            [26999] = new EditionAudibleFacts
            {
                Product = new AudibleProduct { Asin = "B0DP3KS9Y4", Status = AudibleProductStatus.Ok, FormatType = "unabridged", RuntimeMinutes = 860, Narrators = new List<string> { "John MacDonald" } }
            }
        };

        [Test]
        public void names_both_readings_and_how_long_the_files_run()
        {
            var resolver = new Resolver();
            resolver.Markers[27004] = AbridgedMarker.Abridged;
            resolver.Markers[26999] = AbridgedMarker.Unabridged;

            var message = TalearrEditionMismatch.Message(Abridged(), Unabridged(), new[] { Abridged(), Unabridged() }, 51621, resolver, Facts());

            Assert.That(message, Is.EqualTo(
                "Grabbed for the abridged recording (Random House Audio, 1993, ISBN 9780553712704), but these files run 14 h 20 min " +
                "and match the unabridged recording narrated by John MacDonald (Random House Audio, 2024, ASIN B0DP3KS9Y4)."));
            Assert.That(message, Does.Not.Contain("—"));
        }

        [Test]
        public void editions_with_nothing_apart_fall_back_to_their_ids()
        {
            var a = new Edition { Id = 1, Title = "The Client" };
            var b = new Edition { Id = 2, Title = "The Client" };

            var message = TalearrEditionMismatch.Message(a, b, new[] { a, b }, null, null, null);

            Assert.That(message, Is.EqualTo("Grabbed for 'The Client' (edition 1), but these files match 'The Client' (edition 2)."));
        }

        [Test]
        public void the_other_reading_fails_the_download_so_another_release_is_tried()
        {
            var resolver = new Resolver();
            resolver.Markers[27004] = AbridgedMarker.Abridged;
            resolver.Markers[26999] = AbridgedMarker.Unabridged;
            var message = TalearrEditionMismatch.Message(Abridged(), Unabridged(), new[] { Abridged(), Unabridged() }, 51621, resolver, Facts());

            Assert.That(ImportRejectionClassifier.ClassifyMessage(message), Is.EqualTo("release is the unabridged recording, not the wanted abridged one"));
        }

        [Test]
        public void two_editions_of_the_same_reading_stay_blocked_for_the_user()
        {
            var resolver = new Resolver();
            resolver.Markers[27004] = AbridgedMarker.Unabridged;
            resolver.Markers[26999] = AbridgedMarker.Unabridged;
            var message = TalearrEditionMismatch.Message(Abridged(), Unabridged(), new[] { Abridged(), Unabridged() }, null, resolver, Facts());

            Assert.That(ImportRejectionClassifier.ClassifyMessage(message), Is.Null);
        }
    }
}

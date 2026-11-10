using System;
using NLog;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.GrabPolicy
{
    public interface ITalearrGrabPolicyService
    {
        TalearrGrabPolicy Get();
        TalearrGrabPolicy Save(TalearrGrabPolicy policy);
    }

    // Talearr: a Talearr-only settings store. Values live in the existing Config key/value
    // table under talearr.grabpolicy.* keys. No schema change is involved, the keys are not
    // properties of ConfigService, and Chaptarr ignores rows it does not know, so Chaptarr
    // can run on the same database again after a rollback.
    public class TalearrGrabPolicyService : ITalearrGrabPolicyService
    {
        public const string AbridgedPolicyKey = "talearr.grabpolicy.abridged";
        public const string AllowBundlesKey = "talearr.grabpolicy.allowbundles";
        public const string FailRejectedImportsKey = "talearr.grabpolicy.failrejectedimports";
        public const string FailStalledDownloadsKey = "talearr.grabpolicy.failstalleddownloads";
        public const string MaxAutomaticRetriesKey = "talearr.grabpolicy.maxautomaticretries";
        public const string RetryFailedGrabsKey = "talearr.grabpolicy.retryfailedgrabs";

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrGrabPolicy _cached;

        public TalearrGrabPolicyService(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        // Null-safe accessor for optional dependencies (tests and partially wired services).
        public static TalearrGrabPolicy Resolve(ITalearrGrabPolicyService service)
        {
            try
            {
                return service?.Get() ?? TalearrGrabPolicy.Default;
            }
            catch (Exception)
            {
                return TalearrGrabPolicy.Default;
            }
        }

        public TalearrGrabPolicy Get()
        {
            lock (_lock)
            {
                if (_cached == null)
                {
                    _cached = Load();
                }

                return _cached.Clone();
            }
        }

        public TalearrGrabPolicy Save(TalearrGrabPolicy policy)
        {
            policy ??= TalearrGrabPolicy.Default;

            lock (_lock)
            {
                _repository.Upsert(AbridgedPolicyKey, policy.AbridgedPolicy.ToString().ToLowerInvariant());
                _repository.Upsert(AllowBundlesKey, policy.AllowBundles.ToString());
                _repository.Upsert(FailRejectedImportsKey, policy.FailRejectedImports.ToString());
                _repository.Upsert(FailStalledDownloadsKey, policy.FailStalledDownloads.ToString());
                _repository.Upsert(MaxAutomaticRetriesKey, policy.EffectiveMaxAutomaticRetries.ToString());
                _repository.Upsert(RetryFailedGrabsKey, policy.RetryFailedGrabs.ToString());

                _cached = null;
                _logger.Info("Talearr grab policy saved: abridged={0}, allowBundles={1}, failRejectedImports={2}, failStalledDownloads={3}, maxAutomaticRetries={4}, retryFailedGrabs={5}",
                    policy.AbridgedPolicy,
                    policy.AllowBundles,
                    policy.FailRejectedImports,
                    policy.FailStalledDownloads,
                    policy.EffectiveMaxAutomaticRetries,
                    policy.RetryFailedGrabs);

                _cached = Load();
                return _cached.Clone();
            }
        }

        private TalearrGrabPolicy Load()
        {
            var policy = TalearrGrabPolicy.Default;

            var abridged = Read(AbridgedPolicyKey);
            if (abridged != null && Enum.TryParse<AbridgedAudiobookPolicy>(abridged, true, out var abridgedPolicy) &&
                Enum.IsDefined(typeof(AbridgedAudiobookPolicy), abridgedPolicy))
            {
                policy.AbridgedPolicy = abridgedPolicy;
            }

            if (bool.TryParse(Read(AllowBundlesKey), out var allowBundles))
            {
                policy.AllowBundles = allowBundles;
            }

            if (bool.TryParse(Read(FailRejectedImportsKey), out var failRejectedImports))
            {
                policy.FailRejectedImports = failRejectedImports;
            }

            if (bool.TryParse(Read(FailStalledDownloadsKey), out var failStalledDownloads))
            {
                policy.FailStalledDownloads = failStalledDownloads;
            }

            if (int.TryParse(Read(MaxAutomaticRetriesKey), out var maxRetries))
            {
                policy.MaxAutomaticRetries = maxRetries;
            }

            if (bool.TryParse(Read(RetryFailedGrabsKey), out var retryFailedGrabs))
            {
                policy.RetryFailedGrabs = retryFailedGrabs;
            }

            return policy;
        }

        private string Read(string key)
        {
            try
            {
                var value = _repository.Get(key)?.Value;
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read Talearr setting {0}, using the default", key);
                return null;
            }
        }
    }
}

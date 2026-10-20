using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    // Talearr: client-side token bucket for the Hardcover API.
    //
    // Hardcover's documented limits (docs.hardcover.app, Getting Started > Rate Limits, read
    // 2026-10-20): a per-minute limit of 60, a burst bucket of 10 (Free) or 15 (Supporter) that
    // refills at the per-minute rate, and a daily limit of 5,000 (Free) or 50,000 (Supporter).
    // Each top-level field in a request costs one token; every query this client sends has
    // exactly one top-level field, so one request costs one token.
    //
    // We stay under that on our own (a smaller bucket of 5, refilling one token every 1.1s, about
    // 55 a minute) and also obey what the server says: the RateLimit header
    // ("Free";r=8;t=0, "daily";r=4231;t=51234), and Retry-After on a 429.
    public class TalearrHardcoverRateLimiter
    {
        public const int DefaultBurst = 5;
        public static readonly TimeSpan DefaultRefillInterval = TimeSpan.FromMilliseconds(1100);
        public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(90);

        private static readonly Regex RateLimitEntry = new Regex(@"""(?<name>[^""]+)""\s*;\s*r\s*=\s*(?<r>\d+)\s*;\s*t\s*=\s*(?<t>\d+)", RegexOptions.Compiled);

        private readonly object _lock = new object();
        private readonly Func<DateTime> _clock;
        private readonly Action<TimeSpan> _sleep;
        private readonly int _burst;
        private readonly TimeSpan _refillInterval;
        private double _tokens;
        private DateTime _lastRefill;
        private DateTime _blockedUntil = DateTime.MinValue;

        public TalearrHardcoverRateLimiter()
            : this(() => DateTime.UtcNow, t => System.Threading.Thread.Sleep(t), DefaultBurst, DefaultRefillInterval)
        {
        }

        public TalearrHardcoverRateLimiter(Func<DateTime> clock, Action<TimeSpan> sleep, int burst, TimeSpan refillInterval)
        {
            _clock = clock;
            _sleep = sleep;
            _burst = Math.Max(1, burst);
            _refillInterval = refillInterval <= TimeSpan.Zero ? DefaultRefillInterval : refillInterval;
            _tokens = _burst;
            _lastRefill = clock();
        }

        // Remaining requests today as last reported by Hardcover; null until a response carried it.
        public int? DailyRemaining { get; private set; }

        public TimeSpan TotalWaited { get; private set; }

        // Blocks until one request may be sent.
        public void Acquire()
        {
            while (true)
            {
                TimeSpan wait;
                lock (_lock)
                {
                    var now = _clock();
                    Refill(now);

                    if (now < _blockedUntil)
                    {
                        wait = _blockedUntil - now;
                    }
                    else if (_tokens >= 1)
                    {
                        _tokens -= 1;
                        return;
                    }
                    else
                    {
                        wait = TimeSpan.FromTicks((long)((1 - _tokens) * _refillInterval.Ticks));
                    }
                }

                if (wait > MaxWait)
                {
                    wait = MaxWait;
                }

                if (wait < TimeSpan.FromMilliseconds(1))
                {
                    wait = TimeSpan.FromMilliseconds(1);
                }

                TotalWaited += wait;
                _sleep(wait);
            }
        }

        // Applies the server's RateLimit header ("Free";r=0;t=12, "daily";r=4231;t=51234).
        public void ObserveRateLimitHeader(string header)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                return;
            }

            lock (_lock)
            {
                foreach (Match match in RateLimitEntry.Matches(header))
                {
                    var name = match.Groups["name"].Value;
                    var remaining = int.Parse(match.Groups["r"].Value, CultureInfo.InvariantCulture);
                    var resetSeconds = int.Parse(match.Groups["t"].Value, CultureInfo.InvariantCulture);

                    if (string.Equals(name, "daily", StringComparison.OrdinalIgnoreCase))
                    {
                        DailyRemaining = remaining;
                        continue;
                    }

                    // The plan bucket (Free, Supporter, ...). Never hold more tokens than the server has.
                    if (remaining < _tokens)
                    {
                        _tokens = remaining;
                    }

                    if (remaining == 0 && resetSeconds > 0)
                    {
                        Block(TimeSpan.FromSeconds(resetSeconds));
                    }
                }
            }
        }

        // A 429 answer: wait Retry-After seconds (or the refill interval when it is missing).
        public void ObserveTooManyRequests(string retryAfter)
        {
            var wait = _refillInterval;
            if (int.TryParse(retryAfter?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            {
                wait = TimeSpan.FromSeconds(seconds);
            }

            lock (_lock)
            {
                _tokens = 0;
                Block(wait);
            }
        }

        private void Block(TimeSpan wait)
        {
            if (wait > MaxWait)
            {
                wait = MaxWait;
            }

            var until = _clock() + wait;
            if (until > _blockedUntil)
            {
                _blockedUntil = until;
            }
        }

        private void Refill(DateTime now)
        {
            if (now <= _lastRefill)
            {
                return;
            }

            var gained = (now - _lastRefill).Ticks / (double)_refillInterval.Ticks;
            _tokens = Math.Min(_burst, _tokens + gained);
            _lastRefill = now;
        }
    }
}

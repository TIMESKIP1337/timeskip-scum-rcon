using System.Collections.Concurrent;

namespace ScumRcon.Security;

/// <summary>
/// Blocks a client key (an IP address) after too many failed logins in a time
/// window. The clock is injectable so behaviour can be tested without waiting.
/// </summary>
public sealed class AuthThrottle
{
    private sealed class State
    {
        public readonly Queue<DateTimeOffset> Failures = new();
        public DateTimeOffset BlockedUntil = DateTimeOffset.MinValue;
    }

    private const int PruneThreshold = 10_000;

    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeSpan _blockFor;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, State> _states = new();

    public AuthThrottle(int maxFailures, TimeSpan window, TimeSpan blockFor, Func<DateTimeOffset>? clock = null)
    {
        if (maxFailures < 1) throw new ArgumentOutOfRangeException(nameof(maxFailures));
        _maxFailures = maxFailures;
        _window = window;
        _blockFor = blockFor;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>True while the key is blocked; <paramref name="remaining"/> says for how long.</summary>
    public bool IsBlocked(string key, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!_states.TryGetValue(key, out State? state)) return false;

        lock (state)
        {
            DateTimeOffset now = _clock();
            if (state.BlockedUntil > now)
            {
                remaining = state.BlockedUntil - now;
                return true;
            }
        }
        return false;
    }

    /// <summary>Records a failed login. Returns true if this failure started a block.</summary>
    public bool RegisterFailure(string key)
    {
        if (_states.Count > PruneThreshold) Prune();

        State state = _states.GetOrAdd(key, _ => new State());
        lock (state)
        {
            DateTimeOffset now = _clock();
            while (state.Failures.Count > 0 && now - state.Failures.Peek() > _window)
                state.Failures.Dequeue();

            state.Failures.Enqueue(now);
            if (state.Failures.Count >= _maxFailures)
            {
                state.BlockedUntil = now + _blockFor;
                state.Failures.Clear();
                return true;
            }
        }
        return false;
    }

    /// <summary>Forgets earlier failures after a successful login.</summary>
    public void RegisterSuccess(string key)
    {
        if (_states.TryGetValue(key, out State? state))
        {
            lock (state)
            {
                state.Failures.Clear();
            }
        }
    }

    private void Prune()
    {
        DateTimeOffset now = _clock();
        foreach (var pair in _states)
        {
            lock (pair.Value)
            {
                bool expired = pair.Value.BlockedUntil <= now &&
                               (pair.Value.Failures.Count == 0 || now - pair.Value.Failures.Peek() > _window);
                if (expired) _states.TryRemove(pair.Key, out _);
            }
        }
    }
}

/// <summary>Allows at most N actions per window (used per connection for commands).</summary>
public sealed class SlidingWindowLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Queue<DateTimeOffset> _hits = new();

    public SlidingWindowLimiter(int limit, TimeSpan window, Func<DateTimeOffset>? clock = null)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        _limit = limit;
        _window = window;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>True and counts the action if under the limit; false otherwise.</summary>
    public bool TryAcquire()
    {
        DateTimeOffset now = _clock();
        while (_hits.Count > 0 && now - _hits.Peek() >= _window)
            _hits.Dequeue();

        if (_hits.Count >= _limit) return false;
        _hits.Enqueue(now);
        return true;
    }
}

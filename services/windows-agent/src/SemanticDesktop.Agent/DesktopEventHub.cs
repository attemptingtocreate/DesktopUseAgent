using SemanticDesktop.Core.Models;

namespace SemanticDesktop.Agent;

public sealed class DesktopEventHub
{
    private readonly object _gate = new();
    private readonly List<DesktopEvent> _buffer = new();
    private readonly Dictionary<string, Subscription> _subs = new(StringComparer.Ordinal);
    private readonly List<TaskCompletionSource<bool>> _waiters = new();
    private const int MaxBuffer = 256;

    public string Subscribe(IReadOnlyList<string>? types = null)
    {
        var id = "sub_" + Guid.NewGuid().ToString("N")[..12];
        lock (_gate)
        {
            _subs[id] = new Subscription(id, types?.ToArray() ?? Array.Empty<string>(), _buffer.Count);
        }

        return id;
    }

    public bool Unsubscribe(string subscriptionId)
    {
        lock (_gate)
        {
            return _subs.Remove(subscriptionId);
        }
    }

    public void Publish(DesktopEvent evt)
    {
        List<TaskCompletionSource<bool>> waiters;
        lock (_gate)
        {
            _buffer.Add(evt);
            if (_buffer.Count > MaxBuffer)
            {
                _buffer.RemoveRange(0, _buffer.Count - MaxBuffer);
                foreach (var sub in _subs.Values)
                {
                    sub.Cursor = Math.Max(0, sub.Cursor - 1);
                }
            }

            waiters = _waiters.ToList();
            _waiters.Clear();
        }

        foreach (var w in waiters)
        {
            w.TrySetResult(true);
        }
    }

    public async Task<IReadOnlyList<DesktopEvent>> PollAsync(
        string subscriptionId,
        int max,
        int waitMs,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_subs.TryGetValue(subscriptionId, out var sub))
                {
                    throw new ArgumentException($"Unknown subscription '{subscriptionId}'.");
                }

                var events = Read(sub, max);
                if (events.Count > 0 || waitMs <= 0)
                {
                    return events;
                }
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _waiters.Add(tcs);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(waitMs);
            try
            {
                await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (_subs.TryGetValue(subscriptionId, out var sub))
                    {
                        return Read(sub, max);
                    }
                }

                return Array.Empty<DesktopEvent>();
            }
        }
    }

    private List<DesktopEvent> Read(Subscription sub, int max)
    {
        var take = Math.Clamp(max, 1, 100);
        var events = new List<DesktopEvent>();
        for (var i = sub.Cursor; i < _buffer.Count && events.Count < take; i++)
        {
            var evt = _buffer[i];
            if (sub.Types.Length == 0 ||
                sub.Types.Any(t => string.Equals(t, evt.Type, StringComparison.OrdinalIgnoreCase)))
            {
                events.Add(evt);
            }

            sub.Cursor = i + 1;
        }

        return events;
    }

    private sealed class Subscription
    {
        public Subscription(string id, string[] types, int cursor)
        {
            Id = id;
            Types = types;
            Cursor = cursor;
        }

        public string Id { get; }
        public string[] Types { get; }
        public int Cursor { get; set; }
    }
}

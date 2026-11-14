namespace VidFlow.Services;

/// <summary>Media jobs share installed tools; component replacement is exclusive. New jobs wait for replacement.</summary>
public sealed class ToolUsage
{
    readonly object _gate = new();
    int _users;
    bool _updating;
    TaskCompletionSource _updated = NewSignal();
    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsUpdating { get { lock (_gate) return _updating; } }

    public async Task<IDisposable> UseAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task waiting;
            lock (_gate)
            {
                if (!_updating)
                {
                    _users++;
                    return new Lease(() => { lock (_gate) _users--; });
                }
                waiting = _updated.Task;
            }
            await waiting.WaitAsync(ct);
        }
    }

    public IDisposable? TryBeginUpdate()
    {
        lock (_gate)
        {
            if (_users > 0 || _updating) return null;
            _updating = true;
            return new Lease(() =>
            {
                TaskCompletionSource signal;
                lock (_gate)
                {
                    _updating = false;
                    signal = _updated;
                    _updated = NewSignal();
                }
                signal.TrySetResult();
            });
        }
    }

    sealed class Lease(Action release) : IDisposable
    {
        Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

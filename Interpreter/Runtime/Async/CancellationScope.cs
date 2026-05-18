using System;
using System.Threading;

namespace RaLanguage.Interpreter.Runtime.Async
{
    // Lazy-allocated CancellationTokenSource scope.
    //
    // Most async tasks never observe cancellation: they run to completion in a few
    // microseconds and never read Token, never get cancelled by a parent, and have
    // no parent that could cancel them. For those tasks we pay zero CTS allocations.
    //
    // The CTS is materialized on first observable use:
    //   * Cancel()
    //   * Token (someone wants to observe / chain)
    //   * a parent already has a CTS that is in a cancellable state
    //
    // Once materialized, the scope behaves identically to the previous eager design,
    // including parent-link propagation.
    public sealed class CancellationScope : IDisposable
    {
        private CancellationTokenSource? _cts;
        private CancellationTokenRegistration _parentLink;
        private readonly CancellationScope? _parent;
        private bool _cancelled;
        private int _disposed;

        public CancellationScope? Parent => _parent;
        public bool IsCancelled => _cancelled || (_cts != null && _cts.IsCancellationRequested);

        public CancellationScope(CancellationScope? parent = null)
        {
            _parent = parent;
            if (parent != null && parent.IsCancelled)
            {
                // Pre-cancelled at construction: skip CTS allocation, remember state,
                // any later Token access will materialize a pre-cancelled CTS.
                _cancelled = true;
            }
        }

        public CancellationToken Token
        {
            get
            {
                if (_cancelled && _cts == null)
                {
                    return new CancellationToken(true);
                }
                return EnsureSource().Token;
            }
        }

        public bool TokenAllocated => _cts != null;

        public void Cancel()
        {
            if (_cancelled) return;
            _cancelled = true;
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
            }
        }

        public void CancelAfter(int milliseconds)
        {
            try { EnsureSource().CancelAfter(milliseconds); } catch { }
        }

        public void ThrowIfCancellationRequested()
        {
            if (IsCancelled) throw new OperationCanceledException();
        }

        private CancellationTokenSource EnsureSource()
        {
            var existing = _cts;
            if (existing != null) return existing;

            var fresh = new CancellationTokenSource();
            var prior = Interlocked.CompareExchange(ref _cts, fresh, null);
            if (prior != null)
            {
                fresh.Dispose();
                return prior;
            }

            if (_cancelled)
            {
                try { fresh.Cancel(); } catch { }
            }

            if (_parent != null)
            {
                // Link parent -> this only when our CTS is materialized.
                if (_parent.IsCancelled)
                {
                    try { fresh.Cancel(); } catch { }
                }
                else
                {
                    var parentToken = _parent.Token;
                    if (parentToken.CanBeCanceled)
                    {
                        _parentLink = parentToken.Register(static state =>
                        {
                            var self = (CancellationScope)state!;
                            self.Cancel();
                        }, this);
                    }
                }
            }

            return fresh;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _parentLink.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }
        }
    }
}

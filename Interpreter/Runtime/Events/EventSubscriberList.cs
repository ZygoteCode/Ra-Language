using RaLanguage.Interpreter.Values.Functions;

namespace RaLanguage.Interpreter.Runtime.Events
{
    // A single subscriber's bookkeeping. Strong-ref by default; weak
    // subscriptions hold the handler via WeakReference<>.
    //
    // Token is monotonic per event-subscriber-list and uniquely
    // identifies the subscription for off-by-handle / dispose.
    //
    // Disposed is set by SubscriptionValue.dispose() so the next
    // snapshot pass can skip it without searching the live list.
    public sealed class EventSubscription
    {
        public long Token;
        public BaseFunctionValue? StrongHandler;
        public WeakReference<BaseFunctionValue>? WeakHandler;
        public bool Once;
        public int Priority;
        public bool Disposed;

        public BaseFunctionValue? ResolveHandler()
        {
            if (StrongHandler != null) return StrongHandler;
            if (WeakHandler != null && WeakHandler.TryGetTarget(out var h)) return h;
            return null;
        }
    }

    // Per-(instance, event) subscriber storage. Lazily allocated on
    // first subscribe. Items is the live, mutable list; snapshots are
    // produced on demand at the start of every emission.
    public sealed class EventSubscriberList
    {
        public readonly List<EventSubscription> Items = new();
        public long NextToken = 1;

        public long Add(EventSubscription sub)
        {
            sub.Token = NextToken++;
            Items.Add(sub);
            return sub.Token;
        }

        public bool RemoveByToken(long token)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].Token == token)
                {
                    Items[i].Disposed = true;
                    Items.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public bool RemoveByHandler(BaseFunctionValue handler)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                var h = Items[i].ResolveHandler();
                if (h != null && ReferenceEquals(h, handler))
                {
                    Items[i].Disposed = true;
                    Items.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public int ClearAll()
        {
            int n = Items.Count;
            for (int i = 0; i < Items.Count; i++) Items[i].Disposed = true;
            Items.Clear();
            return n;
        }

        public int LiveCount()
        {
            // Skips weak-dead refs without mutating the list (count is
            // a read-only operation; weak pruning happens on emit).
            int n = 0;
            foreach (var s in Items)
            {
                if (s.Disposed) continue;
                if (s.StrongHandler != null) { n++; continue; }
                if (s.WeakHandler != null && s.WeakHandler.TryGetTarget(out _)) { n++; }
            }
            return n;
        }
    }
}

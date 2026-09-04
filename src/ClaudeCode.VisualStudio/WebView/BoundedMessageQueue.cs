using System.Collections.Generic;

namespace ClaudeCode.VisualStudio.WebView
{
    /// <summary>
    /// Messages that could not be handed to the WebView because it was momentarily unavailable,
    /// held until it comes back.
    /// <para>
    /// Bounded, and full means the <em>oldest</em> goes. A dropped message is a rendering the user
    /// never sees, and the newest state is the one worth keeping — an ancient <c>setup</c> replayed
    /// an hour late is worse than nothing. The cap exists because the WebView can also be gone for
    /// good (tool window closed while a turn streams), and an unbounded queue would then grow for
    /// as long as Visual Studio stays open.
    /// </para>
    /// <para>Not thread-safe: every caller is on the VS main thread, which owns the WebView.</para>
    /// </summary>
    internal sealed class BoundedMessageQueue
    {
        private readonly Queue<string> _items = new Queue<string>();

        internal BoundedMessageQueue(int capacity)
        {
            Capacity = capacity < 1 ? 1 : capacity;
        }

        internal int Capacity { get; }

        internal int Count => _items.Count;

        /// <summary>How many messages the cap has thrown away, for the log to report.</summary>
        internal int DroppedCount { get; private set; }

        internal void Enqueue(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            while (_items.Count >= Capacity)
            {
                _items.Dequeue();
                DroppedCount++;
            }
            _items.Enqueue(json);
        }

        /// <summary>Takes the oldest message, or returns false when there is nothing left.</summary>
        internal bool TryDequeue(out string json)
        {
            if (_items.Count == 0)
            {
                json = null;
                return false;
            }
            json = _items.Dequeue();
            return true;
        }

        /// <summary>Puts a message back at the front — the delivery attempt failed after all.</summary>
        internal void PushFront(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            var rest = _items.ToArray();
            _items.Clear();
            _items.Enqueue(json);
            foreach (var item in rest)
            {
                if (_items.Count >= Capacity) { DroppedCount++; break; }
                _items.Enqueue(item);
            }
        }

        internal void Clear() => _items.Clear();
    }
}

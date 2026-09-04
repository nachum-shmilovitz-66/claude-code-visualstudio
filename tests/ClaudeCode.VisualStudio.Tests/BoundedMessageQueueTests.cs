using ClaudeCode.VisualStudio.WebView;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    /// <summary>
    /// The replay queue behind <c>WebViewHost.PostRaw</c>.
    /// <para>
    /// Context: messages posted while the WebView was unavailable were thrown away. Visual Studio
    /// disposes and re-creates the control while it settles the window layout at startup, so a
    /// transcript restore or a setup status landing in that window was simply lost — logged as
    /// "PostRaw EXCEPTION: Cannot access a disposed object", which reads like a crash for what is
    /// an ordinary race.
    /// </para>
    /// </summary>
    [TestClass]
    public class BoundedMessageQueueTests
    {
        [TestMethod]
        public void ReplaysInOrder()
        {
            // Order is the whole point: a restore replayed after the setup that followed it would
            // paint the panel with stale state.
            var q = new BoundedMessageQueue(10);
            q.Enqueue("one");
            q.Enqueue("two");
            q.Enqueue("three");

            Assert.AreEqual(3, q.Count);
            Assert.IsTrue(q.TryDequeue(out var a));
            Assert.IsTrue(q.TryDequeue(out var b));
            Assert.IsTrue(q.TryDequeue(out var c));
            Assert.AreEqual("one", a);
            Assert.AreEqual("two", b);
            Assert.AreEqual("three", c);
            Assert.AreEqual(0, q.Count);
        }

        [TestMethod]
        public void EmptyQueueReportsNothingToSend()
        {
            var q = new BoundedMessageQueue(4);
            Assert.IsFalse(q.TryDequeue(out var json));
            Assert.IsNull(json);
        }

        [TestMethod]
        public void AtCapacityTheOldestGoes()
        {
            // The WebView can be gone for good — a tool window closed mid-turn — and the newest
            // state is the one worth keeping.
            var q = new BoundedMessageQueue(3);
            q.Enqueue("1"); q.Enqueue("2"); q.Enqueue("3"); q.Enqueue("4"); q.Enqueue("5");

            Assert.AreEqual(3, q.Count, "the cap must hold");
            Assert.AreEqual(2, q.DroppedCount, "and it must say what it threw away");

            q.TryDequeue(out var first);
            Assert.AreEqual("3", first, "the survivors are the newest three");
        }

        [TestMethod]
        public void NeverGrowsWithoutBound()
        {
            var q = new BoundedMessageQueue(50);
            for (int i = 0; i < 10000; i++) q.Enqueue("msg " + i);

            Assert.AreEqual(50, q.Count);
            Assert.AreEqual(9950, q.DroppedCount);
        }

        [TestMethod]
        public void CapacityIsAlwaysUsable()
        {
            // A zero or negative cap would mean a queue that silently swallows everything.
            Assert.AreEqual(1, new BoundedMessageQueue(0).Capacity);
            Assert.AreEqual(1, new BoundedMessageQueue(-5).Capacity);
        }

        [TestMethod]
        public void FailedDeliveryGoesBackToTheFront()
        {
            // PostRaw pushes a message back when the control vanishes mid-replay; it must stay
            // ahead of the ones behind it.
            var q = new BoundedMessageQueue(10);
            q.Enqueue("one"); q.Enqueue("two");

            q.TryDequeue(out var taken);
            q.PushFront(taken);

            Assert.AreEqual(2, q.Count);
            q.TryDequeue(out var again);
            Assert.AreEqual("one", again);
            q.TryDequeue(out var next);
            Assert.AreEqual("two", next);
        }

        [TestMethod]
        public void PushFrontRespectsTheCap()
        {
            var q = new BoundedMessageQueue(2);
            q.Enqueue("a"); q.Enqueue("b");
            q.PushFront("z");

            Assert.AreEqual(2, q.Count);
            q.TryDequeue(out var first);
            Assert.AreEqual("z", first, "the retried message stays at the front");
        }

        [TestMethod]
        public void IgnoresNothingMessages()
        {
            var q = new BoundedMessageQueue(5);
            q.Enqueue(null);
            q.Enqueue("");
            q.PushFront(null);

            Assert.AreEqual(0, q.Count);
        }

        [TestMethod]
        public void ClearEmptiesIt()
        {
            var q = new BoundedMessageQueue(5);
            q.Enqueue("a"); q.Enqueue("b");
            q.Clear();

            Assert.AreEqual(0, q.Count);
            Assert.IsFalse(q.TryDequeue(out _));
        }
    }
}

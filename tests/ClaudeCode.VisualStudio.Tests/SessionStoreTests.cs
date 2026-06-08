using System;
using ClaudeCode.VisualStudio.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    [TestClass]
    public class SessionStoreTests
    {
        private static string NewCwd() => @"C:\unit-test\" + Guid.NewGuid().ToString("N");

        [TestMethod]
        public void SaveThenLoad_RoundTrips()
        {
            var cwd = NewCwd();
            try
            {
                var rec = new SessionRecord { SessionId = "sid-123", Model = "opus", Mode = "plan", Effort = "high" };
                rec.Messages.Add(new StoredMessage { Role = "user", Text = "hi" });
                rec.Messages.Add(new StoredMessage { Role = "assistant", Text = "hello there" });

                SessionStore.Save(cwd, rec);
                var got = SessionStore.Load(cwd);

                Assert.IsNotNull(got);
                Assert.AreEqual("sid-123", got.SessionId);
                Assert.AreEqual("opus", got.Model);
                Assert.AreEqual("plan", got.Mode);
                Assert.AreEqual("high", got.Effort);
                Assert.AreEqual(2, got.Messages.Count);
                Assert.AreEqual("user", got.Messages[0].Role);
                Assert.AreEqual("hello there", got.Messages[1].Text);
            }
            finally { SessionStore.Clear(cwd); }
        }

        [TestMethod]
        public void Load_Missing_ReturnsNull()
        {
            Assert.IsNull(SessionStore.Load(NewCwd()));
        }

        [TestMethod]
        public void Clear_RemovesRecord()
        {
            var cwd = NewCwd();
            var rec = new SessionRecord { SessionId = "x" };
            rec.Messages.Add(new StoredMessage { Role = "user", Text = "m" });
            SessionStore.Save(cwd, rec);
            Assert.IsNotNull(SessionStore.Load(cwd));

            SessionStore.Clear(cwd);
            Assert.IsNull(SessionStore.Load(cwd));
        }

        [TestMethod]
        public void Save_CapsTranscriptToLast200()
        {
            var cwd = NewCwd();
            try
            {
                var rec = new SessionRecord();
                for (int i = 0; i < 250; i++)
                    rec.Messages.Add(new StoredMessage { Role = "user", Text = "m" + i });

                SessionStore.Save(cwd, rec);
                var got = SessionStore.Load(cwd);

                Assert.AreEqual(200, got.Messages.Count);
                Assert.AreEqual("m50", got.Messages[0].Text);    // first 50 dropped
                Assert.AreEqual("m249", got.Messages[199].Text);
            }
            finally { SessionStore.Clear(cwd); }
        }

        [TestMethod]
        public void DifferentCwds_DoNotCollide()
        {
            var a = NewCwd();
            var b = NewCwd();
            try
            {
                SessionStore.Save(a, new SessionRecord { SessionId = "A" });
                SessionStore.Save(b, new SessionRecord { SessionId = "B" });
                Assert.AreEqual("A", SessionStore.Load(a).SessionId);
                Assert.AreEqual("B", SessionStore.Load(b).SessionId);
            }
            finally { SessionStore.Clear(a); SessionStore.Clear(b); }
        }
    }
}

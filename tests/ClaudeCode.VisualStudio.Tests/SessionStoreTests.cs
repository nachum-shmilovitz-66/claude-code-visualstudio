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

        // ---- resume eligibility ----------------------------------------------------------
        // The CLI stores conversations per working directory, so a session id is only
        // resumable from the directory it was created in. Resuming one from elsewhere makes
        // the CLI exit 1 with "No conversation found with session ID".

        [TestMethod]
        public void SaveThenLoad_RoundTripsCwd()
        {
            var cwd = NewCwd();
            try
            {
                SessionStore.Save(cwd, new SessionRecord { SessionId = "sid", Cwd = cwd });
                Assert.AreEqual(cwd, SessionStore.Load(cwd).Cwd);
            }
            finally { SessionStore.Clear(cwd); }
        }

        [TestMethod]
        public void CanResume_TrueWhenCwdMatches()
        {
            var rec = new SessionRecord { SessionId = "sid", Cwd = @"C:\proj\app" };
            Assert.IsTrue(SessionStore.CanResume(rec, @"C:\proj\app"));
            Assert.IsTrue(SessionStore.CanResume(rec, @"c:\PROJ\APP"), "cwd comparison is case-insensitive");
            Assert.IsTrue(SessionStore.CanResume(rec, @"C:\proj\app\"), "a trailing separator is not a different directory");
        }

        [TestMethod]
        public void CanResume_FalseWhenCwdDiffers()
        {
            // The reported bug: a session created under a solution folder was resumed after the
            // solution closed and the cwd fell back to the user profile.
            var rec = new SessionRecord { SessionId = "sid", Cwd = @"C:\proj\app" };
            Assert.IsFalse(SessionStore.CanResume(rec, @"C:\Users\someone"));
        }

        [TestMethod]
        public void CanResume_FalseWithoutSessionId()
        {
            Assert.IsFalse(SessionStore.CanResume(new SessionRecord { Cwd = @"C:\proj" }, @"C:\proj"));
            Assert.IsFalse(SessionStore.CanResume(null, @"C:\proj"));
        }

        [TestMethod]
        public void CanResume_TrueForLegacyRecordWithNoCwd()
        {
            // Records written before Cwd existed carry no directory. Keep resuming them so an
            // upgrade doesn't drop everyone's live conversation; a wrong one now self-heals on
            // the first failure instead of looping (see ClaudeSessionTests.DescribeExit).
            var rec = new SessionRecord { SessionId = "sid", Cwd = null };
            Assert.IsTrue(SessionStore.CanResume(rec, @"C:\anywhere"));
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

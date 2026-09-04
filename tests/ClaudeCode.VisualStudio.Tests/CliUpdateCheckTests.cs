using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    /// <summary>
    /// Scheduling for the hourly "is there a newer CLI?" check.
    /// <para>
    /// Regression context: hiding the chat panel or switching away from its tab unloads the control,
    /// which stopped the timer. Re-showing the panel restarted it from a flat hour, so a panel that
    /// was hidden and shown every few minutes never reached a check at all — the update banner only
    /// ever appeared at VS startup. The clock has to carry across the unloaded gap.
    /// </para>
    /// </summary>
    [TestClass]
    public class CliUpdateCheckTests
    {
        private const int Hour = ClaudeChatControl.CliCheckIntervalMs;
        private static readonly DateTime Now = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void NoCheckYet_WaitsAFullInterval()
        {
            // Nothing has run, so the load path is mid-check; the timer must not pile on top of it.
            Assert.AreEqual(Hour, ClaudeChatControl.NextCliCheckDelayMs(DateTime.MinValue, Now, Hour));
        }

        [TestMethod]
        public void JustChecked_WaitsAlmostAFullInterval()
        {
            var delay = ClaudeChatControl.NextCliCheckDelayMs(Now.AddSeconds(-1), Now, Hour);
            Assert.IsTrue(delay > Hour - 5000 && delay <= Hour, "expected ~an hour, got " + delay);
        }

        [TestMethod]
        public void HiddenForPartOfTheInterval_KeepsTheOriginalSchedule()
        {
            // Checked 20 minutes ago and the panel has been hidden since: the next check is still
            // due 40 minutes from now, not an hour from now.
            var delay = ClaudeChatControl.NextCliCheckDelayMs(Now.AddMinutes(-20), Now, Hour);
            Assert.AreEqual(40 * 60 * 1000, delay, 1000);
        }

        [TestMethod]
        public void HiddenLongerThanTheInterval_IsDueImmediately()
        {
            // The overnight case from the log: the panel sat unloaded for hours, so a check is
            // overdue the moment it comes back.
            Assert.AreEqual(5000, ClaudeChatControl.NextCliCheckDelayMs(Now.AddHours(-9), Now, Hour));
        }

        [TestMethod]
        public void DueRightNow_StillWaitsOutTheFloor()
        {
            // Never fire a process + network call in the middle of the re-dock layout churn.
            Assert.AreEqual(5000, ClaudeChatControl.NextCliCheckDelayMs(Now.AddMinutes(-60), Now, Hour));
            Assert.AreEqual(5000, ClaudeChatControl.NextCliCheckDelayMs(Now.AddMinutes(-59.99), Now, Hour));
        }

        [TestMethod]
        public void ClockWentBackwards_FallsBackToAFullInterval()
        {
            // A stamp from the future (DST shift, clock sync) must not park the timer past an hour.
            var delay = ClaudeChatControl.NextCliCheckDelayMs(Now.AddHours(2), Now, Hour);
            Assert.AreEqual(Hour, delay);
        }

        [TestMethod]
        public void DelayNeverExceedsTheInterval()
        {
            // Whatever the inputs, the check cannot drift further out than its own period.
            var offsets = new[] { -100000.0, -3600.0, -60.0, -1.0, 0.0, 1.0, 60.0, 100000.0 };
            foreach (var seconds in offsets)
            {
                var delay = ClaudeChatControl.NextCliCheckDelayMs(Now.AddSeconds(seconds), Now, Hour);
                Assert.IsTrue(delay >= 5000 && delay <= Hour,
                    "offset " + seconds + "s produced " + delay + "ms");
            }
        }
        // ---- version comparison: what actually decides whether the banner appears ----

        [DataTestMethod]
        [DataRow("2.1.258", "2.1.260", true)]    // the case from the report
        [DataRow("2.1.258", "2.1.258", false)]
        [DataRow("2.1.260", "2.1.258", false)]   // ahead of the registry (a local build) is not outdated
        [DataRow("2.0.999", "2.1.0", true)]      // minor bump beats a big patch number
        [DataRow("1.9.9", "2.0.0", true)]
        [DataRow("2.1.9", "2.1.10", true)]       // numeric, not lexicographic: "10" > "9"
        [DataRow("2.1.100", "2.1.99", false)]
        public void IsCliOutdated_ComparesNumerically(string installed, string latest, bool expected)
        {
            Assert.AreEqual(expected, ClaudeChatControl.IsCliOutdated(installed, latest));
        }

        [DataTestMethod]
        [DataRow(null, "2.1.260")]
        [DataRow("2.1.258", null)]
        [DataRow("", "")]
        [DataRow("not a version", "2.1.260")]
        [DataRow("2.1", "2.1.260")]
        public void IsCliOutdated_UnreadableVersionsNeverNag(string installed, string latest)
        {
            // A version we cannot parse must not produce an update prompt the user can never satisfy.
            Assert.IsFalse(ClaudeChatControl.IsCliOutdated(installed, latest));
        }

        [TestMethod]
        public void IsCliOutdated_IgnoresTrailingBuildText()
        {
            // `claude --version` prints "2.1.260 (Claude Code)".
            Assert.IsTrue(ClaudeChatControl.IsCliOutdated("2.1.258 (Claude Code)", "2.1.260"));
            Assert.IsFalse(ClaudeChatControl.IsCliOutdated("2.1.260 (Claude Code)", "2.1.260"));
        }

        // ---- CLAUDE_CODE_VS_CHECK_MS: the knob that makes this testable on a running VS ----

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("soon")]
        [DataRow("60s")]
        [DataRow("1e6")]
        public void ResolveInterval_UnsetOrJunk_KeepsTheHour(string raw)
        {
            Assert.AreEqual(Hour, ClaudeChatControl.ResolveCliCheckIntervalMs(raw));
        }

        [TestMethod]
        public void ResolveInterval_AcceptsATestValue()
        {
            Assert.AreEqual(60000, ClaudeChatControl.ResolveCliCheckIntervalMs("60000"));
            Assert.AreEqual(60000, ClaudeChatControl.ResolveCliCheckIntervalMs("  60000  "));
        }

        [DataTestMethod]
        [DataRow("0")]
        [DataRow("1")]
        [DataRow("-5000")]
        [DataRow("9999")]
        public void ResolveInterval_FloorsAtTenSeconds(string raw)
        {
            // A typo here would spawn `claude --version` plus a network call in a hot loop.
            Assert.AreEqual(10000, ClaudeChatControl.ResolveCliCheckIntervalMs(raw));
        }

        [TestMethod]
        public void ResolveInterval_CapsAtADay()
        {
            Assert.AreEqual(24 * 60 * 60 * 1000, ClaudeChatControl.ResolveCliCheckIntervalMs("999999999"));
        }

        [TestMethod]
        public void OverriddenInterval_StillCarriesTheClockAcrossAHiddenPanel()
        {
            // The override has to behave like the real interval, or it would prove nothing.
            var minute = 60000;
            Assert.AreEqual(30000, ClaudeChatControl.NextCliCheckDelayMs(Now.AddSeconds(-30), Now, minute), 500);
            Assert.AreEqual(5000, ClaudeChatControl.NextCliCheckDelayMs(Now.AddMinutes(-5), Now, minute));
        }

    }
}

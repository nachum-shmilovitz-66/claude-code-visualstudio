using System;
using ClaudeCode.VisualStudio.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    [TestClass]
    public class AccountServiceTests
    {
        [TestMethod]
        public void FormatPlanName_KnownAndUnknown()
        {
            Assert.AreEqual("Claude Max", AccountService.FormatPlanName("claude_max"));
            Assert.AreEqual("Claude Pro", AccountService.FormatPlanName("claude_pro"));
            Assert.AreEqual("Free", AccountService.FormatPlanName("free"));
            Assert.AreEqual("Team Plus", AccountService.FormatPlanName("team_plus")); // title-cased fallback
            Assert.IsNull(AccountService.FormatPlanName(null));
        }

        [TestMethod]
        public void FormatReset_PastIsNow_InvalidIsNull()
        {
            Assert.AreEqual("now", AccountService.FormatReset(DateTimeOffset.UtcNow.AddHours(-1).ToString("o")));
            Assert.IsNull(AccountService.FormatReset(null));
            Assert.IsNull(AccountService.FormatReset(""));
            Assert.IsNull(AccountService.FormatReset("not-a-date"));
        }

        [TestMethod]
        public void FormatReset_FutureBuckets()
        {
            Assert.AreEqual("5d", AccountService.FormatReset(DateTimeOffset.UtcNow.AddDays(5).AddMinutes(1).ToString("o")));
            Assert.AreEqual("3h", AccountService.FormatReset(DateTimeOffset.UtcNow.AddHours(3).AddSeconds(5).ToString("o")));
            Assert.AreEqual("30m", AccountService.FormatReset(DateTimeOffset.UtcNow.AddMinutes(30).AddSeconds(5).ToString("o")));
        }

        [TestMethod]
        public void ParseUsageOAuth_PopulatesLimits()
        {
            var future = DateTimeOffset.UtcNow.AddHours(4).ToString("o");
            var json = "{\"five_hour\":{\"utilization\":2,\"resets_at\":\"" + future + "\"}," +
                       "\"seven_day\":{\"utilization\":50}," +
                       "\"seven_day_opus\":null," +
                       "\"seven_day_sonnet\":{\"utilization\":12}}";

            var data = new AccountData();
            AccountService.ParseUsageOAuth(data, json);

            Assert.AreEqual(3, data.Limits.Count); // opus is null -> skipped
            var five = data.Limits[0];
            Assert.AreEqual("Session (5hr)", five.Name);
            Assert.AreEqual(2, five.Percent, 0.001);
            Assert.AreEqual("4h", five.ResetsIn);
            Assert.AreEqual("Weekly (7 day)", data.Limits[1].Name);
            Assert.AreEqual(50, data.Limits[1].Percent, 0.001);
            Assert.AreEqual("Weekly Sonnet", data.Limits[2].Name);
        }

        [TestMethod]
        public void ParseAccountInfo_ExtractsEmailAndOrg()
        {
            var json = "{\"account\":{\"email_address\":\"dev@example.com\"}," +
                       "\"organization\":{\"name\":\"Acme Org\"}}";
            var data = new AccountData();
            AccountService.ParseAccountInfo(data, json);

            Assert.AreEqual("dev@example.com", data.Email);
            Assert.AreEqual("Acme Org", data.Organization);
        }
    }
}

using ClaudeCode.VisualStudio.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    [TestClass]
    public class ClaudeSessionTests
    {
        [DataTestMethod]
        [DataRow(null, 0)]
        [DataRow("", 0)]
        [DataRow("none", 0)]
        [DataRow("low", 4096)]
        [DataRow("medium", 10000)]
        [DataRow("high", 16000)]
        [DataRow("extrahigh", 24000)]
        [DataRow("max", 31999)]
        [DataRow("ultracode", 31999)]
        [DataRow("bogus", 0)]
        public void ThinkingTokensForEffort_MapsLevels(string effort, int expected)
        {
            Assert.AreEqual(expected, ClaudeSession.ThinkingTokensForEffort(effort));
        }

        // A turn that ran a Haiku subagent: the CLI lists Haiku first (shape from a real result).
        private const string TwoModelUsage =
            "{\"claude-haiku-4-5-20251001\":{\"inputTokens\":900,\"outputTokens\":300,\"cacheReadInputTokens\":0,\"cacheCreationInputTokens\":12000,\"contextWindow\":200000,\"canonicalModel\":\"claude-haiku-4-5-20251001\"}," +
            "\"claude-opus-5-5[1m]\":{\"inputTokens\":2,\"outputTokens\":400,\"cacheReadInputTokens\":90000,\"cacheCreationInputTokens\":500,\"contextWindow\":1000000,\"canonicalModel\":\"claude-opus-5-5\"}}";

        private static void Pick(string json, string main, out string model, out long window)
        {
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
                ClaudeSession.PickMainModelUsage(doc.RootElement, main, out model, out window);
        }

        [TestMethod]
        public void PickMainModelUsage_TakesTheSessionsModel_NotASubagents()
        {
            string model; long window;
            Pick(TwoModelUsage, "claude-opus-5-5[1m]", out model, out window);
            Assert.AreEqual("claude-opus-5-5[1m]", model);
            Assert.AreEqual(1000000L, window);
            // The init model can be the canonical id, without the [1m] the usage key carries.
            Pick(TwoModelUsage, "claude-opus-5-5", out model, out window);
            Assert.AreEqual(1000000L, window);
        }

        [TestMethod]
        public void PickMainModelUsage_WithoutAnInitModel_TakesTheBusiestEntry()
        {
            string model; long window;
            Pick(TwoModelUsage, null, out model, out window);
            Assert.AreEqual("claude-opus-5-5[1m]", model);
            Assert.AreEqual(1000000L, window);
            Pick("{}", null, out model, out window);
            Assert.IsNull(model);
            Assert.AreEqual(0L, window);
        }

        [TestMethod]
        public void BuildRemoteControlRequest_MatchesTheCliSchema()
        {
            using (var on = System.Text.Json.JsonDocument.Parse(ClaudeSession.BuildRemoteControlRequest("req_rc_1", true, " app (Visual Studio) ")))
            {
                var r = on.RootElement;
                Assert.AreEqual("control_request", r.GetProperty("type").GetString());
                Assert.AreEqual("req_rc_1", r.GetProperty("request_id").GetString());
                var req = r.GetProperty("request");
                Assert.AreEqual("remote_control", req.GetProperty("subtype").GetString());
                Assert.IsTrue(req.GetProperty("enabled").GetBoolean());
                Assert.AreEqual("app (Visual Studio)", req.GetProperty("name").GetString());
            }
            using (var off = System.Text.Json.JsonDocument.Parse(ClaudeSession.BuildRemoteControlRequest("req_rc_2", false, "ignored")))
            {
                var req = off.RootElement.GetProperty("request");
                Assert.IsFalse(req.GetProperty("enabled").GetBoolean());
                Assert.IsFalse(req.TryGetProperty("name", out _), "turning it off names nothing");
            }
        }

        private static RemoteControlInfo ParseRc(string responseJson, bool enabled)
        {
            using (var doc = System.Text.Json.JsonDocument.Parse(responseJson))
                return ClaudeSession.ParseRemoteControlResponse(doc.RootElement, enabled);
        }

        [TestMethod]
        public void ParseRemoteControlResponse_ReadsTheSessionLink()
        {
            // The response object CLI 2.1.286 sends back (captured 2026-10-01).
            var rc = ParseRc("{\"subtype\":\"success\",\"request_id\":\"rc1\",\"response\":{\"session_url\":\"https://claude.ai/code/session_01AXxzQLenDiPAXUiRhTEjph\"," +
                             "\"connect_url\":\"https://claude.ai/code?environment=\",\"environment_id\":\"\",\"bridge_epoch\":1,\"bridge_session_id\":\"cse_01AXxzQLenDiPAXUiRhTEjph\"}}", true);
            Assert.IsTrue(rc.Enabled);
            Assert.AreEqual("https://claude.ai/code/session_01AXxzQLenDiPAXUiRhTEjph", rc.SessionUrl);
            Assert.AreEqual("cse_01AXxzQLenDiPAXUiRhTEjph", rc.BridgeSessionId);
            Assert.IsNull(rc.Error);

            var off = ParseRc("{\"subtype\":\"success\",\"request_id\":\"rc2\"}", false);
            Assert.IsFalse(off.Enabled);
            Assert.IsNull(off.SessionUrl);
        }

        [TestMethod]
        public void ParseRemoteControlResponse_ReportsARefusal_AndLinksOnlyClaudeAi()
        {
            var err = ParseRc("{\"subtype\":\"error\",\"request_id\":\"rc1\",\"error\":\"Remote Control is disabled by policy\"}", true);
            Assert.IsFalse(err.Enabled);
            Assert.AreEqual("Remote Control is disabled by policy", err.Error);

            var odd = ParseRc("{\"subtype\":\"success\",\"response\":{\"session_url\":\"https://evil.example/x\"}}", true);
            Assert.IsTrue(odd.Enabled);
            Assert.IsNull(odd.SessionUrl);
        }

        [TestMethod]
        public void TurnCost_IsWhatTheRunningTotalGrewBy()
        {
            // Two turns in one process (CLI 2.1.286): the second result reported 0.026168 after 0.0193427.
            Assert.AreEqual(0.0193427, ClaudeSession.TurnCost(0.0193427, 0).Value, 1e-6);
            Assert.AreEqual(0.0068253, ClaudeSession.TurnCost(0.026168, 0.0193427).Value, 1e-6);
            // A resumed process starts from the restored total, not from zero.
            Assert.AreEqual(0.0039252, ClaudeSession.TurnCost(0.0160884, 0.0121632).Value, 1e-6);
        }

        [TestMethod]
        public void TurnCost_UnknownStart_OrATotalThatShrank_IsUnknown()
        {
            Assert.IsNull(ClaudeSession.TurnCost(36.0017, null));
            Assert.IsNull(ClaudeSession.TurnCost(1.0, 2.0));
        }

        [TestMethod]
        public void ThinkingTokensForEffort_IsCaseInsensitive()
        {
            Assert.AreEqual(31999, ClaudeSession.ThinkingTokensForEffort("ULTRACODE"));
            Assert.AreEqual(4096, ClaudeSession.ThinkingTokensForEffort("Low"));
        }
    }
}

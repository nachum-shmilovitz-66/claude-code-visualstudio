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
        public void ThinkingTokensForEffort_IsCaseInsensitive()
        {
            Assert.AreEqual(31999, ClaudeSession.ThinkingTokensForEffort("ULTRACODE"));
            Assert.AreEqual(4096, ClaudeSession.ThinkingTokensForEffort("Low"));
        }
    }
}

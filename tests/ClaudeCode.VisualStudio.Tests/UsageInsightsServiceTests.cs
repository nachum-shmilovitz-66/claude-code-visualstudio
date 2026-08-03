using System;
using System.IO;
using ClaudeCode.VisualStudio.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    [TestClass]
    public class UsageInsightsServiceTests
    {
        private static string Line(string requestId, DateTime utc, long input, long cacheRead, long output, bool sidechain)
        {
            return "{\"isSidechain\":" + (sidechain ? "true" : "false") +
                   ",\"requestId\":\"" + requestId + "\"" +
                   ",\"type\":\"assistant\"" +
                   ",\"timestamp\":\"" + utc.ToString("o") + "\"" +
                   ",\"message\":{\"role\":\"assistant\",\"usage\":{\"input_tokens\":" + input +
                   ",\"cache_read_input_tokens\":" + cacheRead +
                   ",\"cache_creation_input_tokens\":0,\"output_tokens\":" + output + "}}}";
        }

        [TestMethod]
        public void ScanSession_Dedup_Windows_HighContext_Sidechain()
        {
            var now = DateTime.UtcNow;
            var file = Path.Combine(Path.GetTempPath(), "claude-insights-test-" + Guid.NewGuid().ToString("N") + ".jsonl");
            try
            {
                File.WriteAllLines(file, new[]
                {
                    // Recent (in-day), high context (200k), counted once despite the duplicate requestId.
                    Line("req_1", now.AddHours(-1), 1000, 200000, 500, false),
                    Line("req_1", now.AddHours(-1), 1000, 200000, 500, false),
                    // Recent sidechain turn, small context.
                    Line("req_2", now.AddHours(-2), 100, 0, 400, true),
                    // Three days old: counts toward the week only.
                    Line("req_3", now.AddDays(-3), 50, 0, 50, false),
                    // Ten days old: outside both windows.
                    Line("req_4", now.AddDays(-10), 999999, 0, 1, false),
                    // Non-assistant noise must be skipped by the prefilter.
                    "{\"type\":\"user\",\"timestamp\":\"" + now.ToString("o") + "\"}",
                });

                var day = new UsageInsightsService.Accumulator();
                var week = new UsageInsightsService.Accumulator();
                UsageInsightsService.ScanSession(file, now.AddDays(-1), now.AddDays(-7), day, week);

                var d = day.ToInsights();
                var w = week.ToInsights();

                Assert.AreEqual(201500 + 500, d.TotalTokens);   // req_1 once + req_2
                Assert.AreEqual(202100, w.TotalTokens);          // + req_3, req_4 excluded
                Assert.AreEqual(1, d.Sessions);
                Assert.AreEqual(1, w.Sessions);
                // req_1 (201500 of 202000 day tokens) is the only >150k-context request.
                Assert.AreEqual(100, d.PctOver150k);
                Assert.AreEqual(0, d.PctSidechain, "sidechain share rounds to 0% of day tokens");
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }
    }
}

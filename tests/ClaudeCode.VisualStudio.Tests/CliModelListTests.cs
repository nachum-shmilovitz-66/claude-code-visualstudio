using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ClaudeCode.VisualStudio.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    [TestClass]
    public class CliModelListTests
    {
        // The CLI separates the model from its blurb with " · " (space, middle dot, space).
        private static readonly string Mid = " " + (char)0xB7 + " ";

        private static string Row(string value, string resolved, string name, string label, string blurb, string levels, string extra)
        {
            return "{\"value\":" + (value == null ? "null" : "\"" + value + "\"") +
                   ",\"resolvedModel\":\"" + resolved + "\",\"displayName\":\"" + name + "\"" +
                   ",\"description\":\"" + label + Mid + blurb + "\"" +
                   (levels != null ? ",\"supportsEffort\":true,\"supportedEffortLevels\":[" + levels + "]" : "") +
                   extra + "}";
        }

        // The reply CLI 2.1.263 gives to an initialize control request, trimmed to the fields the
        // extension reads (captured 2026-09-07).
        private static string ControlResponse()
        {
            const string all = "\"low\",\"medium\",\"high\",\"xhigh\",\"max\"";
            return "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"req_probe_init\",\"response\":{\"commands\":[],\"models\":[" +
                Row("default", "claude-opus-5[1m]", "Default (recommended)", "Opus 5 with 1M context", "Best for everyday, complex tasks", all, ",\"supportsAdaptiveThinking\":true,\"supportsFastMode\":true,\"supportsAutoMode\":true") + "," +
                Row("opus[1m]", "claude-opus-5[1m]", "Opus (1M context)", "Opus 5 with 1M context", "Best for everyday, complex tasks", all, ",\"supportsAutoMode\":true") + "," +
                Row("claude-fable-5-1[1m]", "claude-fable-5-1", "Fable", "Fable 5.1", "Most capable for your hardest and longest-running tasks", all, ",\"supportsAutoMode\":true") + "," +
                Row("sonnet", "claude-sonnet-5", "Sonnet", "Sonnet 5", "Efficient for routine tasks", all, ",\"supportsAutoMode\":true") + "," +
                Row("haiku", "claude-haiku-4-5-20251001", "Haiku", "Haiku 4.5", "Fastest for quick answers", null, "") +
                "]}}}";
        }

        [TestMethod]
        public void TryParseControlResponse_RealShape_BuildsPickerRows()
        {
            var rows = new List<CliModelInfo>();
            Assert.IsTrue(CliModelList.TryParseControlResponse(ControlResponse(), rows));
            CollectionAssert.AreEqual(
                new[] { "default", "opus[1m]", "claude-fable-5-1[1m]", "sonnet", "haiku" },
                rows.Select(r => r.Id).ToArray());

            var fable = rows[2];
            Assert.AreEqual("Fable", fable.Name);
            Assert.AreEqual("Fable 5.1", fable.Label);
            Assert.AreEqual("Most capable for your hardest and longest-running tasks", fable.Desc);
            Assert.AreEqual("claude-fable-5-1", fable.Wire);
            Assert.AreEqual(10.0, fable.Ratio);
            Assert.IsTrue(fable.AutoMode);
            CollectionAssert.AreEqual(new[] { "none", "low", "medium", "high", "extrahigh", "max", "ultracode" }, fable.Efforts);

            var def = rows[0];
            Assert.AreEqual("Opus 5 with 1M context", def.Label);
            Assert.AreEqual("claude-opus-5[1m]", def.Wire);
            Assert.AreEqual(5.0, def.Ratio);

            // Sonnet: the CLI's full range, but Ultracode stays an Opus/Fable-only step.
            var sonnet = rows[3];
            Assert.AreEqual(2.0, sonnet.Ratio);
            CollectionAssert.AreEqual(new[] { "none", "low", "medium", "high", "extrahigh", "max" }, sonnet.Efforts);

            // Haiku: no effort levels reported -> thinking budgets only; no Auto mode.
            var haiku = rows[4];
            Assert.AreEqual("Haiku 4.5", haiku.Label);
            Assert.AreEqual(1.0, haiku.Ratio);
            Assert.IsFalse(haiku.AutoMode);
            CollectionAssert.AreEqual(new[] { "none", "low", "medium", "high" }, haiku.Efforts);
        }

        [TestMethod]
        public void TryParseControlResponse_ErrorOrModelLessReply_IsTrueButEmpty()
        {
            var rows = new List<CliModelInfo>();
            Assert.IsTrue(CliModelList.TryParseControlResponse(
                "{\"type\":\"control_response\",\"response\":{\"subtype\":\"error\",\"request_id\":\"x\",\"error\":\"nope\"}}", rows));
            Assert.AreEqual(0, rows.Count);
            Assert.IsTrue(CliModelList.TryParseControlResponse(
                "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"x\",\"response\":{\"commands\":[]}}}", rows));
            Assert.AreEqual(0, rows.Count);
        }

        [TestMethod]
        public void TryParseControlResponse_OtherLines_ReturnFalse()
        {
            var rows = new List<CliModelInfo>();
            Assert.IsFalse(CliModelList.TryParseControlResponse("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-opus-5\"}", rows));
            Assert.IsFalse(CliModelList.TryParseControlResponse("{\"type\":\"assistant\"}", rows));
            Assert.IsFalse(CliModelList.TryParseControlResponse("not json", rows));
            Assert.IsFalse(CliModelList.TryParseControlResponse("", rows));
            Assert.IsFalse(CliModelList.TryParseControlResponse(null, rows));
            Assert.IsFalse(CliModelList.TryParseControlResponse("[1,2]", rows));
            Assert.AreEqual(0, rows.Count);
        }

        [TestMethod]
        public void FromCli_SkipsDisabledUnsafeAndDuplicateRows()
        {
            var json = "[" +
                Row("sonnet", "claude-sonnet-5", "Sonnet", "Sonnet 5", "ok", null, "") + "," +
                Row("sonnet", "claude-sonnet-5", "Sonnet again", "Sonnet 5", "dup", null, "") + "," +
                Row("--resume", "x", "Injected", "X", "flag-shaped id", null, "") + "," +
                Row("bad id", "x", "Spaced", "X", "space in id", null, "") + "," +
                Row("claude-mythos-5-1", "claude-mythos-5-1", "Mythos", "Mythos 5.1", "not for this org", null, ",\"disabled\":true") + "," +
                "{\"displayName\":\"no value at all\"}" +
                "]";
            using (var doc = JsonDocument.Parse(json))
            {
                var rows = CliModelList.FromCli(doc.RootElement);
                // "no value" is the default entry (the CLI spells it null / absent).
                CollectionAssert.AreEqual(new[] { "sonnet", "default" }, rows.Select(r => r.Id).ToArray());
                Assert.AreEqual("Sonnet", rows[0].Name);
                Assert.AreEqual("no value at all", rows[1].Name);
            }
        }

        [TestMethod]
        public void FromCliRow_DescriptionWithoutSeparator_IsAllBlurb()
        {
            using (var doc = JsonDocument.Parse("{\"value\":\"opus\",\"displayName\":\"Opus\",\"description\":\"Latest Opus\"}"))
            {
                var m = CliModelList.FromCliRow(doc.RootElement);
                Assert.AreEqual("", m.Label);
                Assert.AreEqual("Latest Opus", m.Desc);
                Assert.IsNull(m.Wire);
                Assert.AreEqual(5.0, m.Ratio, "family read off the id when there is no resolved model");
                Assert.IsFalse(m.AutoMode);
            }
        }

        [TestMethod]
        public void EffortsFor_MapsXhighAndOrdersLevels()
        {
            using (var doc = JsonDocument.Parse("{\"supportedEffortLevels\":[\"max\",\"xhigh\",\"low\",\"bogus\"]}"))
            {
                CollectionAssert.AreEqual(new[] { "none", "low", "extrahigh", "max", "ultracode" }, CliModelList.EffortsFor(doc.RootElement, "opus"));
                CollectionAssert.AreEqual(new[] { "none", "low", "extrahigh", "max" }, CliModelList.EffortsFor(doc.RootElement, "sonnet"));
            }
            using (var doc = JsonDocument.Parse("{\"supportedEffortLevels\":[]}"))
                CollectionAssert.AreEqual(new[] { "none", "low", "medium", "high" }, CliModelList.EffortsFor(doc.RootElement, "opus"));
            using (var doc = JsonDocument.Parse("{}"))
                CollectionAssert.AreEqual(new[] { "none", "low", "medium", "high" }, CliModelList.EffortsFor(doc.RootElement, "opus"));
        }

        [TestMethod]
        public void EffortsByModel_NamesEachStep()
        {
            var map = CliModelList.EffortsByModel(CliModelList.Fallback());
            CollectionAssert.AreEquivalent(new[] { "default", "opus[1m]", "fable", "sonnet", "haiku" }, map.Keys.ToArray());
            var def = JsonSerializer.Serialize(map["default"]);
            StringAssert.Contains(def, "{\"id\":\"none\",\"name\":\"Off\"}");
            StringAssert.Contains(def, "{\"id\":\"extrahigh\",\"name\":\"Extra high\"}");
            StringAssert.Contains(def, "{\"id\":\"ultracode\",\"name\":\"Ultracode\"}");
            Assert.AreEqual(7, map["default"].Length);
            Assert.AreEqual(4, map["haiku"].Length);
        }

        [TestMethod]
        public void Fallback_RowsAreVersionFreeAliasesThatPassValidation()
        {
            foreach (var m in CliModelList.Fallback())
            {
                Assert.AreEqual(m.Id, InputValidation.SanitizeModel(m.Id, null), m.Id);
                Assert.IsFalse(m.Label.Any(char.IsDigit) && !m.Label.Contains("1M"), "no version number baked in: " + m.Label);
                Assert.IsTrue(m.Efforts.Length > 0, m.Id);
                Assert.IsTrue(m.Ratio.HasValue, m.Id);
            }
        }

        [TestMethod]
        public void FamilyOf_ReadsTheFamilyOffAnyIdShape()
        {
            Assert.AreEqual("opus", CliModelList.FamilyOf("claude-opus-5[1m]"));
            Assert.AreEqual("opus", CliModelList.FamilyOf("opus[1m]"));
            Assert.AreEqual("haiku", CliModelList.FamilyOf("claude-haiku-4-5-20251001"));
            Assert.AreEqual("fable", CliModelList.FamilyOf("fable"));
            Assert.AreEqual("sonnet", CliModelList.FamilyOf("Claude-Sonnet-5"));
            Assert.AreEqual("", CliModelList.FamilyOf(""));
            Assert.AreEqual("", CliModelList.FamilyOf(null));
        }

        [TestMethod]
        public void RatioFor_UnknownFamily_IsNull()
        {
            Assert.IsNull(CliModelList.RatioFor("mystery"));
            Assert.IsNull(CliModelList.RatioFor(""));
            Assert.AreEqual(10.0, CliModelList.RatioFor("mythos"));
        }
    }
}

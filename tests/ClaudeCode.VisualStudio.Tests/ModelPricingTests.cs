using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClaudeCode.VisualStudio.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    [TestClass]
    public class ModelPricingTests
    {
        // The shape the price table has inside the CLI bundle (CLI 2.1.281, trimmed): a minified JS
        // object literal with bare keys, nested objects inside each model, and the aliases after.
        private const string Minified =
            "var Vjn={\"//\":\"Hand-maintained baked-in model catalog \\u2014 add one entry to `models` below.\",schema_version:1," +
            "pricing_tiers:{tier_2_10:{input:2,output:10,cache_write_5m:2.5,cache_write_1h:4,cache_read:0.2,web_search:0.01}," +
            "tier_5_25:{input:5,output:25,cache_write_5m:6.25,cache_write_1h:10,cache_read:0.5,web_search:0.01}," +
            "tier_4_20_cache_read_0_20:{input:4,output:20,cache_write_5m:5,cache_write_1h:8,cache_read:0.2,web_search:0.01}," +
            "haiku_45:{input:1,output:5,cache_write_5m:1.25,cache_write_1h:2,cache_read:0.1,web_search:0.01}}," +
            "models:[{id:\"claude-haiku-4-5\",family:\"haiku\",display_name:\"Haiku 4.5\",provider_ids:{first_party:\"claude-haiku-4-5-20251001\",bedrock:\"us.anthropic.claude-haiku-4-5-20251001-v1:0\"},pricing:\"haiku_45\",capabilities:[\"effort\"]}," +
            "{id:\"claude-sonnet-5\",family:\"sonnet\",display_name:\"Sonnet 5\",provider_ids:{first_party:\"claude-sonnet-5\"},pricing:\"tier_2_10\"}," +
            "{id:\"claude-opus-5\",family:\"opus\",display_name:\"Opus 5\",context:{window:1e6},pricing:\"tier_5_25\",effort_cost_index:{low:0.67,medium:0.76}}," +
            "{id:\"claude-opus-5-5\",family:\"opus\",display_name:\"Opus 5.5\",pricing:\"tier_4_20_cache_read_0_20\",default_effort:\"high\"}]," +
            "aliases:{opus:{default:\"claude-opus-5-5\",per_provider:{bedrock:\"claude-opus-5-5\",foundry:\"claude-opus-4-6\"}},sonnet:{default:\"claude-sonnet-5\"},haiku:{default:\"claude-haiku-4-5\"}}," +
            "defaults:{},best:\"fable\"};function w(){return{pricing_tiers:1}}";

        // A later CLI that ships a model the built-in table has never heard of.
        private static string WithNewModel() =>
            Minified
                .Replace("haiku_45:{input:1", "tier_6_30:{input:6,output:30},haiku_45:{input:1")
                .Replace("}],aliases:", "},{id:\"claude-opus-6\",family:\"opus\",pricing:\"tier_6_30\"}],aliases:")
                .Replace("opus:{default:\"claude-opus-5-5\"", "opus:{default:\"claude-opus-6\"");

        private string _dir;

        [TestInitialize]
        public void Scratch()
        {
            _dir = Path.Combine(Path.GetTempPath(), "claude-vs-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            ModelPricing.PathOverride = Path.Combine(_dir, "model-pricing.json");
            ModelPricing.ResetForTests();
        }

        [TestCleanup]
        public void Cleanup()
        {
            ModelPricing.PathOverride = null;
            ModelPricing.ResetForTests();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string WriteCli(string name, string content, int leadingBytes = 1000)
        {
            var path = Path.Combine(_dir, name);
            // Binary-ish lead-in with the decoys the real claude.exe has before the table.
            var lead = "\0MZ pricing_tiers\u0001\u0002 pricing_tiers:me(o(),x()) ";
            var sb = new StringBuilder(lead);
            sb.Append('x', Math.Max(0, leadingBytes - lead.Length));
            sb.Append(content);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        [TestMethod]
        public void BuiltIn_Opus55IsFourTimesHaiku()
        {
            Assert.AreEqual(4.0, ModelPricing.RatioFor("claude-opus-5-5"));
            Assert.AreEqual(4.0, ModelPricing.RatioFor("claude-opus-5-5[1m]"));
            Assert.AreEqual(5.0, ModelPricing.RatioFor("claude-opus-5"));
            Assert.AreEqual(5.0, ModelPricing.RatioFor("claude-opus-4-8"));
            Assert.AreEqual(2.0, ModelPricing.RatioFor("claude-sonnet-5"));
            Assert.AreEqual(3.0, ModelPricing.RatioFor("claude-sonnet-4-6"));
            Assert.AreEqual(10.0, ModelPricing.RatioFor("claude-fable-5-1"));
            Assert.AreEqual(1.0, ModelPricing.RatioFor("claude-haiku-4-5-20251001"));
        }

        [TestMethod]
        public void RatioFor_ResolvesAliasesAndProviderIds()
        {
            Assert.AreEqual(4.0, ModelPricing.RatioFor("opus"));
            Assert.AreEqual(4.0, ModelPricing.RatioFor("opus[1m]"));
            Assert.AreEqual(2.0, ModelPricing.RatioFor("sonnet"));
            Assert.AreEqual(1.0, ModelPricing.RatioFor("haiku"));
            Assert.AreEqual(10.0, ModelPricing.RatioFor("fable"));
            Assert.AreEqual(4.0, ModelPricing.RatioFor("us.anthropic.claude-opus-5-5-v1:0"));
            Assert.AreEqual(5.0, ModelPricing.RatioFor("claude-opus-4-5@20251101"));
        }

        [TestMethod]
        public void RatioFor_UnknownModel_IsNull()
        {
            Assert.IsNull(ModelPricing.RatioFor("claude-opus-9"));
            Assert.IsNull(ModelPricing.RatioFor("mystery"));
            Assert.IsNull(ModelPricing.RatioFor(""));
            Assert.IsNull(ModelPricing.RatioFor(null));
        }

        [TestMethod]
        public void Normalize_StripsEveryDecoration()
        {
            Assert.AreEqual("claude-opus-5-5", ModelPricing.Normalize("Claude-Opus-5-5[1m]"));
            Assert.AreEqual("claude-haiku-4-5", ModelPricing.Normalize("claude-haiku-4-5-20251001"));
            Assert.AreEqual("claude-haiku-4-5", ModelPricing.Normalize("us.anthropic.claude-haiku-4-5-20251001-v1:0"));
            Assert.AreEqual("claude-opus-4-5", ModelPricing.Normalize("claude-opus-4-5@20251101"));
            Assert.AreEqual("opus", ModelPricing.Normalize(" opus[1m] "));
            Assert.AreEqual("", ModelPricing.Normalize(null));
        }

        [TestMethod]
        public void Parse_MinifiedCatalog_ReadsTiersModelsAndAliases()
        {
            var r = ModelPricing.Parse(Minified);
            Assert.IsNotNull(r);
            CollectionAssert.AreEquivalent(new[] { "claude-haiku-4-5", "claude-sonnet-5", "claude-opus-5", "claude-opus-5-5" }, new List<string>(r.Prices.Keys));
            CollectionAssert.AreEqual(new[] { 4.0, 20.0 }, r.Prices["claude-opus-5-5"]);
            CollectionAssert.AreEqual(new[] { 1.0, 5.0 }, r.Prices["claude-haiku-4-5"]);
            Assert.AreEqual("claude-opus-5-5", r.Aliases["opus"]);
            Assert.AreEqual("claude-sonnet-5", r.Aliases["sonnet"]);
            Assert.IsFalse(r.Aliases.ContainsKey("bedrock"), "per-provider targets are not aliases");
        }

        [TestMethod]
        public void Parse_QuotedJsonCatalog_ReadsTheSameTable()
        {
            const string json =
                "{\"pricing_tiers\": {\"tier_2_10\": {\"input\": 2, \"output\": 10}, \"haiku_45\": {\"input\": 1, \"output\": 5}, \"tier_4_20\": {\"input\": 4, \"output\": 20}}, " +
                "\"models\": [{\"id\": \"claude-haiku-4-5\", \"family\": \"haiku\", \"pricing\": \"haiku_45\"}, " +
                "{\"id\": \"claude-sonnet-5\", \"family\": \"sonnet\", \"pricing\": \"tier_2_10\"}, " +
                "{\"id\": \"claude-opus-5-5\", \"family\": \"opus\", \"pricing\": \"tier_4_20\"}], " +
                "\"aliases\": {\"opus\": {\"default\": \"claude-opus-5-5\"}}}";
            var r = ModelPricing.Parse(json);
            Assert.IsNotNull(r);
            Assert.AreEqual(3, r.Prices.Count);
            CollectionAssert.AreEqual(new[] { 4.0, 20.0 }, r.Prices["claude-opus-5-5"]);
            Assert.AreEqual("claude-opus-5-5", r.Aliases["opus"]);
        }

        [TestMethod]
        public void Parse_NoUsableTable_IsNull()
        {
            Assert.IsNull(ModelPricing.Parse(null));
            Assert.IsNull(ModelPricing.Parse("pricing_tiers:me(o(),x())"));
            Assert.IsNull(ModelPricing.Parse("pricing_tiers:{a:{input:1,output:5}},models:[{id:\"claude-x\",family:\"x\",pricing:\"a\"}]"), "one model is too few to trust");
            Assert.IsNull(ModelPricing.Parse(Minified.Replace("input:", "in:")), "tiers without prices");
            Assert.IsNull(ModelPricing.Parse(Minified.Replace("input:5,", "input:5000000,").Replace("input:4,", "input:-4,").Replace("input:2,", "input:0,")), "out-of-range prices are dropped");
        }

        [TestMethod]
        public void ReadWindow_SkipsDecoysAndFindsTheTable()
        {
            var path = WriteCli("claude.exe", Minified);
            var r = ModelPricing.Parse(ModelPricing.ReadWindow(path));
            Assert.IsNotNull(r);
            Assert.AreEqual(4, r.Prices.Count);
        }

        [TestMethod]
        public void ReadWindow_MarkerAcrossAChunkBoundary()
        {
            // The scan reads 4 MB at a time; put the marker's first bytes at the end of the first read.
            var path = WriteCli("claude.exe", Minified.Substring(Minified.IndexOf("pricing_tiers:{", StringComparison.Ordinal)), (4 << 20) - 6);
            var r = ModelPricing.Parse(ModelPricing.ReadWindow(path));
            Assert.IsNotNull(r);
            CollectionAssert.AreEqual(new[] { 4.0, 20.0 }, r.Prices["claude-opus-5-5"]);
        }

        [TestMethod]
        public void RefreshFrom_NewCliModelGetsABadgeWithoutARelease()
        {
            Assert.IsNull(ModelPricing.RatioFor("claude-opus-6"));
            var path = WriteCli("claude.exe", WithNewModel());

            Assert.IsTrue(ModelPricing.RefreshFrom(path));
            Assert.AreEqual(6.0, ModelPricing.RatioFor("claude-opus-6"));
            Assert.AreEqual(6.0, ModelPricing.RatioFor("opus"), "the alias follows the CLI");
            Assert.AreEqual(4.0, ModelPricing.RatioFor("claude-opus-5-5"));
            Assert.AreEqual(3.0, ModelPricing.RatioFor("claude-sonnet-4-6"), "built-in entries the CLI table lacks remain");
        }

        [TestMethod]
        public void RefreshFrom_UnchangedCli_IsNotScannedAgain()
        {
            var path = WriteCli("claude.exe", WithNewModel());
            Assert.IsTrue(ModelPricing.RefreshFrom(path));
            var stamp = File.GetLastWriteTimeUtc(ModelPricing.PathOverride);

            Assert.IsFalse(ModelPricing.RefreshFrom(path));
            Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(ModelPricing.PathOverride), "reading not rewritten");
        }

        [TestMethod]
        public void RefreshFrom_UnreadableCliKeepsTheLastGoodReading()
        {
            var path = WriteCli("claude.exe", WithNewModel());
            Assert.IsTrue(ModelPricing.RefreshFrom(path));

            // A CLI update whose bundle format we cannot parse.
            WriteCli("claude.exe", "no price table in this build", 5000);
            Assert.IsFalse(ModelPricing.RefreshFrom(path));
            Assert.AreEqual(6.0, ModelPricing.RatioFor("claude-opus-6"), "prices in effect kept");

            // ...and across a restart: the saved reading still carries the last good prices.
            ModelPricing.ResetForTests();
            Assert.IsNull(ModelPricing.RatioFor("claude-opus-6"));
            ModelPricing.LoadSaved();
            Assert.AreEqual(6.0, ModelPricing.RatioFor("claude-opus-6"));
        }

        [TestMethod]
        public void RefreshFrom_NoCli_IsANoOp()
        {
            Assert.IsFalse(ModelPricing.RefreshFrom(null));
            Assert.IsFalse(ModelPricing.RefreshFrom(Path.Combine(_dir, "missing.exe")));
            Assert.IsFalse(File.Exists(ModelPricing.PathOverride));
        }

        [TestMethod]
        public void Apply_ReplacesAStaleCachedRatio()
        {
            var rows = new List<CliModelInfo>
            {
                new CliModelInfo { Id = "default", Wire = "claude-opus-5-5[1m]", Ratio = 5.0 },
                new CliModelInfo { Id = "sonnet", Wire = null, Ratio = 9.0 },
                new CliModelInfo { Id = "claude-opus-9", Wire = "claude-opus-9", Ratio = 5.0 },
            };
            ModelPricing.Apply(rows);
            Assert.AreEqual(4.0, rows[0].Ratio);
            Assert.AreEqual(2.0, rows[1].Ratio, "no wire: priced by the id");
            Assert.IsNull(rows[2].Ratio);
        }

        [TestMethod]
        public void ProgramFile_NativeAndNpmInstalls()
        {
            var exe = WriteCli("claude.exe", "");
            Assert.AreEqual(exe, ClaudeCliLocator.ProgramFile(exe));

            var cmd = Path.Combine(_dir, "claude.cmd");
            File.WriteAllText(cmd, "@node cli.js %*");
            Assert.IsNull(ClaudeCliLocator.ProgramFile(cmd), "npm shim without its package");
            var pkg = Directory.CreateDirectory(Path.Combine(_dir, "node_modules", "@anthropic-ai", "claude-code")).FullName;
            File.WriteAllText(Path.Combine(pkg, "cli.js"), Minified);
            Assert.AreEqual(Path.Combine(pkg, "cli.js"), ClaudeCliLocator.ProgramFile(cmd));

            Assert.IsNull(ClaudeCliLocator.ProgramFile("claude (PATH)"));
            Assert.IsNull(ClaudeCliLocator.ProgramFile(null));
        }

        // Against the CLI installed on this machine: fails when a CLI build changes the table's shape,
        // which is the cue to adjust the parser (users keep their last good reading meanwhile).
        [TestMethod]
        public void InstalledCli_PriceTableStillParses()
        {
            var source = ClaudeCliLocator.ProgramFile(ClaudeCliLocator.Locate().ResolvedPath);
            if (source == null) Assert.Inconclusive("no local claude CLI to read");

            var r = ModelPricing.Parse(ModelPricing.ReadWindow(source));
            Assert.IsNotNull(r, "no price table found in " + source);
            Assert.IsTrue(r.Prices.Count >= 10, "only " + r.Prices.Count + " priced models");
            CollectionAssert.AreEqual(new[] { 1.0, 5.0 }, r.Prices["claude-haiku-4-5"], "the 1x reference");
            Assert.IsTrue(r.Prices.ContainsKey("claude-opus-5-5"));
            Assert.IsTrue(r.Aliases.ContainsKey("opus"));
        }
    }
}

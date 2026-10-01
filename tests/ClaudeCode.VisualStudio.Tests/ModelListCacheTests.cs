using System;
using System.Collections.Generic;
using System.IO;
using ClaudeCode.VisualStudio.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    [TestClass]
    public class ModelListCacheTests
    {
        private string _path;

        [TestInitialize]
        public void PointAtScratchFile()
        {
            _path = Path.Combine(Path.GetTempPath(), "claude-vs-tests", Guid.NewGuid().ToString("N"), "models.json");
            ModelListCache.PathOverride = _path;
        }

        [TestCleanup]
        public void RemoveScratchFile()
        {
            ModelListCache.Clear();
            ModelListCache.PathOverride = null;
            try { Directory.Delete(Path.GetDirectoryName(_path), true); } catch { }
        }

        [TestMethod]
        public void SaveThenLoad_RoundTrips()
        {
            var rows = CliModelList.Fallback();
            rows[2].Label = "Fable 5.1";
            CliModelList.ApplyDefaultContextWindow(rows, 1000000);
            ModelListCache.Save(rows);

            var got = ModelListCache.Load();
            Assert.IsNotNull(got);
            Assert.AreEqual(rows.Count, got.Count);
            Assert.AreEqual("claude-fable-5-1", got[2].Id);
            Assert.AreEqual("Fable 5.1", got[2].Label);
            Assert.AreEqual(10.0, got[2].Ratio);
            Assert.IsFalse(got[4].AutoMode);
            CollectionAssert.AreEqual(rows[4].Efforts, got[4].Efforts);
            // The measured window survives a restart, so the ring is right before the probe answers.
            Assert.AreEqual(1000000L, got[0].ContextWindow);
            Assert.IsNull(got[3].ContextWindow);
        }

        [TestMethod]
        public void Load_Missing_ReturnsNull()
        {
            Assert.IsNull(ModelListCache.Load());
        }

        [TestMethod]
        public void Save_EmptyOrNull_WritesNothing()
        {
            ModelListCache.Save(new List<CliModelInfo>());
            ModelListCache.Save(null);
            Assert.IsFalse(File.Exists(_path));
            Assert.IsNull(ModelListCache.Load());
        }

        [TestMethod]
        public void Load_DropsRowsThatCouldNotBeOffered()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            // A hand-edited (or corrupted) file: one good row, one flag-shaped id, one without a name.
            File.WriteAllText(_path,
                "{\"Defaults\":\"" + CliModelList.DefaultsStamp() + "\",\"Models\":" +
                "[{\"Id\":\"sonnet\",\"Name\":\"Sonnet\"},{\"Id\":\"--resume\",\"Name\":\"Bad\"},{\"Id\":\"haiku\"},null]}");
            var got = ModelListCache.Load();
            Assert.IsNotNull(got);
            Assert.AreEqual(1, got.Count);
            Assert.AreEqual("sonnet", got[0].Id);
        }

        [TestMethod]
        public void Load_Garbage_ReturnsNull()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, "not json");
            Assert.IsNull(ModelListCache.Load());
            File.WriteAllText(_path, "[]");
            Assert.IsNull(ModelListCache.Load());
        }

        // Seen 2026-09-26: the picker opened on "Opus 5" - a list cached from an older CLI - for the
        // ~35 s before the live probe replaced it with Opus 5.5. A list cached before this release's
        // defaults must give way to them.
        [TestMethod]
        public void Load_ListCachedBeforeTheseDefaults_IsDropped()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            // The format before the stamp existed: a bare array.
            File.WriteAllText(_path, "[{\"Id\":\"default\",\"Name\":\"Default\",\"Label\":\"Opus 5 with 1M context\"}]");
            Assert.IsNull(ModelListCache.Load());
            // Stamped by an older release whose defaults differ.
            File.WriteAllText(_path, "{\"Defaults\":\"default=claude-opus-5[1m]\",\"Models\":[{\"Id\":\"default\",\"Name\":\"Default\"}]}");
            Assert.IsNull(ModelListCache.Load());
            // Stamped under these defaults: kept.
            ModelListCache.Save(new List<CliModelInfo> { new CliModelInfo { Id = "default", Name = "Default" } });
            Assert.IsNotNull(ModelListCache.Load());
        }
    }
}

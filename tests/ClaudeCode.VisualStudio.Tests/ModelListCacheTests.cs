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
            ModelListCache.Save(rows);

            var got = ModelListCache.Load();
            Assert.IsNotNull(got);
            Assert.AreEqual(rows.Count, got.Count);
            Assert.AreEqual("fable", got[2].Id);
            Assert.AreEqual("Fable 5.1", got[2].Label);
            Assert.AreEqual(10.0, got[2].Ratio);
            Assert.IsFalse(got[4].AutoMode);
            CollectionAssert.AreEqual(rows[4].Efforts, got[4].Efforts);
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
                "[{\"Id\":\"sonnet\",\"Name\":\"Sonnet\"},{\"Id\":\"--resume\",\"Name\":\"Bad\"},{\"Id\":\"haiku\"},null]");
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
    }
}

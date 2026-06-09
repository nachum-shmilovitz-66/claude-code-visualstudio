using System;
using System.Collections.Generic;
using ClaudeCode.VisualStudio.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    [TestClass]
    public class SlashCommandCacheTests
    {
        private static string NewCwd() => @"C:\unit-test-cmds\" + Guid.NewGuid().ToString("N");

        [TestMethod]
        public void SaveThenLoad_RoundTrips()
        {
            var cwd = NewCwd();
            try
            {
                var cmds = new List<string> { "help", "review", "caveman:caveman" };
                SlashCommandCache.Save(cwd, cmds);
                var got = SlashCommandCache.Load(cwd);

                Assert.IsNotNull(got);
                CollectionAssert.AreEqual(cmds, got);
            }
            finally { SlashCommandCache.Clear(cwd); }
        }

        [TestMethod]
        public void Load_Missing_ReturnsNull()
        {
            Assert.IsNull(SlashCommandCache.Load(NewCwd()));
        }

        [TestMethod]
        public void Save_EmptyOrNull_NoFileWritten()
        {
            var cwd = NewCwd();
            try
            {
                SlashCommandCache.Save(cwd, new List<string>());
                Assert.IsNull(SlashCommandCache.Load(cwd));
                SlashCommandCache.Save(cwd, null);
                Assert.IsNull(SlashCommandCache.Load(cwd));
            }
            finally { SlashCommandCache.Clear(cwd); }
        }

        [TestMethod]
        public void DifferentCwds_DoNotCollide()
        {
            var a = NewCwd();
            var b = NewCwd();
            try
            {
                SlashCommandCache.Save(a, new List<string> { "a1" });
                SlashCommandCache.Save(b, new List<string> { "b1", "b2" });
                CollectionAssert.AreEqual(new List<string> { "a1" }, SlashCommandCache.Load(a));
                CollectionAssert.AreEqual(new List<string> { "b1", "b2" }, SlashCommandCache.Load(b));
            }
            finally { SlashCommandCache.Clear(a); SlashCommandCache.Clear(b); }
        }

        [TestMethod]
        public void Clear_RemovesRecord()
        {
            var cwd = NewCwd();
            SlashCommandCache.Save(cwd, new List<string> { "x" });
            Assert.IsNotNull(SlashCommandCache.Load(cwd));
            SlashCommandCache.Clear(cwd);
            Assert.IsNull(SlashCommandCache.Load(cwd));
        }
    }
}

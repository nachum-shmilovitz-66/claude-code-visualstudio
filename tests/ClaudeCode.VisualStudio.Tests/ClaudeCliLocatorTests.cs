using ClaudeCode.VisualStudio.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    [TestClass]
    public class ClaudeCliLocatorTests
    {
        [TestMethod]
        public void Wrap_Exe_LaunchesDirectly()
        {
            var r = ClaudeCliLocator.Wrap(@"C:\tools\claude.exe");
            Assert.AreEqual(@"C:\tools\claude.exe", r.FileName);
            Assert.IsFalse(r.ViaCmd);
            Assert.AreEqual(string.Empty, r.ArgumentPrefix);
            Assert.AreEqual(@"C:\tools\claude.exe", r.ResolvedPath);
        }

        [TestMethod]
        public void Wrap_Cmd_LaunchesViaCmdShim()
        {
            var r = ClaudeCliLocator.Wrap(@"C:\npm\claude.cmd");
            Assert.AreEqual("cmd.exe", r.FileName);
            Assert.IsTrue(r.ViaCmd);
            StringAssert.Contains(r.ArgumentPrefix, "/c");
            StringAssert.Contains(r.ArgumentPrefix, @"""C:\npm\claude.cmd""");
            Assert.AreEqual(@"C:\npm\claude.cmd", r.ResolvedPath);
        }

        [TestMethod]
        public void Wrap_Bat_AlsoUsesCmdShim()
        {
            var r = ClaudeCliLocator.Wrap(@"C:\x\claude.BAT");
            Assert.IsTrue(r.ViaCmd);
            Assert.AreEqual("cmd.exe", r.FileName);
        }
    }
}

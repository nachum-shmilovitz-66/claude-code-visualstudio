using System.Collections.Generic;
using ClaudeCode.VisualStudio.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    [TestClass]
    public class SlashCommandServiceTests
    {
        [TestMethod]
        public void TryParseInit_ExtractsSlashCommands()
        {
            var into = new List<string>();
            var line = "{\"type\":\"system\",\"subtype\":\"init\",\"slash_commands\":[\"help\",\"review\",\"agents\"]}";
            Assert.IsTrue(SlashCommandService.TryParseInit(line, into));
            CollectionAssert.AreEqual(new[] { "help", "review", "agents" }, into);
        }

        [TestMethod]
        public void TryParseInit_InitWithoutCommands_IsTrueButEmpty()
        {
            var into = new List<string>();
            Assert.IsTrue(SlashCommandService.TryParseInit("{\"type\":\"system\",\"subtype\":\"init\"}", into));
            Assert.AreEqual(0, into.Count);
        }

        [TestMethod]
        public void TryParseInit_SkipsEmptyNames()
        {
            var into = new List<string>();
            var line = "{\"type\":\"system\",\"subtype\":\"init\",\"slash_commands\":[\"help\",\"\",null,\"cost\"]}";
            Assert.IsTrue(SlashCommandService.TryParseInit(line, into));
            CollectionAssert.AreEqual(new[] { "help", "cost" }, into);
        }

        [TestMethod]
        public void TryParseInit_NonInitEvents_ReturnFalse()
        {
            var into = new List<string>();
            Assert.IsFalse(SlashCommandService.TryParseInit("{\"type\":\"system\",\"subtype\":\"other\"}", into));
            Assert.IsFalse(SlashCommandService.TryParseInit("{\"type\":\"assistant\"}", into));
            Assert.IsFalse(SlashCommandService.TryParseInit("{\"type\":\"result\",\"subtype\":\"init\"}", into));
            Assert.AreEqual(0, into.Count);
        }

        [TestMethod]
        public void TryParseInit_NonJsonOrEmpty_ReturnFalse()
        {
            var into = new List<string>();
            Assert.IsFalse(SlashCommandService.TryParseInit(null, into));
            Assert.IsFalse(SlashCommandService.TryParseInit("", into));
            Assert.IsFalse(SlashCommandService.TryParseInit("  ", into));
            Assert.IsFalse(SlashCommandService.TryParseInit("not json", into));
            Assert.IsFalse(SlashCommandService.TryParseInit("[1,2,3]", into));
        }
    }
}

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
        public void TryParseInit_MergesSkills_Deduped()
        {
            var into = new List<string>();
            var line = "{\"type\":\"system\",\"subtype\":\"init\",\"slash_commands\":[\"review\",\"init\"],\"skills\":[\"init\",\"deep-research\",\"verify\"]}";
            Assert.IsTrue(SlashCommandService.TryParseInit(line, into));
            // slash_commands first, then skills not already present; "init" not duplicated.
            CollectionAssert.AreEqual(new[] { "review", "init", "deep-research", "verify" }, into);
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

        private static string InitializeReply(string flags) =>
            "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"req_probe_init\",\"response\":{\"models\":[]" + flags + "}}}";

        [TestMethod]
        public void ReadRemoteControlFlags_ReadsAvailabilityAndAutoStart()
        {
            var r = new CliProbeResult();
            SlashCommandService.ReadRemoteControlFlags(InitializeReply(",\"remote_control_available\":true,\"remote_control_auto_enable\":false"), r);
            Assert.AreEqual(true, r.RemoteControlAvailable);
            Assert.IsFalse(r.RemoteControlAutoEnable);

            r = new CliProbeResult();
            SlashCommandService.ReadRemoteControlFlags(InitializeReply(",\"remote_control_available\":false,\"remote_control_auto_enable\":true"), r);
            Assert.AreEqual(false, r.RemoteControlAvailable);
            Assert.IsTrue(r.RemoteControlAutoEnable);
        }

        [TestMethod]
        public void ReadRemoteControlFlags_OlderCli_LeavesAvailabilityUnknown()
        {
            var r = new CliProbeResult();
            SlashCommandService.ReadRemoteControlFlags(InitializeReply(""), r);
            Assert.IsNull(r.RemoteControlAvailable);
            Assert.IsFalse(r.RemoteControlAutoEnable);
            SlashCommandService.ReadRemoteControlFlags("not json", r);
            Assert.IsNull(r.RemoteControlAvailable);
        }
    }
}

using System.Collections.Generic;
using System.Text.Json;
using ClaudeCode.VisualStudio.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    [TestClass]
    public class ClaudeDefaultsTests
    {
        // Settings files in the order ClaudeDefaults loads them: user, project, project local.
        private static List<JsonElement> Settings(params string[] json)
        {
            var list = new List<JsonElement>();
            foreach (var j in json)
                using (var doc = JsonDocument.Parse(j)) list.Add(doc.RootElement.Clone());
            return list;
        }

        [TestMethod]
        public void Effort_FallsBackToTheClisBuiltInDefault()
        {
            Assert.AreEqual("high", ClaudeDefaults.EffortFrom(null, Settings()));
            Assert.AreEqual("high", ClaudeDefaults.EffortFrom("", Settings("{}")));
        }

        [TestMethod]
        public void Effort_TakesTheUsersEffortLevel_SpelledTheSlidersWay()
        {
            Assert.AreEqual("extrahigh", ClaudeDefaults.EffortFrom(null, Settings("{\"effortLevel\":\"xhigh\"}")));
            Assert.AreEqual("max", ClaudeDefaults.EffortFrom(null, Settings("{\"effortLevel\":\"MAX\"}")));
        }

        [TestMethod]
        public void Effort_ProjectSettingsOverrideTheUsers_AndTheEnvironmentOverridesBoth()
        {
            var s = Settings("{\"effortLevel\":\"xhigh\"}", "{\"effortLevel\":\"low\"}");
            Assert.AreEqual("low", ClaudeDefaults.EffortFrom(null, s));
            Assert.AreEqual("medium", ClaudeDefaults.EffortFrom("medium", s));
        }

        [TestMethod]
        public void Effort_IgnoresLevelsTheSliderHasNoStepFor()
        {
            var s = Settings("{\"effortLevel\":\"medium\"}", "{\"effortLevel\":\"auto\"}");
            Assert.AreEqual("medium", ClaudeDefaults.EffortFrom("bogus", s));
        }

        [TestMethod]
        public void Mode_DefaultsToAuto()
        {
            Assert.AreEqual("bypassPermissions", ClaudeDefaults.ModeFrom(Settings()));
            Assert.AreEqual("bypassPermissions", ClaudeDefaults.ModeFrom(Settings("{\"permissions\":{\"allow\":[]}}")));
        }

        [TestMethod]
        public void Mode_FollowsDefaultModeFromTheSettings()
        {
            Assert.AreEqual("acceptEdits", ClaudeDefaults.ModeFrom(Settings("{\"permissions\":{\"defaultMode\":\"acceptEdits\"}}")));
            Assert.AreEqual("plan", ClaudeDefaults.ModeFrom(Settings(
                "{\"permissions\":{\"defaultMode\":\"acceptEdits\"}}",
                "{\"permissions\":{\"defaultMode\":\"plan\"}}")));
            // The CLI's "auto" is the panel's Auto mode; a mode the panel lacks falls through.
            Assert.AreEqual("bypassPermissions", ClaudeDefaults.ModeFrom(Settings("{\"permissions\":{\"defaultMode\":\"auto\"}}")));
            Assert.AreEqual("default", ClaudeDefaults.ModeFrom(Settings(
                "{\"permissions\":{\"defaultMode\":\"default\"}}",
                "{\"permissions\":{\"defaultMode\":\"dontAsk\"}}")));
        }
    }
}

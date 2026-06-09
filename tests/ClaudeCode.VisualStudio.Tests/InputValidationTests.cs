using ClaudeCode.VisualStudio.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    [TestClass]
    public class InputValidationTests
    {
        [TestMethod]
        public void Sanitize_KnownValue_PassesThrough()
        {
            Assert.AreEqual("sonnet", InputValidation.SanitizeChoice("sonnet", InputValidation.AllowedModels, "default"));
            Assert.AreEqual("plan", InputValidation.SanitizeChoice("plan", InputValidation.AllowedModes, "default"));
            Assert.AreEqual("ultracode", InputValidation.SanitizeChoice("ultracode", InputValidation.AllowedEfforts, "none"));
        }

        [TestMethod]
        public void Sanitize_InjectionAttempt_FallsBack()
        {
            Assert.AreEqual("default", InputValidation.SanitizeChoice("opus --dangerously-skip-permissions", InputValidation.AllowedModels, "default"));
            Assert.AreEqual("default", InputValidation.SanitizeChoice("x & calc.exe", InputValidation.AllowedModes, "default"));
            Assert.AreEqual("none", InputValidation.SanitizeChoice("high; rm -rf", InputValidation.AllowedEfforts, "none"));
        }

        [TestMethod]
        public void Sanitize_NullOrEmptyOrUnknown_FallsBack()
        {
            Assert.AreEqual("default", InputValidation.SanitizeChoice(null, InputValidation.AllowedModels, "default"));
            Assert.AreEqual("default", InputValidation.SanitizeChoice("", InputValidation.AllowedModels, "default"));
            Assert.AreEqual("default", InputValidation.SanitizeChoice("gpt-4", InputValidation.AllowedModels, "default"));
        }

        [TestMethod]
        public void Sanitize_CaseSensitive()
        {
            // Allow-list is ordinal: a different case is not a match.
            Assert.AreEqual("default", InputValidation.SanitizeChoice("Opus", InputValidation.AllowedModels, "default"));
        }

        [TestMethod]
        public void AllowLists_HaveExpectedMembers()
        {
            CollectionAssert.AreEquivalent(
                new[] { "default", "sonnet", "haiku" }, InputValidation.AllowedModels);
            CollectionAssert.AreEquivalent(
                new[] { "default", "acceptEdits", "plan", "bypassPermissions" }, InputValidation.AllowedModes);
            CollectionAssert.AreEquivalent(
                new[] { "none", "low", "medium", "high", "extrahigh", "max", "ultracode" }, InputValidation.AllowedEfforts);
        }
    }
}

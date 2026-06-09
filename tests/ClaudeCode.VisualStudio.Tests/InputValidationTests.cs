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
            Assert.AreEqual("plan", InputValidation.SanitizeChoice("plan", InputValidation.AllowedModes, "default"));
            Assert.AreEqual("ultracode", InputValidation.SanitizeChoice("ultracode", InputValidation.AllowedEfforts, "none"));
        }

        [TestMethod]
        public void Sanitize_InjectionAttempt_FallsBack()
        {
            Assert.AreEqual("default", InputValidation.SanitizeChoice("x & calc.exe", InputValidation.AllowedModes, "default"));
            Assert.AreEqual("none", InputValidation.SanitizeChoice("high; rm -rf", InputValidation.AllowedEfforts, "none"));
        }

        [TestMethod]
        public void SanitizeModel_ValidShapes_PassThrough()
        {
            Assert.AreEqual("fable", InputValidation.SanitizeModel("fable", "default"));
            Assert.AreEqual("sonnet", InputValidation.SanitizeModel("sonnet", "default"));
            Assert.AreEqual("claude-fable-5[1m]", InputValidation.SanitizeModel("claude-fable-5[1m]", "default"));
            Assert.AreEqual("claude-haiku-4-5-20251001", InputValidation.SanitizeModel("claude-haiku-4-5-20251001", "default"));
            Assert.AreEqual("claude-3.5-sonnet", InputValidation.SanitizeModel("claude-3.5-sonnet", "default"));
            Assert.AreEqual("a", InputValidation.SanitizeModel("a", "default"));
            Assert.AreEqual(new string('a', 64), InputValidation.SanitizeModel(new string('a', 64), "default"));
        }

        [TestMethod]
        public void SanitizeModel_InjectionShapes_FallBack()
        {
            // Spaces would let a value smuggle extra CLI flags after --model.
            Assert.AreEqual("default", InputValidation.SanitizeModel("opus --dangerously-skip-permissions", "default"));
            // A leading dash could parse as a different CLI flag.
            Assert.AreEqual("default", InputValidation.SanitizeModel("--resume", "default"));
            Assert.AreEqual("default", InputValidation.SanitizeModel("-x", "default"));
            // cmd.exe metacharacters must never reach the shim.
            Assert.AreEqual("default", InputValidation.SanitizeModel("x&calc.exe", "default"));
            Assert.AreEqual("default", InputValidation.SanitizeModel("x|y", "default"));
            Assert.AreEqual("default", InputValidation.SanitizeModel("x>y", "default"));
            Assert.AreEqual("default", InputValidation.SanitizeModel("x^y", "default"));
            Assert.AreEqual("default", InputValidation.SanitizeModel("%PATH%", "default"));
            Assert.AreEqual("default", InputValidation.SanitizeModel("x;y", "default"));
            Assert.AreEqual("default", InputValidation.SanitizeModel("x\"y", "default"));
            Assert.AreEqual("default", InputValidation.SanitizeModel("x`y", "default"));
            Assert.AreEqual("default", InputValidation.SanitizeModel("x y", "default"));
            // .NET '$' would match before a string-final '\n'; the shape uses '\z' so a
            // trailing control char can never reach the cmd.exe shim.
            Assert.AreEqual("default", InputValidation.SanitizeModel("sonnet\n", "default"));
            Assert.AreEqual("default", InputValidation.SanitizeModel("sonnet\r\n", "default"));
            Assert.AreEqual("default", InputValidation.SanitizeModel("sonnet\r", "default"));
        }

        [TestMethod]
        public void SanitizeModel_NullEmptyOrTooLong_FallsBack()
        {
            Assert.AreEqual("default", InputValidation.SanitizeModel(null, "default"));
            Assert.AreEqual("default", InputValidation.SanitizeModel("", "default"));
            Assert.AreEqual("default", InputValidation.SanitizeModel(new string('a', 65), "default"));
        }

        [TestMethod]
        public void Sanitize_NullOrEmptyOrUnknown_FallsBack()
        {
            Assert.AreEqual("default", InputValidation.SanitizeChoice(null, InputValidation.AllowedModes, "default"));
            Assert.AreEqual("default", InputValidation.SanitizeChoice("", InputValidation.AllowedModes, "default"));
            Assert.AreEqual("default", InputValidation.SanitizeChoice("gpt-4", InputValidation.AllowedModes, "default"));
        }

        [TestMethod]
        public void Sanitize_CaseSensitive()
        {
            // Allow-list is ordinal: a different case is not a match.
            Assert.AreEqual("default", InputValidation.SanitizeChoice("Plan", InputValidation.AllowedModes, "default"));
        }

        [TestMethod]
        public void AllowLists_HaveExpectedMembers()
        {
            CollectionAssert.AreEquivalent(
                new[] { "default", "acceptEdits", "plan", "bypassPermissions" }, InputValidation.AllowedModes);
            CollectionAssert.AreEquivalent(
                new[] { "none", "low", "medium", "high", "extrahigh", "max", "ultracode" }, InputValidation.AllowedEfforts);
        }
    }
}

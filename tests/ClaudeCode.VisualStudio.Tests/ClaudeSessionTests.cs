using ClaudeCode.VisualStudio.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    [TestClass]
    public class ClaudeSessionTests
    {
        [DataTestMethod]
        [DataRow(null, 0)]
        [DataRow("", 0)]
        [DataRow("none", 0)]
        [DataRow("low", 4096)]
        [DataRow("medium", 10000)]
        [DataRow("high", 16000)]
        [DataRow("extrahigh", 24000)]
        [DataRow("max", 31999)]
        [DataRow("ultracode", 31999)]
        [DataRow("bogus", 0)]
        public void ThinkingTokensForEffort_MapsLevels(string effort, int expected)
        {
            Assert.AreEqual(expected, ClaudeSession.ThinkingTokensForEffort(effort));
        }

        [TestMethod]
        public void ThinkingTokensForEffort_IsCaseInsensitive()
        {
            Assert.AreEqual(31999, ClaudeSession.ThinkingTokensForEffort("ULTRACODE"));
            Assert.AreEqual(4096, ClaudeSession.ThinkingTokensForEffort("Low"));
        }

        // ---- session id validation -------------------------------------------------------
        // The resume id is the one CLI-bound value that does not go through InputValidation,
        // so it gets its own gate. These pin that gate: it is the last thing standing between
        // a stored session id and the cmd.exe shim's command line.

        [DataTestMethod]
        [DataRow("d290f1ee-6c54-4b01-90e6-d701748f0851")]
        [DataRow("abc123")]
        [DataRow("A")]
        [DataRow("with_underscore-and-dash")]
        public void IsSafeSessionId_AcceptsPlainIds(string id)
        {
            Assert.IsTrue(ClaudeSession.IsSafeSessionId(id));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("-starts-with-dash")]          // could parse as another CLI flag
        [DataRow("--resume")]
        [DataRow("id with space")]
        [DataRow("id&calc")]                    // cmd metacharacters
        [DataRow("id|whoami")]
        [DataRow("id>out.txt")]
        [DataRow("id^x")]
        [DataRow("id%PATH%")]
        [DataRow("id\"q")]
        [DataRow("id'q")]
        [DataRow("id;x")]
        [DataRow("id\nsecond")]                 // .NET '$' would have allowed a trailing newline
        [DataRow("id\r\nsecond")]
        [DataRow("id\n")]
        [DataRow("../../etc/passwd")]
        public void IsSafeSessionId_RejectsAnythingElse(string id)
        {
            Assert.IsFalse(ClaudeSession.IsSafeSessionId(id));
        }

        [TestMethod]
        public void IsSafeSessionId_RejectsNonAsciiAlphanumerics()
        {
            // char.IsLetterOrDigit would accept these; the ASCII class deliberately does not.
            Assert.IsFalse(ClaudeSession.IsSafeSessionId("ａbc"));   // fullwidth 'a'
            Assert.IsFalse(ClaudeSession.IsSafeSessionId("٠123"));  // Arabic-Indic zero
            Assert.IsFalse(ClaudeSession.IsSafeSessionId("idé"));   // 'e' acute
        }

        [TestMethod]
        public void IsSafeSessionId_RejectsOverlongId()
        {
            Assert.IsFalse(ClaudeSession.IsSafeSessionId(new string('a', 101)));
            Assert.IsTrue(ClaudeSession.IsSafeSessionId(new string('a', 100)));
        }

        // ---- command line construction ---------------------------------------------------

        private static string Args(ClaudeSessionOptions options)
        {
            using (var session = new ClaudeSession(options))
                return session.BuildArguments(new ClaudeCliLocator.Result());
        }

        [TestMethod]
        public void BuildArguments_EmitsTheStreamingContract()
        {
            var args = Args(new ClaudeSessionOptions());
            StringAssert.Contains(args, "--print");
            StringAssert.Contains(args, "--input-format stream-json");
            StringAssert.Contains(args, "--output-format stream-json");
            StringAssert.Contains(args, "--verbose");
            StringAssert.Contains(args, "--include-partial-messages");
        }

        [TestMethod]
        public void BuildArguments_DropsUnsafeResumeId()
        {
            var args = Args(new ClaudeSessionOptions { ResumeSessionId = "abc & calc.exe" });
            Assert.IsFalse(args.Contains("--resume"), "unsafe resume id must not reach the command line");
            Assert.IsFalse(args.Contains("calc.exe"));
        }

        [TestMethod]
        public void BuildArguments_KeepsSafeResumeId()
        {
            var args = Args(new ClaudeSessionOptions { ResumeSessionId = "d290f1ee-6c54-4b01" });
            StringAssert.Contains(args, "--resume d290f1ee-6c54-4b01");
        }

        [TestMethod]
        public void BuildArguments_FallsBackWhenModelIsMalformed()
        {
            var args = Args(new ClaudeSessionOptions { Model = "sonnet & calc.exe" });
            Assert.IsFalse(args.Contains("calc.exe"), "malformed model must not reach the command line");
            StringAssert.Contains(args, "--model claude-opus-4-8[1m]");
        }

        [TestMethod]
        public void BuildArguments_FallsBackWhenPermissionModeIsMalformed()
        {
            var args = Args(new ClaudeSessionOptions { PermissionMode = "plan | whoami" });
            Assert.IsFalse(args.Contains("whoami"));
            StringAssert.Contains(args, "--permission-mode default");
        }

        [TestMethod]
        public void BuildArguments_PassesThroughValidValues()
        {
            var args = Args(new ClaudeSessionOptions { Model = "claude-sonnet-5", PermissionMode = "acceptEdits" });
            StringAssert.Contains(args, "--model claude-sonnet-5");
            StringAssert.Contains(args, "--permission-mode acceptEdits");
        }
    }
}

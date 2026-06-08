using System.Collections.Generic;
using System.IO;
using ClaudeCode.VisualStudio.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    [TestClass]
    public class IdeContextServiceTests
    {
        [DataTestMethod]
        [DataRow("a.cs", "csharp")]
        [DataRow("a.ts", "typescript")]
        [DataRow("a.js", "javascript")]
        [DataRow("a.xaml", "xml")]
        [DataRow("a.csproj", "xml")]
        [DataRow("a.py", "python")]
        [DataRow("a.md", "markdown")]
        [DataRow("a.weirdext", "weirdext")]
        [DataRow("", "")]
        public void GuessLanguage_MapsExtensions(string path, string expected)
        {
            Assert.AreEqual(expected, IdeContextService.GuessLanguage(path));
        }

        [TestMethod]
        public void GetRelative_StripsRootAndUsesForwardSlashes()
        {
            Assert.AreEqual("src/app/main.cs",
                IdeContextService.GetRelative(@"C:\repo", @"C:\repo\src\app\main.cs"));
            // trailing slash on root is handled
            Assert.AreEqual("a.cs",
                IdeContextService.GetRelative(@"C:\repo\", @"C:\repo\a.cs"));
            // outside the root -> returned as-is
            Assert.AreEqual(@"D:\other\x.cs",
                IdeContextService.GetRelative(@"C:\repo", @"D:\other\x.cs"));
        }

        [TestMethod]
        public void EnumerateWorkspaceFiles_ReturnsFiles_SkipsBuildAndVcsDirs()
        {
            var root = Path.Combine(Path.GetTempPath(), "ccvs-test-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "src"));
                Directory.CreateDirectory(Path.Combine(root, "bin"));
                Directory.CreateDirectory(Path.Combine(root, "obj"));
                Directory.CreateDirectory(Path.Combine(root, ".git"));
                Directory.CreateDirectory(Path.Combine(root, "node_modules"));

                File.WriteAllText(Path.Combine(root, "Program.cs"), "x");
                File.WriteAllText(Path.Combine(root, "src", "App.cs"), "x");
                File.WriteAllText(Path.Combine(root, "bin", "skip.dll"), "x");
                File.WriteAllText(Path.Combine(root, "obj", "skip.txt"), "x");
                File.WriteAllText(Path.Combine(root, ".git", "config"), "x");
                File.WriteAllText(Path.Combine(root, "node_modules", "pkg.js"), "x");

                var files = IdeContextService.EnumerateWorkspaceFiles(root, 800);

                CollectionAssert.Contains(files, "Program.cs");
                CollectionAssert.Contains(files, "src/App.cs");
                CollectionAssert.DoesNotContain(files, "bin/skip.dll");
                CollectionAssert.DoesNotContain(files, "obj/skip.txt");
                CollectionAssert.DoesNotContain(files, ".git/config");
                CollectionAssert.DoesNotContain(files, "node_modules/pkg.js");
                Assert.AreEqual(2, files.Count);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [TestMethod]
        public void EnumerateWorkspaceFiles_RespectsMaxCap()
        {
            var root = Path.Combine(Path.GetTempPath(), "ccvs-cap-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                for (int i = 0; i < 10; i++) File.WriteAllText(Path.Combine(root, "f" + i + ".txt"), "x");
                var files = IdeContextService.EnumerateWorkspaceFiles(root, 5);
                Assert.IsTrue(files.Count <= 5);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [TestMethod]
        public void EnumerateWorkspaceFiles_MissingRoot_ReturnsEmpty()
        {
            var files = IdeContextService.EnumerateWorkspaceFiles(@"C:\does\not\exist\" + System.Guid.NewGuid().ToString("N"), 10);
            Assert.AreEqual(0, files.Count);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Shell;
// The VS 2019 SDK still ships the legacy Microsoft.VisualStudio.Shell.Task, which makes a
// bare "Task" ambiguous there.
using Task = System.Threading.Tasks.Task;

namespace ClaudeCode.VisualStudio.Services
{
    public sealed class SelectionContext
    {
        public string FilePath;
        public string LanguageId;
        public int StartLine;     // 1-based
        public int EndLine;       // 1-based
        public string Text;       // selected text (may be empty)
        public bool HasSelection;
    }

    public sealed class DiagnosticItem
    {
        public string Level;       // "error" | "warning" | "info"
        public string File;
        public int Line;
        public string Description;
    }

    /// <summary>
    /// Reads editor/solution state from Visual Studio (active file, selection, open
    /// documents, workspace folders) and performs editor actions (open file at line).
    /// All calls marshal to the UI thread.
    /// </summary>
    public sealed class IdeContextService
    {
        public async Task<SelectionContext> GetActiveSelectionAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                var view = await VS.Documents.GetActiveDocumentViewAsync();
                if (view?.TextView == null)
                {
                    return null;
                }

                var textView = view.TextView;
                var selection = textView.Selection;
                var snapshot = textView.TextSnapshot;

                var ctx = new SelectionContext
                {
                    FilePath = view.FilePath,
                    LanguageId = GuessLanguage(view.FilePath),
                };

                var span = selection.StreamSelectionSpan.SnapshotSpan;
                ctx.HasSelection = !selection.IsEmpty && span.Length > 0;
                ctx.StartLine = snapshot.GetLineNumberFromPosition(span.Start.Position) + 1;
                ctx.EndLine = snapshot.GetLineNumberFromPosition(span.End.Position) + 1;
                ctx.Text = ctx.HasSelection ? span.GetText() : string.Empty;

                return ctx;
            }
            catch
            {
                return null;
            }
        }

        public async Task<List<string>> GetOpenFilesAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var result = new List<string>();
            try
            {
                var dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
                if (dte?.Documents != null)
                {
                    foreach (EnvDTE.Document d in dte.Documents)
                    {
                        try { if (!string.IsNullOrEmpty(d.FullName)) result.Add(d.FullName); }
                        catch { }
                    }
                }
            }
            catch { }
            return result;
        }

        public async Task<List<DiagnosticItem>> GetDiagnosticsAsync(int max = 50)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var list = new List<DiagnosticItem>();
            try
            {
                var dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>() as EnvDTE80.DTE2;
                var errorItems = dte?.ToolWindows?.ErrorList?.ErrorItems;
                if (errorItems != null)
                {
                    int count = errorItems.Count;
                    for (int i = 1; i <= count && list.Count < max; i++)
                    {
                        try
                        {
                            var it = errorItems.Item(i);
                            list.Add(new DiagnosticItem
                            {
                                Level = LevelName(it.ErrorLevel),
                                File = it.FileName,
                                Line = it.Line,
                                Description = it.Description,
                            });
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return list;
        }

        private static string LevelName(EnvDTE80.vsBuildErrorLevel level)
        {
            switch (level)
            {
                case EnvDTE80.vsBuildErrorLevel.vsBuildErrorLevelHigh: return "error";
                case EnvDTE80.vsBuildErrorLevel.vsBuildErrorLevelMedium: return "warning";
                default: return "info";
            }
        }

        /// <summary>Enumerate files under a root for @-mention, skipping build/VCS noise.</summary>
        public static List<string> EnumerateWorkspaceFiles(string root, int max = 800)
        {
            var list = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return list;
                var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "bin", "obj", ".git", ".vs", ".vscode", "node_modules", "packages", "dist", "TestResults" };
                var stack = new Stack<string>();
                stack.Push(root);
                while (stack.Count > 0 && list.Count < max)
                {
                    var dir = stack.Pop();
                    string[] entries;
                    try { entries = Directory.GetFileSystemEntries(dir); } catch { continue; }
                    foreach (var e in entries)
                    {
                        if (list.Count >= max) break;
                        var name = Path.GetFileName(e);
                        if (Directory.Exists(e))
                        {
                            if (!skip.Contains(name) && !name.StartsWith(".")) stack.Push(e);
                        }
                        else
                        {
                            try { list.Add(GetRelative(root, e)); } catch { }
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        internal static string GetRelative(string root, string path)
        {
            var r = root.EndsWith("\\") ? root : root + "\\";
            return path.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? path.Substring(r.Length).Replace('\\', '/') : path;
        }

        public async Task<List<string>> GetWorkspaceFoldersAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var folders = new List<string>();
            try
            {
                var solution = await VS.Solutions.GetCurrentSolutionAsync();
                if (solution?.FullPath != null)
                {
                    var dir = Path.GetDirectoryName(solution.FullPath);
                    if (!string.IsNullOrEmpty(dir)) folders.Add(dir);
                }
            }
            catch { }
            return folders;
        }

        public async Task OpenFileAsync(string path, int? line = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                var view = await VS.Documents.OpenAsync(path);
                if (line.HasValue && view?.TextView != null)
                {
                    var snapshot = view.TextView.TextSnapshot;
                    int idx = Math.Max(0, Math.Min(line.Value - 1, snapshot.LineCount - 1));
                    var lineObj = snapshot.GetLineFromLineNumber(idx);
                    view.TextView.Caret.MoveTo(new SnapshotPoint(snapshot, lineObj.Start.Position));
                    view.TextView.ViewScroller.EnsureSpanVisible(lineObj.Extent);
                }
            }
            catch { }
        }

        internal static string GuessLanguage(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".cs": return "csharp";
                case ".ts": return "typescript";
                case ".js": return "javascript";
                case ".json": return "json";
                case ".xml": case ".xaml": case ".csproj": case ".vsixmanifest": return "xml";
                case ".cpp": case ".h": case ".hpp": case ".cc": return "cpp";
                case ".py": return "python";
                case ".html": return "html";
                case ".css": return "css";
                case ".sql": return "sql";
                case ".md": return "markdown";
                default: return ext.TrimStart('.');
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Shell;

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

        private static string GuessLanguage(string path)
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

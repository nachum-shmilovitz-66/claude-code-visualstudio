using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCode.VisualStudio.Services
{
    public sealed class DebugLocal
    {
        public string Name;
        public string Value;
        public string Type;
    }

    /// <summary>Snapshot of the VS debugger at a point in time.</summary>
    public sealed class DebugState
    {
        public bool IsActive;          // debugging (running or paused)
        public bool IsPaused;          // stopped in break mode
        public string Mode;            // "Designing" | "Running" | "Paused"
        public string ProcessName;
        public string Function;
        public string File;
        public int Line;
        public string Exception;       // non-null only when broken on/with an exception
        public List<string> CallStack = new List<string>();
        public List<DebugLocal> Locals = new List<DebugLocal>();
    }

    /// <summary>Why the debugger entered break mode, plus where.</summary>
    public sealed class DebugBreakInfo
    {
        public string Reason;          // "Exception" | "Breakpoint" | "Step" | "Break"
        public string Exception;       // exception type + message, if any
        public string File;
        public int Line;
        public string Function;
    }

    /// <summary>
    /// Reads live VS debugger state (mode, current location, call stack, locals, the active
    /// exception) and signals when execution pauses — so Claude can reason about the running
    /// app, not just static source. All COM access marshals to the UI thread.
    /// </summary>
    public sealed class DebugContextService
    {
        private const int MaxFrames = 20;
        private const int MaxLocals = 40;
        private const int MaxValueLen = 200;

        // Held so the COM event sink is not collected; raised on the UI thread when the
        // debugger enters break mode (breakpoint, step, or a thrown/unhandled exception).
        public event Action<DebugBreakInfo> Break;

        private EnvDTE.DTE _dte;
        private EnvDTE.DebuggerEvents _debuggerEvents;
        private bool _listening;

        /// <summary>Subscribe to debugger events. Safe to call more than once.</summary>
        public async Task StartAsync()
        {
            if (_listening) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                _dte = await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
                if (_dte?.Events == null) return;
                _debuggerEvents = _dte.Events.DebuggerEvents;   // hold the ref (COM sink lifetime)
                _debuggerEvents.OnEnterBreakMode += OnEnterBreakMode;
                _listening = true;
            }
            catch { }
        }

        private void OnEnterBreakMode(EnvDTE.dbgEventReason reason, ref EnvDTE.dbgExecutionAction action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var info = new DebugBreakInfo { Reason = ReasonName(reason) };
                info.Exception = ReadException(_dte);
                ReadLocation(_dte, out info.File, out info.Line, out info.Function);
                Break?.Invoke(info);
            }
            catch { }
        }

        public async Task<DebugState> GetDebugStateAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var st = new DebugState { Mode = "Designing" };
            try
            {
                var dte = _dte ?? await VS.GetServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
                var dbg = dte?.Debugger;
                if (dbg == null) return st;

                var mode = dbg.CurrentMode;
                if (mode == EnvDTE.dbgDebugMode.dbgDesignMode) return st;

                st.IsActive = true;
                try { st.ProcessName = dbg.CurrentProcess?.Name; } catch { }

                if (mode == EnvDTE.dbgDebugMode.dbgRunMode)
                {
                    st.Mode = "Running";
                    return st;   // no current frame while running — no locals/stack to read
                }

                // Break mode: read the rich snapshot.
                st.Mode = "Paused";
                st.IsPaused = true;
                st.Exception = ReadException(dte);
                ReadLocation(dte, out st.File, out st.Line, out st.Function);
                ReadCallStack(dbg, st.CallStack);
                ReadLocals(dbg, st.Locals);
            }
            catch { }
            return st;
        }

        private static string ReasonName(EnvDTE.dbgEventReason reason)
        {
            switch (reason)
            {
                case EnvDTE.dbgEventReason.dbgEventReasonExceptionThrown:
                case EnvDTE.dbgEventReason.dbgEventReasonExceptionNotHandled:
                    return "Exception";
                case EnvDTE.dbgEventReason.dbgEventReasonBreakpoint:
                    return "Breakpoint";
                case EnvDTE.dbgEventReason.dbgEventReasonStep:
                    return "Step";
                default:
                    return "Break";
            }
        }

        // The $exception pseudo-variable holds the in-flight exception (type + message).
        private static string ReadException(EnvDTE.DTE dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dbg = dte?.Debugger;
                if (dbg == null) return null;
                var ex = dbg.GetExpression("$exception", false, 50);
                if (ex == null || !ex.IsValidValue) return null;
                var type = ex.Type;
                if (string.IsNullOrEmpty(type) || type == "<error>") return null;
                string msg = null;
                try
                {
                    var m = dbg.GetExpression("$exception.Message", false, 50);
                    if (m != null && m.IsValidValue) msg = Trim(m.Value);
                }
                catch { }
                msg = (msg ?? "").Trim('"');
                return string.IsNullOrEmpty(msg) ? type : type + ": " + msg;
            }
            catch { return null; }
        }

        private static void ReadLocation(EnvDTE.DTE dte, out string file, out int line, out string function)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            file = null; line = 0; function = null;
            try { function = dte?.Debugger?.CurrentStackFrame?.FunctionName; } catch { }
            try
            {
                var doc = dte?.ActiveDocument;
                if (doc != null)
                {
                    file = doc.FullName;
                    var sel = doc.Selection as EnvDTE.TextSelection;
                    if (sel?.ActivePoint != null) line = sel.ActivePoint.Line;
                }
            }
            catch { }
        }

        private static void ReadCallStack(EnvDTE.Debugger dbg, List<string> outStack)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var frames = dbg?.CurrentThread?.StackFrames;
                if (frames == null) return;
                int i = 0;
                foreach (EnvDTE.StackFrame f in frames)
                {
                    if (i++ >= MaxFrames) break;
                    try { if (!string.IsNullOrEmpty(f.FunctionName)) outStack.Add(f.FunctionName); }
                    catch { }
                }
            }
            catch { }
        }

        private static void ReadLocals(EnvDTE.Debugger dbg, List<DebugLocal> outLocals)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var frame = dbg?.CurrentStackFrame;
                if (frame == null) return;
                AddExpressions(frame.Arguments, outLocals);
                AddExpressions(frame.Locals, outLocals);
            }
            catch { }
        }

        private static void AddExpressions(EnvDTE.Expressions exprs, List<DebugLocal> outLocals)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (exprs == null) return;
            try
            {
                foreach (EnvDTE.Expression e in exprs)
                {
                    if (outLocals.Count >= MaxLocals) break;
                    try { outLocals.Add(new DebugLocal { Name = e.Name, Value = Trim(e.Value), Type = e.Type }); }
                    catch { }
                }
            }
            catch { }
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length > MaxValueLen ? s.Substring(0, MaxValueLen) + "…" : s;
        }
    }
}

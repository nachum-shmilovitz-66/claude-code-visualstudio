using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// Load-time instrumentation: where the milliseconds go between the package loading and the
    /// chat being usable.
    /// <para>
    /// Deliberately does no I/O while the extension is starting. A mark is a stopwatch read and a
    /// queue push (sub-microsecond); the collected report is written to the session log in ONE
    /// append, once, well after the load has finished — so measuring the load cannot itself
    /// become part of what is being measured.
    /// </para>
    /// <para>
    /// Read the report in <c>%LOCALAPPDATA%\ClaudeCodeVS\session.log</c>, under "perf report".
    /// Times are milliseconds since this class was first touched (≈ package load).
    /// </para>
    /// </summary>
    internal static class Perf
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly ConcurrentQueue<string> Marks = new ConcurrentQueue<string>();
        private static System.Threading.Timer _flushTimer;
        private static int _flushed;
        private static bool _closed;   // report written; this is a load-time tool, so stop collecting

        /// <summary>Milliseconds since the first touch of this class (≈ package load).</summary>
        public static long Now => Clock.ElapsedMilliseconds;

        /// <summary>Record that something happened, at the current elapsed time.</summary>
        public static void Mark(string what)
        {
            if (_closed) return;
            try { Marks.Enqueue(Now.ToString().PadLeft(6) + "ms  " + what); } catch { }
        }

        /// <summary>Record how long a step took, given the <see cref="Now"/> value from before it.</summary>
        public static void Step(string what, long startedAt)
        {
            if (_closed) return;
            try
            {
                long now = Now;
                Marks.Enqueue(now.ToString().PadLeft(6) + "ms  " + what + "  (+" + (now - startedAt) + "ms)");
            }
            catch { }
        }

        /// <summary>
        /// Write the report once, <paramref name="delayMs"/> from now — long enough after the load
        /// that the slow async steps (WebView boot, CLI version, npm registry) have all reported.
        /// Repeat calls are ignored, so any number of load paths may ask for it.
        /// </summary>
        public static void FlushSoon(int delayMs)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _flushed, 1, 0) != 0) return;
            try
            {
                _flushTimer = new System.Threading.Timer(_ => Flush(), null, delayMs, System.Threading.Timeout.Infinite);
            }
            catch { Flush(); }
        }

        private static void Flush()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("perf report (ms since package load):");
                while (Marks.TryDequeue(out var line)) sb.Append(Environment.NewLine).Append("        ").Append(line);
                Log.Write(sb.ToString());
            }
            catch { }
            finally
            {
                // Anything measured after the report is not load time; drop it rather than let
                // a days-long VS session accumulate marks nobody will ever read.
                _closed = true;
                try { _flushTimer?.Dispose(); } catch { }
                _flushTimer = null;
            }
        }
    }
}

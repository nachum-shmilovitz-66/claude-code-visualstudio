using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClaudeCode.VisualStudio.Services
{
    public sealed class UsagePeriodInsights
    {
        public long TotalTokens;   // input + cache write + cache read + output
        public int Sessions;       // session files with at least one turn in the window
        public int PctOver150k;    // share of tokens spent while context was >150k
        public int PctSidechain;   // share of tokens spent by subagents (isSidechain)
    }

    /// <summary>
    /// Approximate "what's contributing to your limits usage" numbers, mirroring the
    /// VS Code panel: computed from local session logs on this machine only, so other
    /// devices and claude.ai usage are not included.
    /// </summary>
    public static class UsageInsightsService
    {
        private const long HighContextThreshold = 150_000;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        private static readonly object _gate = new object();
        private static DateTime _cachedAtUtc;
        private static UsagePeriodInsights _day, _week;

        public static void Compute(out UsagePeriodInsights day, out UsagePeriodInsights week)
        {
            lock (_gate)
            {
                if (_day != null && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
                {
                    day = _day; week = _week;
                    return;
                }
            }

            var d = new Accumulator();
            var w = new Accumulator();
            try
            {
                ScanProjects(d, w);
            }
            catch (Exception ex) { Log.Write("UsageInsightsService.Compute: " + ex.Message); }

            var dayResult = d.ToInsights();
            var weekResult = w.ToInsights();
            lock (_gate)
            {
                _day = dayResult; _week = weekResult; _cachedAtUtc = DateTime.UtcNow;
            }
            day = dayResult; week = weekResult;
        }

        private static void ScanProjects(Accumulator day, Accumulator week)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var root = Path.Combine(home, ".claude", "projects");
            if (!Directory.Exists(root)) return;

            var now = DateTime.UtcNow;
            var dayAgo = now.AddDays(-1);
            var weekAgo = now.AddDays(-7);

            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < weekAgo) continue;
                    ScanSession(file, dayAgo, weekAgo, day, week);
                }
                catch (Exception ex) { Log.WriteVerbose("UsageInsightsService: skip " + file + ": " + ex.Message); }
            }
        }

        internal static void ScanSession(string file, DateTime dayAgo, DateTime weekAgo, Accumulator day, Accumulator week)
        {
            // Streaming assistant events repeat the same usage per API request, so count
            // each requestId once.
            var seenRequests = new HashSet<string>();
            bool inDay = false, inWeek = false;

            foreach (var line in File.ReadLines(file))
            {
                // Cheap prefilter: only assistant events carry usage.
                if (line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) < 0 ||
                    line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0)
                    continue;

                try { CountLine(line, dayAgo, weekAgo, seenRequests, day, week, ref inDay, ref inWeek); }
                catch { /* one malformed line must not kill the scan */ }
            }

            if (inDay) day.Sessions++;
            if (inWeek) week.Sessions++;
        }

        private static void CountLine(string line, DateTime dayAgo, DateTime weekAgo, HashSet<string> seenRequests,
            Accumulator day, Accumulator week, ref bool inDay, ref bool inWeek)
        {
            using (var doc = JsonDocument.Parse(line))
            {
                var root = doc.RootElement;

                if (!root.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(ts.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var when))
                    return;
                if (when.UtcDateTime < weekAgo) return;

                if (root.TryGetProperty("requestId", out var rid) && rid.ValueKind == JsonValueKind.String &&
                    !seenRequests.Add(rid.GetString()))
                    return;

                if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object ||
                    !msg.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                    return;

                long input = Num(usage, "input_tokens");
                long cacheRead = Num(usage, "cache_read_input_tokens");
                long cacheWrite = Num(usage, "cache_creation_input_tokens");
                long output = Num(usage, "output_tokens");
                long context = input + cacheRead + cacheWrite;
                long total = context + output;
                if (total <= 0) return;

                bool sidechain = root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True;
                bool highContext = context > HighContextThreshold;

                week.Add(total, highContext, sidechain);
                inWeek = true;
                if (when.UtcDateTime >= dayAgo)
                {
                    day.Add(total, highContext, sidechain);
                    inDay = true;
                }
            }
        }

        private static long Num(JsonElement el, string key)
        {
            return el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
                ? n : 0;
        }

        internal sealed class Accumulator
        {
            public long Total;
            public long Over150k;
            public long Sidechain;
            public int Sessions;

            public void Add(long tokens, bool highContext, bool sidechain)
            {
                Total += tokens;
                if (highContext) Over150k += tokens;
                if (sidechain) Sidechain += tokens;
            }

            public UsagePeriodInsights ToInsights()
            {
                return new UsagePeriodInsights
                {
                    TotalTokens = Total,
                    Sessions = Sessions,
                    PctOver150k = Total > 0 ? (int)Math.Round(Over150k * 100.0 / Total) : 0,
                    PctSidechain = Total > 0 ? (int)Math.Round(Sidechain * 100.0 / Total) : 0,
                };
            }
        }
    }
}

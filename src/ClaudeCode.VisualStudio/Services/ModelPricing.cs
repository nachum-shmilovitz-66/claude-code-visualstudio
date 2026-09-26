using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// A price reading taken from the installed CLI, as saved to disk: the stamp of the file it came
    /// from (so an unchanged CLI is never scanned twice) and what it found. After a failed scan the
    /// stamp moves on but <see cref="Prices"/> and <see cref="Aliases"/> keep the last good reading.
    /// </summary>
    public sealed class PriceReading
    {
        public string Source { get; set; }
        public long Length { get; set; }
        public long LastWriteTicks { get; set; }
        /// <summary>True when the last scan of <see cref="Source"/> found no usable price table.</summary>
        public bool Failed { get; set; }
        /// <summary>Model id to [input, output] list price in USD per million tokens.</summary>
        public Dictionary<string, double[]> Prices { get; set; }
        /// <summary>Alias ("opus") to the model id it resolves to ("claude-opus-5-5").</summary>
        public Dictionary<string, string> Aliases { get; set; }
    }

    /// <summary>
    /// Per-model list prices, for the picker's cost badge ("4×" = four times Haiku's price per token).
    /// Prices are per model, not per family: Opus 5.5 is $4/$20 while Opus 5 is $5/$25.
    ///
    /// The CLI carries its own price table (it prices each session with it), and a new model ships
    /// with a CLI update, so the table is read out of the installed CLI instead of being hardcoded
    /// here - no extension release per model, and no network access. That table is not a published
    /// interface (it is a JS object literal inside the CLI bundle), so reading it is best effort:
    /// the last good reading is saved and outlives a CLI build whose format we cannot parse, and
    /// <see cref="BuiltInPrices"/> covers the very first run. A model none of them knows gets no
    /// badge rather than a guess.
    ///
    /// The scan (~0.25 s for a 230 MB claude.exe) runs on a background thread, only when the CLI
    /// file's size or timestamp has changed since the last reading.
    /// </summary>
    public static class ModelPricing
    {
        // List prices, USD per million input/output tokens, as of 2026-09-26. The last fallback only.
        private static readonly Dictionary<string, double[]> BuiltInPrices = new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            ["claude-haiku-4-5"] = new[] { 1.0, 5.0 },
            ["claude-sonnet-5"] = new[] { 2.0, 10.0 },
            ["claude-sonnet-4-6"] = new[] { 3.0, 15.0 },
            ["claude-sonnet-4-5"] = new[] { 3.0, 15.0 },
            ["claude-opus-5-5"] = new[] { 4.0, 20.0 },
            ["claude-opus-5"] = new[] { 5.0, 25.0 },
            ["claude-opus-4-8"] = new[] { 5.0, 25.0 },
            ["claude-opus-4-7"] = new[] { 5.0, 25.0 },
            ["claude-opus-4-6"] = new[] { 5.0, 25.0 },
            ["claude-opus-4-5"] = new[] { 5.0, 25.0 },
            ["claude-fable-5-1"] = new[] { 10.0, 50.0 },
            ["claude-fable-5"] = new[] { 10.0, 50.0 },
            ["claude-mythos-5-1"] = new[] { 10.0, 50.0 },
            ["claude-mythos-5"] = new[] { 10.0, 50.0 },
        };

        // What the CLI's family aliases resolve to, for picker rows that carry an alias rather than
        // the resolved model (the fallback rows shown before the CLI has answered).
        private static readonly Dictionary<string, string> BuiltInAliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["opus"] = "claude-opus-5-5",
            ["sonnet"] = "claude-sonnet-5",
            ["haiku"] = "claude-haiku-4-5",
            ["fable"] = "claude-fable-5-1",
        };

        // The badge's 1×: the cheapest current model.
        private const string ReferenceModel = "claude-haiku-4-5";

        // A reading with fewer priced models than this is taken as a misparse, not a price table.
        private const int MinModels = 3;

        // Bytes kept after the table's marker; the whole catalog spans ~16 KB today.
        private const int WindowBytes = 256 * 1024;

        // The tiers object's key and opening brace: bare (minified JS) or quoted (JSON). The bare
        // name alone also turns up elsewhere in the bundle (a string table, the catalog schema).
        private static readonly byte[][] Markers =
        {
            Encoding.ASCII.GetBytes("pricing_tiers:{"),
            Encoding.ASCII.GetBytes("pricing_tiers\":{"),
            Encoding.ASCII.GetBytes("pricing_tiers\": {"),
        };

        private static readonly Regex TierRx = new Regex("\"?([A-Za-z0-9_]+)\"?\\s*:\\s*\\{([^{}]*)\\}", RegexOptions.CultureInvariant);
        private static readonly Regex InputRx = new Regex("(?:^|[,{\\s])\"?input\"?\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)", RegexOptions.CultureInvariant);
        private static readonly Regex OutputRx = new Regex("(?:^|[,{\\s])\"?output\"?\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)", RegexOptions.CultureInvariant);
        private static readonly Regex ModelStartRx = new Regex("\\{\\s*\"?id\"?\\s*:\\s*\"(claude-[a-z0-9.-]{1,60})\"", RegexOptions.CultureInvariant);
        private static readonly Regex FamilyRx = new Regex("\"?family\"?\\s*:\\s*\"[a-z]+\"", RegexOptions.CultureInvariant);
        private static readonly Regex PricingRx = new Regex("\"?pricing\"?\\s*:\\s*\"([A-Za-z0-9_]+)\"", RegexOptions.CultureInvariant);
        private static readonly Regex AliasesRx = new Regex("\"?aliases\"?\\s*:\\s*\\{", RegexOptions.CultureInvariant);
        private static readonly Regex AliasRx = new Regex("\"?([a-z]+)\"?\\s*:\\s*\\{\\s*\"?default\"?\\s*:\\s*\"(claude-[a-z0-9.-]{1,60})\"", RegexOptions.CultureInvariant);
        private static readonly Regex DateSuffixRx = new Regex("-[0-9]{8}$", RegexOptions.CultureInvariant);
        private static readonly Regex VersionSuffixRx = new Regex("-v[0-9]+$", RegexOptions.CultureInvariant);

        private sealed class Table
        {
            public readonly Dictionary<string, double[]> Prices;
            public readonly Dictionary<string, string> Aliases;

            public Table(Dictionary<string, double[]> prices, Dictionary<string, string> aliases)
            {
                Prices = prices;
                Aliases = aliases;
            }
        }

        private static volatile Table _table = Merge(null);
        private static readonly object RefreshGate = new object();

        private static readonly string DefaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeVS", "model-pricing.json");

        // Tests point this at a scratch file so they never touch the real reading.
        internal static string PathOverride { get; set; }

        private static string FilePath => PathOverride ?? DefaultPath;

        /// <summary>
        /// Price per token relative to Haiku (Haiku = 1), for a model id in any shape the CLI hands
        /// out ("claude-opus-5-5[1m]", "claude-haiku-4-5-20251001", "opus"). Null when unknown.
        /// Input and output prices scale together for every current model, so one number covers both.
        /// </summary>
        public static double? RatioFor(string modelId)
        {
            var t = _table;
            var price = Lookup(t, modelId);
            if (price == null) return null;
            double[] reference;
            double baseInput = t.Prices.TryGetValue(ReferenceModel, out reference) ? reference[0] : 1.0;
            return Math.Round(price[0] / baseInput, 2);
        }

        /// <summary>Sets each row's <see cref="CliModelInfo.Ratio"/> from its resolved model (or its id).</summary>
        public static void Apply(IEnumerable<CliModelInfo> rows)
        {
            if (rows == null) return;
            foreach (var m in rows)
                if (m != null) m.Ratio = RatioFor(string.IsNullOrEmpty(m.Wire) ? m.Id : m.Wire);
        }

        /// <summary>
        /// Loads the last saved reading, if any. A small file read; call it off the UI thread.
        /// Until it runs, <see cref="RatioFor"/> answers from the built-in table.
        /// </summary>
        public static void LoadSaved()
        {
            var saved = LoadReading();
            if (saved != null) _table = Merge(saved);
        }

        /// <summary>
        /// Re-reads the price table from the installed CLI when the CLI file has changed since the
        /// last reading. Returns true when the prices in effect changed (the caller re-posts the
        /// picker rows). Blocking - run it on a background thread.
        /// </summary>
        public static bool RefreshFromCli()
        {
            string launcher;
            try { launcher = ClaudeCliLocator.Locate().ResolvedPath; }
            catch { return false; }
            return RefreshFrom(ClaudeCliLocator.ProgramFile(launcher));
        }

        internal static bool RefreshFrom(string source)
        {
            if (string.IsNullOrEmpty(source)) return false;
            lock (RefreshGate)
            {
                try
                {
                    var fi = new FileInfo(source);
                    if (!fi.Exists) return false;
                    var saved = LoadReading();
                    if (saved != null &&
                        string.Equals(saved.Source, fi.FullName, StringComparison.OrdinalIgnoreCase) &&
                        saved.Length == fi.Length && saved.LastWriteTicks == fi.LastWriteTimeUtc.Ticks)
                        return false;

                    var sw = Stopwatch.StartNew();
                    var found = Parse(ReadWindow(fi.FullName));
                    sw.Stop();

                    var reading = new PriceReading
                    {
                        Source = fi.FullName,
                        Length = fi.Length,
                        LastWriteTicks = fi.LastWriteTimeUtc.Ticks,
                        Failed = found == null,
                        Prices = found != null ? found.Prices : saved?.Prices,
                        Aliases = found != null ? found.Aliases : saved?.Aliases,
                    };
                    SaveReading(reading);

                    if (found == null)
                    {
                        Log.Write("pricing: no price table found in " + fi.FullName + " (" + sw.ElapsedMilliseconds + " ms); keeping the last reading");
                        return false;
                    }
                    Log.Write("pricing: " + found.Prices.Count + " model price(s) from " + fi.FullName + " in " + sw.ElapsedMilliseconds + " ms");
                    var before = _table;
                    _table = Merge(reading);
                    return !SameTable(before, _table);
                }
                catch (Exception ex)
                {
                    Log.Write("pricing: refresh failed: " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>
        /// Canonical model id: lower-case, no "[1m]" suffix, no provider prefix
        /// ("us.anthropic.claude-..."), no "@date" / ":0" / "-v1" / "-20251001" suffix.
        /// An alias ("opus") comes back as itself.
        /// </summary>
        internal static string Normalize(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "";
            var s = id.Trim().ToLowerInvariant();
            int cut = s.IndexOf('[');
            if (cut >= 0) s = s.Substring(0, cut);
            int claude = s.IndexOf("claude-", StringComparison.Ordinal);
            if (claude > 0) s = s.Substring(claude);
            cut = s.IndexOfAny(new[] { '@', ':' });
            if (cut >= 0) s = s.Substring(0, cut);
            s = VersionSuffixRx.Replace(s, "");
            s = DateSuffixRx.Replace(s, "");
            return s;
        }

        /// <summary>
        /// Pulls the price table out of the CLI bundle text around the marker: the tiers object
        /// ({tier: {input, output, ...}}), each catalog model's tier, and the family aliases. Keys
        /// may be bare (minified JS) or quoted (JSON). Null when the text holds no usable table.
        /// </summary>
        internal static PriceReading Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int from = 0;
            while (true)
            {
                int at = text.IndexOf("pricing_tiers", from, StringComparison.Ordinal);
                if (at < 0) return null;
                from = at + 1;

                int brace = SkipTo(text, at + "pricing_tiers".Length, '{');
                if (brace < 0) continue;
                int end = MatchBrace(text, brace);
                if (end < 0) continue;

                var tiers = new Dictionary<string, double[]>(StringComparer.Ordinal);
                foreach (Match t in TierRx.Matches(text.Substring(brace + 1, end - brace - 1)))
                {
                    var body = t.Groups[2].Value;
                    double input, output;
                    if (TryNumber(InputRx.Match(body), out input) && TryNumber(OutputRx.Match(body), out output))
                        tiers[t.Groups[1].Value] = new[] { input, output };
                }
                if (tiers.Count == 0) continue;

                // The models array and the aliases follow the tiers; stop at the aliases so a model
                // scan cannot run on into unrelated code.
                var aliasesAt = AliasesRx.Match(text, end);
                int modelsEnd = aliasesAt.Success ? aliasesAt.Index : text.Length;

                var prices = new Dictionary<string, double[]>(StringComparer.Ordinal);
                var starts = ModelStartRx.Matches(text.Substring(0, modelsEnd), end);
                for (int i = 0; i < starts.Count; i++)
                {
                    int segEnd = i + 1 < starts.Count ? starts[i + 1].Index : modelsEnd;
                    var seg = text.Substring(starts[i].Index, segEnd - starts[i].Index);
                    if (!FamilyRx.IsMatch(seg)) continue;
                    var tier = PricingRx.Match(seg);
                    double[] p;
                    if (tier.Success && tiers.TryGetValue(tier.Groups[1].Value, out p))
                        prices[starts[i].Groups[1].Value] = p;
                }
                if (prices.Count < MinModels) continue;

                var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
                if (aliasesAt.Success)
                {
                    int aBrace = aliasesAt.Index + aliasesAt.Length - 1;
                    int aEnd = MatchBrace(text, aBrace);
                    if (aEnd > aBrace)
                        foreach (Match a in AliasRx.Matches(text.Substring(aBrace + 1, aEnd - aBrace - 1)))
                            aliases[a.Groups[1].Value] = a.Groups[2].Value;
                }
                return new PriceReading { Prices = prices, Aliases = aliases };
            }
        }

        /// <summary>
        /// Streams the file for the table's marker and returns the text from there on (up to
        /// <see cref="WindowBytes"/>). Opened so the CLI's self-update can still replace the file
        /// while we read. Null when the marker is absent.
        /// </summary>
        internal static string ReadWindow(string path)
        {
            const int Chunk = 4 << 20;
            int longest = 0;
            foreach (var m in Markers) longest = Math.Max(longest, m.Length);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
            {
                var buf = new byte[Chunk + longest];
                int carry = 0, n;
                long baseOffset = 0;
                while ((n = fs.Read(buf, carry, Chunk)) > 0)
                {
                    int len = carry + n;
                    int hit = IndexOfAny(buf, len, Markers);
                    if (hit >= 0)
                    {
                        fs.Seek(baseOffset + hit, SeekOrigin.Begin);
                        var window = new byte[WindowBytes];
                        int got = 0, r;
                        while (got < window.Length && (r = fs.Read(window, got, window.Length - got)) > 0) got += r;
                        return Encoding.UTF8.GetString(window, 0, got);
                    }
                    carry = Math.Min(longest - 1, len);
                    Buffer.BlockCopy(buf, len - carry, buf, 0, carry);
                    baseOffset += len - carry;
                }
            }
            return null;
        }

        // First index in buf[0..len) where any of the patterns (all sharing a first byte) starts.
        private static int IndexOfAny(byte[] buf, int len, byte[][] patterns)
        {
            byte first = patterns[0][0];
            for (int i = 0; i < len; i++)
            {
                if (buf[i] != first) continue;
                foreach (var p in patterns)
                {
                    if (i + p.Length > len) continue;
                    int k = 1;
                    while (k < p.Length && buf[i + k] == p[k]) k++;
                    if (k == p.Length) return i;
                }
            }
            return -1;
        }

        // Index of `ch` after `from`, allowing only a closing quote, a colon and whitespace between.
        private static int SkipTo(string s, int from, char ch)
        {
            for (int i = from; i < s.Length && i < from + 8; i++)
            {
                char c = s[i];
                if (c == ch) return i;
                if (c != '"' && c != ':' && !char.IsWhiteSpace(c)) return -1;
            }
            return -1;
        }

        // Index of the brace closing the one at `open`, skipping string literals. -1 if unbalanced.
        private static int MatchBrace(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"')
                {
                    for (i++; i < s.Length && s[i] != '"'; i++)
                        if (s[i] == '\\') i++;
                    continue;
                }
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return i;
            }
            return -1;
        }

        private static bool TryNumber(Match m, out double value)
        {
            value = 0;
            return m.Success &&
                   double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                   value > 0 && value <= 1000;
        }

        private static double[] Lookup(Table t, string modelId)
        {
            var id = Normalize(modelId);
            if (id.Length == 0) return null;
            double[] p;
            if (t.Prices.TryGetValue(id, out p)) return p;
            string target;
            if (t.Aliases.TryGetValue(id, out target) && t.Prices.TryGetValue(Normalize(target), out p)) return p;
            return null;
        }

        // Built-in prices and aliases, overlaid with a reading's (the reading wins where both have an entry).
        private static Table Merge(PriceReading reading)
        {
            var prices = new Dictionary<string, double[]>(BuiltInPrices, StringComparer.Ordinal);
            var aliases = new Dictionary<string, string>(BuiltInAliases, StringComparer.Ordinal);
            if (reading != null)
            {
                if (reading.Prices != null)
                    foreach (var kv in reading.Prices)
                        if (kv.Value != null && kv.Value.Length >= 2 && kv.Value[0] > 0 && kv.Value[0] <= 1000)
                            prices[Normalize(kv.Key)] = kv.Value;
                if (reading.Aliases != null)
                    foreach (var kv in reading.Aliases)
                        if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                            aliases[kv.Key.ToLowerInvariant()] = kv.Value;
            }
            return new Table(prices, aliases);
        }

        private static bool SameTable(Table a, Table b)
        {
            if (a.Prices.Count != b.Prices.Count || a.Aliases.Count != b.Aliases.Count) return false;
            foreach (var kv in a.Prices)
            {
                double[] other;
                if (!b.Prices.TryGetValue(kv.Key, out other) || other[0] != kv.Value[0] || other[1] != kv.Value[1]) return false;
            }
            foreach (var kv in a.Aliases)
            {
                string other;
                if (!b.Aliases.TryGetValue(kv.Key, out other) || other != kv.Value) return false;
            }
            return true;
        }

        private static PriceReading LoadReading()
        {
            try
            {
                var path = FilePath;
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<PriceReading>(File.ReadAllText(path));
            }
            catch { return null; }
        }

        private static void SaveReading(PriceReading reading)
        {
            try
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(reading));
            }
            catch { }
        }

        // Tests only: back to the built-in table.
        internal static void ResetForTests() { _table = Merge(null); }
    }
}

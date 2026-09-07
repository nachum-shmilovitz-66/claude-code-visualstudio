using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// One row of the model picker, in the shape the page consumes (WebMessage serializes it
    /// camelCase). Rows normally come from the CLI: its <c>initialize</c> control response lists
    /// the models the installed CLI and the signed-in account can use, with their current display
    /// names, so a new model release renames or adds a row here without an extension update. The
    /// hardcoded <see cref="CliModelList.Fallback"/> rows cover the first run before that response
    /// lands, and a CLI too old to answer.
    /// </summary>
    public sealed class CliModelInfo
    {
        /// <summary>Picker id. Doubles as the <c>--model</c> value, so it must pass <see cref="InputValidation.SanitizeModel"/>.</summary>
        public string Id { get; set; }
        /// <summary>Row title: "Default (recommended)", "Fable".</summary>
        public string Name { get; set; }
        /// <summary>
        /// The model half of the description line: "Fable 5.1", "Opus 5 with 1M context". Empty when
        /// unknown; the page then shows the id the CLI resolved once a session has run.
        /// </summary>
        public string Label { get; set; }
        /// <summary>What the model is for: "Most capable for your hardest and longest-running tasks".</summary>
        public string Desc { get; set; }
        /// <summary>Canonical wire id the picker id resolves to ("claude-fable-5-1"); null when unknown.</summary>
        public string Wire { get; set; }
        /// <summary>Per-token price relative to the cheapest family (Haiku = 1); null when unknown (no badge).</summary>
        public double? Ratio { get; set; }
        /// <summary>Effort ids the slider offers for this model, in order (values of <see cref="InputValidation.AllowedEfforts"/>).</summary>
        public string[] Efforts { get; set; }
        /// <summary>False hides "Auto mode" (bypassPermissions) while this model is selected.</summary>
        public bool AutoMode { get; set; }
    }

    /// <summary>
    /// Builds picker rows: from the CLI's <c>initialize</c> control response when it has answered,
    /// else the hardcoded fallback. Also derives the per-model effort ranges the page's slider uses.
    /// </summary>
    public static class CliModelList
    {
        // The CLI writes each description as "<model> · <what it is for>"; the page renders the two
        // halves separately, so split on the CLI's separator (space, middle dot, space).
        private const string DescriptionSeparator = " \u00B7 ";

        // Effort ids in slider order. The CLI's "xhigh" is the extension's "extrahigh".
        private static readonly string[] EffortOrder = { "none", "low", "medium", "high", "extrahigh", "max", "ultracode" };

        // Effort names shown on the slider, by id.
        private static readonly Dictionary<string, string> EffortNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["none"] = "Off",
            ["low"] = "Low",
            ["medium"] = "Medium",
            ["high"] = "High",
            ["extrahigh"] = "Extra high",
            ["max"] = "Max",
            ["ultracode"] = "Ultracode",
        };

        // Thinking budgets only (Off/Low/Medium/High): the range for a model the CLI reports no
        // effort levels for (Haiku), whose extended thinking is still a plain token budget.
        private static readonly string[] BudgetOnlyEfforts = { "none", "low", "medium", "high" };

        /// <summary>
        /// The rows shown before the CLI has answered (and if it never does). Every id is a CLI
        /// alias that resolves to the newest model in its family at launch, and the labels carry
        /// no version number, so nothing here goes stale when a model ships: the CLI list renames
        /// the rows the moment it lands, and it is what the picker normally shows.
        /// </summary>
        public static List<CliModelInfo> Fallback()
        {
            var full = new[] { "none", "low", "medium", "high", "extrahigh", "max", "ultracode" };
            return new List<CliModelInfo>
            {
                new CliModelInfo { Id = "default",  Name = "Default (recommended)", Label = "Opus with 1M context", Desc = "Best for everyday, complex tasks", Wire = "opus[1m]", Ratio = RatioFor("opus"), Efforts = full, AutoMode = true },
                new CliModelInfo { Id = "opus[1m]", Name = "Opus (1M context)",     Label = "Opus with 1M context", Desc = "Best for everyday, complex tasks", Wire = "opus[1m]", Ratio = RatioFor("opus"), Efforts = full, AutoMode = true },
                new CliModelInfo { Id = "fable",    Name = "Fable",                 Label = "Fable",  Desc = "Most capable for your hardest and longest-running tasks", Wire = "fable", Ratio = RatioFor("fable"), Efforts = full, AutoMode = true },
                new CliModelInfo { Id = "sonnet",   Name = "Sonnet",                Label = "Sonnet", Desc = "Efficient for routine tasks", Wire = "sonnet", Ratio = RatioFor("sonnet"), Efforts = new[] { "none", "low", "medium", "high", "extrahigh", "max" }, AutoMode = true },
                new CliModelInfo { Id = "haiku",    Name = "Haiku",                 Label = "Haiku",  Desc = "Fastest for quick answers", Wire = "haiku", Ratio = RatioFor("haiku"), Efforts = BudgetOnlyEfforts, AutoMode = false },
            };
        }

        /// <summary>
        /// The page's <c>effortsByModel</c> map: picker id to the slider entries for that model.
        /// </summary>
        public static Dictionary<string, object[]> EffortsByModel(IEnumerable<CliModelInfo> models)
        {
            var map = new Dictionary<string, object[]>(StringComparer.Ordinal);
            if (models == null) return map;
            foreach (var m in models)
            {
                if (m == null || string.IsNullOrEmpty(m.Id) || map.ContainsKey(m.Id)) continue;
                var ids = m.Efforts != null && m.Efforts.Length > 0 ? m.Efforts : BudgetOnlyEfforts;
                var rows = new List<object>(ids.Length);
                foreach (var id in ids)
                {
                    string name;
                    if (!EffortNames.TryGetValue(id, out name)) continue;
                    rows.Add(new { id = id, name = name });
                }
                map[m.Id] = rows.ToArray();
            }
            return map;
        }

        /// <summary>
        /// Returns true when <paramref name="line"/> is a <c>control_response</c> (the CLI's reply
        /// to a control request), and fills <paramref name="into"/> with the picker rows built from
        /// its <c>models</c> array. A reply carrying no usable model list (an error reply, or a CLI
        /// that predates the field) still returns true with <paramref name="into"/> left empty, so
        /// the caller can stop waiting for it.
        /// </summary>
        internal static bool TryParseControlResponse(string line, List<CliModelInfo> into)
        {
            line = line == null ? null : line.Trim();
            if (string.IsNullOrEmpty(line) || line[0] != '{') return false;
            try
            {
                using (var doc = JsonDocument.Parse(line))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return false;
                    JsonElement t;
                    if (!root.TryGetProperty("type", out t) || t.ValueKind != JsonValueKind.String || t.GetString() != "control_response") return false;
                    JsonElement outer, inner, models;
                    if (root.TryGetProperty("response", out outer) && outer.ValueKind == JsonValueKind.Object &&
                        outer.TryGetProperty("response", out inner) && inner.ValueKind == JsonValueKind.Object &&
                        inner.TryGetProperty("models", out models) && models.ValueKind == JsonValueKind.Array)
                    {
                        into.AddRange(FromCli(models));
                    }
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Builds picker rows from the CLI's <c>models</c> array, skipping rows the picker cannot offer.</summary>
        internal static List<CliModelInfo> FromCli(JsonElement models)
        {
            var list = new List<CliModelInfo>();
            if (models.ValueKind != JsonValueKind.Array) return list;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in models.EnumerateArray())
            {
                var m = FromCliRow(row);
                if (m != null && seen.Add(m.Id)) list.Add(m);
            }
            return list;
        }

        /// <summary>
        /// One CLI row to one picker row, or null when it cannot be offered: no id, an id that would
        /// not survive <see cref="InputValidation.SanitizeModel"/> (it becomes a <c>--model</c>
        /// argument), or a row the CLI marks disabled.
        /// </summary>
        internal static CliModelInfo FromCliRow(JsonElement row)
        {
            if (row.ValueKind != JsonValueKind.Object) return null;
            if (Bool(row, "disabled")) return null;

            var id = Str(row, "value");
            // The CLI spells the default entry with a null value; the picker calls it "default".
            if (string.IsNullOrEmpty(id)) id = "default";
            if (InputValidation.SanitizeModel(id, null) == null) return null;

            var name = Str(row, "displayName");
            if (string.IsNullOrEmpty(name)) name = id;

            string label = "", desc = Str(row, "description") ?? "";
            int sep = desc.IndexOf(DescriptionSeparator, StringComparison.Ordinal);
            if (sep > 0)
            {
                label = desc.Substring(0, sep).Trim();
                desc = desc.Substring(sep + DescriptionSeparator.Length).Trim();
            }

            var wire = Str(row, "resolvedModel");
            var family = FamilyOf(string.IsNullOrEmpty(wire) ? id : wire);

            return new CliModelInfo
            {
                Id = id,
                Name = name,
                Label = label,
                Desc = desc,
                Wire = string.IsNullOrEmpty(wire) ? null : wire,
                Ratio = RatioFor(family),
                Efforts = EffortsFor(row, family),
                AutoMode = Bool(row, "supportsAutoMode"),
            };
        }

        /// <summary>
        /// The slider range for a CLI row. The CLI's <c>supportedEffortLevels</c> drive it (its
        /// "xhigh" is the slider's "extrahigh"), with Off in front. Ultracode - the extension's own
        /// top step, max thinking plus multi-agent workflows - is offered on the families that carry
        /// it today (not Sonnet or Haiku). A row without effort levels gets thinking budgets only.
        /// </summary>
        internal static string[] EffortsFor(JsonElement row, string family)
        {
            JsonElement levels;
            if (!row.TryGetProperty("supportedEffortLevels", out levels) || levels.ValueKind != JsonValueKind.Array)
                return BudgetOnlyEfforts;

            var have = new HashSet<string>(StringComparer.Ordinal) { "none" };
            foreach (var l in levels.EnumerateArray())
            {
                if (l.ValueKind != JsonValueKind.String) continue;
                var v = l.GetString();
                if (v == "xhigh") v = "extrahigh";
                if (!string.IsNullOrEmpty(v)) have.Add(v);
            }
            if (have.Count == 1) return BudgetOnlyEfforts;
            if (have.Contains("max") && family != "sonnet" && family != "haiku") have.Add("ultracode");

            var ordered = new List<string>(have.Count);
            foreach (var id in EffortOrder) if (have.Contains(id)) ordered.Add(id);
            return ordered.ToArray();
        }

        /// <summary>
        /// Family name of a model id: "claude-opus-5[1m]" and "opus[1m]" are both "opus";
        /// "claude-haiku-4-5-20251001" is "haiku". Lower-case; empty for an empty id.
        /// </summary>
        internal static string FamilyOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            var s = id.Trim().ToLowerInvariant();
            int b = s.IndexOf('[');
            if (b >= 0) s = s.Substring(0, b);
            if (s.StartsWith("claude-", StringComparison.Ordinal)) s = s.Substring(7);
            int dash = s.IndexOf('-');
            return dash > 0 ? s.Substring(0, dash) : s;
        }

        /// <summary>
        /// Per-token price relative to Haiku, by family. Input and output prices scale by the same
        /// factor, so one number covers both: Haiku $1/$5, Sonnet $2/$10, Opus $5/$25, Fable $10/$50
        /// per MTok. Null (no badge) for a family this table does not know.
        /// </summary>
        internal static double? RatioFor(string family)
        {
            switch (family)
            {
                case "haiku": return 1.0;
                case "sonnet": return 2.0;
                case "opus": return 5.0;
                case "fable":
                case "mythos": return 10.0;
                default: return null;
            }
        }

        private static string Str(JsonElement o, string name)
        {
            JsonElement v;
            return o.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static bool Bool(JsonElement o, string name)
        {
            JsonElement v;
            return o.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.True;
        }
    }
}

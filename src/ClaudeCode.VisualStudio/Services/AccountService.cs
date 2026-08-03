using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClaudeCode.VisualStudio.Services
{
    public sealed class AccountData
    {
        public string AuthMethod = "Claude AI";
        public string Email;
        public string Organization;
        public string Plan;
        public List<UsageLimitData> Limits = new List<UsageLimitData>();
        public ExtraUsageData ExtraUsage;
        public string ManageUrl = "https://claude.ai";
        public string Error;
    }

    public sealed class UsageLimitData
    {
        public string Name;       // "Session (5hr)", "Weekly (7 day)", "Weekly Fable"
        public double Percent;    // 0–100
        public string ResetsIn;   // "2h", "5d"
        public string Severity;   // "normal" | "warning" | "critical" (as reported by the API)
    }

    /// <summary>Pay-as-you-go credits that kick in past the plan limits ("extra usage").</summary>
    public sealed class ExtraUsageData
    {
        public bool Enabled;
        public double UsedCredits;   // minor currency units (see DecimalPlaces)
        public double MonthlyLimit;  // minor currency units
        public double Utilization;   // 0–100
        public string Currency = "USD";
        public int DecimalPlaces = 2;
    }

    public static class AccountService
    {
        private static readonly HttpClient _http;

        static AccountService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // The "claude-code/" User-Agent prefix is REQUIRED by the OAuth usage endpoint;
            // without it the request lands in an aggressively rate-limited bucket (persistent 429s).
            _http.DefaultRequestHeaders.Add("User-Agent", "claude-code/1.0.0");
            _http.DefaultRequestHeaders.Add("anthropic-beta", "oauth-2025-04-20");
            _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        }

        public static async Task<AccountData> FetchAsync()
        {
            var result = new AccountData();
            try
            {
                var token = ReadToken(out var authMethod);
                if (token == null)
                {
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    result.Error = "Not logged in (credentials not found — checked " + home + @"\.claude\.credentials.json and AppData\Claude)";
                    return result;
                }
                result.AuthMethod = authMethod;

                // Account profile (email + organization). OAuth token works against
                // api.anthropic.com — NOT claude.ai/api, which sits behind a Cloudflare
                // bot challenge and returns 403 "Just a moment..." for programmatic calls.
                using (var req = AuthRequest(HttpMethod.Get, "https://api.anthropic.com/api/oauth/profile", token))
                {
                    var resp = await _http.SendAsync(req);
                    var body = await resp.Content.ReadAsStringAsync();
                    Log.WriteVerbose("AccountService profile: " + (int)resp.StatusCode + " body=" + Trunc(body));
                    if (resp.IsSuccessStatusCode)
                        ParseAccountInfo(result, body);
                }

                // Usage / rate-limit windows (5-hour session, 7-day weekly, per-model weekly).
                using (var req = AuthRequest(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage", token))
                {
                    var resp = await _http.SendAsync(req);
                    var body = await resp.Content.ReadAsStringAsync();
                    Log.WriteVerbose("AccountService usage: " + (int)resp.StatusCode + " body=" + Trunc(body));
                    if (resp.IsSuccessStatusCode)
                        ParseUsageOAuth(result, body);
                }
            }
            catch (Exception ex)
            {
                Log.Write("AccountService.FetchAsync exception: " + ex.Message);
                result.Error = ex.Message;
            }
            return result;
        }

        private static HttpRequestMessage AuthRequest(HttpMethod method, string url, string token)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return req;
        }

        private static string[] CredentialCandidates()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return new[]
            {
                Path.Combine(home, ".claude", ".credentials.json"),
                Path.Combine(home, ".claude", "credentials.json"),
                Path.Combine(appData, "Claude", ".credentials.json"),
                Path.Combine(appData, "Claude", "credentials.json"),
                Path.Combine(appData, "claude", ".credentials.json"),
                Path.Combine(appData, "claude", "credentials.json"),
            };
        }

        /// <summary>
        /// True when a stored CLI credential/token exists (no network call). Drives the
        /// first-run "log in" guidance — distinct from whether that token is still valid.
        /// </summary>
        public static bool HasStoredToken()
        {
            try { return !string.IsNullOrEmpty(ReadToken(out _)); }
            catch { return false; }
        }

        private static string ReadToken(out string authMethod)
        {
            authMethod = "Claude AI";
            try
            {
                string credPath = null;
                foreach (var candidate in CredentialCandidates())
                {
                    Log.WriteVerbose("AccountService: trying " + candidate + " exists=" + File.Exists(candidate));
                    if (File.Exists(candidate)) { credPath = candidate; break; }
                }

                if (credPath == null)
                {
                    Log.Write("AccountService: no credentials found in any candidate path");
                    return null;
                }

                var json = File.ReadAllText(credPath);
                Log.WriteVerbose("AccountService: read credentials from " + credPath + " len=" + json.Length);
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;

                    // Claude Code stores credentials under "claudeAiOauth" or legacy "claudeAiOauthToken"
                    JsonElement oauth;
                    if (!root.TryGetProperty("claudeAiOauth", out oauth))
                        root.TryGetProperty("claudeAiOauthToken", out oauth);
                    if (oauth.ValueKind == JsonValueKind.Object)
                    {
                        if (oauth.TryGetProperty("accessToken", out var tok) && tok.ValueKind == JsonValueKind.String)
                        { authMethod = "Claude AI"; return tok.GetString(); }
                    }
                    if (root.TryGetProperty("apiKey", out var key) && key.ValueKind == JsonValueKind.String)
                    { authMethod = "API Key"; return key.GetString(); }
                    if (root.TryGetProperty("accessToken", out var flat) && flat.ValueKind == JsonValueKind.String)
                        return flat.GetString();
                }
                Log.Write("AccountService: no token found in credentials");
                return null;
            }
            catch (Exception ex)
            {
                Log.Write("AccountService.ReadToken: " + ex.Message);
                return null;
            }
        }

        internal static void ParseAccountInfo(AccountData data, string json)
        {
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    ParseAccountFields(data, root);
                    if (root.TryGetProperty("account", out var acct)) ParseAccountFields(data, acct);
                    if (root.TryGetProperty("user", out var user)) ParseAccountFields(data, user);
                }
            }
            catch (Exception ex) { Log.Write("AccountService.ParseAccountInfo: " + ex.Message); }
        }

        private static void ParseAccountFields(AccountData data, JsonElement el)
        {
            Str(el, "email", v => data.Email = v);
            Str(el, "email_address", v => data.Email = v);
            Str(el, "org_name", v => data.Organization = v);

            if (el.TryGetProperty("organization", out var org))
            {
                if (org.ValueKind == JsonValueKind.String) data.Organization = org.GetString();
                else Str(org, "name", v => data.Organization = v);
            }
            if (el.TryGetProperty("plan", out var plan))
            {
                if (plan.ValueKind == JsonValueKind.String)
                    data.Plan = FormatPlanName(plan.GetString());
                else
                {
                    Str(plan, "display_name", v => data.Plan = v);
                    Str(plan, "name", v => { if (data.Plan == null) data.Plan = FormatPlanName(v); });
                }
            }
            Str(el, "plan_name", v => data.Plan = FormatPlanName(v));
        }

        // Parses the api.anthropic.com/api/oauth/usage response. Newer responses carry a
        // unified "limits" array — the only place per-model windows (e.g. "Weekly Fable")
        // appear now that the legacy "seven_day_opus"/"seven_day_sonnet" keys are null:
        //   { "limits": [ { "kind": "session", "percent": 17, "severity": "normal",
        //                   "resets_at": "...", "scope": null },
        //                 { "kind": "weekly_scoped", "percent": 2,
        //                   "scope": { "model": { "display_name": "Fable" } } } ],
        //     "extra_usage": { "is_enabled": false, "monthly_limit": 4000, ... },
        //     "five_hour": {...}, "seven_day": {...} }   // legacy fallback keys
        internal static void ParseUsageOAuth(AccountData data, string json)
        {
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
                        foreach (var l in limits.EnumerateArray())
                            AddLimit(data, l);

                    if (data.Limits.Count == 0)
                    {
                        AddWindow(data, root, "five_hour", "Session (5hr)");
                        AddWindow(data, root, "seven_day", "Weekly (7 day)");
                        AddWindow(data, root, "seven_day_opus", "Weekly Opus");
                        AddWindow(data, root, "seven_day_sonnet", "Weekly Sonnet");
                    }

                    if (root.TryGetProperty("extra_usage", out var extra) && extra.ValueKind == JsonValueKind.Object)
                        data.ExtraUsage = ParseExtraUsage(extra);
                }
            }
            catch (Exception ex) { Log.Write("AccountService.ParseUsageOAuth: " + ex.Message); }
        }

        private static void AddLimit(AccountData data, JsonElement l)
        {
            if (l.ValueKind != JsonValueKind.Object) return;
            string kind = null, scopeName = null;
            Str(l, "kind", v => kind = v);
            if (l.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object &&
                scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
                Str(model, "display_name", v => scopeName = v);

            var limit = new UsageLimitData { Name = LimitLabel(kind, scopeName) };
            if (l.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var pct))
                limit.Percent = pct;
            if (l.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String)
                limit.ResetsIn = FormatReset(r.GetString());
            Str(l, "severity", v => limit.Severity = v);
            data.Limits.Add(limit);
        }

        internal static string LimitLabel(string kind, string scopeName)
        {
            if (!string.IsNullOrEmpty(scopeName)) return "Weekly " + scopeName;
            switch (kind)
            {
                case "session": return "Session (5hr)";
                case "weekly_all": return "Weekly (7 day)";
                case "weekly_scoped": return "Weekly (model)";
                case null: case "": return "Usage";
                default:
                    var ti = System.Globalization.CultureInfo.InvariantCulture.TextInfo;
                    return ti.ToTitleCase(kind.Replace("_", " "));
            }
        }

        private static ExtraUsageData ParseExtraUsage(JsonElement el)
        {
            var x = new ExtraUsageData();
            if (el.TryGetProperty("is_enabled", out var e) &&
                (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False))
                x.Enabled = e.GetBoolean();
            if (el.TryGetProperty("monthly_limit", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetDouble(out var ml))
                x.MonthlyLimit = ml;
            if (el.TryGetProperty("used_credits", out var u) && u.ValueKind == JsonValueKind.Number && u.TryGetDouble(out var uc))
                x.UsedCredits = uc;
            if (el.TryGetProperty("utilization", out var ut) && ut.ValueKind == JsonValueKind.Number && ut.TryGetDouble(out var utv))
                x.Utilization = utv;
            Str(el, "currency", v => x.Currency = v);
            if (el.TryGetProperty("decimal_places", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var dp))
                x.DecimalPlaces = dp;
            return x;
        }

        private static void AddWindow(AccountData data, JsonElement root, string key, string label)
        {
            if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object)
                return;
            var limit = new UsageLimitData { Name = label };
            if (w.TryGetProperty("utilization", out var u) && u.TryGetDouble(out var pct))
                limit.Percent = pct;
            if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String)
                limit.ResetsIn = FormatReset(r.GetString());
            data.Limits.Add(limit);
        }

        // ISO-8601 reset timestamp -> short human delta ("4h", "5d", "12m").
        internal static string FormatReset(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return null;
            if (!DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var when))
                return null;
            var delta = when - DateTimeOffset.UtcNow;
            if (delta <= TimeSpan.Zero) return "now";
            if (delta.TotalDays >= 1) return (int)Math.Round(delta.TotalDays) + "d";
            if (delta.TotalHours >= 1) return (int)Math.Round(delta.TotalHours) + "h";
            return Math.Max(1, (int)Math.Round(delta.TotalMinutes)) + "m";
        }

        internal static string FormatPlanName(string raw)
        {
            if (raw == null) return null;
            switch (raw.ToLowerInvariant())
            {
                case "claude_max": return "Claude Max";
                case "claude_pro": return "Claude Pro";
                case "claude_team": return "Claude Team";
                case "free": return "Free";
                default:
                    var ti = System.Globalization.CultureInfo.InvariantCulture.TextInfo;
                    return ti.ToTitleCase(raw.Replace("_", " ").Replace("-", " ").ToLower());
            }
        }

        private static void Str(JsonElement el, string key, Action<string> set)
        {
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                set(v.GetString());
        }

        private static string Trunc(string s) => s == null ? "" : s.Length > 300 ? s.Substring(0, 300) + "…" : s;
    }
}

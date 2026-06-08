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
        public string ManageUrl = "https://claude.ai";
        public string Error;
    }

    public sealed class UsageLimitData
    {
        public string Name;       // "Session (5hr)", "Weekly (7 day)"
        public double Percent;    // 0–100
        public string ResetsIn;   // "2h", "5d"
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

        private static void ParseAccountInfo(AccountData data, string json)
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

        // Parses the api.anthropic.com/api/oauth/usage response:
        //   { "five_hour": { "utilization": 2, "resets_at": "..." },
        //     "seven_day": {...}, "seven_day_opus": null, "seven_day_sonnet": {...} }
        private static void ParseUsageOAuth(AccountData data, string json)
        {
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    AddWindow(data, root, "five_hour", "Session (5hr)");
                    AddWindow(data, root, "seven_day", "Weekly (7 day)");
                    AddWindow(data, root, "seven_day_opus", "Weekly Opus");
                    AddWindow(data, root, "seven_day_sonnet", "Weekly Sonnet");
                }
            }
            catch (Exception ex) { Log.Write("AccountService.ParseUsageOAuth: " + ex.Message); }
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
        private static string FormatReset(string iso)
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

        private static string FormatPlanName(string raw)
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

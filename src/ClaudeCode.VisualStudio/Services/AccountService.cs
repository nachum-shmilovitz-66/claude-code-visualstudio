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
            _http.DefaultRequestHeaders.Add("User-Agent", "ClaudeCodeVS/0.1");
            _http.DefaultRequestHeaders.Add("anthropic-client-platform", "vscode");
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

                // Clone headers so we don't race between calls
                using (var req = AuthRequest(HttpMethod.Get, "https://claude.ai/api/account_info", token))
                {
                    var resp = await _http.SendAsync(req);
                    var body = await resp.Content.ReadAsStringAsync();
                    Log.Write("AccountService account_info: " + (int)resp.StatusCode + " body=" + Trunc(body));
                    if (resp.IsSuccessStatusCode)
                        ParseAccountInfo(result, body);
                }

                // Try to get usage/rate-limit data from a few possible endpoints
                string[] usagePaths = {
                    "https://claude.ai/api/usage_stats",
                    "https://claude.ai/api/rate_limits",
                    "https://claude.ai/api/usage",
                };
                foreach (var path in usagePaths)
                {
                    using (var req = AuthRequest(HttpMethod.Get, path, token))
                    {
                        var resp = await _http.SendAsync(req);
                        var body = await resp.Content.ReadAsStringAsync();
                        Log.Write("AccountService " + path + ": " + (int)resp.StatusCode + " body=" + Trunc(body));
                        if (resp.IsSuccessStatusCode)
                        {
                            ParseUsage(result, body);
                            if (result.Limits.Count > 0) break;
                        }
                    }
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
                    Log.Write("AccountService: trying " + candidate + " exists=" + File.Exists(candidate));
                    if (File.Exists(candidate)) { credPath = candidate; break; }
                }

                if (credPath == null)
                {
                    Log.Write("AccountService: no credentials found in any candidate path");
                    return null;
                }

                var json = File.ReadAllText(credPath);
                Log.Write("AccountService: read credentials from " + credPath + " len=" + json.Length);
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;

                    if (root.TryGetProperty("claudeAiOauthToken", out var oauth))
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

        private static void ParseUsage(AccountData data, string json)
        {
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;

                    // Try array at root
                    if (root.ValueKind == JsonValueKind.Array) { ParseLimitArray(data, root); return; }

                    // Try known keys
                    foreach (var key in new[] { "limits", "usage", "rate_limits", "data" })
                    {
                        if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                        { ParseLimitArray(data, arr); return; }
                    }
                }
            }
            catch (Exception ex) { Log.Write("AccountService.ParseUsage: " + ex.Message); }
        }

        private static void ParseLimitArray(AccountData data, JsonElement arr)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var limit = new UsageLimitData();
                Str(item, "name", v => limit.Name = v);
                Str(item, "display_name", v => limit.Name = v);
                Num(item, "used_percent", v => limit.Percent = v);
                Num(item, "percent", v => limit.Percent = v);
                Str(item, "resets_in", v => limit.ResetsIn = v);
                Str(item, "resets_in_human", v => limit.ResetsIn = v);
                if (!string.IsNullOrEmpty(limit.Name)) data.Limits.Add(limit);
            }
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

        private static void Num(JsonElement el, string key, Action<double> set)
        {
            if (el.TryGetProperty(key, out var v) && v.TryGetDouble(out var d))
                set(d);
        }

        private static string Trunc(string s) => s == null ? "" : s.Length > 300 ? s.Substring(0, 300) + "…" : s;
    }
}

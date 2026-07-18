// GitHubSync — auto-pull Carbon/Oxide plugin files from GitHub as soon as you push an update.
//
//
// Commands (server owner / AuthLevel 2 only):
//   ghsync list
//   ghsync add <owner> <repo> <branch> <path/in/repo/File.cs> [targetFileName.cs]
//   ghsync remove <owner> <repo>
//   ghsync branch <owner> <repo> <newBranch>
//   ghsync sync [<owner> <repo>]
//   ghsync webhookinfo
// (same subcommands work via the server console as: githubsync <subcommand> ...)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;

namespace Oxide.Plugins
{
    [Info("GitHubSync", "tobi-polar", "1.0.0")]
    [Description("Subscribes to GitHub repos and auto-downloads plugin files to this server in near real time via webhook + polling.")]
    public class GitHubSync : RustPlugin
    {
        #region Config / Data models

        private class Subscription
        {
            public string Owner;
            public string Repo;
            public string Branch = "main";
            public string FilePath;       // path to the .cs file inside the repo, e.g. "MyPlugin.cs" or "src/MyPlugin.cs"
            public string TargetFileName; // filename written into the plugins folder, e.g. "MyPlugin.cs"

            [JsonIgnore]
            public string Key => $"{Owner}/{Repo}@{Branch}:{FilePath}";
        }

        private class WebhookSettings
        {
            public bool Enabled = false;
            public int Port = 8093;
            public string Path = "/github-webhook";
            // Use "*" to listen on all network interfaces (typical for a VPS).
            public string BindAddress = "*";
            public string Secret = "CHANGE-ME-generate-a-random-string-and-use-the-same-value-in-GitHub";
        }

        private class PollingSettings
        {
            public bool Enabled = true;
            public int IntervalSeconds = 30;
        }

        private class PluginConfig
        {
            [JsonProperty("GitHub Personal Access Token")]
            public string GitHubToken = "";
            public WebhookSettings Webhook = new WebhookSettings();
            public PollingSettings Polling = new PollingSettings();
            public List<Subscription> Subscriptions = new List<Subscription>();
        }

        private class StoredState
        {
            public Dictionary<string, string> LastKnownSha = new Dictionary<string, string>();
            public Dictionary<string, string> LastSyncTimeUtc = new Dictionary<string, string>();
        }

        private PluginConfig config;
        private StoredState state;

        // The webhook listener runs on its own background thread and reads config.Subscriptions
        // concurrently with the main thread (which can add/remove/edit subscriptions via commands).
        // This lock keeps those two paths from tripping over each other.
        private readonly object configLock = new object();

        protected override void LoadDefaultConfig() => config = new PluginConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<PluginConfig>();
                if (config == null) throw new Exception("config deserialized to null");
            }
            catch (Exception ex)
            {
                PrintError($"Config file is invalid, generating a fresh default config. ({ex.Message})");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config);

        private void LoadState()
        {
            try
            {
                if (Interface.Oxide.DataFileSystem.ExistsDatafile(Name))
                    state = Interface.Oxide.DataFileSystem.GetFile(Name).ReadObject<StoredState>();
            }
            catch (Exception ex)
            {
                PrintError("Failed to load stored sync state, starting fresh: " + ex.Message);
            }
            if (state == null) state = new StoredState();
        }

        private void SaveState()
        {
            try { Interface.Oxide.DataFileSystem.GetFile(Name).WriteObject(state); }
            catch (Exception ex) { PrintError("Failed to save sync state: " + ex.Message); }
        }

        #endregion

        #region Lifecycle

        private HttpListener listener;
        private volatile bool shuttingDown;
        private Timer pollTimer;

        private void Init()
        {
            permission.RegisterPermission("githubsync.admin", this);
        }

        private void OnServerInitialized()
        {
            LoadState();

            if (config.Subscriptions.Count > 0 && string.IsNullOrEmpty(config.GitHubToken))
            {
                PrintWarning("No GitHub Personal Access Token configured. Public repos may still work " +
                              "(subject to low rate limits) but private repos will fail with 404. " +
                              $"Set it in carbon/configs/{Name}.json or oxide/config/{Name}.json.");
            }

            if (config.Webhook.Enabled)
            {
                if (string.IsNullOrEmpty(config.Webhook.Secret) || config.Webhook.Secret.StartsWith("CHANGE-ME"))
                {
                    PrintError("Webhook.Enabled is true but Webhook.Secret is still empty/default. Refusing to start " +
                               "the listener, since an unsigned endpoint would let anyone who finds the URL trigger a " +
                               "sync. Set a real random secret (e.g. `openssl rand -hex 32`) in the config, use the " +
                               "same value in GitHub's webhook settings, then reload the plugin. Polling will keep " +
                               "running in the meantime.");
                }
                else
                {
                    StartWebhookServer();
                }
            }

            if (config.Polling.Enabled)
            {
                int interval = Math.Max(5, config.Polling.IntervalSeconds);
                pollTimer = timer.Every(interval, PollAll);
            }

            // Kick off an initial sync shortly after startup so the server always
            // boots with the latest committed version of every subscribed plugin.
            timer.Once(2f, PollAll);
        }

        private void Unload()
        {
            shuttingDown = true;
            StopWebhookServer();
            pollTimer?.Destroy();
        }

        #endregion

        #region GitHub API

        private Dictionary<string, string> BuildHeaders()
        {
            return new Dictionary<string, string>
            {
                ["Authorization"] = "token " + config.GitHubToken,
                ["User-Agent"] = "IcebergGames-GitHubSync-CarbonPlugin",
                ["Accept"] = "application/vnd.github+json",
                ["X-GitHub-Api-Version"] = "2022-11-28"
            };
        }

        private void FetchLatestSha(Subscription sub, Action<string> onResult, Action<string> onError)
        {
            string url = $"https://api.github.com/repos/{sub.Owner}/{sub.Repo}/branches/{Uri.EscapeDataString(sub.Branch)}";
            webrequest.Enqueue(url, null, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    onError($"HTTP {code} fetching branch info for {sub.Owner}/{sub.Repo}@{sub.Branch}");
                    return;
                }
                try
                {
                    var obj = JObject.Parse(response);
                    string sha = obj["commit"]?["sha"]?.ToString();
                    if (string.IsNullOrEmpty(sha)) { onError("Response had no commit sha"); return; }
                    onResult(sha);
                }
                catch (Exception ex) { onError("Parse error: " + ex.Message); }
            }, this, RequestMethod.GET, BuildHeaders());
        }

        private void FetchFileContent(Subscription sub, Action<string> onResult, Action<string> onError)
        {
            string encodedPath = string.Join("/", sub.FilePath.Split('/').Select(Uri.EscapeDataString));
            string url = $"https://api.github.com/repos/{sub.Owner}/{sub.Repo}/contents/{encodedPath}?ref={Uri.EscapeDataString(sub.Branch)}";
            webrequest.Enqueue(url, null, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    onError($"HTTP {code} fetching file '{sub.FilePath}'");
                    return;
                }
                try
                {
                    var obj = JObject.Parse(response);
                    string encoding = obj["encoding"]?.ToString();
                    string content = obj["content"]?.ToString();
                    if (encoding == "base64" && !string.IsNullOrEmpty(content))
                    {
                        string clean = content.Replace("\n", "").Replace("\r", "");
                        byte[] bytes = Convert.FromBase64String(clean);
                        onResult(Encoding.UTF8.GetString(bytes));
                        return;
                    }

                    // Files over 1MB don't include inline content — fall back to download_url.
                    string downloadUrl = obj["download_url"]?.ToString();
                    if (!string.IsNullOrEmpty(downloadUrl))
                    {
                        webrequest.Enqueue(downloadUrl, null, (code2, body2) =>
                        {
                            if (code2 != 200 || body2 == null) { onError($"HTTP {code2} fetching raw file content"); return; }
                            onResult(body2);
                        }, this, RequestMethod.GET, BuildHeaders());
                        return;
                    }

                    onError("File response had neither inline content nor a download_url (is the path a directory?)");
                }
                catch (Exception ex) { onError("Parse error: " + ex.Message); }
            }, this, RequestMethod.GET, BuildHeaders());
        }

        #endregion

        #region Sync logic

        private void SyncSubscription(Subscription sub, string trigger, bool force = false)
        {
            FetchLatestSha(sub, sha =>
            {
                state.LastKnownSha.TryGetValue(sub.Key, out var known);
                if (!force && sha == known) return; // no change, nothing to do

                FetchFileContent(sub, content =>
                {
                    NextTick(() => ApplySync(sub, sha, content, trigger));
                }, err => PrintError($"[{trigger}] Failed to fetch file for {sub.Owner}/{sub.Repo}: {err}"));
            }, err => PrintError($"[{trigger}] Failed to check latest commit for {sub.Owner}/{sub.Repo}@{sub.Branch}: {err}"));
        }

        private void ApplySync(Subscription sub, string sha, string content, string trigger)
        {
            try
            {
                string targetPath = Path.Combine(Interface.Oxide.PluginDirectory, sub.TargetFileName);
                string existing = File.Exists(targetPath) ? File.ReadAllText(targetPath) : null;

                if (existing != content)
                {
                    File.WriteAllText(targetPath, content);
                    Puts($"[{trigger}] Synced {sub.Owner}/{sub.Repo}@{sub.Branch} -> {sub.TargetFileName} " +
                         $"(commit {sha.Substring(0, Math.Min(7, sha.Length))}). Carbon/Oxide will hot-reload it now.");
                }
                else
                {
                    Puts($"[{trigger}] {sub.Owner}/{sub.Repo}@{sub.Branch}: new commit {sha.Substring(0, Math.Min(7, sha.Length))} " +
                         "but file content is identical, skipping write/reload.");
                }

                state.LastKnownSha[sub.Key] = sha;
                state.LastSyncTimeUtc[sub.Key] = DateTime.UtcNow.ToString("o");
                SaveState();
            }
            catch (Exception ex)
            {
                PrintError($"[{trigger}] Failed writing plugin file for {sub.Owner}/{sub.Repo}: {ex.Message}");
            }
        }

        private void PollAll()
        {
            List<Subscription> subs;
            lock (configLock) { subs = config.Subscriptions.ToList(); }
            foreach (var sub in subs)
                SyncSubscription(sub, "poll");
        }

        #endregion

        #region Webhook server

        private void StartWebhookServer()
        {
            try
            {
                listener = new HttpListener();
                string bind = string.IsNullOrEmpty(config.Webhook.BindAddress) || config.Webhook.BindAddress == "*"
                    ? "+" : config.Webhook.BindAddress;
                string path = config.Webhook.Path.Trim('/');
                listener.Prefixes.Add($"http://{bind}:{config.Webhook.Port}/{path}/");
                listener.Start();
                Puts($"GitHub webhook listener started on port {config.Webhook.Port}, path /{path}/. " +
                     "Make sure that port is open in your firewall / hosting provider's security group.");
                BeginAccept();
            }
            catch (Exception ex)
            {
                PrintError($"Failed to start webhook listener ({ex.Message}). Falling back to polling only. " +
                            "Check that the port isn't already in use and that the process has permission to bind it.");
                listener = null;
            }
        }

        private void StopWebhookServer()
        {
            try { listener?.Stop(); listener?.Close(); } catch { /* ignore */ }
            listener = null;
        }

        private void BeginAccept()
        {
            if (listener == null || !listener.IsListening) return;
            try { listener.BeginGetContext(OnContext, null); }
            catch (Exception ex) { if (!shuttingDown) PrintError("BeginGetContext failed: " + ex.Message); }
        }

        private void OnContext(IAsyncResult ar)
        {
            HttpListenerContext ctx;
            try
            {
                if (listener == null || !listener.IsListening) return;
                ctx = listener.EndGetContext(ar);
            }
            catch (Exception)
            {
                return; // listener was stopped/disposed
            }
            finally
            {
                BeginAccept();
            }

            try { HandleWebhookRequest(ctx); }
            catch (Exception ex) { PrintError("Error handling webhook request: " + ex.Message); }
        }

        // Runs on the HttpListener's background thread.
        private void HandleWebhookRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            try
            {
                if (req.HttpMethod != "POST") { res.StatusCode = 405; return; }

                // A push event payload is at most a few hundred KB even for large commits;
                // cap well above that to reject obviously-abusive bodies before buffering them.
                const long maxBodyBytes = 10 * 1024 * 1024; // 10 MB
                if (req.ContentLength64 > maxBodyBytes) { res.StatusCode = 413; return; }

                string body;
                using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                    body = reader.ReadToEnd();

                if (Encoding.UTF8.GetByteCount(body) > maxBodyBytes) { res.StatusCode = 413; return; }

                byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

                // The listener only ever starts if a real (non-default) secret is configured
                // (see OnServerInitialized), so signature verification is always required here.
                string webhookSecret = config.Webhook.Secret;
                string sigHeader = req.Headers["X-Hub-Signature-256"];
                if (string.IsNullOrEmpty(sigHeader) || !VerifySignature(bodyBytes, sigHeader, webhookSecret))
                {
                    res.StatusCode = 401;
                    return;
                }

                string eventType = req.Headers["X-GitHub-Event"];
                if (eventType != "push")
                {
                    res.StatusCode = 200; // acknowledge pings/other events, ignore them
                    return;
                }

                JObject payload;
                try { payload = JObject.Parse(body); }
                catch { res.StatusCode = 400; return; }

                string refName = payload["ref"]?.ToString();          // e.g. refs/heads/main
                string fullName = payload["repository"]?["full_name"]?.ToString(); // owner/repo
                res.StatusCode = 200;
                if (string.IsNullOrEmpty(refName) || string.IsNullOrEmpty(fullName)) return;

                string branch = refName.StartsWith("refs/heads/") ? refName.Substring("refs/heads/".Length) : refName;

                List<Subscription> matches;
                lock (configLock)
                {
                    matches = config.Subscriptions.Where(s =>
                        string.Equals($"{s.Owner}/{s.Repo}", fullName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(s.Branch, branch, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                foreach (var sub in matches)
                {
                    var captured = sub;
                    NextTick(() => SyncSubscription(captured, "webhook"));
                }
            }
            catch (Exception ex)
            {
                try { res.StatusCode = 500; } catch { /* ignore */ }
                PrintError("Webhook handling error: " + ex.Message);
            }
            finally
            {
                try { res.OutputStream.Close(); } catch { /* ignore */ }
            }
        }

        private static bool VerifySignature(byte[] body, string header, string secret)
        {
            if (!header.StartsWith("sha256=")) return false;
            string theirHex = header.Substring("sha256=".Length);
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
            {
                byte[] hash = hmac.ComputeHash(body);
                string ourHex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                return FixedTimeEquals(ourHex, theirHex.ToLowerInvariant());
            }
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        #endregion

        #region Commands

        [ChatCommand("ghsync"), AuthLevel(2)]
        private void CmdGhSyncChat(BasePlayer player, string command, string[] args)
        {
            player.ChatMessage(HandleCommand(args));
        }

        [ConsoleCommand("githubsync"), AuthLevel(2)]
        private void CmdGhSyncConsole(ConsoleSystem.Arg arg)
        {
            // Carbon's ConsoleSystem.Arg.Args is a StringView[] (not string[]) — convert explicitly.
            string[] args = arg.Args != null ? arg.Args.Select(v => v.ToString()).ToArray() : new string[0];
            arg.ReplyWith(HandleCommand(args));
        }

        private string HandleCommand(string[] args)
        {
            if (args == null || args.Length == 0) return HelpText();
            string sub = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();
            switch (sub)
            {
                case "list": return CmdList();
                case "add": return CmdAdd(rest);
                case "remove": return CmdRemove(rest);
                case "branch": return CmdBranch(rest);
                case "sync": return CmdSync(rest);
                case "webhookinfo": return CmdWebhookInfo();
                default: return HelpText();
            }
        }

        private string HelpText() =>
            "GitHubSync commands:\n" +
            "  ghsync add <owner> <repo> <branch> <path/in/repo/File.cs> [targetFileName.cs]\n" +
            "  ghsync remove <owner> <repo>\n" +
            "  ghsync branch <owner> <repo> <newBranch>\n" +
            "  ghsync sync [<owner> <repo>]\n" +
            "  ghsync list\n" +
            "  ghsync webhookinfo\n" +
            "(console: 'githubsync <subcommand> ...')";

        private string CmdAdd(string[] args)
        {
            if (args.Length < 4)
                return "Usage: add <owner> <repo> <branch> <path/in/repo/File.cs> [targetFileName.cs]";

            string owner = args[0], repo = args[1], branch = args[2], filePath = args[3].Trim('/');
            string targetFileName = args.Length >= 5 ? args[4] : Path.GetFileName(filePath);

            if (string.IsNullOrEmpty(targetFileName) ||
                targetFileName.IndexOfAny(new[] { '/', '\\' }) >= 0 ||
                targetFileName.Contains(".."))
                return "targetFileName must be a plain filename with no path separators.";

            if (!targetFileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                targetFileName += ".cs";

            var newSub = new Subscription { Owner = owner, Repo = repo, Branch = branch, FilePath = filePath, TargetFileName = targetFileName };

            lock (configLock)
            {
                if (config.Subscriptions.Any(s =>
                        s.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase) &&
                        s.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase) &&
                        s.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
                    return "A subscription for that repo/file already exists. Use 'branch' to change its branch, or 'remove' first.";

                config.Subscriptions.Add(newSub);
            }
            SaveConfig();
            SyncSubscription(newSub, "manual-add", force: true);
            return $"Subscribed to {owner}/{repo}@{branch}:{filePath} -> {targetFileName}. Syncing now...";
        }

        private string CmdRemove(string[] args)
        {
            if (args.Length < 2) return "Usage: remove <owner> <repo>";
            int removed;
            lock (configLock)
            {
                removed = config.Subscriptions.RemoveAll(s =>
                    s.Owner.Equals(args[0], StringComparison.OrdinalIgnoreCase) &&
                    s.Repo.Equals(args[1], StringComparison.OrdinalIgnoreCase));
            }
            if (removed == 0) return "No matching subscription found.";
            SaveConfig();
            return $"Removed {removed} subscription(s) for {args[0]}/{args[1]}.";
        }

        private string CmdBranch(string[] args)
        {
            if (args.Length < 3) return "Usage: branch <owner> <repo> <newBranch>";
            Subscription replacement;
            lock (configLock)
            {
                int idx = config.Subscriptions.FindIndex(s =>
                    s.Owner.Equals(args[0], StringComparison.OrdinalIgnoreCase) &&
                    s.Repo.Equals(args[1], StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return "No matching subscription found.";

                // Replace with a new object rather than mutating the existing one in place —
                // an async sync for this subscription may currently be in flight and holds a
                // reference to the old object; mutating it out from under that sync could mix
                // the old branch's fetch result with the new branch's state-tracking key.
                var old = config.Subscriptions[idx];
                replacement = new Subscription
                {
                    Owner = old.Owner,
                    Repo = old.Repo,
                    Branch = args[2],
                    FilePath = old.FilePath,
                    TargetFileName = old.TargetFileName
                };
                config.Subscriptions[idx] = replacement;
            }
            SaveConfig();
            SyncSubscription(replacement, "manual-branch-change", force: true);
            return $"Changed {args[0]}/{args[1]} to branch '{args[2]}'. Syncing now...";
        }

        private string CmdSync(string[] args)
        {
            if (args.Length == 0)
            {
                List<Subscription> all;
                lock (configLock) { all = config.Subscriptions.ToList(); }
                foreach (var sub in all) SyncSubscription(sub, "manual-sync-all", force: true);
                return $"Force-syncing all {all.Count} subscription(s)...";
            }
            if (args.Length < 2) return "Usage: sync [<owner> <repo>]";
            Subscription match;
            lock (configLock)
            {
                match = config.Subscriptions.FirstOrDefault(s =>
                    s.Owner.Equals(args[0], StringComparison.OrdinalIgnoreCase) &&
                    s.Repo.Equals(args[1], StringComparison.OrdinalIgnoreCase));
            }
            if (match == null) return "No matching subscription found.";
            SyncSubscription(match, "manual-sync", force: true);
            return $"Force-syncing {args[0]}/{args[1]}...";
        }

        private string CmdList()
        {
            List<Subscription> subs;
            lock (configLock) { subs = config.Subscriptions.ToList(); }

            if (subs.Count == 0)
                return "No subscriptions configured. Use 'ghsync add <owner> <repo> <branch> <path> [targetFile]'.";

            var sb = new StringBuilder();
            sb.AppendLine($"GitHubSync subscriptions ({subs.Count}):");
            foreach (var s in subs)
            {
                state.LastKnownSha.TryGetValue(s.Key, out var sha);
                state.LastSyncTimeUtc.TryGetValue(s.Key, out var time);
                string shaShort = sha != null ? sha.Substring(0, Math.Min(7, sha.Length)) : "never synced";
                sb.AppendLine($"- {s.Owner}/{s.Repo}@{s.Branch} : {s.FilePath} -> {s.TargetFileName} | last commit: {shaShort} | last sync: {time ?? "never"}");
            }
            return sb.ToString();
        }

        private string CmdWebhookInfo()
        {
            string path = config.Webhook.Path.StartsWith("/") ? config.Webhook.Path : "/" + config.Webhook.Path;
            var sb = new StringBuilder();
            sb.AppendLine("--- GitHub Webhook Setup ---");
            sb.AppendLine($"Webhook enabled in config: {config.Webhook.Enabled}");
            sb.AppendLine($"Payload URL to enter in GitHub: http://YOUR_SERVER_PUBLIC_IP:{config.Webhook.Port}{path}/");
            sb.AppendLine("Content type: application/json");
            sb.AppendLine($"Secret: set in carbon/configs/{Name}.json (or oxide/config/{Name}.json) under Webhook.Secret, and paste the same value into GitHub.");
            sb.AppendLine("Which events: choose 'Just the push event'.");
            sb.AppendLine($"Also open port {config.Webhook.Port}/tcp in your VPS firewall / hosting provider's security group.");
            return sb.ToString();
        }

        #endregion
    }
}

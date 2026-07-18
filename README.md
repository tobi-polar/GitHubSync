# GitHubSync — auto-deploy Rust plugins from GitHub

A Carbon (also works on Oxide/uMod) plugin for your **test server**. Subscribe it to a
GitHub repo + branch + file, and it keeps that plugin file in sync automatically:

- **Polling** (on by default, zero setup): checks the branch every 30 seconds and
  pulls down any new commit. This alone gets you updates within ~30 seconds of a push,
  no server configuration needed.
- **Webhook** (optional, for true instant delivery): GitHub calls your server the moment
  you push, and the plugin downloads the update immediately — no waiting for the next
  poll. Requires your test server to be reachable on a port you choose.

Either way, once the new file lands in your plugins folder, Carbon/Oxide's own file
watcher picks it up and hot-reloads the plugin — you don't need to run any reload
command yourself.

**Important:** GitHub's Contents API returns single files inline, so each subscription
tracks one `.cs` file per repo (this matches how most solo-dev Oxide/Carbon plugins are
structured — one file per plugin). If you keep multiple plugins in one repo, just add a
separate subscription per file.

---

## 1. Install the plugin

Upload `GitHubSync.cs` into your server's plugins folder:

- Carbon: `carbon/plugins/GitHubSync.cs`
- Oxide/uMod: `oxide/plugins/GitHubSync.cs`

It'll compile and load automatically. On first load it writes a default config to
`carbon/configs/GitHubSync.json` (or `oxide/config/GitHubSync.json`).

## 2. Create a GitHub Personal Access Token

1. On GitHub: **Settings → Developer settings → Personal access tokens → Fine-grained tokens → Generate new token**.
2. Set **Resource owner** to your account, and under **Repository access** pick "Only select repositories" and choose the repo(s) you'll subscribe to (or "All repositories" if you'd rather not maintain this list).
3. Under **Permissions → Repository permissions**, set **Contents: Read-only**. That's the only permission this needs.
4. Generate the token and copy it — GitHub only shows it once.
5. Paste it into the config file under `"GitHub Personal Access Token"`.

This works for private repos. If all your repos are public you can leave the token
blank, but you'll hit GitHub's unauthenticated rate limit (60 requests/hour) much
faster, so setting a token is recommended either way.

## 3. Subscribe to a repo

In the server console (or in-game chat as the server owner), run:

```
githubsync add <owner> <repo> <branch> <path/in/repo/File.cs> [targetFileName.cs]
```

Example — pull `MyPlugin.cs` from the `main` branch of `tobybaillie/my-rust-plugins`:

```
githubsync add tobybaillie my-rust-plugins main MyPlugin.cs
```

That immediately does a first sync, then keeps polling every 30 seconds after that.
`targetFileName.cs` is optional — it defaults to the same filename as in the repo; only
specify it if you want the file saved under a different name in your plugins folder.

To point the same plugin at a different branch later (e.g. a `dev` branch while
you're testing):

```
githubsync branch tobybaillie my-rust-plugins dev
```

## 4. (Optional) Turn on the real-time webhook

Since your test server is hosted on a VPS with its own public IP, you can receive
GitHub's webhook directly — no relay service needed, and no local port-forwarding to
worry about (that's only an issue for servers behind a home router).

1. In the config file, set:
   ```json
   "Webhook": {
     "Enabled": true,
     "Port": 8093,
     "Path": "/github-webhook",
     "BindAddress": "*",
     "Secret": "<a long random string you generate>"
   }
   ```
   Generate a secret with something like `openssl rand -hex 32`.
2. Restart/reload the plugin so it picks up the new config and starts listening.
3. Open that port (`8093/tcp` by default) in your VPS's firewall — `ufw allow 8093/tcp`
   on most Linux VPS setups, or the equivalent security-group rule if your host uses
   one (DigitalOcean, Vultr, AWS, etc. all have a firewall panel for this).
4. Run `githubsync webhookinfo` in the console — it prints the exact payload URL and
   reminds you of the settings below.
5. On GitHub: repo → **Settings → Webhooks → Add webhook**.
   - **Payload URL**: `http://YOUR_SERVER_IP:8093/github-webhook/`
   - **Content type**: `application/json`
   - **Secret**: the same string you put in the config
   - **Which events**: "Just the push event"
6. GitHub will send a ping immediately — check your server console for
   `GitHub webhook listener started...` and confirm no errors appear.

Polling stays on as a safety net even with the webhook enabled, so if a webhook
delivery ever fails (network blip, GitHub retry exhausted, etc.) you're never more than
30 seconds behind anyway.

If your host doesn't let you open arbitrary inbound ports (some managed panels like
Pterodactyl restrict this to pre-allocated ports), just leave the webhook off — polling
alone is a very solid "auto-deploy on push" experience already.

## 5. Command reference

Run these from the server console (`githubsync ...`) or in-game chat as the owner
(`/ghsync ...`):

| Command | Description |
|---|---|
| `add <owner> <repo> <branch> <path> [targetFile]` | Subscribe to a file and sync it immediately |
| `remove <owner> <repo>` | Unsubscribe |
| `branch <owner> <repo> <newBranch>` | Switch which branch a subscription tracks |
| `sync [<owner> <repo>]` | Force an immediate re-sync (all subscriptions, or just one) |
| `list` | Show subscriptions, last synced commit, and last sync time |
| `webhookinfo` | Print the payload URL/settings for GitHub's webhook form |

All commands require server-owner auth level (AuthLevel 2) — the same level as
`ownerid`/RCon admin access.

## 6. Notes & troubleshooting

- **Nothing downloads / 404 errors in console**: usually a missing or wrong-scoped
  PAT for a private repo, or a typo in owner/repo/branch/path. `githubsync sync
  <owner> <repo>` and watch the console for the exact error.
- **File downloads but plugin doesn't reload**: Carbon/Oxide only reload on a real
  file change — GitHubSync already skips rewriting the file if the content is
  byte-identical, so check `list` to confirm the commit sha actually advanced.
- **Webhook listener fails to start**: check the console error at startup. The most
  common cause is the port already being used by something else, or a hosting panel
  that doesn't allow the plugin to bind extra ports — fall back to polling in that
  case (just leave `Webhook.Enabled: false`).
- **Security**: the PAT and webhook secret live in the plain-text config file — treat
  that file like a password. Only give the PAT `Contents: Read-only` access, scoped to
  the specific repos you need, so a leaked token can't do much damage. Always set a
  real webhook secret before exposing the port publicly — anyone who has the URL
  without a secret set could trigger arbitrary syncs of whatever repos you've
  subscribed to.

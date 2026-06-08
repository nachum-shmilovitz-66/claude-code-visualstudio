# Claude Code for Visual Studio

Brings the [Claude Code](https://www.anthropic.com/claude-code) agentic coding assistant into
**Visual Studio 2022 and Visual Studio 2026** as a native tool-window chat — the same kind of
experience as the VS Code extension. It drives the real `claude` CLI under the hood, so you get
the full agent: streaming responses, tool use, file edits, and multi-turn conversations, scoped
to your open solution.

> Status: **v0.1 — working core.** See the [Feature parity](#feature-parity) matrix for exactly
> what is implemented and what is on the roadmap.

---

## How it works

```
┌─────────────────────────── Visual Studio (devenv) ───────────────────────────┐
│                                                                               │
│  Tool window  ──► WebView2 (HTML/JS chat UI)  ◄──► WebViewHost (JSON bridge)   │
│                                                          │                     │
│                                                   ClaudeChatControl            │
│                                                    │            │              │
│                              IdeContextService ◄───┘            └──► ClaudeSession
│                              ThemeService                              │       │
│                              (DTE / Roslyn / VSColorTheme)             ▼       │
└───────────────────────────────────────────────────────────────────┬─────────┘
                                                                      │ stdin/stdout
                                                            stream-json (NDJSON)
                                                                      ▼
                                                          claude CLI (Node, your login)
```

- **WebView2** hosts the chat UI (served from a virtual host, so it looks/behaves like the VS Code panel).
- **`ClaudeSession`** spawns a long-lived `claude --print --input-format stream-json --output-format stream-json`
  process and speaks the bidirectional stream-json protocol: it writes your turns to stdin and parses the
  streamed `stream_event` / `tool_use` / `tool_result` / `result` events from stdout.
- **`IdeContextService`** reads your active file + selection from the editor and auto-attaches them as context;
  it also opens files Claude edits.
- **`ThemeService`** maps the current VS theme to the chat UI's colors.

The agent itself is the real Claude Code CLI, so it uses **your existing login**, your `CLAUDE.md`,
your MCP servers, hooks, and settings — exactly like running `claude` in a terminal.

---

## Prerequisites

1. **Visual Studio 2022 (17.x)** or **Visual Studio 2026 (18.x)** — Community, Professional, or Enterprise.
2. **Node.js** and the **Claude Code CLI** installed and on `PATH`:
   ```powershell
   npm install -g @anthropic-ai/claude-code
   claude --version
   ```
3. **Logged in once** — run `claude` in any terminal and complete login (`/login`) so the CLI has credentials.
4. **WebView2 Runtime** — already present on Windows 11 and with Visual Studio; otherwise install the
   [Evergreen runtime](https://developer.microsoft.com/microsoft-edge/webview2/).

---

## Install

### From the built VSIX
1. Build (see below) or grab `src/ClaudeCode.VisualStudio/bin/Release/ClaudeCode.VisualStudio.vsix`.
2. Double-click the `.vsix` and let the **VSIX Installer** add it to VS 2022 and/or 2026.
3. Restart Visual Studio.
4. Open the panel: **View ▸ Other Windows ▸ Claude Code**.

### Build it yourself
This repo builds with the MSBuild that ships in VS — the *Visual Studio extension development*
workload is **not** required (the `Microsoft.VSSDK.BuildTools` NuGet package supplies the targets).

```powershell
# from the repo root
$msb = "C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe"  # or your 2022 path
& $msb -t:Restore .\ClaudeCode.sln
& $msb -t:Build -p:Configuration=Release .\ClaudeCode.sln
# -> src\ClaudeCode.VisualStudio\bin\Release\ClaudeCode.VisualStudio.vsix
```

Or just open `ClaudeCode.sln` in Visual Studio and press **F5** — that launches the VS *experimental
instance* with the extension loaded for debugging.

---

## Usage

1. Open your solution.
2. **View ▸ Other Windows ▸ Claude Code**.
3. Type a request and press **Enter** (Shift+Enter for a newline). Claude works in your solution's folder.
4. Pick a **model** (default / sonnet / haiku) and a **permission mode** in the toolbar.
5. Select code in the editor before sending — the selection and active file are sent as context automatically.
6. Paste an image into the input to attach it.
7. **Stop** interrupts the current turn; **＋** starts a fresh session.

### Permission modes
| Mode | Behavior |
|------|----------|
| `acceptEdits` *(default)* | Auto-applies file edits and safe filesystem commands. |
| `default` | Runs read-only/safe tools automatically; other tools are skipped if not pre-approved. |
| `plan` | Read-only planning; Claude proposes without changing files. |
| `bypassPermissions` | Full autonomy — Claude runs any tool without prompting. Use with care. |

---

## Feature parity

| Capability | Status |
|---|---|
| Chat with streaming responses (markdown, code blocks) | ✅ |
| Full agent loop: Read / Edit / Write / Bash / Grep / Glob etc. | ✅ (real CLI) |
| Multi-turn conversation in one session | ✅ |
| Live token streaming + thinking | ✅ |
| Tool-call cards (collapsible, with results) | ✅ |
| Cost / token / duration usage readout | ✅ |
| Uses your login, `CLAUDE.md`, MCP servers, hooks, settings | ✅ (inherited from CLI) |
| Active-file + selection auto-context | ✅ |
| Opens files Claude edits in the editor | ✅ |
| Model picker + permission-mode picker | ✅ |
| Image paste | ✅ |
| Interrupt / new session / `--resume` continuity | ✅ |
| VS theme matching (light/dark/blue) | ✅ |
| Works in VS 2022 **and** VS 2026 | ✅ (manifest `[17.0,19.0)`) |
| In-process **MCP "ide" server** (native `openDiff`, on-demand `getDiagnostics`, `getCurrentSelection` as a tool) | 🚧 protocol handshake validated; not shipped |
| **Interactive per-tool permission cards** (allow/deny) | 🚧 UI present; backend pending stable control-protocol support |
| Native side-by-side diff preview before apply | 🚧 roadmap |
| Slash commands / skills picker in-panel | 🚧 roadmap (use natural language; the CLI still runs your hooks/skills) |

🚧 items are scaffolded (UI + protocol research done — see `ClaudeSession.HandleControlRequest`
and the permission card UI in `media/app.js`) but intentionally not enabled until verified inside a
live VS instance, to avoid shipping flaky behavior.

---

## Project layout

```
ClaudeCode.sln
src/ClaudeCode.VisualStudio/
  ClaudeCode.VisualStudio.csproj      classic VSIX project (net4.8), NuGet-only build
  source.extension.vsixmanifest       targets VS [17.0, 19.0)
  ClaudeCodePackage.cs                 AsyncPackage; registers tool window + commands
  VSCommandTable.vsct                  View ▸ Other Windows ▸ Claude Code
  Commands/OpenChatCommand.cs
  ToolWindows/
    ClaudeChatToolWindow.cs            tool window pane
    ClaudeChatControl.cs               orchestrates WebView + session + IDE services
  WebView/
    WebViewHost.cs                     WebView2 lifecycle + JSON message bridge
    WebMessage.cs                      envelope (System.Text.Json)
  Services/
    ClaudeCliLocator.cs                finds claude.exe / claude.cmd
    ClaudeSession.cs                   spawns CLI; parses stream-json; raises events
    ClaudeMessages.cs                  DTOs
    IdeContextService.cs               selection / open files / open file (DTE)
    ThemeService.cs                    VS theme -> CSS variables
  media/                               chat UI (index.html, app.js, style.css, markdown.js)
```

---

## Troubleshooting

- **"claude exited (code …)" / nothing happens** — make sure `claude --version` works in a terminal and
  you've logged in (`claude` then `/login`). The extension uses whatever `claude` resolves to on `PATH`.
  You can override the path with the `CLAUDE_CODE_VS_CLI` environment variable.
- **Panel is blank** — install/repair the WebView2 Evergreen Runtime.
- **Tools never run** — switch the permission mode away from `plan`; use `acceptEdits` or `bypassPermissions`.
- **Wrong working directory** — Claude runs in your solution's directory; open a solution first
  (otherwise it falls back to your user profile folder).

---

## License / disclaimer

This is an unofficial community integration. "Claude" and "Claude Code" are products of Anthropic.
You must have a valid Claude Code subscription/credentials to use it.

---

## Publishing to the Visual Studio Marketplace

This is an **unofficial, community** extension. The artifacts below publish the built VSIX
(`dist\ClaudeCode.VisualStudio.vsix`, v0.2.15) to the Visual Studio Marketplace with
`VsixPublisher.exe`.

Files involved:

- `marketplace/publishManifest.json` — the publish manifest (categories, publisher, `overview` path, repo).
- `marketplace/overview.md` — the formatted listing page shown on the Marketplace.
- `PRIVACY.md` — the privacy policy linked from the listing (lives at the repo root).
- `scripts/publish-marketplace.ps1` — the publish script.

### One-time setup

1. **Create a Marketplace publisher.** Sign in at <https://marketplace.visualstudio.com/manage> with
   a Microsoft account and create a publisher. (Note: `createPublisher` was removed from
   `VsixPublisher.exe` — publishers are created via the website only.) Copy your **publisher ID**
   (the slug).
2. **Fill the publisher placeholder.** In `marketplace/publishManifest.json`, replace
   `<YOUR_MARKETPLACE_PUBLISHER_ID>` with that publisher ID. (The publish script refuses to run while
   any `REPLACE` or `<YOUR_` placeholder is still present in the manifest.)
3. **Generate an Azure DevOps PAT.** At <https://dev.azure.com> → *User settings → Personal access
   tokens → New Token*:
   - **Organization:** *All accessible organizations*
   - **Scopes:** *Marketplace → **Publish*** (Manage also works; Publish is the minimum)
   - Copy the token. **Do not commit or hardcode it.** Provide it via the environment variable:
     ```powershell
     $env:CLAUDE_VS_MARKETPLACE_PAT = '<your-pat>'
     ```
   - **Do not run the publish script on a shared/multi-user host or under `Start-Transcript` /
     `Set-PSDebug -Trace`** — `VsixPublisher.exe` only accepts the PAT as a command-line argument, so
     it is briefly visible in the process table. On a shared host, use the two-step
     `VsixPublisher.exe login` / `publish` flow instead (see the script's `.NOTES`).
4. **Replace the support-URL placeholder.** There is no privacy/support field in the VS VSIX publish
   manifest, so these live in the listing text instead. Replace `<YOUR_HOSTED_SUPPORT_URL>` in
   **both** `marketplace/overview.md` and `PRIVACY.md` with your support / issue-tracker URL (the repo
   issues page is fine). The token is identical in both files; the publish script fails if either file
   still contains it.
5. **Commit `PRIVACY.md`.** `overview.md` links to
   `https://bitbucket.org/nachumsh66/vs-claude-code/src/develop/PRIVACY.md`, so `PRIVACY.md` must be
   committed at the repo root on the `develop` branch (or wherever that URL points) before publishing,
   so the link resolves.
6. **Listing images.** `overview.md` references images by **absolute hosted `https` URLs** (the raw
   repo URL for `marketplace/Preview.png`), which need **no** `assetFiles` mapping. If you instead want
   to embed local screenshots, add them under `marketplace/` (e.g. `screenshot-chat.png`), reference
   them in the markdown, **and** add a matching `assetFiles` array to `publishManifest.json` mapping each
   absolute `pathOnDisk` to the `targetPath` used in the markdown — otherwise local references render as
   broken images on the listing. Use your own UI captures; do **not** ship the Anthropic logo.

### Publish

From the repo root, with the PAT set in the environment:

```powershell
# Preview the exact command (token redacted), publish nothing:
.\scripts\publish-marketplace.ps1 -DryRun

# Publish for real:
.\scripts\publish-marketplace.ps1
```

The script locates `VsixPublisher.exe` (it tries
`C:\Program Files\Microsoft Visual Studio\18\Professional\VSSDK\VisualStudioIntegration\Tools\Bin\VsixPublisher.exe`
first, then searches `Program Files`), verifies the VSIX and manifest exist, checks for leftover
placeholders, reads the PAT from `CLAUDE_VS_MARKETPLACE_PAT`, and runs:

```
VsixPublisher.exe publish -payload <vsix> -publishManifest marketplace\publishManifest.json -personalAccessToken <pat>
```

`publish` overwrites the existing listing if the version matches, otherwise creates a new version. To
release an update, bump `Version` in `source.extension.vsixmanifest`, rebuild Release, and run the
script again.

> **Path resolution:** `VsixPublisher` resolves the manifest's `overview` (and any `assetFiles`) paths
> relative to the **manifest file's own directory**, not the working directory. That is why
> `publishManifest.json` uses `"overview": "overview.md"` (the file sits beside the manifest in
> `marketplace/`). Do **not** prefix it with `marketplace/`, and do not rely on the working directory.

### Signing

You do **not** need an author code-signing certificate. The Marketplace **repository-signs every
extension on publish**, and Visual Studio verifies that signature at install. A normal user installing
from the Marketplace gets an integrity-verified install with no scary warning. (Self-signed
certificates are *rejected* by the Marketplace, so they are worse than publishing unsigned. Optional
author signing, if you ever want it, uses a public-CA cert via the `dotnet sign` CLI — never
self-signed.)

### Trademark / naming caveat

"Claude", "Claude Code", and "Anthropic" are trademarks/products of **Anthropic**. This listing uses
them only **descriptively** ("for Visual Studio", "unofficial") to identify the CLI it integrates with,
ships **no Anthropic logo**, and carries a prominent "not affiliated with / not endorsed by Anthropic"
disclaimer — to comply with the Marketplace Terms of Use (no impersonation / no misrepresenting
affiliation) and Microsoft's trademark guidance (nominative use OK; third-party logos require a
license). Microsoft compliance does **not** grant any permission from Anthropic; your obligations to
Anthropic's own brand/trademark policy are separate. The Marketplace's automated impersonation review
can flag duplicated logos or repo links, so use your own original icon and your own repo link.

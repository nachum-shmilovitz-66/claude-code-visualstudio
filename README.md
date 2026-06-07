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
4. Pick a **model** (default / opus / sonnet / haiku) and a **permission mode** in the toolbar.
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

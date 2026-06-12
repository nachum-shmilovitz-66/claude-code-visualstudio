# Claude Code for Visual Studio (Unofficial)

> **Unofficial, community extension.** Not affiliated with, endorsed by, or sponsored by Anthropic.
> "Claude", "Claude Code", and "Anthropic" are trademarks/products of Anthropic. This project is an
> independent Visual Studio integration that drives the Claude Code CLI you install yourself. No
> Anthropic logo is used or shipped.

A native Visual Studio tool-window chat that drives the real `claude` CLI as a child process — an
agentic coding workflow docked right next to Solution Explorer.

![Claude Code chat in Visual Studio](https://raw.githubusercontent.com/nachum-shmilovitz-66/claude-code-visualstudio/develop/docs/images/hero.png)

## What it is

This extension hosts a chat panel inside Visual Studio and connects it to the **Claude Code CLI**
that is already installed on your machine. It talks to the CLI over `stream-json` on stdin/stdout, so
you get the full agent loop: Claude reads, edits, and writes files, runs tools, and carries on a
multi-turn conversation — all scoped to your open solution.

Because it shells out to *your* locally-installed CLI, it inherits your existing Claude login, your
`CLAUDE.md` project memory, your configured MCP servers, hooks, and settings — just like running
`claude` in a terminal.

## Features

- **Agentic chat** — Claude reads, searches, and edits your code, runs tools, and shows results inline.
- **Activity timeline** — every response renders each step (thinking, searches, file reads/edits) on a connected rail with status dots.
- **Inline diffs** — each edit shows as a red/green diff card in the chat, with an **Open diff** button for the full native Visual Studio Before/After window.
- **Runs in your solution folder** automatically — no extra configuration.
- **Model, mode & effort pickers** — pick Default (Opus 4.8 · 1M), **Fable 5**, Sonnet, Haiku, or **any custom model id**, set the permission mode, and dial reasoning effort right from the composer.
- **Rich auto-context** — the active file and selection, your open editors, and the VS **Error List (Problems)** are attached automatically; `@`-mention any file by name.
- **Live debugger/runtime context** — when the debugger pauses (breakpoint, step, or exception), a banner offers *"Ask Claude about this,"* and the paused call stack, locals, and exception are attached to your next message.
- **Live usage** — context-window usage ring (hover it to see the breakdown and % remaining; click to compact) plus an account & usage panel with plan and rate-limit windows.
- **Slash commands** — built-in commands plus everything your CLI exposes (its own commands, your skills, and project/user `.claude/commands/`).
- **Image paste**, **sent-input history** (Up/Down in the composer), and **one-click compaction**.
- **First-run guidance** — in-panel banners help you install the CLI, sign in, and update it when a newer version ships.

## Requirements

- **Visual Studio 2022 (17.x)** or **Visual Studio 2026 (18.x)** — Community, Professional, or Enterprise (amd64). Separate Marketplace listings cover Visual Studio 2019 and 2017.
- **Node.js** and the **Claude Code CLI** on your `PATH`:
  ```
  npm install -g @anthropic-ai/claude-code
  claude --version
  ```
- **Signed in once** — run `claude` in a terminal and complete `/login` so the CLI has credentials. Claude Code needs a paid **Pro/Max** plan or API credits.
- **WebView2 Runtime** — already present on Windows 11 and with Visual Studio; otherwise install the [Evergreen runtime](https://developer.microsoft.com/microsoft-edge/webview2/).

## Install

1. Install this extension from the Marketplace (or double-click the `.vsix`).
2. Restart Visual Studio.
3. Open a solution.
4. Open the panel: **View → Claude Code** (also under **View → Other Windows**; it docks next to Solution Explorer).
5. Ask away — Claude works in your solution's folder.

## Privacy & data flow

**This extension runs no servers of its own and sends nothing to the extension author.** There is no
analytics endpoint and no author backend. Be aware of two distinct network paths, both authenticated
with **your own existing Claude credentials**:

1. **The CLI handles your prompts and code.** When you send a message, the extension launches your
   locally-installed `claude` CLI as a child process and passes it your prompts, selected code,
   active-file context, and any pasted images over `stream-json`. The **CLI** transmits that data to
   Anthropic to produce responses — exactly the same as running `claude` in a terminal.

2. **The extension itself calls Anthropic to show your account & usage.** To populate the account and
   usage panel, the extension reads your existing Claude OAuth access token from the local credential
   file (`~/.claude/.credentials.json`, or the equivalent under `%AppData%\Claude`) and makes its own
   direct HTTPS requests to `api.anthropic.com` (`/api/oauth/profile` and `/api/oauth/usage`) using
   that token as a Bearer credential. It receives your account email, organization, and plan plus your
   usage-limit windows, which it displays in the panel. The token is used in memory for those requests
   only — it is **not** persisted by the extension and is **not** sent anywhere except `api.anthropic.com`.

No data goes to the extension author or any third party other than Anthropic. All model and account
data handling at Anthropic is governed by [Anthropic's Privacy Policy](https://www.anthropic.com/legal/privacy)
and your Claude subscription. Conversation transcripts shown in the panel are stored **locally only**
and **encrypted at rest** with Windows DPAPI (per-user); nothing is stored remotely by this extension.

Full details: see [PRIVACY.md](https://github.com/nachum-shmilovitz-66/claude-code-visualstudio/blob/develop/PRIVACY.md)
in the project repository.

## Support

Questions and issues: the [issue tracker](https://github.com/nachum-shmilovitz-66/claude-code-visualstudio/issues)
on the [project repository](https://github.com/nachum-shmilovitz-66/claude-code-visualstudio).

## License & trademark

This is an unofficial community integration and is **not affiliated with, endorsed by, or sponsored
by Anthropic**. "Claude", "Claude Code", and "Anthropic" are trademarks/products of Anthropic, used
here only descriptively to identify the CLI this extension integrates with. No Anthropic logo is used
or shipped. You must have a valid Claude account/credentials to use the underlying CLI.

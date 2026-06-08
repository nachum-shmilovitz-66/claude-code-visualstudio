# Claude Code for Visual Studio

**Anthropic's agentic coding assistant, right inside Visual Studio** — the same experience as the VS Code extension, docked next to Solution Explorer.

![Claude Code](https://raw.githubusercontent.com/REPLACE_ME/claude-vs/main/marketplace/Preview.png)

Chat with Claude about your open solution and let it read, search, and edit your code — every step streamed back to you on a live activity timeline.

## Features

- **Agentic chat** — Claude reads and edits your code, runs tools, and shows results inline.
- **Activity timeline** — every response renders each step (thinking, searches, file reads/edits) on a connected rail with status dots: 🟠 running · 🟢 done · 🔴 error.
- **Runs in your solution folder** automatically — no extra configuration.
- **Model, mode & effort** — pick the model, permission mode, and thinking effort right from the composer.
- **Live usage** — context-window usage ring plus an account & usage panel.
- **Productivity** — slash commands, image attachments, and one-click compaction.
- **Diff review** — edited files open automatically so you can review the change.

## Requirements

- Powered by the **Claude Code CLI** — sign in once by running `claude` in a terminal.
- **Visual Studio 2022 or 2026** (amd64).

## Getting started

1. Install the extension.
2. Open a solution.
3. **Tools → Claude Code** (or the tab next to Solution Explorer).
4. Ask away — Claude works in your project folder.

## How it works

The extension drives the official `claude` CLI in stream-json mode, so it shares your CLI login, your `CLAUDE.md` project memory, and any MCP servers you've configured. Token usage and rate limits are the same account-wide pool as the CLI and the VS Code extension.

---

*Claude and Claude Code are products of Anthropic. This is an independent Visual Studio integration.*

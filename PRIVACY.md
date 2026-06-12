# Privacy Policy — Claude Code for Visual Studio (Unofficial)

_Last updated: 2026-06-08 · Applies to extension version 0.2.15_

**Unofficial, community extension.** This project is not affiliated with, endorsed by, or sponsored
by Anthropic. "Claude", "Claude Code", and "Anthropic" are trademarks/products of Anthropic.

## Summary

- The extension **runs no servers of its own** and has **no analytics endpoint**.
- It does **not** send your code, prompts, or any data to the extension author. The only third party
  any data reaches is **Anthropic**, and only over the two paths described below, authenticated with
  **your own existing Claude credentials**.
- Your prompts and code context are sent to Anthropic **by the Claude Code CLI** you install yourself
  — the same as running `claude` in a terminal.
- The extension **itself** also makes direct requests to `api.anthropic.com` to display your account
  and usage information (see "Data the extension sends to Anthropic itself" below).
- Session state shown in the panel is stored **locally only**, on your machine.

## What the extension does

Claude Code for Visual Studio is a Visual Studio integration around the Claude Code CLI. When you send
a message, it launches the locally-installed `claude` command-line tool as a child process and
communicates with it over `stream-json` on stdin/stdout. The extension has no backend of its own and
does not report telemetry to the author.

## Data the extension passes to the local CLI

To drive the CLI, the extension passes the following to the local `claude` process on your machine:

- The text you type into the chat composer (your prompts).
- Code you select in the editor and the active file's context, when attached.
- Images you paste into the composer, when attached.
- Slash commands you invoke.

This data is handed to the **local CLI process**. The extension does not transmit it to the author or
to any service other than via the CLI (see below) and the account/usage path (see below).

## Data sent to Anthropic by the CLI

The Claude Code CLI — which you install and log into yourself — transmits your prompts, attached
code/selection/active-file context, and images to **Anthropic** in order to produce responses. This is
identical to running `claude` directly in a terminal. That data flow is between **you and Anthropic**,
authenticated with **your own Claude credentials/subscription**, and is governed by:

- **Anthropic's Privacy Policy:** https://www.anthropic.com/legal/privacy
- **Anthropic's Consumer / Commercial Terms** and your Claude plan.

## Data the extension sends to Anthropic itself (account & usage panel)

To populate the in-panel account and usage display, the **extension itself** (not the CLI) does the
following, in memory, on your machine:

1. **Reads your local Claude credential file** to obtain your existing OAuth access token. It checks,
   in order, paths such as `~/.claude/.credentials.json` and the equivalents under `%AppData%\Claude`
   (e.g. `%AppData%\Claude\.credentials.json`). It uses the token already created by your `claude`
   login; it does not create, refresh, or store credentials of its own.
2. **Makes direct HTTPS requests to `api.anthropic.com`** using that token as a `Bearer` credential:
   - `https://api.anthropic.com/api/oauth/profile` — returns your account email, organization, and plan.
   - `https://api.anthropic.com/api/oauth/usage` — returns your usage-limit windows (e.g. 5-hour session,
     7-day weekly).
   The responses are shown in the account/usage panel.

The access token is used **only in memory** for these requests. The extension does **not** persist the
token, and does **not** send it (or any of the returned data) anywhere except `api.anthropic.com`. No
account data is sent to the extension author or any third party other than Anthropic. This data flow is
between **you and Anthropic**, governed by Anthropic's Privacy Policy and your Claude plan (linked above).

## Local storage

- Session state (conversation history shown in the panel, UI selections such as model/mode/effort) is
  stored **locally** on your machine.
- CLI credentials, `CLAUDE.md` project memory, MCP server configuration, and other settings are owned
  and stored by the **Claude Code CLI** under your user profile — not created by this extension. The
  extension only *reads* the credential file to obtain the access token described above.
- Nothing is stored on any remote server operated by this extension or its author.

## Children's privacy

This is a developer tool not directed at children.

## Changes to this policy

Material changes will be reflected in this file in the project repository, with an updated date.

## Contact / support

Questions or concerns: please open an issue on the
[project repository](https://github.com/nachum-shmilovitz-66/claude-code-visualstudio/issues).

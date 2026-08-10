# Changelog

All notable changes to **Claude Code for Visual Studio (Unofficial)** are documented here.
The format is loosely based on [Keep a Changelog](https://keepachangelog.com/); versions
follow the `source.extension.vsixmanifest` Identity version. Releases are published on the
[GitHub Releases](https://github.com/nachum-shmilovitz-66/claude-code-visualstudio/releases) page.

## [Unreleased]

## [1.0.6] - 2026-08-10

- **The chat keeps its scroll position across tool-window tab switches.** Docked beside Solution
  Explorer, fronting the other tab hides the WebView, and a hidden WebView is laid out at zero
  height — which clamped the transcript's scroll to the top and, worse, looked like the user had
  scrolled up, so stick-to-bottom was lost too. Coming back always landed at the very beginning of
  the conversation. The position is now remembered while the view has a real size and re-applied
  when it returns — repeatedly, until it holds and the content height stops moving, because
  Visual Studio restores the view over several frames and a single write lands while the content
  is still short and gets clamped.
- **The conversation is restored even when the tool window opens before the solution.** The chat is
  usually restored *before* the solution finishes loading, at which point the working directory is
  still the user-home fallback. Transcripts are stored per working directory, so the lookup missed —
  and because it was attempted exactly once, the chat stayed empty for the rest of the session. The
  restore is retried when a solution or folder finishes opening, against the real project directory.
  A live session keeps its own directory, so an in-progress conversation is never re-pointed.
- **Updating the Claude CLI no longer opens a console window.** `claude update` runs in the
  background with its output captured, and the banner reports progress. A failure or a ten-minute
  stall is surfaced in the banner with the captured output and a **Run in terminal** button, so a
  hidden process can never fail silently — that fallback is also the way out if the updater needs
  interactive input.
- **The update banner notices when the update finishes, and says so.** Nothing used to watch for
  the result, so the "update available" banner sat there even after a successful update. Completion
  is now reported as "Claude CLI updated to `<version>`" rather than the banner silently vanishing,
  and the confirmation retires itself after 30 seconds — counting only while the chat is actually
  on screen, so an update that lands behind another tab still gets read. A newly-available update
  always outranks a pending countdown.
- **Re-check reports its result.** It used to re-render an identical banner, which read as the
  button doing nothing. It now shows "checking…", and when nothing has changed it says so and notes
  that a running Claude session can hold the binary open and delay the swap. The npm "latest" lookup
  is also cached for the lifetime of the process, so an explicit re-check now clears that cache
  instead of re-reporting the old answer.
- **Diagnostics for the WebView UI.** Release builds ship without DevTools, which made layout and
  setup problems invisible from the outside. Visibility, size and scroll transitions, and the
  installed/latest/outdated verdict, are logged to `%LOCALAPPDATA%\ClaudeCodeVS\session.log`.

## [1.0.5] - 2026-08-04

- **Permission mode applies immediately.** Switching mode (e.g. Ask → Auto) used to take effect
  only when the session relaunched on the *next* message, so a switch made mid-turn — the moment
  users actually reach for it, with an approval card on screen — kept prompting for the rest of
  the turn. The new mode is now pushed to the running CLI over the control protocol, and any card
  still waiting is allowed when switching to Auto. Switching *into* Ask from a session launched
  without prompting still relaunches, as before.
- **Context ring reports the real context size.** It was fed by the turn's `result` usage, which
  is summed over every API request in the turn — each tool round-trip re-reads the cached prefix,
  so a handful of tool calls could show 154% of a 1M window on a short session. Usage is now read
  per request from the stream, and sub-agent requests no longer move the ring. Session totals in
  the Usage dialog stay cumulative, as they should be.
- **The ring and the Context dialog agree.** The ring measured against a flat 200k window while
  the dialog fell back to the window the selected model implies, so on a 1M model the ring drew
  five times the dialog's percentage (110k read as 55%, not 11%). Both share one window helper now.

## [1.0.4] - 2026-08-03

- **Account & Usage matches the VS Code panel.** The dialog now reads the API's new unified
  `limits` array, so every reported window appears — including per-model ones like
  **Weekly Fable** — instead of the legacy per-model keys the API no longer populates.
  Bars turn amber/red when the API reports warning/critical severity.
- **Extra usage credits.** When pay-as-you-go credits are enabled (or any were spent), a
  section shows the amount used of the monthly limit with its own bar.
- **"What's contributing to your limits usage?"** New section mirroring VS Code: a Day/Week
  toggle over sessions, total tokens, the share of usage at >150k context (with the
  `/compact` / `/clear` tip), and the share coming from subagents. Approximate, computed
  from this machine's local session logs only — other devices and claude.ai are not included.

## [1.0.3] - 2026-08-01

- **Model picker matches the VS Code panel.** Every row reads `<model> · <what it is for>`
  ("Opus 5 with 1M context · Best for everyday, complex tasks"), with an explicit
  **Opus (1M context)** entry alongside Default so Opus can be selected directly.
- The model name is derived from the id the CLI actually resolved, not a hardcoded string — so
  when a new Opus ships the row renames itself. The bundled names are only a first-run fallback,
  which keeps the VS Code look without reintroducing the staleness the v1.0.0 alias change removed.
- The **Custom model** screen uses the same format, showing a friendly name plus the wire id, and
  typing a built-in id (e.g. `opus[1m]`) now selects that row instead of falling through to Default.

## [1.0.2] - 2026-07-31

- **Compaction now uses the CLI's own `/compact`** instead of asking the model for a summary brief.
  Pressing the context ring (or picking `/compact` in the palette) previously sent a hidden
  "summarize our conversation" prompt, and the model's answer streamed into the chat as an ordinary
  reply — so compacting printed a wall of markdown — then the session was torn down and restarted
  seeded with that text. The CLI accepts `/compact` over its stream-json stdin, rewrites its context
  in place, keeps the same session, and emits **no assistant text**. So: nothing is printed, no extra
  model turn is billed, and no session restart is needed.
- The compaction divider now reports the real before/after figures from `compact_boundary`
  (e.g. *Compacted · 35.0k → 3.0k tokens*), and the context ring re-baselines off the CLI's
  reported post-compaction size. Automatic compaction (when the window fills) is labelled
  *Auto-compacted* and is now surfaced too, rather than passing silently.

## [1.0.1] - 2026-07-31

Security hardening, from a full audit of the extension. No exploitable vulnerability was found;
these close depth gaps and one logging defect.

- **Chat traffic no longer logged in Release builds.** `WebViewHost.PostRaw` wrote the first 80
  characters of every outbound message to `session.log` unconditionally, bypassing the `Verbose`
  gate that was supposed to keep Release quiet — enough to capture the start of assistant output
  and part of the account email. Now `WriteVerbose`; failures still log, without the payload.
- **CSP `img-src` narrowed** to `data: blob:`. Nothing loads a remote image, so the previous
  `https:` wildcard only stood to become an exfil channel if image markdown were ever supported.
- **Markdown NUL sentinel made safe by construction.** `inline()` delimits code-span placeholders
  with `\u0000`, which `esc()` does not escape, so model output containing a literal NUL could
  forge one. Not exploitable (the placeholder table only ever holds escaped content), but the
  escaping no longer rests on that invariant — NULs are stripped on entry to `render()`.
- **WebView-supplied file paths confined to the workspace.** `openFile` now requires the path to
  resolve inside a workspace root or to be an already-open document, so a crafted message cannot
  pull an arbitrary file (a credential store, unrelated source) into the editor.
- CodeQL workflow (C# + JavaScript, weekly and on PR).

## [1.0.0] - 2026-07-31

First stable release.

- **Models now follow new releases automatically.** The four built-in picker entries send CLI
  *aliases* (`opus[1m]`, `fable`, `sonnet`, `haiku`) instead of pinned ids, so a new Opus/Sonnet
  ships without an extension update. Default previously pinned `claude-opus-4-8[1m]`, which
  downgraded sessions once Opus 5 was out. The picker shows the id the CLI actually resolved.
- **Chat transcript restyled to match the VS Code panel** — borderless tool and thinking rows,
  tool names in the plain foreground instead of the brand accent, dim monospace arguments that
  wrap instead of being ellipsised.
- Context/Usage panels no longer auto-open on hover; they open on click only.
- Project docs: CHANGELOG, CONTRIBUTING, SECURITY policy, issue templates, and a PR test workflow.

## [0.2.45] - 2026-06-12

- Tighter, more scannable in-VS extension description (all three manifests).

## [0.2.44] - 2026-06-12

- README **Screenshots** section and a **Debug-aware** callout (Claude reads the paused runtime to find the root cause).

## [0.2.43] - 2026-06-11

- Inline **red/green diff card** per edit in the chat; opt-in native VS diff; wrapped long tool output.

## [0.2.39] - 2026-06-11

- Editor **selection chip**; live **runtime/debugger context** (pause banner, call stack, locals, exceptions); first-run and debug auto-show; themed initial load.

## [0.2.34] - 2026-06-10

- Browse previously **sent inputs with Up/Down** in the composer.

## [0.2.33] - 2026-06-10

- Default model is **Opus 4.8 (1M)**; CLI version check and correct self-update.

## [0.2.28 - 0.2.29] - 2026-06-10

- Model token-cost ratio badge in the picker; fix tool-card label wrapping in chat.

## [0.2.27] - 2026-06-10

- Separate **VS 2017** and **VS 2019** VSIX flavors; VSIX files named by target VS.

## [0.2.24 - 0.2.26] - 2026-06-09

- **Interactive per-tool permission cards** (allow/deny) in "Ask before edits" mode; custom model ids; popovers close on selection.

## [0.2.20 - 0.2.23] - 2026-06-09

- `/mcp` screen actions (scope groups, refresh, missing-env diagnosis, authenticate); **slash-command palette parity** with the CLI; first-run onboarding guidance; skills merged into the palette.

## [0.2.12 - 0.2.18] - 2026-06-08

- Sessions/history; Tools-menu toggle; VS Code parity (diagnostics feed, native diff viewer, `@`-mention file picker); per-model reasoning effort; unit-test project; `/mcp` screen.

## [0.2.8] - 2026-06-08

- Activity-timeline UI; usage / prompt-caching breakdown; `CLAUDE.md` and MCP surfacing in Context; security hardening (input allow-lists, safer default permission mode, gated logging).

## [0.1.0] - 2026-06-07

- Initial release: native chat tool window driving the `claude` CLI over `stream-json`.

[Unreleased]: https://github.com/nachum-shmilovitz-66/claude-code-visualstudio/compare/v0.2.45...HEAD

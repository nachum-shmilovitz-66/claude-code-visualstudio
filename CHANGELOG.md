# Changelog

All notable changes to **Claude Code for Visual Studio (Unofficial)** are documented here.
The format is loosely based on [Keep a Changelog](https://keepachangelog.com/); versions
follow the `source.extension.vsixmanifest` Identity version. Releases are published on the
[GitHub Releases](https://github.com/nachum-shmilovitz-66/claude-code-visualstudio/releases) page.

## [Unreleased]

## [1.0.14] - 2026-09-02

- **The model picker says which model is answering.** The button read "Model" whatever was running,
  so the only way to find out was to open the picker - and even that showed the selection rather
  than what it resolved to, which for "Default" is whichever model the CLI currently maps the alias
  to. It now reads the model itself - "Opus 5 (1M)", "Sonnet 5" - the way the VS Code panel labels
  it, taken from the id the CLI reports it actually ran the turn with, so it is the model that
  answered and not a guess. Before a session has reported one the selected row's own label fills
  in, the full wire id sits on the tooltip, and an id the extension does not recognise is shown
  verbatim instead of being trimmed to its first word.
- **The context ring is green.** It was drawn in the brand orange, the same colour as the Send
  button sitting beside it, which reads as a warning on a gauge that spends nearly all its time
  reporting a healthy, mostly-empty context window. The ring now has a colour of its own - a light
  green chosen to stay legible against the panel without competing with the button next to it -
  rather than borrowing the accent every other control uses.

## [1.0.13] - 2026-08-26

- **A conversation the CLI can no longer find no longer breaks every message.** The extension
  remembers the CLI session id per working directory and resumes it. When that conversation is gone
  - a cleaned-up transcript, a directory that never had it - `claude` exits with code 1 before
  reading a single message, and the extension kept handing it the same dead id on every send, so
  the chat failed identically for ever with no way out from inside the panel. The refusal is now
  recognised, the stale id dropped, and the turn re-sent once on a fresh session. The transcript is
  kept: only the CLI-side conversation was missing.
- **Failures say what actually failed.** Every non-zero exit was reported as "check that you are
  logged in" - which sent people off to fix an account that was never broken, while the CLI's real
  stderr was captured and then thrown away. The message now carries what the CLI said, and the
  sign-in route is offered only when the CLI actually blamed authentication.
- **Signing in happens in the panel.** `claude auth login` prints an authorize URL, opens the
  browser, and waits on stdin for the code the callback page shows - so all three steps now have
  somewhere to go in the banner: a progress line, a paste-code box, and a confirmation. Opening a
  terminal remains as the fallback for flows that need more than a pasted code.
- **A login is checked with the CLI, not by looking for a file.** Readiness was decided by the
  presence of a credentials file, which outlives the token inside it - so an expired login read as
  healthy right up until a turn failed. The CLI's own `auth status` now has the final word, asked
  after the load path so the first paint is as quick as before.
- **An update that silently does nothing is reported instead of ignored.** `claude update` can
  fetch a release, fail to put it in place, and still exit 0. That counted as success, the banner
  re-rendered unchanged, and clicking Update looked like it did nothing at all. A run that leaves
  the installed version untouched is now called out, with the updater's own output shown in the
  banner and written to the log, which is exactly what was being discarded before.
- **The update check follows the release channel you actually update along.** It always compared
  against npm's `latest`. On the `stable` channel - which trails `latest` by about a week - that is
  a prompt updating can never satisfy, so the banner would return every hour on a CLI that was
  already as current as its channel allows. `autoUpdatesChannel` is now read from your settings.

## [1.0.12] - 2026-08-19

- **The slash palette fills in three seconds sooner.** Fetching the command list began by asking
  Visual Studio for the working directory - a question that has to be answered on the UI thread,
  which during startup is busy. The measured hop was 3.1 seconds, and the palette sat empty for all
  of it, even though the cache underneath answers in 6 milliseconds. The callers that had just
  resolved the directory now say so instead of asking again.
- **The extension stops competing with Visual Studio for the startup.** Load instrumentation showed
  nothing blocking the UI thread, but two `claude` processes were being launched straight into the
  middle of the IDE coming up: the version probe (~3s) and the slash-command refresh (~13s), the
  latter while the palette had *already* been filled from cache 6ms earlier and nothing was waiting
  on it. The warm-cache refresh now waits 30 seconds, and the version probe 5 seconds - which takes
  roughly sixteen seconds of process churn out of the startup window. A cold cache still fetches at
  once, because there the palette really is waiting, and the install / sign-in banners still post
  immediately, since they never depended on the version.
- **The CLI update check keeps running while Visual Studio is open.** It used to run once, when the
  chat window loaded, so a release that shipped while the IDE stayed open - often for days - was
  never noticed. The same check now repeats every hour. It is started from the tail of the first
  check, which already runs on a background thread, and its first tick is an hour out, so it adds
  nothing to the load path; it also stands aside while an update is in flight, leaving that to the
  watcher already following it.
- **Dismissing the update reminder snoozes it instead of muting it.** Dismiss used to hide the
  banner for the rest of the session. It now clears at the next hourly check, so the reminder comes
  back about an hour later, and a version released in the meantime brings it back straight away.
- **Load-time instrumentation.** A one-shot report of where the milliseconds go between the package
  loading and the chat being usable - package init, WebView2 boot, page ready, working directory,
  transcript restore, the CLI version and slash-command fetches. Marks cost a stopwatch read, and
  the whole report is written to `session.log` in a single append well after the load, so measuring
  the load never becomes part of it.

## [1.0.11] - 2026-08-12

- **Diff bands are readable again, and in colours that mean "added" and "removed".** They were
  tinted with the panel's `--green` and `--red`, which are the *syntax* colours for a type name and
  a string, so an addition came out teal and read as "highlighted" rather than "added". Taking the
  tints straight from VS Code's theme file did not work either: those are translucent
  (`#347d3926` and the like) and composite over this panel's near-black background to something
  indistinguishable from it, which made the bands vanish. The diff palette is now its own set of
  opaque colours, tuned against the panel so a band reads clearly while the text on top stays
  legible, with a stronger pair marking the characters that changed inside a modified line.

## [1.0.10] - 2026-08-12

- **Edit diffs read like a diff instead of a wall of colour.** The panel used to draw every line of
  the "before" text in red followed by every line of the "after" text in green, so changing one
  identifier in a twenty-line edit lit up forty lines and told you nothing about what had moved.
  Diffs are now computed properly: untouched lines stay as plain context, only genuine changes get
  a red or green band, and inside a modified line the characters that actually changed are marked.
  There is a two-column gutter of before/after line numbers and a `+N −M` summary, and long lines
  scroll sideways rather than wrapping, which used to destroy the column alignment. Line numbers
  count from the start of the edit rather than the file, since the panel is given the edit and not
  the file — the diffs are labelled accordingly, and "Open diff" still shows the true before/after.
- **The About dialog and the panel report the right version.** Both were left at 1.0.8 by the 1.0.9
  release.

## [1.0.9] - 2026-08-12

- **The "not signed in" banner clears itself once you log in.** Login is handled by the CLI in a
  terminal outside the IDE, so nothing reported back when it finished and the banner simply sat
  there — the only way out was noticing the manual "I've logged in — re-check" button. The panel
  now re-checks readiness every few seconds while that banner is up (for up to ten minutes, and
  only while the tool window is actually visible), and re-checks immediately when you switch back
  to a tool window that was hidden. Clicking "Open terminal to log in" restarts the window. The
  manual button stays for anyone who wants it.
- **The context panel no longer looks like MCP support is missing.** The server list is reported by
  the CLI on a session's first turn, so on a freshly opened panel there was nothing to show and the
  whole section was hidden — indistinguishable from "this build has no MCP". It now always renders,
  falling back to the servers configured in your MCP config (the same `claude mcp list` behind the
  `/mcp` screen) until the session reports its own, and saying "none configured" when there really
  are none.

## [1.0.8] - 2026-08-11

- **The install-time licence now matches the licence the project publishes.** The agreement shown
  by the VSIX installer was not the MIT licence in `LICENSE`: it named a different copyright holder
  ("Claude Code VS"), granted only the right to *use* the extension rather than MIT's copy, modify,
  distribute, sublicense and sell, dropped MIT's requirement to retain the notice, and shortened the
  liability clause. Users were therefore accepting materially less than the repository, README badge
  and marketplace listing all advertise. The installer now shows the MIT licence in full, under the
  correct copyright holder, after a short preamble covering the unofficial status, the Anthropic
  trademarks, and the fact that the Claude Code CLI is neither bundled nor covered by this licence.

## [1.0.7] - 2026-08-11

- **Compact works on a conversation that has no live CLI process.** Compaction is delegated to the
  CLI's own `/compact` over the running process's stdin, so it refused with "Nothing to compact yet
  — send a message first" whenever nothing was running — which is the normal state for a transcript
  restored at startup, or one whose process has since exited. It failed exactly when compaction is
  most wanted: a long conversation reopened the next day with the context ring full. The context is
  still on disk and resumable, so compact now starts the CLI with `--resume` the same way sending a
  message does. The refusal remains only when there is genuinely nothing to continue.

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

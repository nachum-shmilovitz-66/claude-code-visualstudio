# Changelog

All notable changes to **Claude Code for Visual Studio (Unofficial)** are documented here.
The format is loosely based on [Keep a Changelog](https://keepachangelog.com/); versions
follow the `source.extension.vsixmanifest` Identity version. Releases are published on the
[GitHub Releases](https://github.com/nachum-shmilovitz-66/claude-code-visualstudio/releases) page.

## [Unreleased]

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

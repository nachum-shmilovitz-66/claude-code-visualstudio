# Security Policy

## Supported versions

This is an actively developed community project. Security fixes target the **latest released
version** only; please update before reporting.

## Reporting a vulnerability

**Do not open a public issue for security problems.**

Report privately via GitHub's **[Report a vulnerability](https://github.com/nachum-shmilovitz-66/claude-code-visualstudio/security/advisories/new)**
button (Security ▸ Advisories). Include:

- affected version (Extensions ▸ Manage Extensions shows it),
- Visual Studio version,
- a description and, if possible, reproduction steps and impact.

You'll get an acknowledgement; fixes are coordinated through a private advisory and credited unless
you prefer otherwise.

## Scope and sensitive areas

This extension is a thin UI over your locally installed `claude` CLI. The security-sensitive
surfaces are:

- **Credentials.** The extension reads your existing Claude OAuth token from the local credential
  file to populate the account/usage panel and makes direct HTTPS calls to `api.anthropic.com`
  only. The token is used in memory, never persisted by the extension, and never logged. Reports
  of token exposure (logs, telemetry, disk) are high priority.
- **CLI process launch.** Model / permission-mode / effort values from the UI are allow-listed
  before being passed to the CLI; report any path that lets webview input reach a shell unescaped.
- **WebView2 UI.** The panel loads only local UI; external links open in the system browser.
  Report any content-injection or navigation-escape issue.
- **Transcripts** are stored locally and encrypted at rest with Windows DPAPI (per-user).

Out of scope: vulnerabilities in the upstream Claude Code CLI, Node.js, or Visual Studio itself —
report those to their respective projects.

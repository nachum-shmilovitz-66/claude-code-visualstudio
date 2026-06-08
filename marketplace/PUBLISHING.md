# Publishing to the Visual Studio Marketplace

The local VSIX details pane shows the manifest `<Description>` as **plain, whitespace-collapsed text** — no markdown. The fully-formatted page (headings, bullets, screenshot) only renders when the extension is published to the VS Marketplace, which uses `overview.md`.

## One-time setup

1. Create a free publisher at <https://marketplace.visualstudio.com/manage> (sign in with a Microsoft account).
2. Note your **publisher ID** → put it in `publishManifest.json` (`publisher` field).
3. Create a **Personal Access Token (PAT)** at <https://dev.azure.com> → User settings → Personal access tokens →
   - Organization: **All accessible organizations**
   - Scope: **Marketplace → Manage**
4. Copy `marketplace/Preview.png` somewhere web-hosted (e.g. the GitHub repo) and fix the image URL in `overview.md` (and the `repo` URL in `publishManifest.json`).

## Publish command

`VsixPublisher.exe` ships with the VS SDK:

```
C:\Program Files\Microsoft Visual Studio\18\Professional\VSSDK\VisualStudioIntegration\Tools\Bin\VsixPublisher.exe ^
  publish ^
  -payload "D:\claude\vs-claude-code\dist\ClaudeCode.VisualStudio.vsix" ^
  -publishManifest "D:\claude\vs-claude-code\marketplace\publishManifest.json" ^
  -personalAccessToken <YOUR_PAT>
```

(PowerShell: use backticks instead of `^`, or put it on one line.)

## Updating later

Bump the `Version` in `source.extension.vsixmanifest`, rebuild Release, then run the same `publish` command — it replaces the listing and re-renders `overview.md`.

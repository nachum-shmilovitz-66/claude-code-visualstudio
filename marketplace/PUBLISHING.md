# Publishing to the Visual Studio Marketplace

> Maintainer documentation. Regular users install from [Releases](https://github.com/nachum-shmilovitz-66/vs-claude-code/releases/latest); contributors don't need this.

This is an **unofficial, community** extension. The artifacts below publish the built VSIX
(`dist\ClaudeCode.VisualStudio-vs2022-2026.vsix`) to the Visual Studio Marketplace with `VsixPublisher.exe`.

> The VS 2019 and VS 2017 flavors (`dist\ClaudeCode.VisualStudio-vs2019.vsix`,
> `dist\ClaudeCode.VisualStudio-vs2017.vsix`) each have their own VSIX Identity Id, so publishing
> them to the Marketplace requires a separate listing per flavor (the Marketplace allows one VSIX
> per listing). Until then they ship via GitHub Releases only.

Files involved:

- `marketplace/publishManifest.json` — the publish manifest (categories, publisher, `overview` path, repo).
- `marketplace/overview.md` — the formatted listing page shown on the Marketplace.
- `PRIVACY.md` — the privacy policy linked from the listing (lives at the repo root).
- `scripts/publish-marketplace.ps1` — the publish script.

## One-time setup

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
5. **Commit `PRIVACY.md`.** `overview.md` links to a hosted `PRIVACY.md`; point that link at the raw
   GitHub URL for this repo
   (`https://raw.githubusercontent.com/nachum-shmilovitz-66/vs-claude-code/develop/PRIVACY.md`) and
   commit `PRIVACY.md` at the repo root on `develop` so the link resolves.
6. **Listing images.** `overview.md` references images by **absolute hosted `https` URLs** (the raw
   repo URL for `marketplace/Preview.png`), which need **no** `assetFiles` mapping. If you instead want
   to embed local screenshots, add them under `marketplace/` (e.g. `screenshot-chat.png`), reference
   them in the markdown, **and** add a matching `assetFiles` array to `publishManifest.json` mapping each
   absolute `pathOnDisk` to the `targetPath` used in the markdown — otherwise local references render as
   broken images on the listing. Use your own UI captures; do **not** ship the Anthropic logo.

## Publish

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

## Signing

You do **not** need an author code-signing certificate. The Marketplace **repository-signs every
extension on publish**, and Visual Studio verifies that signature at install. A normal user installing
from the Marketplace gets an integrity-verified install with no scary warning. (Self-signed
certificates are *rejected* by the Marketplace, so they are worse than publishing unsigned. Optional
author signing, if you ever want it, uses a public-CA cert via the `dotnet sign` CLI — never
self-signed.)

## Trademark / naming caveat

"Claude", "Claude Code", and "Anthropic" are trademarks/products of **Anthropic**. This listing uses
them only **descriptively** ("for Visual Studio", "unofficial") to identify the CLI it integrates with,
ships **no Anthropic logo**, and carries a prominent "not affiliated with / not endorsed by Anthropic"
disclaimer — to comply with the Marketplace Terms of Use (no impersonation / no misrepresenting
affiliation) and Microsoft's trademark guidance (nominative use OK; third-party logos require a
license). Microsoft compliance does **not** grant any permission from Anthropic; your obligations to
Anthropic's own brand/trademark policy are separate. The Marketplace's automated impersonation review
can flag duplicated logos or repo links, so use your own original icon and your own repo link.

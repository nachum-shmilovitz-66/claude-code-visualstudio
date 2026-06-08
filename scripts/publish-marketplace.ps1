#requires -Version 5.1
<#
.SYNOPSIS
    Publishes the "Claude Code for Visual Studio" VSIX (v0.2.15) to the Visual Studio Marketplace
    using VsixPublisher.exe.

.DESCRIPTION
    - Locates VsixPublisher.exe (tries the known VS 18 Professional path, then searches Program Files).
    - Validates that the built VSIX exists at dist\ClaudeCode.VisualStudio.vsix.
    - Validates that the publish manifest has no unreplaced placeholders.
    - Reads the Azure DevOps Personal Access Token from the CLAUDE_VS_MARKETPLACE_PAT environment
      variable (NEVER hardcoded). Errors clearly if it is unset.
    - Invokes:  VsixPublisher.exe publish -payload <vsix> -publishManifest <manifest> -personalAccessToken <pat>

    VsixPublisher resolves the manifest's "overview" path RELATIVE TO THE MANIFEST FILE's own
    directory (not the process working directory). The manifest therefore uses "overview": "overview.md"
    (overview.md sits beside the manifest in marketplace\), and any images must be referenced either by
    absolute https URLs in the markdown or via an assetFiles array using absolute pathOnDisk values.
    Setting the working directory has NO effect on this resolution, so this script does not depend on it.

    Before running: create your Marketplace publisher, put its ID in marketplace\publishManifest.json
    (replace <YOUR_MARKETPLACE_PUBLISHER_ID>), set CLAUDE_VS_MARKETPLACE_PAT, and replace any
    <YOUR_HOSTED_SUPPORT_URL> placeholders in the listing text.

.PARAMETER DryRun
    Print exactly what would run without contacting the Marketplace.

.EXAMPLE
    $env:CLAUDE_VS_MARKETPLACE_PAT = '<your-pat>'
    .\scripts\publish-marketplace.ps1 -DryRun
    .\scripts\publish-marketplace.ps1

.NOTES
    Signing: the Marketplace repository-signs every extension on publish; you do NOT need an author
    code-signing certificate. (Self-signed certs are rejected by the Marketplace.)

    SECRET-HANDLING / RESIDUAL EXPOSURE:
      * VsixPublisher.exe offers no stdin/file token input, so the PAT must be passed as a command-line
        argument (-personalAccessToken). On Windows, a running process's command line is readable by
        other processes on the same machine (e.g. via WMI / Get-CimInstance Win32_Process). The token
        is therefore briefly visible in the process table while VsixPublisher runs. DO NOT run this
        script on a shared or multi-user host. The script nulls out the token after the call as a
        best effort, but cannot remove the OS-level command-line exposure.
      * On a shared host, prefer the two-step flow to keep the PAT out of repeated publish calls:
            VsixPublisher.exe login -personalAccessToken <pat> -publisherName <id>
            VsixPublisher.exe publish -payload <vsix> -publishManifest <manifest>   # no -pat; uses login
        The login still passes the PAT as an argument once, but the repeated publish calls do not.
      * DO NOT run this script under Start-Transcript or with Set-PSDebug -Trace enabled: an ambient
        transcript/trace will capture the real argument array verbatim, defeating the console redaction.
#>
[CmdletBinding()]
param(
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Resolve repo-relative paths (this script lives in <repo>\scripts) ---------------------------
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir

$VsixPath     = Join-Path $RepoRoot 'dist\ClaudeCode.VisualStudio.vsix'
$ManifestPath = Join-Path $RepoRoot 'marketplace\publishManifest.json'

Write-Host 'Claude Code for Visual Studio - Marketplace publish (v0.2.15)' -ForegroundColor Cyan
Write-Host ("Repo root : {0}" -f $RepoRoot)

# --- 1. Locate VsixPublisher.exe -----------------------------------------------------------------
function Find-VsixPublisher {
    # Known path for this machine first (fast path).
    $known = 'C:\Program Files\Microsoft Visual Studio\18\Professional\VSSDK\VisualStudioIntegration\Tools\Bin\VsixPublisher.exe'
    if (Test-Path -LiteralPath $known) {
        return $known
    }

    # Otherwise search both Program Files roots for any VS edition / year that ships the VS SDK.
    $roots = @(
        ${env:ProgramFiles},
        ${env:ProgramFiles(x86)}
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique

    foreach ($root in $roots) {
        $vsDir = Join-Path $root 'Microsoft Visual Studio'
        if (-not (Test-Path -LiteralPath $vsDir)) { continue }

        $hit = Get-ChildItem -LiteralPath $vsDir -Recurse -Filter 'VsixPublisher.exe' `
                   -ErrorAction SilentlyContinue |
               Select-Object -First 1
        if ($hit) {
            return $hit.FullName
        }
    }

    return $null
}

$VsixPublisher = Find-VsixPublisher
if (-not $VsixPublisher) {
    throw "VsixPublisher.exe not found. Install the Visual Studio SDK / 'Visual Studio extension " +
          "development' workload, or set the path manually. Expected under " +
          "'...\VSSDK\VisualStudioIntegration\Tools\Bin\VsixPublisher.exe'."
}
Write-Host ("VsixPublisher: {0}" -f $VsixPublisher)

# --- 2. Validate the VSIX exists -----------------------------------------------------------------
if (-not (Test-Path -LiteralPath $VsixPath)) {
    throw "VSIX not found at '$VsixPath'. Build the Release configuration first so the packaged " +
          "VSIX is copied to dist\."
}
Write-Host ("Payload   : {0}" -f $VsixPath)

# --- 3. Validate the publish manifest exists and has no unreplaced placeholders ------------------
if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "Publish manifest not found at '$ManifestPath'."
}
Write-Host ("Manifest  : {0}" -f $ManifestPath)

$manifestText = Get-Content -LiteralPath $ManifestPath -Raw
# Catch ANY unreplaced placeholder form: the documented '<YOUR_...' token as well as the older
# 'REPLACE_WITH_...' / 'REPLACE_ME' strings that have appeared on disk. Match before contacting the
# Marketplace so we never send the PAT for a request that is guaranteed to be rejected.
if ($manifestText -match 'REPLACE|<YOUR_') {
    throw "publishManifest.json still contains an unreplaced placeholder (matched 'REPLACE' or " +
          "'<YOUR_'). Set the real Marketplace publisher ID (and a real 'repo' URL) before publishing."
}

# --- 4. Warn on leftover support-URL placeholders in the listing text ----------------------------
# Not a hard publish blocker (there is no support-URL field in the VS publish manifest), but shipping
# a raw placeholder to the public listing reads as unfinished, so fail loudly here.
$OverviewPath = Join-Path $RepoRoot 'marketplace\overview.md'
$PrivacyPath  = Join-Path $RepoRoot 'PRIVACY.md'
foreach ($doc in @($OverviewPath, $PrivacyPath)) {
    if ((Test-Path -LiteralPath $doc) -and
        ((Get-Content -LiteralPath $doc -Raw) -match 'YOUR_HOSTED_SUPPORT_URL')) {
        throw "'$doc' still contains the placeholder 'YOUR_HOSTED_SUPPORT_URL'. Replace it with your " +
              "real support / issue-tracker URL before publishing (the token must match in overview.md " +
              "and PRIVACY.md)."
    }
}

# --- 5. Read the PAT from the environment (never hardcoded) --------------------------------------
$Pat = $env:CLAUDE_VS_MARKETPLACE_PAT
if ([string]::IsNullOrWhiteSpace($Pat)) {
    throw "Environment variable CLAUDE_VS_MARKETPLACE_PAT is not set. Create an Azure DevOps " +
          "Personal Access Token (scope: Marketplace > Publish) and set it, e.g.:`n" +
          "    `$env:CLAUDE_VS_MARKETPLACE_PAT = '<your-pat>'`n" +
          "Do NOT commit or hardcode the token. Do NOT run this script on a shared host or under " +
          "Start-Transcript / Set-PSDebug -Trace (see .NOTES)."
}

# --- 6. Build the argument list ------------------------------------------------------------------
# VsixPublisher uses single-hyphen flags. The PAT is passed as a separate argument. NOTE: on Windows
# the command line is briefly readable by other processes (see .NOTES) -- this is a VsixPublisher CLI
# limitation. Never echo $Pat; the display form below redacts it.
$pubArgs = @(
    'publish'
    '-payload',             $VsixPath
    '-publishManifest',     $ManifestPath
    '-personalAccessToken', $Pat
)

# Display form with the token redacted, for the console / dry run.
$displayArgs = @(
    'publish'
    '-payload',             "`"$VsixPath`""
    '-publishManifest',     "`"$ManifestPath`""
    '-personalAccessToken', '***REDACTED***'
)

Write-Host ''
Write-Host 'Command:' -ForegroundColor Yellow
Write-Host ("  `"{0}`" {1}" -f $VsixPublisher, ($displayArgs -join ' '))
Write-Host ''

if ($DryRun) {
    $Pat = $null
    Write-Host 'DRY RUN: nothing was published. Re-run without -DryRun to publish for real.' -ForegroundColor Green
    return
}

# --- 7. Publish ----------------------------------------------------------------------------------
# Path resolution does not depend on the working directory (VsixPublisher resolves the manifest's
# overview/assetFiles relative to the manifest file's own folder), so we do not change CWD here.
try {
    & $VsixPublisher @pubArgs
    $exit = $LASTEXITCODE
}
finally {
    # Best-effort: drop the token from this scope as soon as the call returns. This does NOT remove
    # the OS-level command-line exposure that occurred while the process was running (see .NOTES).
    $Pat = $null
    $pubArgs = $null
}

if ($exit -ne 0) {
    throw "VsixPublisher.exe exited with code $exit. See the messages above for the reason " +
          "(common causes: invalid category for the Marketplace backend, name/impersonation " +
          "rejection, expired or wrong-scope PAT)."
}

Write-Host ''
Write-Host 'Published successfully. The Marketplace repository-signs the extension automatically.' -ForegroundColor Green

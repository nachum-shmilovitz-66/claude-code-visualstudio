# Deletes all GitHub Releases + tags EXCEPT v0.2.47.
# Run from the repo root in PowerShell. Irreversible (release VSIX assets are removed).
$ErrorActionPreference = 'Stop'
$owner = 'nachum-shmilovitz-66'
$repo  = 'claude-code-visualstudio'

# Release IDs to delete (everything except v0.2.47 = 338530523)
$ids = 338499941,338487795,338221918,338161100,337467782,337345105,337295851,
       337229634,337075668,336868760,336865140,336704320,336554666,336544080,
       336485526,336481817,336453706
# Matching tags to delete from the remote
$tags = 'v0.2.45','v0.2.44','v0.2.43','v0.2.39','v0.2.34','v0.2.33','v0.2.29',
        'v0.2.28','v0.2.27','v0.2.26','v0.2.25','v0.2.24','v0.2.23','v0.2.22',
        'v0.2.21','v0.2.20','v0.2.19'

# Pull the GitHub token from the local git credential helper
$cred  = "protocol=https`nhost=github.com`n`n" | git credential fill
$token = ($cred | Select-String '^password=').ToString().Substring(9)
if (-not $token) { throw 'No GitHub token found in git credential store.' }

$h = @{ Authorization = "Bearer $token"; 'User-Agent' = 'release-cleanup'; Accept = 'application/vnd.github+json' }

foreach ($id in $ids) {
    try {
        Invoke-RestMethod -Method Delete -Uri "https://api.github.com/repos/$owner/$repo/releases/$id" -Headers $h | Out-Null
        "deleted release $id"
    } catch {
        "FAILED release $id : $($_.Exception.Message)"
    }
}

# Delete the tags on the remote (one push, all refs)
git push origin --delete $tags

"DONE - only v0.2.47 should remain"

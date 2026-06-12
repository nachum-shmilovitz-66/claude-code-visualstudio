# Contributing

Thanks for your interest in **Claude Code for Visual Studio (Unofficial)**. This is a community
project and not affiliated with Anthropic.

## Reporting bugs / requesting features

Use the [issue tracker](https://github.com/nachum-shmilovitz-66/claude-code-visualstudio/issues)
and pick the **Bug report** or **Feature request** template. For **security** issues, follow
[SECURITY.md](SECURITY.md) instead — do not file a public issue.

## Building

Prerequisites: Visual Studio 2022/2026 with the **Visual Studio extension development** workload
(or the build SDK restored via NuGet — the projects are NuGet-only, no workload required for CI).

```powershell
# Restore once, then build the main VSIX (VS 2022/2026 flavor)
msbuild ClaudeCode.sln -t:Restore -p:Configuration=Release
msbuild src\ClaudeCode.VisualStudio\ClaudeCode.VisualStudio.csproj -t:Rebuild -p:Configuration=Release
```

Use **`-t:Rebuild`** (not `Build`) after a version or manifest change — `Build` is incremental and
can re-ship a stale packaged manifest. The post-build step copies each VSIX into `dist\`.
There are three flavors (`ClaudeCode.VisualStudio`, `.Vs2019`, `.Vs2017`); see the **Project
layout** section in [README.md](README.md).

## Running the tests

The unit tests (`tests/ClaudeCode.VisualStudio.Tests`, MSTest, net48) cover the pure-logic helpers.
Build them with **full MSBuild** (not `dotnet test`, which can't build the referenced VSSDK
project), then run with VSTest:

```powershell
msbuild ClaudeCode.sln -t:Restore -p:Configuration=Debug
msbuild tests\ClaudeCode.VisualStudio.Tests\ClaudeCode.VisualStudio.Tests.csproj -t:Build `
  -p:Configuration=Debug -p:DeployExtension=false -p:CreateVsixContainer=false
vstest.console.exe tests\ClaudeCode.VisualStudio.Tests\bin\Debug\net48\ClaudeCode.VisualStudio.Tests.dll
```

The `ci` workflow runs exactly this on every pull request.

## Versioning & releases

Patch the version in **all six spots** (the three `source.extension.vsixmanifest` files,
`ClaudeCodePackage.cs`, `ClaudeChatControl.cs`, and the README badge), then push: a Git hook
tags `v<version>` and the `release` workflow builds and publishes the VSIXs. Keep
[CHANGELOG.md](CHANGELOG.md) updated.

## Code style

Match the surrounding code (C# for the extension, vanilla HTML/JS/CSS for the `media/` webview UI).
Keep webview input strictly validated before it reaches the CLI process.

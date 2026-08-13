# Contributing

TubeForge is maintainer-led and does not currently accept unsolicited code pull requests. Sanitized bug reports and focused design feedback are welcome. Discuss substantial changes in an issue before starting work.

## Before reporting a problem

- Confirm the content is public and you are authorized to save it.
- Remove cookies, signatures, visitor data, media URLs, private titles, local usernames, and full output paths.
- Never attach downloaded copyrighted media.
- Include the TubeForge version, Windows version, failure code, and minimal reproduction steps.
- Use the general bug form for app, installation, update, queue, or UI defects; use the extractor form for analysis and format regressions.

## Engineering rules

- No third-party NuGet/npm packages, external executables, hosted downloader services, or copied extractor code.
- Keep YouTube-specific behavior inside `TubeForge.YouTube`.
- Add focused dependency-free tests for behavior changes.
- Stream media; never buffer a complete download in memory.
- Fail closed on malformed player scripts, JSON, URLs, paths, or container data.
- Preserve cancellation across all network and disk operations.

## Local checks

```powershell
dotnet build TubeForge.slnx --configuration Release
dotnet run --project tests/TubeForge.Tests --configuration Release -- --all
```

Run focused synthetic media gates when changing finalization, chapters, timeline editing, or HLS capture:

```powershell
.\scripts\Test-ChapterEmbedding.ps1 -Configuration Release
.\scripts\Test-TimelineEditing.ps1 -Configuration Release
.\scripts\Test-HlsCapture.ps1 -Configuration Release
```

Run the isolated [performance budget](docs/PERFORMANCE_BUDGET.md) after downloader, parser, queue, or UI performance changes:

```powershell
dotnet run --project tools/TubeForge.Performance --configuration Release --no-build
```

Live probes are opt-in and must use public media you are authorized to test. Never commit canary URLs, IDs, titles, channels, signed media URLs, or output media. Follow the [extractor playbook](docs/EXTRACTOR_PLAYBOOK.md).

## Release checks

Use an explicit release version. Current example:

```powershell
.\scripts\Publish-Release.ps1 -Version 2.2.8
.\scripts\Test-Release.ps1 -Version 2.2.8
.\scripts\Publish-Installer.ps1 -Version 2.2.8
.\scripts\Test-Installer.ps1 -Version 2.2.8
```

Release artifacts are generated output and must not be committed. Authenticode signing is optional and fails closed when requested signing cannot be verified.

TubeForge is MIT-licensed. Do not submit code copied from projects whose terms are incompatible or unknown.

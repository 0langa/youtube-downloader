# Installation, upgrades, and removal

TubeForge provides a recommended per-user installer and portable ZIP distributions. Neither option adds a Windows service or modifies `PATH`.

Download releases only from the [official TubeForge releases page](https://github.com/0langa/TubeForge/releases/latest).

## Choose a build

- `setup.exe`: recommended. Installs the self-contained app for the current user, adds a Start Menu shortcut, and registers TubeForge in Add/Remove Programs without elevation.
- `self-contained`: recommended portable option. Includes the .NET 10 Windows Desktop runtime and does not require a separate runtime installation.
- `framework-dependent`: smaller. Requires the x64 .NET 10 Windows Desktop Runtime already installed.

Both builds target Windows 10/11 x64. Extract the whole archive; do not run `TubeForge.exe` from inside the ZIP.

The self-contained release build restores only the exact Microsoft .NET runtime packs selected by the pinned SDK from the official NuGet feed. Application projects still reject every `PackageReference`. Every x64 distribution also contains `ffmpeg/ffmpeg.exe`, pinned by SHA-256 and used as a separate process for MP4, WebM, and MKV stream-copy finalization plus optional audio/video conversion. Licenses and exact source/build provenance ship beside it and in [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md).

## Verify and install

Keep the downloaded installer or ZIP and `SHA256SUMS.txt` in the same directory. In PowerShell:

```powershell
$version = '2.3.1'
$name = "TubeForge-$version-win-x64-setup.exe"
$expected = (Get-Content .\SHA256SUMS.txt | Where-Object { $_ -match "  $([regex]::Escape($name))$" }).Split(' ')[0]
$actual = (Get-FileHash -LiteralPath ".\$name" -Algorithm SHA256).Hash
if ($actual -cne $expected) { throw 'TubeForge checksum mismatch.' }
```

Run the verified setup executable. The default per-user installation directory is:

```text
%LOCALAPPDATA%\Programs\TubeForge\
```

Portable users can instead extract a complete ZIP to a versioned directory and run `TubeForge.exe`. Windows may warn for an unsigned build. Check the release manifest field `authenticodeSigned`; do not assume an unsigned artifact is signed.

GitHub-hosted release artifacts also have signed build-provenance attestations. Online verification requires GitHub CLI:

```powershell
gh attestation verify ".\$name" -R 0langa/TubeForge
```

## Upgrade and rollback

> [!IMPORTANT]
> TubeForge v2.1.0 requires one manual upgrade. Its update check can find a newer release, but its installed binary cannot enable the update action and does not contain the startup prompt. Download, verify, and run the current setup executable once. Installed v2.2.0 and later releases can use the flow below.

1. Let active downloads finish or pause them, then close TubeForge.
2. Let the startup check show the update prompt, use Settings → Check now, or download the new installer from the official release.
3. Choose `Update now` to authorize the full update. TubeForge shows the current and target versions, installer size, active phase, and percentage while it downloads the official installer and verifies the repository, version, asset name, size, GitHub digest, and matching SHA-256 manifest.
4. TubeForge keeps progress visible during the final staged-installer SHA-256 recheck, then closes the running version, installs the update for the current Windows user, and launches the updated app. Existing local settings, queue, and Library are reused from `%LOCALAPPDATA%\TubeForge`.

Portable users should verify and extract the new archive to a sibling directory. Keep the prior portable directory until the new version has completed an analyze/download smoke test.

Portable rollback uses the previous version directory. Installer rollback requires reinstalling a previously verified setup asset. Settings schema v1-v5 files migrate in memory to schema v6 and are written as v6 on the next save; legacy quality/container filename tokens migrate to the quality-suffix option and extension-free template. Queue and Library history retain their own schemas; archive profiles migrate from schema v1 to v2. Downgrading after TubeForge writes these newer schemas is unsupported, so keep the newer installer available.

## Local data and retention

TubeForge stores application state in `%LOCALAPPDATA%\TubeForge`:

- `settings.json`: download directory, filename template, optional quality suffix, default preset, simple/advanced disclosure, global/per-host concurrency, accelerated-transfer preference, retry/timeout limits, update preference, proxy mode and optional credential-free manual proxy endpoint, Library sort preference, responsible-use acknowledgement;
- `archives.json`: user-created playlist/channel archive sources, local destination/template/quality-suffix/output preferences, and bounded checked video identifiers; no signed media URLs or credentials;
- `queue.json`: video IDs, display titles, format/source identities, destination paths, output/caption/chapter/trim/SponsorBlock/live-capture selections, byte counts, attempt counts, timestamps, and failure codes; no signed media URLs, SponsorBlock payloads, or HLS manifest URLs;
- `history.json`: completed video IDs, display titles, format identities, destination paths, sizes, and timestamps;
- `.bak` and `.pending` siblings: crash-recovery copies of those stores.

Signed media URLs, cookies, credentials, and downloaded media are not stored in these application-state files. Downloads, captions, thumbnails, metadata sidecars, and partial transfer files remain in the destination selected by the user. A diagnostic JSON exists only when the user explicitly exports it.

## Uninstall

First close TubeForge. Use Add/Remove Programs or the TubeForge uninstaller in the installation directory. User data and downloaded files are preserved by default; the uninstaller offers an explicit local-data removal choice. Portable users can remove their extracted application directory.

Inspect retained state before deleting it:

```powershell
Get-ChildItem -LiteralPath "$env:LOCALAPPDATA\TubeForge" -Force
```

To reset TubeForge state, delete `%LOCALAPPDATA%\TubeForge` after reviewing the path. This does not delete downloaded media stored elsewhere. Remove downloaded media separately only if that is your intent.

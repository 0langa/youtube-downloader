<p align="center"><img src="assets/TubeForge.svg" width="104" height="104" alt="TubeForge icon"></p>

# TubeForge

[![CI](https://github.com/0langa/TubeForge/actions/workflows/ci.yml/badge.svg)](https://github.com/0langa/TubeForge/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/0langa/TubeForge?display_name=tag)](https://github.com/0langa/TubeForge/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

TubeForge is an ad-free Windows desktop app for public YouTube media you own or are authorized to save. It is built from scratch in C# without `yt-dlp`, hosted conversion services, telemetry, accounts, or third-party NuGet packages. Releases bundle a pinned LGPL FFmpeg executable for local stream-copy finalization and optional conversion.

> [!IMPORTANT]
> You are responsible for complying with YouTube's terms, copyright, privacy, and local law. Download only content you own or have permission to save. TubeForge does not bypass DRM, payment, membership, login, or other access controls.

## Download

**[Download latest stable release](https://github.com/0langa/TubeForge/releases/latest)**

TubeForge v2.2.6 is the current stable release. For normal use, choose `TubeForge-2.2.6-win-x64-setup.exe`. It installs for the current Windows user without administrator access. Portable self-contained and framework-dependent ZIPs are also available.

TubeForge v2.1.0 users must install the current release manually once: v2.1.0 can detect an update but cannot enable its update button and does not contain the startup prompt. Later installed releases support the verified in-app update flow.

Requirements: Windows 10 or 11, x64. Self-contained builds include the .NET runtime; framework-dependent builds require the x64 .NET 10 Windows Desktop Runtime. See [installation, verification, upgrades, rollback, and removal](docs/INSTALLATION.md).

## Features

### Downloads and formats

- Public videos, Shorts, completed live replays, playlists, and channels with per-item selection.
- Bounded public active/upcoming YouTube HLS capture with wait/record modes, duration and size limits, recoverable segment journals, and validated MKV finalization.
- Progressive, native-audio, and adaptive video/audio streams with resolution, container, codec, FPS/HDR, bitrate, and exact-stream filters.
- Highest-quality MP4, WebM, or MKV stream-copy output plus resumable separate-track downloads.
- Native M4A/WebM audio and optional MP3, AAC/M4A, Opus/OGG, WAV, or FLAC conversion.
- Optional H.264/AAC MP4, H.265/AAC MP4, and VP9/Opus WebM conversion presets; original-quality stream copy remains default.

### Download workflow

- Presets for Best original, Windows MP4, Small file, and MP3 320, plus full custom selection.
- Queue with 1–4 global transfers, progress/speed/ETA, pause, resume, cancel, retry, reveal, and interrupted-download recovery.
- Automatic validated multi-worker transfers for large media with bounded resume state and sequential fallback.
- Preflight disk forecasting, retry limits, atomic publication, collision-safe names, and output validation.
- Token-based filename templates with extensions applied once and an optional quality suffix for bitrate/lossless audio or video resolution.
- Searchable local Library, duplicate detection, moved-file rescans, JSON import/export, and playlist/channel archive profiles.

### Optional media tools

- SRT/WebVTT caption sidecars and ordered soft-subtitle embedding for MP4, MKV, and WebM.
- Chapter embedding and lossless chapter splitting with sanitized numbered filenames.
- Start/end trimming with synchronized caption and chapter rebasing.
- Disabled-by-default SponsorBlock chapter markers or explicit transcode removal using a privacy-preserving hash-prefix lookup.
- Validated thumbnail downloads and stable JSON metadata sidecars without signed stream URLs.

### Privacy, networking, and updates

- No ads, telemetry, accounts, hosted services, cookie import, or credential storage.
- System, manual, or disabled proxy mode across metadata, collections, captions, thumbnails, media, SponsorBlock, and update checks; credential-bearing proxy URLs are rejected.
- Redacted diagnostics export that excludes media URLs, IDs, titles, channels, local paths, headers, cookies, signatures, and visitor data.
- Configurable automatic update checks. Explicit `Update now` shows phase and percentage through download and final verification, then closes TubeForge, installs per-user, and relaunches the updated version.
- Reproducible Windows x64 packages with SHA-256 manifests, GitHub build-provenance attestations, and optional Authenticode signatures.

## Not supported

- Authenticated, private, paid, membership, age/region-bypass, or other access-controlled media.
- Encrypted or DRM-protected streams.
- Generic non-YouTube URLs or arbitrary M3U8 capture.
- macOS, Linux, ARM64, or 32-bit Windows builds.

## Documentation

- [Installation, updates, rollback, data retention, and uninstall](docs/INSTALLATION.md)
- [Current release notes](docs/RELEASE_NOTES.md) and [complete changelog](CHANGELOG.md)
- [Support policy and current limitations](docs/SUPPORT_POLICY.md)
- [Extraction compatibility history](docs/EXTRACTION_COMPATIBILITY.md) and [maintainer playbook](docs/EXTRACTOR_PLAYBOOK.md)
- [Security policy](SECURITY.md), [threat model](TUBEFORGE_THREAT_MODEL.md), and [false-positive response guide](docs/SECURITY_FALSE_POSITIVE_RESPONSE.md)
- [FFmpeg licensing and build provenance](THIRD_PARTY_NOTICES.md)
- [Contributor and maintainer checks](CONTRIBUTING.md)

## Build from source

Required toolchain: .NET 10 SDK on Windows.

```powershell
dotnet build TubeForge.slnx --configuration Release
dotnet run --project tests/TubeForge.Tests --configuration Release -- --all
dotnet run --project src/TubeForge.App --configuration Release
```

Release packaging downloads one pinned, SHA-256-verified FFmpeg x64 archive. Advanced extractor, media, performance, packaging, and installer checks are documented in [CONTRIBUTING.md](CONTRIBUTING.md).

## Security

Do not include media URLs, cookies, signatures, visitor data, private-video data, local paths, or downloaded media in reports. Use Diagnostics → Export JSON, review the output, then follow [SECURITY.md](SECURITY.md). Report vulnerabilities through GitHub's private vulnerability reporting flow.

## License

TubeForge is available under the [MIT License](LICENSE). Bundled FFmpeg licensing, exact source, build provenance, and notices are documented in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

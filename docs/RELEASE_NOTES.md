# TubeForge v2.2.8

TubeForge v2.2.8 fixes a false 360p-only fallback affecting some public videos whose actual YouTube format ladder includes separate high-resolution video and audio streams. Adaptive Googlevideo accessibility checks now use the same bounded player-style query ranges as the download engines instead of HTTP `Range` headers that some media endpoints reject.

> [!IMPORTANT]
> TubeForge v2.1.0 can detect a newer release, but its installed binary cannot enable the update button and does not contain the startup prompt. It cannot repair itself. If you are running v2.1.0, download and run `TubeForge-2.2.8-win-x64-setup.exe` once from the official release. Updates after that can use the in-app flow.

Choose the per-user Windows x64 installer for normal use or a portable archive when needed. Verify `SHA256SUMS.txt` before running or extracting an asset. GitHub Actions release artifacts carry build-provenance attestations; the release manifest states whether Windows executables also have an Authenticode signature.

Highlights:

- use bounded `range`, `rn`, and `rbuf` query parameters for direct Googlevideo media probes, matching actual direct and segmented downloads;
- retain HTTP header ranges for public HLS manifest probes;
- keep strict HTTPS Googlevideo redirect validation and per-format provider user agents;
- preserve adaptive MP4, WebM, and MKV video-plus-audio selection instead of falling back to a lone progressive stream after a false probe rejection;
- label direct-client extraction truthfully as `DIRECT STREAMS VERIFIED` in the desktop app;
- add deterministic coverage proving query-range probes for direct-client fallback, transformed watch-page media, and high-resolution adaptive video/audio pairs.

Pre-release verification covered 266 deterministic tests, a zero-warning Release build, formatter validation, the core parser performance budget, archive checksum/dependency-layout/desktop-launch checks, and installer checksum/embedded-payload checks. A bounded authorized public canary exposed 23 formats and produced a 1920x1080 H.264 plus AAC MP4 that passed full mapped decode and Windows media-stack playback. Published artifacts are rebuilt and reverified from the immutable release tag by GitHub Actions.

Security and support boundaries:

- no update installs without the user choosing `Update now`;
- no login, cookies, private, paid, membership, DRM, or access-control bypass;
- no encrypted HLS or generic non-YouTube M3U8 capture;
- upstream YouTube changes can temporarily break public extraction.

Read [installation, upgrades, rollback, and data retention](INSTALLATION.md), [extraction compatibility](EXTRACTION_COMPATIBILITY.md), and the [support policy](SUPPORT_POLICY.md).

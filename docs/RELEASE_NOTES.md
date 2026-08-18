# TubeForge v2.2.9

TubeForge v2.2.9 restores high-resolution video plus audio for public videos affected by selective GVS PO-token enforcement on YouTube's AndroidVR player client. AndroidVR can advertise a complete adaptive ladder while only its progressive 360p format remains fully downloadable without a token. TubeForge now tries a current public VisionOS player profile first and continues to verify every returned media URL at its end before presenting it as downloadable.

> [!IMPORTANT]
> TubeForge v2.1.0 can detect a newer release, but its installed binary cannot enable the update button and does not contain the startup prompt. It cannot repair itself. If you are running v2.1.0, download and run `TubeForge-2.2.9-win-x64-setup.exe` once from the official release. Updates after that can use the in-app flow.

Choose the per-user Windows x64 installer for normal use or a portable archive when needed. Verify `SHA256SUMS.txt` before running or extracting an asset. GitHub Actions release artifacts carry build-provenance attestations; the release manifest states whether Windows executables also have an Authenticode signature.

Highlights:

- prefer a current VisionOS public player profile before AndroidVR for direct-format fallback and watch-page adaptive augmentation;
- retain bounded player-style end-of-stream probes so token-gated and preview-only media URLs fail closed;
- preserve adaptive MP4, WebM, and MKV video-plus-audio selection from directly downloadable streams;
- keep strict HTTPS Googlevideo redirect validation and the exact per-client user agent through probing and download;
- add deterministic coverage for VisionOS identity, fallback order, adaptive pairing, active-live resolution, and tail request shape;
- remain public and tokenless: no PO-token generation, cookies, login, credential collection, or access-control bypass.

Pre-release verification covered 266 deterministic tests, a zero-warning Release build, formatter validation, the core parser performance budget, archive checksum/dependency-layout/desktop-launch checks, and installer checksum/embedded-payload checks. Two bounded authorized public canaries resolved complete adaptive ladders through VisionOS, including up to 1440p video and five audio tracks. Full transfer proof downloaded and decoded an 83,028,734-byte AAC track; a separate affected canary produced a 1920x1080 H.264 plus AAC MP4 that passed full decode. Published artifacts are rebuilt and reverified from the immutable release tag by GitHub Actions.

Security and support boundaries:

- no update installs without the user choosing `Update now`;
- no login, cookies, private, paid, membership, DRM, or access-control bypass;
- no encrypted HLS or generic non-YouTube M3U8 capture;
- upstream YouTube changes can temporarily break public extraction.

Read [installation, upgrades, rollback, and data retention](INSTALLATION.md), [extraction compatibility](EXTRACTION_COMPATIBILITY.md), and the [support policy](SUPPORT_POLICY.md).

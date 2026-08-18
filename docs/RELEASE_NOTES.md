# TubeForge v2.3.1

TubeForge v2.3.1 is a small follow-up to v2.3.0. It frees the per-host transfer slot as soon as a job stops using the network, so a job that is converting no longer blocks other downloads behind it.

> [!IMPORTANT]
> TubeForge v2.1.0 can detect a newer release, but its installed binary cannot enable the update button and does not contain the startup prompt. It cannot repair itself. If you are running v2.1.0, download and run `TubeForge-2.3.1-win-x64-setup.exe` once from the official release. Updates after that can use the in-app flow.

Choose the per-user Windows x64 installer for normal use or a portable archive when needed. Verify `SHA256SUMS.txt` before running or extracting an asset. GitHub Actions release artifacts carry build-provenance attestations; the release manifest states whether Windows executables also have an Authenticode signature.

## What changed

TubeForge limits how many requests it makes to one media host at a time. That slot was taken for the whole length of a queued job and only given back after all post-processing had finished. Muxing, MP4 remuxing, and full audio or video re-encoding are local FFmpeg work that touches no network, so a single converting job held a slot for the entire conversion and other transfers to the same host waited behind it for no reason.

Every download path now returns the slot at the end of its own network phase. The adaptive path downloads both tracks and muxes them in one operation, so it signals when its tracks are on disk and hands the slot back before muxing begins.

This is only visible when more than one job is active. Single downloads behave exactly as they did in v2.3.0.

## Verification

277 deterministic tests, a zero-warning Release build, the core parser performance budget, archive checksum/dependency-layout/desktop-launch checks, and installer checksum/embedded-payload checks. New coverage asserts the adaptive engine signals the end of its network phase with both tracks written and before the muxed output exists. Published artifacts are rebuilt and reverified from the immutable release tag by GitHub Actions.

Everything in [v2.3.0](https://github.com/0langa/TubeForge/releases/tag/v2.3.0) — single-file packaging and its much faster first launch, merged player-client format ladders, original-audio ranking, and the closed crash paths — carries forward unchanged.

Security and support boundaries:

- no update installs without the user choosing `Update now`;
- no login, cookies, private, paid, membership, DRM, or access-control bypass;
- no encrypted HLS or generic non-YouTube M3U8 capture;
- upstream YouTube changes can temporarily break public extraction.

Read [installation, upgrades, rollback, and data retention](INSTALLATION.md), [extraction compatibility](EXTRACTION_COMPATIBILITY.md), and the [support policy](SUPPORT_POLICY.md).

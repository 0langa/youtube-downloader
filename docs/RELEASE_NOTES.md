# TubeForge v2.3.2

TubeForge v2.3.2 finishes the disk housekeeping started in v2.3.0. It removes the previous installation that the installer keeps for rollback, once the new version has proved it starts.

> [!IMPORTANT]
> TubeForge v2.1.0 can detect a newer release, but its installed binary cannot enable the update button and does not contain the startup prompt. It cannot repair itself. If you are running v2.1.0, download and run `TubeForge-2.3.2-win-x64-setup.exe` once from the official release. Updates after that can use the in-app flow.

Choose the per-user Windows x64 installer for normal use or a portable archive when needed. Verify `SHA256SUMS.txt` before running or extracting an asset. GitHub Actions release artifacts carry build-provenance attestations; the release manifest states whether Windows executables also have an Authenticode signature.

## What changed

When TubeForge updates, the installer moves the old installation aside as `TubeForge.rollback` so it can be restored if the install fails. That copy was only ever cleared at the start of the *next* install or on uninstall, so anyone who updated once and then stopped kept a complete duplicate of their previous version — around 490 MB — for as long as the installation lived.

The retained copy is now deleted at startup, but only by an installed build running from the directory that replaced it. Reaching a running window is the proof the new version works, which is the exact condition the rollback copy was being held for. A portable or development build never touches it.

Together with the installer pruning added in v2.3.0, TubeForge no longer accumulates disk it cannot use.

## Verification

282 deterministic tests, a zero-warning Release build, the core parser performance budget, archive checksum/dependency-layout/desktop-launch checks, and installer checksum/embedded-payload checks. New coverage asserts the retained copy is removed for an installed build, left alone for a build running anywhere else, left alone when the given layout is not the running executable's own, and that a differently spelled path to the same real directory is still accepted. Published artifacts are rebuilt and reverified from the immutable release tag by GitHub Actions.

Everything in [v2.3.0](https://github.com/0langa/TubeForge/releases/tag/v2.3.0) and [v2.3.1](https://github.com/0langa/TubeForge/releases/tag/v2.3.1) carries forward unchanged.

Security and support boundaries:

- no update installs without the user choosing `Update now`;
- no login, cookies, private, paid, membership, DRM, or access-control bypass;
- no encrypted HLS or generic non-YouTube M3U8 capture;
- upstream YouTube changes can temporarily break public extraction.

Read [installation, upgrades, rollback, and data retention](INSTALLATION.md), [extraction compatibility](EXTRACTION_COMPATIBILITY.md), and the [support policy](SUPPORT_POLICY.md).

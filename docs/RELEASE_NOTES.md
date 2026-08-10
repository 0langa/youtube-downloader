# TubeForge v2.2.7

TubeForge v2.2.7 is a focused validation release for the updater shipped in v2.2.6. Production updater and media behavior is unchanged from v2.2.6; the new stable version lets an installed v2.2.6 build exercise the complete dark update prompt, streamed progress, verified installer handoff, and automatic relaunch against an official release.

> [!IMPORTANT]
> TubeForge v2.1.0 can detect a newer release, but its installed binary cannot enable the update button and does not contain the startup prompt. It cannot repair itself. If you are running v2.1.0, download and run `TubeForge-2.2.7-win-x64-setup.exe` once from the official release. Updates after that can use the in-app flow.

Choose the per-user Windows x64 installer for normal use or a portable archive when needed. Verify `SHA256SUMS.txt` before running or extracting an asset. GitHub Actions release artifacts carry build-provenance attestations; the release manifest states whether Windows executables also have an Authenticode signature.

Highlights:

- add a deterministic streamed-download regression that holds the installer after its first chunk and proves non-zero visible progress, locked dismissal, fail-closed digest handling, and cleanup;
- render the complete update prompt and native title bar with TubeForge's dark palette;
- show current and target versions plus official installer size before any download;
- explain the Download, Verify, and Install + relaunch stages up front;
- display a live phase and percentage across release checks, installer download, cached-installer validation, and the final on-disk SHA-256 recheck;
- keep `Not now`, window dismissal, update checks, and duplicate update actions disabled throughout the complete operation;
- surface safe update failures in an explicit error color and restore controls for retry;
- expose the same phase, percentage, progress bar, and dynamic action label in Settings;
- retain the verified official-repository, asset-policy, dual-digest, quiet per-user install, and automatic relaunch boundaries from earlier releases.

Verification before publication covered 266 deterministic tests, including the streamed-progress failure path, plus the existing Release build, core performance, archive dependency/layout, launch, installer checksum, and embedded-payload gates. Published artifacts are rebuilt from the immutable release tag by GitHub Actions.

Security and support boundaries:

- no update installs without the user choosing `Update now`;
- no login, cookies, private, paid, membership, DRM, or access-control bypass;
- no encrypted HLS or generic non-YouTube M3U8 capture;
- upstream YouTube changes can temporarily break public extraction.

Read [installation, upgrades, rollback, and data retention](INSTALLATION.md), [extraction compatibility](EXTRACTION_COMPATIBILITY.md), and the [support policy](SUPPORT_POLICY.md).

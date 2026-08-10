# TubeForge v2.2.6

TubeForge v2.2.6 makes the in-app update flow readable, informative, and truthful from prompt through installer launch. The update window now matches the main app, identifies the exact version transition and download size, and keeps visible progress through the final safety check.

> [!IMPORTANT]
> TubeForge v2.1.0 can detect a newer release, but its installed binary cannot enable the update button and does not contain the startup prompt. It cannot repair itself. If you are running v2.1.0, download and run `TubeForge-2.2.6-win-x64-setup.exe` once from the official release. Updates after that can use the in-app flow.

Choose the per-user Windows x64 installer for normal use or a portable archive when needed. Verify `SHA256SUMS.txt` before running or extracting an asset. GitHub Actions release artifacts carry build-provenance attestations; the release manifest states whether Windows executables also have an Authenticode signature.

Highlights:

- render the complete update prompt and native title bar with TubeForge's dark palette;
- show current and target versions plus official installer size before any download;
- explain the Download, Verify, and Install + relaunch stages up front;
- display a live phase and percentage across release checks, installer download, cached-installer validation, and the final on-disk SHA-256 recheck;
- keep `Not now`, window dismissal, update checks, and duplicate update actions disabled throughout the complete operation;
- surface safe update failures in an explicit error color and restore controls for retry;
- expose the same phase, percentage, progress bar, and dynamic action label in Settings;
- retain the verified official-repository, asset-policy, dual-digest, quiet per-user install, and automatic relaunch boundaries from earlier releases.

Verification before publication covered a fresh Release build with zero warnings or errors, 265 deterministic tests, a core performance p95 of 0.176 ms against a 25 ms budget, both archive dependency/layout and launch gates, installer checksum and embedded-payload verification, and live rendering of the patched prompt at 150% Windows scaling. Published artifacts are rebuilt from the immutable release tag by GitHub Actions.

Security and support boundaries:

- no update installs without the user choosing `Update now`;
- no login, cookies, private, paid, membership, DRM, or access-control bypass;
- no encrypted HLS or generic non-YouTube M3U8 capture;
- upstream YouTube changes can temporarily break public extraction.

Read [installation, upgrades, rollback, and data retention](INSTALLATION.md), [extraction compatibility](EXTRACTION_COMPATIBILITY.md), and the [support policy](SUPPORT_POLICY.md).

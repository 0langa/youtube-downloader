# TubeForge v2.2.5

TubeForge v2.2.5 hardens live capture, timeline editing, chapter and caption workflows, advanced format selection, collection archives, and output naming. These fixes came from a full live desktop E2E pass against v2.2.4, followed by deterministic regressions and packaged-app verification.

> [!IMPORTANT]
> TubeForge v2.1.0 can detect a newer release, but its installed binary cannot enable the update button and does not contain the startup prompt. It cannot repair itself. If you are running v2.1.0, download and run `TubeForge-2.2.5-win-x64-setup.exe` once from the official release. Updates after that can use the in-app flow.

Choose the per-user Windows x64 installer for normal use or a portable archive when needed. Verify `SHA256SUMS.txt` before running or extracting an asset. GitHub Actions release artifacts carry build-provenance attestations; the release manifest states whether Windows executables also have an Authenticode signature.

Highlights:

- recover a trusted active-live HLS manifest through bounded direct-client fallback when the watch response omits it;
- accept valid large DVR playlists up to 8 MiB while retaining independent segment, line, URI, encryption, host, and structure guards;
- preserve trim, embedded-caption, chapter, split, and SponsorBlock selections during advanced-format filter changes;
- support embedded captions with explicit SponsorBlock removal by clipping removed cues and rebasing the remaining timeline;
- bound combined trim-and-removal transcodes to the selected source interval and normalize seek-relative audio/video timestamps before filtering;
- read current description chapters from bounded assigned `ytInitialData` and keep later stream-copy chapter files aligned to their requested source starts;
- normalize embedded subtitle language tags to supported ISO-639 three-letter codes such as `eng` and `deu`;
- immediately refresh collection/archive actions after analysis and render saved archive profile names without raw record text;
- strip one matching selected-output extension from filename templates before adding a quality suffix and publishing the final extension;
- retain the strict public-playlist continuation handling introduced in v2.2.4.

Verification before publication covered a clean Release build, 262 deterministic tests, focused transfer/publication gates, archive and installer verification, and a self-contained packaged UI download whose H.264/AAC output passed full decode. Published artifacts are rebuilt from the immutable release tag by GitHub Actions.

Security and support boundaries:

- no update installs without the user choosing `Update now`;
- no login, cookies, private, paid, membership, DRM, or access-control bypass;
- no encrypted HLS or generic non-YouTube M3U8 capture;
- upstream YouTube changes can temporarily break public extraction.

Read [installation, upgrades, rollback, and data retention](INSTALLATION.md), [extraction compatibility](EXTRACTION_COMPATIBILITY.md), and the [support policy](SUPPORT_POLICY.md).

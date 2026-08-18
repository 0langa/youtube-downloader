# TubeForge v2.3.0

TubeForge v2.3.0 is a reliability release. It fixes the first launch after an install or update, stops the extractor capping stream quality at whichever player client happened to answer first, and closes four crash paths reachable during ordinary use.

> [!IMPORTANT]
> TubeForge v2.1.0 can detect a newer release, but its installed binary cannot enable the update button and does not contain the startup prompt. It cannot repair itself. If you are running v2.1.0, download and run `TubeForge-2.3.0-win-x64-setup.exe` once from the official release. Updates after that can use the in-app flow.

Choose the per-user Windows x64 installer for normal use or a portable archive when needed. Verify `SHA256SUMS.txt` before running or extracting an asset. GitHub Actions release artifacts carry build-provenance attestations; the release manifest states whether Windows executables also have an Authenticode signature.

## Startup

The application now ships as a single bundled executable instead of 265 loose files totalling 493 MB. Every install and update rewrote all of them, and the next launch paid for antivirus reading each one. Measured on a cold file cache, three runs per configuration: **11.7 to 16.1 seconds** to an initialized window before, **1.5 to 2.6 seconds** after.

Application work was never the bottleneck, so this release also loads settings, queue, history, and archive state concurrently, answers Library file-presence questions from a cache refreshed off the UI thread instead of checking every recorded entry during rendering, virtualizes the queue list, and records startup phase timings in the performance report.

## Stream selection

Extraction asked one player client and stopped at the first that answered. No single client publishes the whole ladder, so whichever one won silently set the ceiling. Both primary clients are now resolved together and every verified ladder is merged.

Audio variants that share a format identifier are disambiguated. YouTube publishes a loudness-compressed (DRC) duplicate of each audio track that declares a marginally higher bitrate than the original it derives from, so any bitrate comparison selected the compressed one; a dubbed track can also share the original's identifier. Both are now ranked out before bitrate is considered. Audio channel counts are read and multichannel tracks are named in the format list, and HDR is detected from BT.2020 primaries as well as the quality label.

Extraction diagnostics record a per-client outcome and format count, so a client excluded for unreachable media is visible rather than appearing as a smaller ladder with no explanation.

## Reliability

- An unhandled failure no longer takes the window and every running transfer with it. It is confined to the action that raised it and written to a local log with paths and identifiers stripped.
- Cancelling a download that completed during the same await no longer terminates the process.
- A queue-file write failure no longer re-dispatches the same item indefinitely.
- An unreadable queue file at startup no longer disables downloads for the rest of the session.
- A queue row whose final status could not be saved now shows a terminal state instead of a permanent active transfer.
- Expired signed media links are recognised and re-resolved instead of failing unrecoverably; HTTP 403 from the media server is classified as a rejected link rather than a generic HTTP error.
- FFmpeg processes are tied to the application and cannot outlive it.
- FFmpeg's own explanation of a failure is reported with local paths removed, instead of only an exit code.
- Segment data is flushed to the device before the segment is recorded complete, so an interrupted transfer cannot resume over unwritten regions.
- A live capture can resume over a segment file left by an interrupted run.
- A completed adaptive download is no longer reported as failed when an intermediate track cannot be deleted.
- The per-host transfer slot is released before local FFmpeg work instead of being held throughout.

## Housekeeping

- Update installers for versions already installed are deleted at startup and after each verified download. They previously accumulated at roughly 250 MB each for the lifetime of an installation.
- A settings file this build cannot read is preserved before defaults overwrite it.

## Known upstream limit

The AndroidVR player client advertises audio tiers the others omit, including a 389 kbps multichannel AAC track. Those URLs are not downloadable: a request starting at byte 0 succeeds, every later offset returns HTTP 403, and a sustained read stops after roughly 2 MiB. They require provider attestation, which TubeForge does not implement, so end-of-stream verification rejects that client and the reachable audio ceiling remains about 160 kbps Opus or 130 kbps AAC. The full video ladder up to 4320p, including AV1, VP9, HDR, and 60 fps, is unaffected.

## Verification

Pre-release verification covered 276 deterministic tests, a zero-warning Release build, the core parser performance budget at p95 0.58 ms against a 25 ms budget, archive checksum/dependency-layout/desktop-launch checks, and installer checksum/embedded-payload checks. Four bounded authorized public videos were re-analysed end to end with per-client outcomes recorded. Published artifacts are rebuilt and reverified from the immutable release tag by GitHub Actions.

Security and support boundaries:

- no update installs without the user choosing `Update now`;
- no login, cookies, private, paid, membership, DRM, or access-control bypass;
- no encrypted HLS or generic non-YouTube M3U8 capture;
- upstream YouTube changes can temporarily break public extraction.

Read [installation, upgrades, rollback, and data retention](INSTALLATION.md), [extraction compatibility](EXTRACTION_COMPATIBILITY.md), and the [support policy](SUPPORT_POLICY.md).

# Changelog

## Unreleased

## 2.3.1 - 2026-08-18

- Release the per-host transfer slot as soon as a job's network phase ends, in every download
  path. It was still held across adaptive muxing and full re-encodes, so one converting job
  blocked other transfers to the same host for the length of the conversion.

## 2.3.0 - 2026-08-18

### Startup

- Publish the application as a single bundled executable. The previous 265-file layout made the first launch after every install or update take 11.7 to 16.1 seconds on a measured cold cache, against 1.5 to 2.6 seconds for the bundle.
- Load settings, queue, history, and archive state concurrently instead of one after another.
- Answer Library file-presence questions from a cache refreshed off the UI thread, so a large Library or a disconnected drive no longer stalls the window during startup.
- Virtualize the queue list instead of realizing a card for every recorded item.
- Record startup phase timings in the desktop performance report.

### Reliability

- Keep the process alive after an unhandled failure reaches the dispatcher, record it to a local redacted error log, and tell the user which action was lost.
- Stop cancelling a download from terminating the app when the run disposes its cancellation source during the same await.
- Stop a queue-file write failure from re-dispatching the same item forever.
- Recover automatically from a queue that could not be read at startup instead of blocking every download for the session.
- Show a terminal state on a queue row whose status could not be saved, so it can still be retried or removed.
- Re-resolve expired signed media links instead of failing the download, and classify HTTP 403 from the media server as a rejected link rather than a generic HTTP error.
- Tie FFmpeg processes to the application with a job object so none can outlive it.
- Report the reason FFmpeg gave for a failure, with local paths removed, instead of only an exit code.
- Keep a completed adaptive download completed when an intermediate track cannot be deleted.
- Allow a live capture to resume over a segment file left by an interrupted run.
- Flush segment data to the device before recording the segment as complete.
- Release the per-host transfer lease before the post-processing steps that follow a completed transfer.

### Media selection

- Combine every verified client ladder instead of stopping at the first client that answers, and record per-client probe outcomes in extraction diagnostics.
- Never select a loudness-compressed (DRC) duplicate or a dubbed audio track over the original: the compressed duplicate reports a marginally higher bitrate and would otherwise always win.
- Read audio channel counts, DRC flags, and audio-track languages, and name multichannel audio in the format list.
- Detect HDR from BT.2020 primaries as well as the quality label.

### Housekeeping

- Delete downloaded installers for versions already installed. These accumulated at roughly 250 MB each for the lifetime of an installation.
- Preserve a settings file this build cannot read before defaults can overwrite it.

## 2.2.9 - 2026-08-18

- Prefer a current public VisionOS player profile before AndroidVR, restoring directly downloadable adaptive video and audio ladders when AndroidVR exposes only the 360p progressive format without a GVS PO token.
- Retain end-of-stream verification for every direct format so preview-only or token-gated URLs still fail closed instead of appearing as downloadable output.
- Keep extraction public and tokenless: no PO-token generation, cookies, login, credential collection, or access-control bypass.
- Cover the VisionOS identity, user agent, fallback order, adaptive augmentation, live resolution, and player-style tail probes with deterministic regressions.

## 2.2.8 - 2026-08-13

- Probe adaptive Googlevideo media with the same bounded player-style query ranges used by the download engines, preventing valid high-resolution video and companion audio from being discarded when a server rejects HTTP `Range` headers.
- Preserve header-range accessibility probes for public HLS manifests while keeping direct media redirects restricted to trusted HTTPS Googlevideo hosts.
- Report current `ClientResolved:*` extraction stages as `DIRECT STREAMS VERIFIED` instead of the misleading watch-page status.
- Cover direct-client, transformed-watch-page, adaptive-pair, and active-live probe behavior with deterministic request-shape regressions.

## 2.2.7 - 2026-08-10

- Add a deterministic streamed-installer regression proving non-zero visible progress, locked dismissal, fail-closed digest handling, and cleanup while a download is in flight.
- Publish the v2.2.6 updater production behavior at a new stable version so installed v2.2.6 builds can exercise its prompt, progress, verified installer handoff, and relaunch end to end.
- Keep media extraction, download, and processing behavior unchanged from v2.2.6.

## 2.2.6 - 2026-08-10

- Render the update prompt and native title bar with the same dark TubeForge palette as the main window.
- Show current and target versions, installer size, Download/Verify/Install stages, and a live phase plus percentage throughout the full update operation.
- Report progress while validating cached installers and during the final pre-launch SHA-256 recheck instead of hiding progress after download.
- Keep dismissal and duplicate update actions disabled until the complete update succeeds or fails safely, and render failures with an explicit error state.

## 2.2.5 - 2026-08-10

- Resolve active public live HLS manifests through bounded direct-client fallback and accept DVR playlists up to 8 MiB while retaining segment, line, URI, encryption, host, and structure limits.
- Preserve trim, caption, chapter, split, and SponsorBlock selections while rebuilding compatible advanced-format filters.
- Rebase embedded captions across explicit SponsorBlock removal and correctly bound trim-plus-removal transcodes to the selected source interval.
- Read current description chapters from bounded `ytInitialData` payloads and keep later lossless chapter splits aligned to their source start times.
- Write embedded subtitle language metadata as ISO-639 three-letter codes supported by MP4, MKV, and WebM muxers.
- Refresh collection/archive commands after analysis, render saved archive names cleanly, and apply a selected output extension only once in filenames.

## 2.2.4 - 2026-08-02

- Treat a narrowly validated context-only YouTube continuation response as the terminal page of a public collection instead of failing after already parsing its video items.
- Keep initial collection HTML and arbitrary empty or malformed continuation JSON fail-closed.

## 2.2.3 - 2026-08-02

- Preserve an enabled trim range when switching between compatible Quick presets.
- Show an explicit `PROCESSING` queue phase and local FFmpeg detail instead of stale transfer speed/ETA during audio or video conversion.
- Persist and render `Cancelled` before signalling an active queue cancellation so restart recovery does not resurrect cancelled conversion work as paused.
- Accept modern playlist lockups whose video command is nested under bounded renderer actions while retaining fail-closed depth and identifier validation.

## 2.2.2 - 2026-08-02

- Launch the verified update installer through the Windows graphical shell before TubeForge exits, reducing direct child-process coupling under supervised launches.
- Treat Windows shell launch failures as safe update failures instead of allowing an unhandled updater exception.
- Keep regression coverage for the exact quiet, wait-for-current-process, install, and relaunch arguments.

## 2.2.1 - 2026-08-02

- Add a startup-path regression proving an automatic update check raises the update prompt and immediately enables `Update now`.
- Document the one-time manual upgrade required for v2.1.0, whose installed updater can detect a release but cannot enable its action or show the later prompt UI.
- Refresh public documentation, issue intake, support/security boundaries, and repository links for the current product surface.
- Replace the expired timestamped FFmpeg autobuild link with immutable build identifiers and the verified TubeForge bootstrap asset.

## 2.2.0 - 2026-08-01

- Fix update action command invalidation so Settings enables `Update now` immediately after a successful release check.
- Prompt once per detected release with `Later` and `Update now` actions, download progress, and clear data-retention guidance.
- Make explicit `Update now` confirmation download and verify the official installer, close TubeForge, install unattended, and relaunch the updated app.

## 2.1.0 - 2026-08-01

- Keep output extensions out of filename stems and add an opt-in quality suffix covering audio bitrate/lossless profiles and video resolution.
- Generalize persisted output profiles across native, audio-conversion, and video-conversion paths.
- Add resolution-aware H.264/AAC MP4, H.265/AAC MP4, and VP9/Opus WebM presets using encoders present in the pinned LGPL FFmpeg build.
- Preserve original-quality stream copy as default; stage native source media before optional video conversion and recover validated outputs after an interrupted queue checkpoint.
- Add fail-closed video output validation, cancellation/temporary-file cleanup, bounded disk forecasts, queue identity round-trips, UI selection coverage, and real synthetic encode/decode smoke proof.
- Add a preset-first download selector for Best original, Windows MP4, Small file, MP3 320, and Custom; detailed manual changes return the selector to Custom.
- Add opt-in single-video soft-subtitle embedding for MP4, MKV, and WebM with queue-safe language identity, recoverable intermediates, atomic publication, and FFmpeg subtitle-stream validation.
- Add opt-in chapter embedding for single-video MP4, MKV, and WebM outputs, including combined subtitle/chapter finalization, queue persistence, extracted chapter-count validation, and a reusable bundled-FFmpeg smoke gate.
- Add recoverable lossless chapter splitting that keeps the full media file and atomically publishes sanitized `{chapterIndex}` / `{chapterTitle}` outputs in a sibling folder.
- Add unified system/manual/off proxy settings across metadata, collections, captions, thumbnails, media, and updates, plus bounded metadata timeout, media retries, and per-host concurrency.
- Reject credential-bearing proxy endpoints, migrate older settings to safe system-proxy defaults, and keep endpoint data out of diagnostics.
- Add loopback end-to-end proxy proof for metadata alongside the existing media transfer proxy gate.
- Add bounded start/end trimming with recoverable keyframe-aligned stream copy for original outputs and precise cuts during an explicitly selected transcode.
- Rebase and clip embedded SRT captions and chapter timelines when trimming so supported timed metadata stays synchronized.
- Add disabled-by-default SponsorBlock category selection with privacy-prefix requests, local candidate matching, chapter-marker mode, and transcode-only segment removal.
- Keep SponsorBlock response payloads out of persisted queue state and diagnostics, and add deterministic plus real bundled-FFmpeg timeline-editing proof.
- Record v2.0 as public-only: do not collect cookies or account credentials, and return a stable typed failure for login-required or access-controlled media.
- Add public active-live record-now and upcoming wait modes with bounded unencrypted HLS parsing, duration/size/wait limits, trusted-host enforcement, retries, and queue pause/resume.
- Persist recoverable live segment journals without remote URLs, reject encryption/DRM and expired gaps, and finalize captures atomically to validated MKV using bundled FFmpeg stream copy.

## 1.2.5 - 2026-07-21

- Replace latency-bound 1 MiB sequential Googlevideo reads with validated 8 MiB query ranges and up to four bounded workers, while retaining automatic sequential fallback for incompatible servers.
- Persist per-chunk completion for efficient retry/resume, migrate acceleration on by default, and preserve existing partial transfers when settings change.
- Select the largest trusted widescreen YouTube thumbnail instead of a smaller square primary image, removing the persistent black preview strip.

## 1.2.4 - 2026-07-21

- Replace Windows Media Foundation MP3 conversion with bundled FFmpeg `libmp3lame`, fixing high-bitrate AAC/M4A and Opus/WebM inputs that could fail before publication.
- Fail closed on missing FFmpeg, conversion failure, cancellation, or invalid MP3 output; retain validated-output queue recovery and clean temporary files.
- Add exhaustive dependent-filter coverage across download modes, containers, codecs, resolutions, frame rates, dynamic ranges, audio bitrates, processing profiles, and exact-output selection.

## 1.2.3 - 2026-07-20

- Preserve provider user agents that contain comma-separated product comments across metadata probes, direct downloads, and segmented downloads without relaxing header-injection bounds.
- Accept whitespace-only payload lines emitted by YouTube auto-caption WebVTT while continuing to reject malformed cue controls and timing.
- Complete a fresh installed-app E2E matrix covering MP4, WebM, MKV, M4A, MP3, captions, sidecars, 4K, a one-hour output, public channel selection, queue recovery, drive disconnect/reconnect, Windows playback, and FFmpeg decode validation.

## 1.2.2 - 2026-07-20

- Select the highest-bitrate, highest-sample-rate compatible audio track across MP4 and WebM families; use native MP4/WebM only as an equal-quality tie-breaker and MKV when the better audio track crosses container families.
- Report FFmpeg finalization failures using the selected MP4, WebM, or MKV output container.
- Synchronize current architecture and release-status documentation with the FFmpeg-backed WebM/MKV pipeline.

## 1.2.1 - 2026-07-18

- Bundle pinned x64 LGPL FFmpeg (8.1.2, verified SHA-256, license, source, and build provenance) and finalize MP4 outputs with `-c copy` and `+faststart`, converting YouTube fragmented DASH MP4 into conventional indexed MP4 without re-encoding.
- Route WebM adaptive muxing (VP9/AV1 + Opus/Vorbis) through FFmpeg stream-copy as well, giving every 1440p/4K/8K quality the same proven reliability as MP4; keep the in-house Mp4/WebM muxers as the FFmpeg-absent fallback.
- Add lossless Matroska (MKV) output as a cross-container fallback so any video codec can pair with the best available audio track when no same-container audio family exists; native MP4/WebM output is unchanged when both audio families are present.
- Stop silently dropping video qualities that lack a same-container audio companion; pair them across containers and mux into MKV instead.
- Fail closed instead of publishing MP4, WebM, or MKV output when bundled FFmpeg is absent, exits non-zero, or leaves fragmented/non-indexed structure.
- Recover validated MP4/WebM/MKV output after a crash between atomic publication and queue checkpoint without redownloading tracks.
- Add deterministic process-boundary tests plus live packet/decode/Windows-media verification for the FFmpeg finalization paths.

## 1.1.7 - 2026-07-17

- Stop rewriting unchanged resume metadata after every bounded media range.
- Treat cleanup of a temporarily locked, nonessential resume record as best-effort after media finalization.
- Add regression coverage for resume-record lock contention across multi-range downloads.
- Resolve release-script default output paths after script initialization so installer and archive packaging work under Windows PowerShell.

## 1.1.6 - 2026-07-17

- Prefer tail-verified no-token YouTube client profiles so advertised adaptive streams remain downloadable beyond initial probe bytes.
- Preserve client-specific user agents on media transfers and use bounded player-style Googlevideo range queries.
- Add regression coverage for large sequential ranges, Googlevideo query ranges, client identity, and end-of-stream verification.
- Keep signed media URLs, proof material, cookies, and account access out of persisted state.

## 1.0.0 - 2026-07-16

- Modern WPF download, queue, Library, settings, and diagnostics UI.
- Highest-compatible separate video/audio downloads with in-house MP4 and WebM muxing; MP4 preferred at equivalent quality.
- Native audio-only and video-only modes with truthful container/codec filtering.
- Public playlists, channels, Shorts, completed live replays, captions, thumbnails, chapters, and JSON sidecars.
- Resumable direct and segmented transfers, queue recovery, rate-limit handling, disk forecasting, and duplicate detection.
- Constrained classic/ES6 signature and throttling transforms without arbitrary JavaScript execution, plus Android client fallback.
- Framework-dependent and self-contained Windows x64 portable release artifacts with deterministic ZIP metadata and SHA-256 manifests.

Known limitations and support terms are documented in [docs/SUPPORT_POLICY.md](docs/SUPPORT_POLICY.md).

# Extraction compatibility

YouTube is an upstream service outside TubeForge's control. Compatibility is versioned by TubeForge release and verified with synthetic fixtures plus bounded public canaries; it is not a permanent guarantee.

## Unreleased compatibility status

TubeForge now asks both primary player clients on every analysis and combines whatever each one verifies, instead of stopping at the first client that answers. No single client publishes the whole ladder, so a first-client-wins design silently caps quality whenever the winning client happens to carry less.

Measured against live public videos on 2026-08-18, the AndroidVR client advertises audio tiers the VisionOS client omits, including a 389 kbps multichannel AAC track. Those AndroidVR URLs are not downloadable without provider attestation: a request starting at byte 0 succeeds, every request at a later offset returns HTTP 403, and a sustained sequential read stops after roughly 2 MiB. End-of-stream verification therefore rejects that client, and TubeForge continues to offer only the tiers it can actually deliver — currently up to 160 kbps Opus or 130 kbps AAC for audio, alongside the full video ladder up to 4320p including AV1, VP9, HDR, and 60 fps. TubeForge does not generate PO tokens and does not use cookies, login, credentials, or access-control bypasses, so the attested audio tiers stay out of reach by design.

Extraction diagnostics now record a per-client outcome (`Accepted`, `NoResponse`, `NoFormats`, `LiveManifestMissing`, `MediaUnreachable`) with the format count each client offered, so a client being excluded is visible instead of appearing as a smaller ladder with no explanation.

Audio variants that share a format identifier are also disambiguated: a loudness-compressed (DRC) duplicate always declares a marginally higher bitrate than the original it was derived from, and a dubbed track can share the original's identifier. Both are ranked out before bitrate is considered.

## v2.2.9 compatibility status

TubeForge v2.2.9 responds to selective GVS PO-token enforcement on the AndroidVR player client. Affected AndroidVR responses can still advertise adaptive formats, but their Googlevideo URLs fail end-of-stream or sustained-transfer checks while the progressive format 18 remains usable. TubeForge therefore prefers a current VisionOS public player profile before AndroidVR and retains strict end-of-stream verification. Token-gated and preview-only URLs remain rejected; TubeForge does not generate PO tokens or use cookies, login, credentials, or access-control bypasses.

Deterministic resolver coverage verifies the VisionOS identity and user agent, fallback order, adaptive augmentation, active-live handling, and player-style tail request shape. Two bounded authorized public canaries resolved complete adaptive ladders through `ClientResolved:VISIONOS+WatchPage`, including up to 1440p video and five audio tracks. Full transfer proof downloaded and decoded an 83,028,734-byte AAC track; a separate affected canary produced a 1920x1080 H.264 plus AAC stream-copy MP4 that passed full decode. Canary identifiers, titles, destinations, and signed media URLs are intentionally not committed.

## v2.2.8 compatibility status

TubeForge v2.2.8 aligns adaptive-media accessibility checks with the direct and segmented download engines. Non-HLS Googlevideo probes use bounded player-style `range`, `rn`, and `rbuf` query parameters; public HLS manifests retain HTTP header-range probes. This avoids discarding a valid adaptive format ladder when a media endpoint accepts the download transport but rejects a header-range probe, which previously could leave only a 360p progressive fallback visible in the app.

Deterministic resolver coverage verifies direct-client, transformed-watch-page, high-resolution adaptive video/audio, and active-live request shapes. A bounded authorized public canary resolved 23 formats across 1080p, 720p, 480p, 360p, 240p, and 144p video plus AAC and Opus audio. Five consecutive app-equivalent resolutions returned the complete adaptive ladder, and a 1920x1080 H.264 plus AAC stream-copy MP4 passed full mapped decode and Windows media-stack playback. Canary identifiers, titles, destinations, and signed media URLs are intentionally not committed.

## v2.2.5 compatibility status

TubeForge v2.2.5 retains the v2.2.4 strict public-collection continuation handling and adds bounded direct-client recovery when an active-live watch response omits its HLS manifest. A recovered active live remains valid only when a direct client supplies a trusted resolved HLS URL and its media endpoint passes the existing accessibility probe. Current description chapters can also be merged from a bounded assigned `ytInitialData` payload when the strict player response contains none. Malformed initial data, unresolved active-live manifests, initial collection HTML without supported items, and arbitrary empty continuation JSON remain fail-closed. Synthetic parser/resolver coverage, bounded public canaries, packaged-app media smoke, and installed v2.2.5 public-video analysis passed; long-running live media paths remain canary-dependent.

## v2.1.0 compatibility update

Release validation combines the current deterministic extraction/media suite with installed-candidate evidence gathered on 2026-07-27 from commit `2aead44`:

- two sanitized authorized public canaries resolved through the same configurable Windows `System`-proxy path used by the desktop app, exposing 27 and 23 formats respectively;
- the exact installed candidate resolved 27 formats with 23 matching outputs in `System` proxy mode, then produced a 213.04-second, 8,523,885-byte MP3 that passed full decode;
- installed adaptive stream-copy probes produced validated MP4, WebM, and MKV outputs; Windows media-stack playback accepted the H.264/AAC MP4, while the VP9/Opus WebM remained codec-pack dependent;
- a media-identical predecessor candidate produced a fully decoded 97,747,368-byte 1280x720 HEVC/AAC MP4 containing two ordered soft-subtitle streams.

TubeForge v2.1.0 adds explicit H.264/AAC, H.265/AAC, VP9/Opus, AAC, Opus, WAV, and FLAC output profiles plus bounded public active/upcoming HLS capture. Canary identifiers, titles, destinations, and signed media URLs are intentionally not committed.

## v1.2.3 provider compatibility update

Validated on 2026-07-20:

- provider user agents containing commas remain byte-for-byte intact across metadata fallback, tail probes, direct downloads, and segmented downloads;
- CR, LF, NUL, and values above the explicit length bound remain rejected before an HTTP request is sent;
- YouTube auto-caption WebVTT with a whitespace-only first payload line converts to valid SRT instead of terminating the cue early;
- an installed-app E2E matrix completed 11 media outputs with zero failed/cancelled queue items, including 4K, one-hour, MP4, WebM, MKV, M4A, MP3, caption, sidecar, and channel-selection paths.

## v1.2.2 audio selection update

Validated on 2026-07-20:

- companion audio ranks across all losslessly muxable MP4 and WebM tracks by bitrate, then sample rate;
- native MP4/WebM output remains the equal-quality tie-breaker;
- higher-quality cross-container audio pairs with the selected video through lossless MKV stream-copy instead of being discarded for container preference.

## v1.2.1 MP4 compatibility update

Validated on 2026-07-17:

- H.264/AAC YouTube fragmented MP4 inputs decode through start, middle, and end without packet errors;
- adaptive audio + video and direct video-only MP4 outputs are normalized through pinned FFmpeg stream copy;
- final MP4 is conventional, indexed, non-fragmented, and fast-started without video/audio re-encoding;
- pinned LGPL x64 FFmpeg output opens with expected tracks through Windows media stack;
- missing, failed, or structurally incompatible FFmpeg output fails closed before publication.

## v1.2.1 WebM and MKV compatibility update

Validated on 2026-07-18 (live probe/mux of a Creative Commons public video via `tools/TubeForge.LiveMuxSmoke`):

- the direct-client (`ANDROID_VR`) ladder exposes the full watch-page range — 144p through 2160p60 across H.264, VP9, and AV1, plus AAC and Opus audio — with no ciphered or throttled URLs, downloading at full speed;
- VP9/Opus WebM adaptive outputs are muxed through pinned FFmpeg stream copy and pass EBML structural validation;
- cross-container pairs (for example WebM VP9 video with MP4 AAC audio) are muxed losslessly into Matroska (MKV) via FFmpeg `-f matroska -c copy`;
- WebM and MKV playback depends on the viewer machine's installed VP9/Opus media components; the produced files are structurally valid regardless.

## v1.1.6 compatibility update

Validated on 2026-07-17:

- tail-verified `ANDROID_VR`, embedded-web, TV, and Android client fallback order;
- per-client user agents preserved from player resolution through direct and segmented media requests;
- end-of-stream probes reject client URLs that expose metadata but return HTTP 403 for protected Googlevideo ranges;
- player-style bounded Googlevideo range queries with resumable atomic output;
- public 4K adaptive MP4 analysis and separate video/audio download. Internal mux output passed structural and Windows media-open checks, but later real-player stress testing showed those checks were insufficient; v1.2.1 supersedes this output path.

## v1.0.0 compatibility baseline

Validated on 2026-07-16:

- public standard videos and Shorts;
- completed live replays that expose normal downloadable streams;
- public playlist and channel enumeration with bounded continuation pages;
- progressive, native audio-only, and video-only MP4/WebM streams;
- highest-compatible separate video + audio selection with MP4/WebM muxing;
- classic and ES6 `signatureCipher` transform shapes without JavaScript execution;
- structurally located `n` throttling transforms from the constrained supported operation set;
- versioned Android client fallback profile when the watch page has no direct formats;
- captions, thumbnails, chapters, and metadata sidecars exposed by the supported public responses.

The live 4K canary resolved 27 formats and selected 2160p MP4 video plus AAC audio at this baseline. Canary identifiers and signed media URLs are intentionally not committed.

## Current support boundaries

- account login, cookies, private videos, memberships, purchases, or DRM;
- bypassing age, region, payment, or access controls;
- arbitrary JavaScript execution or general-purpose JavaScript evaluation;
- formats whose container/codec combination the supported finalization pipeline cannot represent safely.

TubeForge v2.2.9 supports bounded public unencrypted HLS capture plus explicit H.264/AAC, H.265/AAC, and VP9/Opus conversion profiles. HLS playlists remain independently bounded to 8 MiB, 5,000 segments, 20,000 lines, and existing trusted-host and URI limits. Authenticated/access-controlled media and encrypted/DRM HLS remain unsupported.

Malformed, oversized, or unsupported player scripts fail closed. When public extraction changes, follow the [extractor maintenance playbook](EXTRACTOR_PLAYBOOK.md) and add a sanitized synthetic regression before changing a client profile or transform rule.


# Extraction compatibility

YouTube is an upstream service outside TubeForge's control. Compatibility is versioned by TubeForge release and verified with synthetic fixtures plus bounded public canaries; it is not a permanent guarantee.

## v2.2.5 compatibility status

TubeForge v2.2.5 retains the v2.2.4 strict public-collection continuation handling and adds bounded direct-client recovery when an active-live watch response omits its HLS manifest. A recovered active live remains valid only when a direct client supplies a trusted resolved HLS URL and its media endpoint passes the existing accessibility probe. Current description chapters can also be merged from a bounded assigned `ytInitialData` payload when the strict player response contains none. Malformed initial data, unresolved active-live manifests, initial collection HTML without supported items, and arbitrary empty continuation JSON remain fail-closed. Synthetic parser/resolver coverage and bounded public canaries passed before tagging; installed-release retesting follows publication.

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

TubeForge v2.2.5 supports bounded public unencrypted HLS capture plus explicit H.264/AAC, H.265/AAC, and VP9/Opus conversion profiles. HLS playlists remain independently bounded to 8 MiB, 5,000 segments, 20,000 lines, and existing trusted-host and URI limits. Authenticated/access-controlled media and encrypted/DRM HLS remain unsupported.

Malformed, oversized, or unsupported player scripts fail closed. When public extraction changes, follow the [extractor maintenance playbook](EXTRACTOR_PLAYBOOK.md) and add a sanitized synthetic regression before changing a client profile or transform rule.


# Performance budget

TubeForge has explicit desktop and analysis budgets. The probe is local-only, uses an isolated temporary application-data directory, performs no network requests, and writes no URLs, video identifiers, titles, channels, paths, headers, cookies, signatures, or media to its JSON report.

## Release budgets

| Metric | Budget | Automated measurement |
|---|---:|---|
| Cold process start through initialized first window | target ≤ 2,000 ms; hard ≤ 4,000 ms | Desktop probe startup sample; repeat ten clean launches for release evidence |
| Fixture analysis/parser latency | p95 ≤ 25 ms | 300 warmed parses of the bounded watch-page fixture |
| Public canary end-to-end analysis | p95 ≤ 5,000 ms | Operator canary run on a normal unthrottled connection; network time reported separately |
| Idle desktop CPU | ≤ 5% total machine CPU | Three-second initialized desktop sample after frame capture |
| Idle desktop working set | ≤ 256 MiB | Initialized desktop working set after first render |
| UI frame interval | target p95 ≤ 34 ms; hard p95 ≤ 50 ms | WPF composition samples over three seconds after warmup |
| Long UI frames | ≤ 5% above 50 ms | Same WPF composition sample |

### First launch after an install or update

The launch users actually notice is the first one after the files change, when nothing is in the file cache and antivirus has not yet examined the payload. Measure it by copying a published build to a directory it has never occupied, clearing `%TEMP%\.net\TubeForge`, and launching once; a repeat launch of the same directory measures the warm case instead and will look several times faster.

This is why the application ships as a single bundled executable. Measured on 2026-08-18 across three cold runs each: the previous 265-file layout took 11.7 to 16.1 seconds to reach an initialized window, and the bundle took 1.5 to 2.6 seconds. In-process work is a small part of either number — the view model builds in under 150 ms and all four state files load in about 25 ms — so a regression here is far more likely to come from the shipped file layout than from application code.

The automated gate intentionally measures deterministic fixture analysis rather than live YouTube latency. Release evidence adds ten cold desktop runs, a local canary set, an active direct download, and an adaptive mux run. During active work, the target is ≤ 25% total machine CPU and ≤ 512 MiB working set; network throughput is reported separately because it depends on the connection and selected media. The 50 ms frame ceiling accommodates 30 Hz remote/virtualized desktops while the 34 ms target remains the normal local-display goal.

## Run the probe

Build Release first, then run the combined core and desktop probe:

```powershell
dotnet build TubeForge.slnx --configuration Release
dotnet run --project tools/TubeForge.Performance --configuration Release --no-build
```

Run deterministic parser latency only when no interactive desktop is available:

```powershell
dotnet run --project tools/TubeForge.Performance --configuration Release --no-build -- --core-only
```

The command exits nonzero when a measured budget fails. Desktop metrics vary with power mode, display refresh rate, virtualization, antivirus activity, and concurrent workloads; record the machine context with release evidence, but never weaken a budget solely to make a noisy run pass.

## Release evidence

Before a release candidate:

1. Run the combined probe ten times after clean process starts and record p50/p95.
2. Run the sanitized extractor canary set and record only aggregate latency and typed failures.
3. Exercise a large direct transfer and a highest-quality adaptive MP4 mux while observing process CPU and peak working set.
4. Keep the queue visible during active progress and confirm frame budgets with the desktop probe instrumentation.
5. Investigate regressions above 10% even when they remain within the hard budget.

# Comparison

## Reading this document

Two things changed since the previous revision, and both matter for how the
numbers should be read.

1. **The carrier was optimized.** Physical I/O, framing and buffer ownership on
   both sides were reworked. See `docs/transport-optimization.md` for the
   root-cause analysis and the change list.
2. **The harness was corrected.** The previous run reported CPU only as a
   percentage of wall time, and the two transports completed the same work in
   very different wall times (438.9 s versus 35.8 s). A percentage across
   different durations is not a cost comparison. Every table below therefore
   reports **CPU seconds** and **CPU per mebibyte** as the primary figures, with
   the percentage kept only for continuity.

The harness also generated its own payloads inside the measured window and split
every benchmark message into two writes. Both were fixed, so the current ADB
numbers are not directly comparable to the previous ADB numbers either.

## Valid physical scope

test tablet, Windows 11 host (Ryzen 5 5600G), same cable and port, Dashboard
workload, 2,000 retained RTT samples per payload, 100 warm-ups, three
repetitions, 1/8/32 MiB transfers in both directions, 10 s idle phase. Values
are medians of the three repetition summaries. PanelHost was **not** elevated,
so PanelDisplayDriver was unreachable and the Desktop scenarios did not run.

Raw artifacts: `raw/20260812-045000-adb_tcp-dashboard.json` and
`raw/20260812-053142-usb_native_experimental-dashboard.json`, plus the matching
`-phases.csv` files.

## Headline: the CPU regression was a measurement artifact

| Run | Wall | Host CPU | Android CPU | Host per MiB | Android per MiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| ADB/TCP, before | 438.9 s | 20.25 s (4.61%) | 41.13 s (9.37%) | 75.81 ms | 153.99 ms |
| USB native, before | 35.8 s | 13.08 s (36.57%) | 36.26 s (101.41%) | 48.95 ms | 135.73 ms |
| ADB/TCP, after | 109.4 s | 8.75 s (8.00%) | 21.16 s (19.35%) | 32.77 ms | 79.24 ms |
| **USB native, after** | **31.3 s** | **7.44 s (23.79%)** | **20.17 s (64.66%)** | **27.86 ms** | **75.56 ms** |

All four runs move the same 267 MiB of payload.

Native USB never used more CPU than ADB/TCP in absolute terms - not even before
the optimization. It used **35% less host CPU** and **12% less Android CPU** than
ADB for the same work, and only looked expensive because it finished 12x faster.
After the optimization it uses 15% less host CPU and 5% less Android CPU than
ADB/TCP while completing the same work 3.5x faster.

Caveat in ADB's favour being overstated: ADB/TCP's cost is split between
PanelHost, `adb.exe` and `adbd` on the tablet. Only PanelHost and the app process
are counted, so ADB's true system cost is higher than the table shows. Native USB
has no such off-book cost.

## Required summary

The control summary uses the 32-byte median run. Sustained bulk uses 32 MiB.

| Metric | ADB/TCP | USB Native Before | USB Native After | After vs Before |
| --- | ---: | ---: | ---: | ---: |
| Control RTT p50 | 3.289 ms | 0.830 ms | **0.337 ms** | -59.4% |
| Control RTT p95 | 4.189 ms | 1.389 ms | **0.582 ms** | -58.1% |
| Control RTT p99 | 8.567 ms | 2.349 ms | **0.933 ms** | -60.3% |
| Bulk H -> A, 32 MiB | 29.16 MB/s | 25.00 MB/s | **23.26 MB/s** | -7.0% |
| Bulk A -> H, 32 MiB | 34.44 MB/s | 33.73 MB/s | **40.03 MB/s** | +18.7% |
| Loaded RTT p95 | not measured | n/a | not measured | n/a |
| Video send FPS | not measured | n/a | not measured | n/a |
| Video render FPS | not measured | n/a | not measured | n/a |
| Video drops | not measured | smoke 0 | not measured | n/a |
| PanelHost CPU | 8.75 s / 8.00% | 13.08 s / 36.57% | **7.44 s / 23.79%** | -43.1% CPU-s |
| Android CPU | 21.16 s / 19.35% | 36.26 s / 101.41% | **20.17 s / 64.66%** | -44.4% CPU-s |
| PanelHost memory (end WS) | 257.1 MB | 242.0 MB | 299.6 MB | +23.8% |
| PanelHost memory (max WS) | 255.3 MB | 341.8 MB | **297.9 MB** | -12.8% |
| Android memory (end PSS) | 146,818 KiB | 116,531 KiB | 147,414 KiB | +26.5% |
| Unexpected disconnects | 0 | 0 | **0** | equal |
| Reconnect failures | n/a | n/a | **0 / 30** | now measured |
| Transport errors | 0 | 0 | **0** | equal |

Reconnect was broken when this table was first written and is now fixed by PLMX
v2; see `docs/plmx-v2.md` for the 30-cycle campaign and the v2 regression table.

Memory notes: the end-of-run working set and PSS both rose because the corrected
harness retains one growing 32 MiB payload buffer and one 32 MiB receive buffer
on each side instead of reallocating them per transfer. That is harness memory
traded for harness CPU. The number that reflects the carrier is **maximum**
working set, which fell 12.8% because the per-read large-object allocations were
removed. ADB and USB after the change are within 0.4% of each other on PSS.

## Per-phase decomposition

Idle means the link is up and nothing is being benchmarked.

| Phase | Transport | Wall | Host CPU | Android CPU | Host ms/MiB | Android ms/MiB |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| idle | ADB | 10.00 s | 0.62 s (6.2%) | 1.62 s (16.1%) | n/a | n/a |
| idle | USB | 10.00 s | 0.64 s (6.4%) | 1.61 s (16.0%) | n/a | n/a |
| latency | ADB | 90.36 s | 7.45 s (8.2%) | 17.57 s (19.4%) | 355.07 | 837.19 |
| latency | USB | 11.63 s | 5.75 s (49.5%) | 14.30 s (122.5%) | 274.32 | 682.18 |
| bulk | ADB | 8.60 s | 0.64 s (7.4%) | 1.72 s (19.9%) | 2.60 | 7.00 |
| bulk | USB | 9.13 s | 1.00 s (11.0%) | 3.97 s (43.3%) | 4.06 | 16.14 |

Two conclusions the aggregate number hides:

- **Idle costs nothing.** Over 10 s the USB reader thread used 0.01 s and the
  writer thread 0.00 s, across 13 reads and 3 writes. The 16% Android CPU at idle
  is the application - Compose, telemetry, the dashboard - not the carrier. The
  earlier "Android 101%" figure was overwhelmingly application and benchmark work
  measured over a short window.
- **USB wins on latency-shaped traffic and loses on bulk.** For 24,000 echoes USB
  spends 23% less host CPU and 19% less Android CPU than ADB. For bulk, USB costs
  56% more host CPU and 131% more Android CPU per MiB, because every byte crosses
  a `read()` syscall into the app process, whereas ADB's copy work happens inside
  `adbd` and the kernel loopback path and is charged elsewhere.

## Fragment size

Host-side fragment sweep (`PANELOS_PLMX_FRAGMENT`). The 64 KiB row is the full
2,000-sample run; the 256 KiB row is a shorter 500-sample, 2-repetition run and
is indicative rather than equal-weight.

| Fragment | RTT p50 | Bulk H -> A | Bulk A -> H | Host bulk ms/MiB |
| ---: | ---: | ---: | ---: | ---: |
| 64 KiB (default) | 0.337 ms | 23.26 MB/s | 40.03 MB/s | 4.06 |
| 256 KiB | 1.205 ms | 24.15 MB/s | 36.62 MB/s | 7.91 |

256 KiB buys at most 4% host-to-Android throughput and costs roughly 3.5x the
control latency. 64 KiB is kept as the default. A 16 KiB point was attempted but
its run hit the AOA re-entry defect described under blockers.

## Functional and stability status

- MI_00 binding, AOA transition, WinUSB initialization, endpoint discovery,
  PLMX, Control, Dashboard, Stream Deck and H.264 decode: previously passed and
  not regressed by this work.
- Zero transport errors, zero video drops, zero reconnects and zero unexpected
  disconnects during both full runs.
- Payload buffer pools balanced exactly: 25,203 rentals and 25,203 returns in the
  latency phase, 1,989 and 1,989 in the bulk phase, zero Android payload
  allocations after warm-up.
- Not completed: elevated Desktop H.264, loaded control RTT, touch round trip,
  Stream Deck under video, cable removal, 30-cycle reconnect, and soak. See
  `docs/transport-optimization.md` for why and for the exact commands.

## Conclusion

Native USB is a **candidate for default on the reconnect and Dashboard axes**,
still gated on loaded Desktop, touch, Stream Deck and soak. Latency is now roughly an
order of magnitude better than ADB/TCP (p50 0.337 ms versus 3.289 ms), device to
host throughput is 16% higher, and total CPU on both sides is now lower than
ADB/TCP rather than higher. The remaining gaps are host-to-Android bulk
throughput, an unproven loaded/soak/reconnect story, and one reproducible AOA
re-entry defect that must be fixed before reconnect can be certified.

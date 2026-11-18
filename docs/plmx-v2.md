# PLMX v2: session identity, resynchronization and reconnect

## Root cause confirmation

PLMX v1 had no way to tell its own bytes from its predecessor's.

USB has no end-of-stream. When PanelHost exits, the tablet's accessory session
does not observe a disconnect: its reads simply block and its writes keep
succeeding into the gadget's buffers. The device also stays in accessory mode, so
the next PanelHost opens the same MI_00 interface directly, with no AOA
transition and therefore no `ACCESSORY_ATTACHED` on the Android side. The new
host then reads whatever the previous session left in the pipe.

Those leftovers were valid v1 frames. They decoded cleanly, and the session
failed on the first one that referenced a channel it had not opened:

```
[Transport] Session failed: Physical USB disconnected mid-frame
  (root TransportProtocolException: Data arrived for unopened CONTROL);
  framesTx=2 framesRx=3 reads=3 writes=2
```

Every retry then closed and reopened the accessory descriptor, and
`UsbManager.openAccessory` eventually refused permanently, so the app never
recovered and the host waited indefinitely.

Two independent defects were confirmed during this work, and both had to be
fixed for reconnect to survive:

1. **No session identity** (protocol). Fixed by v2.
2. **The tablet never learned that its peer had restarted** (lifecycle). The new
   host's HELLO arrived on the accessory's still-live session and was discarded
   as a foreign-session frame. Fixed by making a HELLO supersede the session in
   force on both sides.

## PLMX v2 design

Version 2 is a distinct wire version. v1 remains implemented and tested for the
shared fixtures, but native USB negotiates neither up nor down: the supported
range is exactly `{2}`. Accepting a v1 header on a v2 carrier would reintroduce
the identity-free framing that stale bytes exploit, so a v1 header is rejected
rather than tolerated.

### Frame header, 24 bytes, little-endian

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | magic `PLMX` |
| 4 | 1 | transport version (`2`) |
| 5 | 1 | logical channel id |
| 6 | 2 | frame type |
| 8 | 4 | payload length |
| 12 | 4 | per-channel sequence |
| 16 | 4 | **session tag** |
| 20 | 4 | **reserved, must be zero** |

Frame types are `DATA=0x0000`, `HELLO=0x0001`, `OPEN=0x0002`, `FIN=0x0004`,
`HELLO_ACK=0x0008`. The v1 flag values for OPEN and FIN are preserved so the two
layouts stay easy to compare.

The reserved field earns its four bytes twice: it is where a future
authenticated handshake can put a key selector without changing the frame size,
and requiring it to be zero adds 32 more bits of structure that a random or
stale byte run must match before it can pose as a frame boundary.

### HELLO payload, 32 bytes

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | magic `PNTR` |
| 4 | 4 | transport version (`2`) |
| 8 | 4 | capability flags |
| 12 | 16 | **session id** (128-bit nonce) |
| 28 | 4 | reserved, must be zero |

Capabilities now include `SessionIdentity` (`1 << 3`) and `Resync` (`1 << 4`)
alongside the existing multiplexing, control-priority and video-backpressure
flags.

## Session identity

Each physical session carries 128 bits of cryptographic randomness -
`RandomNumberGenerator.Fill` on Windows, `SecureRandom` on Android. It is
deliberately **not** derived from a clock, a process id, a counter or a device
serial: all of those repeat across reconnects, which is the exact case that has
to be distinguishable.

Every frame carries the **first four bytes** as a `sessionTag` rather than the
whole nonce. That is the cost/robustness trade: a stale frame survives the filter
with probability 2⁻³², while per-frame overhead stays at four bytes instead of
sixteen. Measured framing overhead over a full Dashboard run rose from
**0.3103% to 0.4647%** of wire bytes - exactly 8 bytes per frame on 27,224
frames - plus a one-off 112 bytes per session for the two handshake frames.

**A random session id is not authentication.** It separates sessions and rejects
stale or accidentally replayed frames. It does not establish who the peer is and
does not address SEC-001. The nonce is shaped so a future authenticated
handshake can derive session keys or a MAC from it without a wire change.

## Handshake

Ownership is explicit: **PanelHost is always the PLMX host** and generates the
nonce; the tablet is always the accessory and adopts it. The host owns the
physical carrier - it performs the AOA transition and opens WinUSB - so it is the
side that knows a new session has begun.

```
Host                                  Accessory
  |  HELLO   { version 2, caps, sessionId }  ->  |
  |                                              |  adopt sessionId
  |  <-  HELLO_ACK { same sessionId, caps }      |
  |  verify sessionId matches                    |
Established                                Established
```

The handshake completes on the calling task **before** either loop starts, so the
reader never has to distinguish a handshake from channel traffic, and a dirty
stream is resolved once, up front. No `OPEN`, `DATA` or `FIN` is emitted or
interpreted before the session state reaches `Established`.

The host accepts only a `HELLO_ACK` whose nonce equals the one it generated.
Acknowledgements for any other session are counted as stale and skipped.

## Resync algorithm

Both sides open in `SearchingForHandshake`. The scanner:

1. buffers, then searches for the four magic bytes with a vectorized
   `IndexOf` - never one `read()` per byte, because the carrier's syscall budget
   is the whole point of the transport optimization;
2. on a hit, validates the **entire** candidate before trusting it: magic,
   version, channel, frame type, the exact payload length for that type, the
   zero reserved field, the `PNTR` payload magic, the payload's version, a
   non-zero nonce, and agreement between the header tag and the nonce;
3. on a mismatch, steps forward one byte and keeps looking - this is what stops a
   `PLMX` magic sitting inside a previous session's payload from capturing the
   scan;
4. counts every discarded byte into `resyncDiscardedBytes`.

A header that fails to decode is **left in the buffer**, not consumed. Consuming
it would let a resync eat the head of the very handshake it needs to find, which
is precisely the situation when the previous session ended mid-frame.

Resync is bounded by `MaxResyncBytes` (1 MiB) and `MaxResyncDuration` (10 s).
Exceeding either raises `PlmxResyncException`; the transport then closes the
physical connection and reopens with exponential backoff from 250 ms to 4 s. It
never spins.

### The newest HELLO wins

While searching a dirty stream, the accessory cannot tell whether the first
well-formed HELLO it finds is the current one or the predecessor's greeting still
in the pipe. Because a bulk pipe is FIFO, a stale HELLO always arrives *before*
the real one, so **any valid HELLO supersedes the session in force** - during the
search and afterwards, in the established reader loop.

That single rule makes reconnect deterministic without anyone having to know how
many stale bytes exist:

- the accessory may briefly adopt the predecessor's nonce and acknowledge it;
- the host ignores that acknowledgement because the nonce is not its own;
- the accessory then reaches the real HELLO, supersedes, resets its channels and
  acknowledges again;
- the host matches and establishes.

It is also what lets the tablet survive a host restart **without reopening the
accessory descriptor**, which is the fragile part of the Android lifecycle.

On a framing error after establishment the accessory re-enters
`SearchingForHandshake` rather than tearing down the physical connection. The
host, which owns identity, instead fails the session so the transport starts a
fresh one with a new nonce.

## Sequence semantics

Sequences are now **per channel** and validated, where v1 carried a single global
counter that was written and never checked.

| Channel | Policy | On violation |
| --- | --- | --- |
| Control, Assets, Audio | strict: next must be previous + 1 | fail the session |
| Video | monotonic: must advance; gaps allowed | old or duplicate dropped and counted; gap counted |

Control is strict because the carrier is a reliable in-order pipe, so a gap there
means corruption. Video is monotonic only because latest-frame-wins upstream
legitimately leaves holes; treating those as corruption would confuse a normal
drop with a broken stream. Comparison is wrap-safe (`(int)(seq - previous)`).

## Stale frame handling

| Situation | Behaviour |
| --- | --- |
| Frame tagged for another session | dropped, `staleFramesDropped++`, session unaffected |
| Stale FIN | dropped; the current channel stays open |
| Stale DATA for an unopened channel | dropped before it can reach the dispatch that used to be fatal |
| DATA before OPEN **in the current session** | still a protocol violation and still fatal |
| HELLO for another session | supersedes (see above) |
| HELLO for the current session | idempotent; re-acknowledged, nothing torn down |
| Duplicate OPEN in the current session | replaces the channel and completes the previous stream with an explicit disconnect |

## Lifecycle

### AOA re-entry

```
USB normal -> AOA transition -> WinUSB MI_00 -> physical connection
  -> pipes reset and flushed -> resync -> HELLO -> HELLO_ACK -> OPEN Control -> Dashboard
```

`WinUsb_ResetPipe` issues `CLEAR_FEATURE(ENDPOINT_HALT)` on both bulk pipes when
a session opens, and `WinUsb_FlushPipe` drops data the host stack had cached for
the previous session. A PanelHost that exited without a bus reset leaves the
endpoint data toggles out of step; without this the accessory never even sees the
new HELLO. This is hygiene on the host's own buffers, not a substitute for
resync - the peer can still send stale frames afterwards, and those are handled
by session identity.

### Windows teardown

`WinUsb_AbortPipe` first, so every in-flight overlapped transfer completes as
`ERROR_OPERATION_ABORTED`; then `CancelIoEx` for anything the driver never
reported; then a bounded drain before the overlapped bookkeeping is released;
then `WinUsb_Free` and handle close. A transfer that never drains keeps its state
rather than corrupting the completion port.

### Android accessory handle

`AccessoryHandle` owns one descriptor for exactly one session. The streams are
built from the descriptor's `FileDescriptor`, which does not transfer ownership,
so **only the ParcelFileDescriptor closes the fd, exactly once**. Closing the same
fd twice is how a later `openAccessory` ends up refusing forever: the second close
can land on a descriptor number the runtime has already recycled. The handle is
also released before a new one is requested, because `/dev/usb_accessory` is
single-open.

### Session-scoped state

Nothing crosses a session boundary. On supersede or reconnect, logical channels
are completed with an explicit disconnect, accepted-channel queues are drained,
receive sequences reset, and the payload pool is cleared. Transmit sequences keep
climbing because the writer thread owns them and the new peer has no expectation
for a channel it has not seen. A generation counter on both transports means a
late failure from an already-replaced session cannot tear down its successor.

Because channels are completed rather than silently reused, the application layer
performs its own handshake again on the new session: Panel Protocol HELLO,
Stream Deck layout republication, and a fresh video session with CONFIG/SPS/PPS/IDR
rather than a continuation of an interrupted stream.

## Metrics and logs

`transportSessionsStarted`, `reconnectAttempts`, `reconnectSuccesses`,
`resyncEvents`, `resyncDiscardedBytes`, `staleFramesDropped`, `handshakeFailures`
and `sequenceGaps` are exported on both sides. Logs carry a truncated session
prefix only - never the full nonce, which keeps the shape compatible with a
future pairing secret:

```
[PLMX] state=SearchingForHandshake discarded=202 bytes foundMagic=true version=2 session=4C3EE1...
[PLMX] session established role=Host session=4C3EE1... version=2 fragment=65536B
[PLMX] session=4C3EE1... superseded by session=6A1E7C...; re-adopting and resetting channels
```

## Physical results

### 30-cycle reconnect campaign

`scripts/run-usb-reconnect-campaign.ps1 -Cycles 30`. Each cycle starts a fresh
PanelHost against a tablet already in accessory mode, with no delay between
cycles - the case most likely to leave residual bytes in the pipe.

| Result | Value |
| --- | --- |
| Cycles succeeded | **30 / 30** |
| Distinct session ids | **30 / 30** (no reuse) |
| Time to established | 6.03 - 7.07 s, median 6.05 s |
| Stale bytes discarded per cycle | 0 on the first, **202 on every subsequent cycle** |
| Handshake failures | 0 |
| Unexpected disconnects | 0 |

The 202 bytes are the previous session's in-flight frames. Under v1 those bytes
were fatal; under v2 every cycle discarded them and established anyway. Os logs brutos não são publicados (resumo: 30/30 reconexões sem falha, em
`docs/benchmarks/transport/comparison.md`).

### Benchmark regression

Dashboard, 2,000 samples per payload, 100 warm-ups, three repetitions, same
tablet, cable and host.

| Metric | USB before PLMX v2 | USB after PLMX v2 | Delta |
| --- | ---: | ---: | ---: |
| Control RTT p50 | 0.337 ms | **0.332 ms** | -1.5% |
| Control RTT p95 | 0.582 ms | 0.650 ms | +11.7% |
| Control RTT p99 | 0.933 ms | 1.028 ms | +10.2% |
| Bulk H -> A, 32 MiB | 23.26 MB/s | 22.94 MB/s | -1.4% |
| Bulk A -> H, 32 MiB | 40.03 MB/s | **40.63 MB/s** | +1.5% |
| Host CPU total | 7.44 s | **6.92 s** | -7.0% |
| Android CPU total | 20.17 s | 20.37 s | +1.0% |
| Host CPU per MiB | 27.86 ms | **25.93 ms** | -6.9% |
| Android CPU per MiB | 75.56 ms | 76.29 ms | +1.0% |
| Framing overhead (tx) | 0.3103% | 0.4647% | +0.155 pp |
| Errors / reconnects / drops | 0 / 0 / 0 | **0 / 0 / 0** | equal |

Session identity costs 8 bytes per frame and produced no measurable throughput
or CPU regression; the p95/p99 movement is within the run-to-run spread seen
across repeated Dashboard runs. None of the transport optimizations were given
up: writes are still one physical transfer per frame, fragments are still sliced
rather than copied, payload buffers still come from a pool with balanced
rent/return, and the accessory still allocates nothing per frame after warm-up.

## Not covered

Cable pull, Desktop reconnect under H.264, touch and Stream Deck after reconnect,
and the long soak were not executed: they need an operator at the tablet and, for
Desktop, an elevated PanelHost that only a UAC prompt can grant. The commands are
in `docs/transport-optimization.md`.

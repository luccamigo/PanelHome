# PanelOS transport architecture

## Existing TCP coupling

The pre-refactor carrier assumptions were:

| Area | Transport assumption | Protocol assumption | Lifecycle assumption |
| --- | --- | --- | --- |
| PanelHost control | authenticated `TcpListener` on loopback `:8765` | ordered UTF-8 JSONL after carrier bootstrap | TCP EOF/exception ends the session |
| PanelHost video | second authenticated `TcpListener` on loopback `:8766` | binary video v1 handshake and H.264/zlib packets after bootstrap | socket close releases the video slot |
| PanelHome control | `Socket` to `127.0.0.1:8765` | JSONL plus heartbeat | retry loop and close unblock reads |
| PanelHome video | `Socket` to `127.0.0.1:8766` | binary payload, never JSON/Base64 | latest frame wins; close unblocks reads |
| Control Center | rotates a token, configures two reverses and launches both peers | carrier bootstrap only | reports ADB/device/reverse/host status |
| development script | rotates a token and validates both `adb reverse` mappings | carrier bootstrap only | starts app and PanelHost after both mappings exist |

ADB was not removed. Its references remain deliberately inside the development adapter, tooling, diagnostics, and documentation.

## Transport abstraction

Windows uses `IPanelTransport`, `IPanelConnectionListener`, and `IPanelConnection`. Android uses `PanelTransport` and `PanelByteConnection`. Application protocol code receives only a logical byte stream with connect/read/write/close semantics. TCP types and loopback endpoints are confined to `AdbTcpTransport`.

Backend failures cross the boundary as `PanelTransportDisconnectedException` / `TransportDisconnectedException` (or the existing typed disconnected event on Android), rather than requiring features to understand socket or USB exceptions.

## Logical channels

| ID | Channel | Payload |
| ---: | --- | --- |
| 0 | Transport | capability handshake and carrier lifecycle |
| 1 | Control | unchanged Panel Protocol v1 JSONL |
| 2 | Video | unchanged Panel Video Protocol v1 binary bytes |
| 3 | Assets | reserved |
| 4 | Audio | reserved |

ADB/TCP maps Control and Video to the existing two sockets. Before exposing
either stream it performs the bounded `PADA`/`PADO` development authentication
documented in [`adb-tcp-authentication.md`](adb-tcp-authentication.md). Panel
Protocol v1 and Panel Video Protocol v1 remain unchanged. Native USB maps the
channels to one physical full-duplex byte stream and does not use this token.

## Multiplex framing

Native USB speaks **PLMX v2**. The v1 layout below is retained for the shared
fixtures and its tests; it is never negotiated on the wire, because accepting an
identity-free header would reintroduce the stale-byte defect v2 exists to fix.
See `docs/plmx-v2.md` for the full protocol, the resync algorithm and the
reconnect lifecycle.

Every integer wider than one byte is little-endian.

### PLMX v2 header (24 bytes, current)

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | magic `PLMX` |
| 4 | 1 | transport version (`2`) |
| 5 | 1 | logical channel ID |
| 6 | 2 | frame type (`DATA=0`, `HELLO=1`, `OPEN=2`, `FIN=4`, `HELLO_ACK=8`) |
| 8 | 4 | unsigned payload length |
| 12 | 4 | unsigned per-channel sequence |
| 16 | 4 | session tag (first four bytes of the session nonce) |
| 20 | 4 | reserved, must be zero |

The 32-byte HELLO payload carries `PNTR`, the transport version, capability
flags, a 128-bit session nonce and four reserved zero bytes. PanelHost generates
the nonce; the tablet adopts it and answers `HELLO_ACK`. No channel frame is
emitted or interpreted before that exchange completes.

Capability flags: multiplexing, control priority, video backpressure, session
identity, resync.

### PLMX v1 header (16 bytes, fixtures and tests only)

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | magic `PLMX` |
| 4 | 1 | transport version (`1`) |
| 5 | 1 | logical channel ID |
| 6 | 2 | flags (`HANDSHAKE=1`, `OPEN=2`, `FIN=4`) |
| 8 | 4 | unsigned payload length |
| 12 | 4 | unsigned sequence |

v1's capability handshake was symmetric and carried no identity, so a session
could not tell its own bytes from a predecessor's.

### Sequence policy

Sequences are per channel and validated. Control, Assets and Audio are strictly
ordered and a gap fails the session. Video is monotonic with gaps allowed,
because latest-frame-wins upstream legitimately leaves holes; old or duplicate
video frames are dropped and counted.

### Fragmentation

A logical write is fragmented at 64 KiB by default. That is a scheduling knob,
not a wire limit: the peer validates only against the per-channel maximum
payload, so any smaller fragment stays protocol compatible. Smaller fragments
return control to the writer's priority check more often, which is what keeps
touch latency bounded while video saturates the pipe.

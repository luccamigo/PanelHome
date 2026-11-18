# Resource limits and queue policies

Panel Protocol v1 remains newline-delimited UTF-8 JSON. Both peers count bytes
incrementally before decoding JSON and close a session when a line exceeds
1,048,576 bytes, contains invalid UTF-8, or ends at EOF without `LF`. The limit
fits the largest current message: a 512 KiB icon encoded as Base64 plus its
envelope.

Queue semantics are part of the implementation:

- telemetry, media, volume, desktop state and layouts are state: one pending
  value, latest wins;
- touch MOVE is coalesced; DOWN, UP and CANCEL remain ordered and a saturated
  critical queue is rejected explicitly;
- media, desktop and Stream Deck commands are ordered and bounded;
- each H.264 packet is limited to 2 MiB; video retains at most 8 access units
  and 2 MiB of payload in total. Overflow
  removes the oldest non-config access unit, marks reference loss, and rejects
  P-frames until the next IDR;
- queue metrics are aggregated as `queueDrop`, `queueReject` and
  `queueHighWatermark`, rather than logging every hot-path event.

Stream Deck configuration accepts at most 16 profiles, 16 pages per profile,
a 2x2 through 6x6 grid, 1,024 buttons total, 256 distinct icons, 80-character
names/labels, 128-character identifiers, and a 1 MiB document. Android accepts
PNG icons up to 512 KiB and 512x512, decodes near 128x128, and maintains an LRU
disk cache of at most 256 files or 16 MiB. The Windows importer emits only
128x128 cache assets and rejects source images over 4096x4096 or 16 megapixels.

Golden protocol examples live in `protocol-fixtures/`. C# and Kotlin tests read
the same JSON and binary hex vectors. Exact-version handshake negotiation is
unchanged; a future protocol revision should add compatible min/max version
ranges only with an explicit migration and downgrade policy.

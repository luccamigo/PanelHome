# Transport benchmark artifact retention

## Policy

Benchmark output is transient by default. Local runs write to the ignored
`.artifacts/benchmarks/transport/` directory. CI retains that directory as a
workflow artifact; milestone bundles may instead be attached to a release.

An artifact is committed under `raw/` only when it is one of:

1. **Published conclusion evidence**: the minimal JSON/CSV or operator record
   needed to reproduce a number or claim in the published transport documents.
2. **Fixture/regression evidence**: a bounded corpus whose individual entries
   prove a regression property that a summary alone cannot preserve.

Everything else is class 3, **intermediate/repeatable execution output**, and is
kept only as a local artifact, CI artifact, or release asset. Promoting a new
file requires documenting the claim or regression it supports and force-adding
it because `raw/**` is ignored. `comparison.md` is published documentation and
is never treated as disposable raw output.

## Classification of the 2026-08-12/19 inventory

The rules below are mutually exclusive and cover all 230 files that were
versioned before this cleanup. A timestamp family includes every extension in
that family unless an exception is stated.

| Class | Files | Count | Reason |
| --- | --- | ---: | --- |
| 1 | `raw/20260812-004953-*`, `raw/20260812-010756-*` | 4 | Original fixed-workload ADB/USB comparison published by `README.md`. |
| 1 | `raw/20260812-045000-*`, `raw/20260812-053142-*` | 6 | Corrected primary comparison and per-phase figures published by `comparison.md`. |
| 1 | `raw/20260812-151129-*` | 3 | Full post-PLMX-v2 benchmark behind the published regression table. |
| 1 | `raw/sweep/20260812-054430-*` | 3 | Published 256 KiB fragment-sweep result. |
| 1 | `raw/reconnect/20260812-150744-reconnect-campaign.csv` | 1 | Published 30/30 reconnect summary. |
| 1 | `raw/physical-*` | 2 | Sanitized physical binding/probe evidence. |
| 1 | `raw/final-validation/*.md` | 4 | Published operator procedure and signed-off session evidence referenced by current docs. |
| 2 | `raw/reconnect/20260812-150744-cycle-*.log` and `*.log.err` | 60 | Per-cycle regression evidence for unique sessions, bounded resync, and zero failures. |
| 3 | Every other formerly versioned file under `raw/` | 147 | Failed setup, host/Android log, short probe, superseded run, or per-cycle benchmark output reproducible from the retained campaign. |

The cleanup retains 83 files (23 class 1 and 60 class 2) and removes 147 class
3 files from Git. The removed working copies were staged under
`.artifacts/benchmarks/transport/legacy-raw/` during this cleanup so the local
evidence was not destroyed.

## Review checklist for promotion

- Link the exact artifact from the conclusion or regression document.
- Retain the smallest sufficient set; host/Android logs stay transient unless a
  specific line is the only evidence for a published claim.
- Sanitize device identifiers and machine-specific paths.
- Record the command, workload, schema version, and relevant environment.
- Prefer a summary plus a bounded regression corpus over every intermediate run.

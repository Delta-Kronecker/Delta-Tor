# Bench campaign 2026-08 — conflux defaults A/B (v1.5.0 candidate)

Setup: webtunnel mode, ultimate strategy, iters=1, streams=1/4/8,
10 MB per stream per direction, single run per cell (tor variance is high —
directional conclusions only).

Raw data: `2026-08-webtunnel.csv`

| variant | watch % | rtt-max | sets | select | down avg (1/4/8 streams) |
|---|---|---|---|---|---|
| base_old | off | off | 32 | round-robin | 1.63 / 2.00 / **2.91** MB/s |
| v1_newdef | 25 | 200 ms | 32 | round-robin | 1.07 / 0.86 / 1.60 MB/s |
| v2_sets16 | 25 | 200 ms | 16 | round-robin | 0.27 / 0.19 / 0.22 MB/s |

## Conclusions

1. Turning the RTT filters on by default (watch25 + rtt-max200) cost
   ~35-50% sustained download across every stream count → shipped defaults
   stay OFF; the filters live behind the opt-in `lowlatency` preset.
2. `ConfluxNumSets 16` collapsed circuit building under load (final state:
   8 sets / 8 legs, last iteration lost all uploads) → keep the 32-set
   default.
3. Upload benchmark added in this release measures up to ~1.1 MB/s at 8
   parallel streams on this path.

Latency was not separately measured in this campaign (keep-alive RTT is not
active during `--bench`); the lowlatency preset's ping benefit remains to be
quantified with a dedicated run.

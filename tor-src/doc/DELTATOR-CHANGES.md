# DeltaTor modifications to tor 0.4.9.11

This tree is the official tor 0.4.9.11 release source plus the DeltaTor
conflux extensions listed below. Everything else is untouched upstream
code, so this file is the single place documenting where DeltaTor deviates.

## New torrc options (client side)

All options are validated in `src/app/config/config.c` and documented in
`doc/man/tor.1.txt`.

| Option | Range | Purpose |
|---|---|---|
| `ConfluxNumSets` | 0-32 | Sets kept alive; overrides consensus `cfx_max_prebuilt_set`/`cfx_max_linked_set` |
| `ConfluxNumLegs` | 0-16 | Legs built per set; overrides consensus `cfx_num_legs_set` |
| `ConfluxNumLinkedSets` | 0-32 | Ceiling on *linked* sets; binds only when below `ConfluxNumSets` |
| `ConfluxSetSelection` | 0-3 | Set pick policy for new streams: first / round-robin / least-streams / fastest |
| `ConfluxSetRttMax` | >= 0 ms | Skip sets whose best-leg RTT is at/above this (never empties the candidate list) |
| `ConfluxSetRttPct` | 0-100 % | Restrict new streams to the best N% of sets by RTT (keeps at least one set) |

Runtime plumbing lives in `src/core/or/conflux_params.c`; set selection,
RTT filters and stream load-balancing in
`conflux_get_circ_for_conn()` (`src/core/or/conflux_pool.c`).

## CONFLUX control command

`src/feature/control/control_cmd.c`, subcommands:

- `CONFLUX QUERY` — one line per leg circuit:
  `SET=<id> CIRC=<id> EXIT=<fp> LEGS=<n> STATE=LINKED|UNLINKED`
  plus, for LINKED legs, `RTT_US=<usec> STREAMS=<total> BYTES=<sent+recv>`
  (set-wide totals tracked on the conflux object).
- `CONFLUX ADD <set-id>` — launch a replacement leg for a set.
- `CONFLUX SET <n>` — override legs-per-set at runtime.

The launcher's circuit-health monitor uses QUERY to rank legs by RTT and
CLOSECIRCUIT to prune weak ones.

## Keep-alive spread

Streams whose SOCKS5 auth username equals `deltator-keepalive` bypass the
RTT-based set filters (`ConfluxSetRttMax`/`ConfluxSetRttPct`) and are sent
to the set with the fewest cumulative streams, so keep-alive traffic keeps
exercising every set regardless of the user's selection policy.
(`conflux_conn_is_keepalive()` / `conflux_pick_set_from_candidates()` in
`src/core/or/conflux_pool.c`.)

## Per-set accounting

New fields on `conflux_t` (`src/core/or/conflux_st.h`): `total_streams`,
`bytes_sent`, `bytes_recv`. Updated from `circuituse.c`
(`link_apconn_to_circ()`, `circuit_sent_valid_data()`,
`circuit_read_valid_data()`).

## Launch-budget refund for deliberate closes

Upstream counts every leg launch against a per-set budget
(`num_leg_launch`) so failing builds cannot churn forever. DeltaTor refunds
one slot in `cfx_del_leg()` **only** when a healthy linked leg was closed
deliberately (no in-flight data, sequence numbers intact, not the current
leg). Genuine teardowns keep their slot consumed — the set dies anyway and
any replacement starts with a fresh nonce and fresh budget. This lets the
launcher prune/rebuild legs for an entire session without exhausting the
cap.

## Set rebuild after sole-leg close

`linked_circuit_closed()` (`src/core/or/conflux_pool.c`) now relaunches a
leg when a set survives a deliberate close, and rebuilds the whole set with
the same nonce when its last leg was closed deliberately. The conflux
object ownership handoff to/from the unlinked pool is preserved to avoid
double-free (see comments at the site).

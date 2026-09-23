# NT8ReplayDownloader — EdgeLab hardened fork candidate

Derived under MIT from [`jalv92/NT8ReplayDownloader`](https://github.com/jalv92/NT8ReplayDownloader), pinned to upstream commit `d4a9c7aa2048846ab6e6aa45b8f23530c5d60af3`.

**Status:** `SOURCE_HARDENED_NOT_RUNTIME_CERTIFIED`. The source was reviewed and hardened, but still needs compilation and runtime testing inside NinjaTrader 8.1.8.0.

## Install

Copy only `AddOns/ReplayDownloaderEdgeLab.cs` to:

```text
Documents\NinjaTrader 8\bin\Custom\AddOns\
```

Then compile with F5 and open **Control Center → Tools → EdgeLab Replay Downloader**. The unsafe upstream add-on remains available only in Git history, not in this hardened branch.

## Hardened contract

- Exact contracts only (`NQ 09-26;GC 08-26`); no inferred rolls.
- Every calendar date is requested, including Sundays and holidays.
- Only completed dates before today; maximum 120 days and 20 contracts.
- Delay constrained to 3–60 seconds.
- Undocumented API/header assumptions pinned to NT Core 8.1.8.0.
- Exactly one discoverable HDS client required.
- Structured depth validation: both bid and ask, valid counts and raw header times.
- Invalid pre-existing artifacts quarantined before retry.
- File must remain stable for three seconds before validation.
- Timeout/cancel terminates the entire batch, preventing overlap with an unknown in-flight request.
- Valid artifacts receive SHA-256 custody hashes.
- Every result appends to `db/replay/edgelab_replay_manifest.csv`.
- Continuity remains explicitly uncertified until EdgeLab validates each D→D+1 boundary.

## Mandatory Windows/NT8 test campaign

1. Clean NT 8.1.8.0 compile against baseline.
2. Valid existing file → `SKIP_EXISTING_VALID`.
3. Truncated/no-depth file → quarantine, never skip/pass.
4. Server no-data → `NO_SERVER_DATA`.
5. Known Sunday file → requested and acquired.
6. Disconnect, Stop, and forced timeout → no subsequent request starts.
7. Header counts compared with full `.nrd` decode.
8. D→D+1 clock, bootstrap, BBO and expected-calendar boundary validation in EdgeLab.

`ACQUIRED_VALID_DEPTH` proves only a stable artifact with plausible nonzero L2 header metadata and a hash. It does not prove complete stream decodability, exchange-clock interpretation, MBO/FIFO, or continuity.

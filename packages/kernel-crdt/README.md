# Harborline.Api.Kernel.Crdt

CRDT engine abstraction for the Harborline local-node architecture. Wave 1.2 of the
paper-alignment plan. Paper §2.2 (AP-class data, leaderless CRDT merge) and §9
(CRDT growth and GC). ADR 0008 (CRDT engine selection).

## Status

**YDotNet (Yjs/yrs) is the sole production engine of record.** ADR 0008 supersedes
legacy ADR 0028's earlier primary-engine selection. The 2026-04-22 validation history is in
[`SPIKE-OUTCOME.md`](SPIKE-OUTCOME.md).

## Spike outcome (2026-04-22, revisited)

| Candidate | Decision | Rationale |
|---|---|---|
| Loro via `LoroCs` (NuGet v1.10.3, 2025-12-09) | Not selected | Self-described as "very bare bones"; public API exposed no snapshot / delta / vector-clock surface at the C# level. This is historical validation context, not a deferred engine choice. |
| Loro via `loro-ffi` + hand-rolled P/Invoke | Not selected | Larger binding effort than the validation spike could close responsibly. This is historical validation context, not a deferred engine choice. |
| Yjs/yrs via `YDotNet` (NuGet v0.6.0, 2026-02-14) | **Engine of record** | Mature; MIT-licensed; targets net8.0 and runs on .NET 11 preview. Exposes the full `StateVectorV1` / `StateDiffV1` / `ApplyV1` surface. Documented client-ID-truncation bug mitigated in the wrapper (see below). |
| In-memory stub | Test-only fake | Deterministic total-order replay for unit tests that do not need real CRDT semantics. Never a production fallback. |

### Client-ID uniqueness workaround

YDotNet 0.6.0's default `new Doc()` produces non-unique client IDs (200 docs → 16 unique IDs in our
probe). Even with explicit random `ulong` IDs, values above `2^32` cause concurrent-insertion
divergence because the yrs wire format encodes client IDs with only 32 bits of precision in practice.
`YDotNetCrdtEngine` constructs every `Doc` with an explicit `DocOptions.Id` drawn from a
cryptographically-random `uint32`; this eliminates divergence (0 / 80 stress-trial failures).
See [`SPIKE-OUTCOME.md`](SPIKE-OUTCOME.md) for the full investigation.

## What's here

| File | Role |
|---|---|
| `ICrdtDocument.cs` | Root document contract (snapshot, delta, vector clock). |
| `ICrdtText.cs` | Rich-text container contract. |
| `ICrdtMap.cs` | Key-value container contract. |
| `ICrdtList.cs` | Ordered-list container contract. |
| `ICrdtEngine.cs` | Factory/registry surface. |
| `Backends/YDotNetCrdtEngine.cs` | Default production backend — wraps YDotNet 0.6.0 (Yjs/yrs). |
| `Backends/StubCrdtEngine.cs` | Test-only fake (heavily flagged; never a production fallback). |
| `DependencyInjection/ServiceCollectionExtensions.cs` | `AddHarborlineCrdtEngine()` / `AddHarborlineCrdtEngineYDotNet()` / `AddHarborlineCrdtEngineStub()`. |
| `tests/` | xUnit + FsCheck property-based harness — runs against both backends. |
| `SPIKE-OUTCOME.md` | Full 2026-04-22 spike write-up. |

## Using it

```csharp
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.DependencyInjection;

var services = new ServiceCollection();
services.AddHarborlineCrdtEngine();   // YDotNet (Yjs/yrs) backend by default

var engine = services.BuildServiceProvider().GetRequiredService<ICrdtEngine>();

await using var doc = engine.CreateDocument("note/2026-04-22/lunch");
var text = doc.GetText("body");
text.Insert(0, "CRDTs keep concurrent writers consistent.");

var snapshot = doc.ToSnapshot();          // send to peer via federation layer
await using var peer = engine.OpenDocument("note/2026-04-22/lunch", snapshot);
```

See `tests/CrdtTextTests.cs` for the full convergence exchange pattern (two peers,
concurrent edits, delta exchange, assert identical `Value`). See
`tests/YDotNetCrdtEngineTests.cs` for property-based convergence + idempotence
coverage against the real Yjs/yrs backend (exercises concurrent-insert RGA
tiebreak, map LWW, array fractional positions — things the stub's total-order
replay cannot honestly test).

## Backend selection

Three DI entry points:

| Extension | Backend | When to use |
|---|---|---|
| `AddHarborlineCrdtEngine()` | YDotNet (default) | Production and almost every test. |
| `AddHarborlineCrdtEngineYDotNet()` | YDotNet (explicit) | When you want the host wiring to be unambiguous. |
| `AddHarborlineCrdtEngineStub()` | Stub | Deterministic unit tests that do not care about real CRDT merge semantics. |

All three use `TryAddSingleton`, so calling `AddHarborlineCrdtEngineStub()` **before**
`AddHarborlineCrdtEngine()` wins.

## Engine selection boundary

YDotNet/yrs is the one production engine. Adding or substituting another production
adapter requires a superseding architecture decision and wire-compatibility evidence;
runtime backend selection is not a deployment option. `StubCrdtEngine` remains a test-only
fake so consumers can exercise the `ICrdtEngine` seam without native loading.

## What's *not* here

- **Manual tombstone-GC trigger.** YDotNet 0.6 does not expose one. Its default `Doc`
  option garbage-collects deleted payloads automatically when write transactions commit;
  shallow snapshots remain a distinct paper §9 mitigation.
- **Wire-format stability guarantees.** YDotNet emits lib0 v1 encoded binary,
  which is a stable and documented Yjs format. The stub's JSON envelope is
  versionless and MUST NOT be persisted across a backend swap.
- **Integration with `IEventLog`.** Wave 1.2 ships the contract; the event-log
  hook lands alongside Wave 1.3's persistent event-log work.

## References

- Paper §2.2 — AP-class data
- Paper §6.1 — gossip-based delta sync
- Paper §9 — CRDT growth and GC
- Paper §15 Level 1 — property-based testing for convergence / idempotency /
  commutativity / monotonicity
- ADR 0008 — controlling engine selection
- Legacy ADR 0028 — superseded primary-engine selection
- [Wave 1.2 deliverable](../../_shared/product/paper-alignment-plan.md#wave-1---phase-1-kernel-primitives)

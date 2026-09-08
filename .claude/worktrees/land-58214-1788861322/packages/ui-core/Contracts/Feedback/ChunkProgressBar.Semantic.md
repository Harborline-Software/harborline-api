# ChunkProgressBar — Semantic Contract (Alias)

- **Component:** ChunkProgressBar
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic (alias redirect)
- **Status:** Accepted
- **Canonical contracts:** [ProgressBar](./ProgressBar.Semantic.md) (all 4 contracts)
- **Reference implementation:** `packages/ui-react/src/components/feedback/ProgressBar.tsx`
- **Catalog row:** #27 ChunkProgressBar (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled segmented progress bar

---

ChunkProgressBar is an alias for **ProgressBar** with `type="chunk"`. It renders a segmented multi-chunk progress indicator rather than a continuous bar.

Usage:
```tsx
<ProgressBar type="chunk" value={3} max={5} />
```

All contracts (Semantic, Interaction, Accessibility, Styling) are defined in the ProgressBar contract family. See ProgressBar contracts for full specification including the `type="chunk"` variant.

---

## Wave-N upgrade notice (2026-06-11)

**The alias pattern is DEPRECATED as of wave-N.** ChunkProgressBar becomes a
first-class component with its own prop surface. See
[ProgressBar.Semantic.md §6](./ProgressBar.Semantic.md#6-wave-n-expansion--chunkprogressbar-separation--orientation--naming-2026-06-11)
for the full wave-N specification.

Key changes at wave-N:

| Change | From | To |
|---|---|---|
| Implementation strategy | `type="chunk"` alias on ProgressBar | Standalone `ChunkProgressBar` component |
| Chunk count prop | `chunks` | `chunkCount` (`chunks` deprecated) |
| Inherited props REMOVED | `label`, `labelPlacement`, `labelVisible` | Not supported (Kendo ChunkProgressBar has no label surface) |
| New props ADDED | — | `orientation`, `reverse`, `min`, `disabled`, `emptyClassName`, `progressClassName` |
| `max` semantics | Previously used as chunk count ceiling | `max` is value range ceiling only; `chunkCount` is segment count |

Migration from `type="chunk"` alias:

```tsx
// Before (deprecated; alias)
<ProgressBar type="chunk" value={3} max={5} chunks={5} />

// After (wave-N)
<ChunkProgressBar value={3} max={5} chunkCount={5} />

// IMPORTANT: if your old code used max as the chunk count (max={5} meaning
// "5 chunks"), update to explicit chunkCount + correct max:
// value=3/max=5 with chunkCount=5 = 60% filled bar with 5 segments
```

The `type="chunk"` prop on ProgressBar emits a dev-mode console warning at wave-N and is removed at wave-N+1.

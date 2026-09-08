# PanelBar — Semantic Contract (Redirect)

- **Component:** PanelBar
- **ADR 0017 family:** Layout
- **Contract type:** Semantic (redirect stub)
- **Status:** Deprecated — absorbed by Panel
- **Canonical contracts:** [Panel.Semantic](./Panel.Semantic.md) (all 4 contracts)
- **Reference implementation:** `packages/ui-react/src/components/navigation/PanelBar.tsx` (passthrough shim)
- **Phase:** Consolidation Cohort 1 (2026-06-12) — disclosure/container cluster

---

PanelBar has been absorbed by **Panel** via the `mode="accordion"` discriminated union.

Use `<Panel mode="accordion" items={...} />` instead.

Migration:

```tsx
// Before
<PanelBar items={items} expandMode="single" onSelect={onSelect} />

// After
<Panel mode="accordion" items={items} expandMode="single" onSelect={onSelect} />
```

The `PanelBarItem` type is now canonical on Panel and re-exported from PanelBar for backward compatibility.

All contracts (Semantic, Interaction, Accessibility, Styling) for the accordion capability are now defined in the **Panel** contract family under `mode="accordion"` sections.

The `PanelBar` export is a passthrough shim kept for one release. It will be removed in the next major release.

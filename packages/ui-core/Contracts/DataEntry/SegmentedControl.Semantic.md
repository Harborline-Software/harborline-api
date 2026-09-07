# SegmentedControl — Semantic Contract (Alias)

- **Component:** SegmentedControl
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic (alias redirect)
- **Status:** Accepted
- **Canonical contracts:** [ToggleGroup](./ToggleGroup.Semantic.md) (all 4 contracts)
- **Reference implementation:** `packages/ui-react/src/components/forms/ToggleGroup.tsx`
- **Catalog row:** #116 SegmentedControl (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled toggle button group

---

SegmentedControl is an alias for **ToggleGroup** with `type="single"`. The segmented control pattern (iOS-style, mutually exclusive options in a connected strip) maps exactly to ToggleGroup's single-select mode with `variant="outline"`.

```tsx
<ToggleGroup
  type="single"
  variant="outline"
  value={view}
  onValueChange={setView}
  options={[
    { value: 'list', label: 'List' },
    { value: 'grid', label: 'Grid' },
    { value: 'calendar', label: 'Calendar' },
  ]}
  aria-label="View mode"
/>
```

All contracts (Semantic, Interaction, Accessibility, Styling) are defined in the ToggleGroup contract family.

---

**Implementation note (2026-07-07, small-screen program #78 §3):** the shipping
`packages/ui-react/src/components/buttons/SegmentedControl.tsx` is a distinct, pre-existing
hand-rolled implementation (predates this alias contract; not reconciled here — out of scope
for this note). It gained a `size="touch"` variant (44px hit-area floor, WCAG 2.5.8) and an
optional per-option `testId` prop, both additive — no existing prop, size, or behavior was
narrowed. Consumers: the form/workflow builders' Edit/Split/Preview mode toggle and the
workflow builder's Outline/Graph canvas-view toggle, JS-gated to `BP_PHONE` or
`(pointer: coarse)`.

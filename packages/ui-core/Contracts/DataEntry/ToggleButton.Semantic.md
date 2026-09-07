# ToggleButton — Semantic Contract (Alias)

- **Component:** ToggleButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic (alias redirect)
- **Status:** Accepted
- **Canonical contracts:** [ToggleGroup](./ToggleGroup.Semantic.md) (all 4 contracts)
- **Reference implementation:** `packages/ui-react/src/components/forms/ToggleGroup.tsx`
- **Catalog row:** #138 ToggleButton (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<button>` with pressed state

---

ToggleButton is an alias for **ToggleGroup** used when a single independent toggle button is needed rather than a group. Render a ToggleGroup with one option entry:

```tsx
<ToggleGroup
  type="single"
  value={pressed ? 'on' : ''}
  onValueChange={v => setPressed(v === 'on')}
  options={[{ value: 'on', label: <BoldIcon />, 'aria-label': 'Bold' }]}
/>
```

All contracts (Semantic, Interaction, Accessibility, Styling) are defined in the ToggleGroup contract family. No separate implementation exists for ToggleButton.

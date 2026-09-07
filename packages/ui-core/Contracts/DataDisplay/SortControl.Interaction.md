# SortControl — Interaction Contract

- **Component:** SortControl
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SortControl.Semantic.md) · [Interaction](./SortControl.Interaction.md) · [Accessibility](./SortControl.Accessibility.md) · [Styling](./SortControl.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/SortControl.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

SortControl is fully controlled — no internal state.

```
value=null ──field select──> onChange({field, dir:'asc'}) ──> [sorted asc]
value=null ──dir toggle────> onChange({field:first, dir:'desc'}) ──> [sorted desc]

[sorted asc] ──field select──> onChange({newField, dir:'asc'}) ──> [sorted asc on new field]
[sorted asc] ──dir toggle────> onChange({field, dir:'desc'}) ──> [sorted desc]
[sorted desc] ──dir toggle───> onChange({field, dir:'asc'}) ──> [sorted asc]
```

---

## 2. Field select behaviour

Native `<select>` onChange:

```typescript
onChange({ field: e.target.value, direction: currentDir })
```

The direction is preserved when the field changes.

---

## 3. Direction toggle behaviour

| `value` | `currentDir` | Icon | Toggle result |
|---|---|---|---|
| `null` | `'asc'` (default) | `ArrowUpDown` | `onChange({field: options[0].value, direction: 'desc'})` |
| `{field, direction: 'asc'}` | `'asc'` | `ArrowUp` | `onChange({field, direction: 'desc'})` |
| `{field, direction: 'desc'}` | `'desc'` | `ArrowDown` | `onChange({field, direction: 'asc'})` |

The toggle button carries `aria-pressed={currentDir === 'desc'}` — AT announces pressed state.

---

## 4. Keyboard behaviour

| Key | Element | Action |
|---|---|---|
| Tab | Select | Focuses the select |
| Arrow Up/Down | Select | Changes field option (native browser behaviour) |
| Tab | Toggle button | Focuses toggle |
| Enter / Space | Toggle button | Toggles direction |

---

## 5. Known gaps

| # | Gap | Notes |
|---|---|---|
| I1 | No "no sort" / reset affordance | Host sets `value={null}` externally to reset |
| I2 | Direction preserved on field change | Some UX patterns expect direction to reset to `'asc'` on field change — current behaviour preserves direction |

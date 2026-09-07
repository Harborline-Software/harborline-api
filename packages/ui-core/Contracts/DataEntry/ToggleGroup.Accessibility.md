# ToggleGroup — Accessibility Contract

- **Component:** ToggleGroup
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ToggleGroup.Semantic.md) · [Interaction](./ToggleGroup.Interaction.md) · [Styling](./ToggleGroup.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ToggleGroup.tsx`
- **Catalog row:** #A14 ToggleGroup (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="group"` | Container `<div>` | Groups the buttons semantically |
| `aria-label={...}` | Container `<div>` | Host-provided label for the group |
| `<button type="button">` | Each option | Native button — keyboard focusable |
| `aria-pressed={boolean}` | Each button | `true` when selected; `false` when not |
| `aria-label={opt['aria-label']}` | Each button | Names icon-only buttons |
| `disabled` | Each button | Boolean attribute when disabled |

---

## 2. Selection announcement

`aria-pressed` communicates toggle state per button:
- `aria-pressed="false"` → AT reads `"[label], button, not pressed"`
- `aria-pressed="true"` → AT reads `"[label], button, pressed"`

The group `aria-label` provides context (e.g. `"Text alignment"`).

---

## 3. Group labeling

Host must supply `aria-label` on ToggleGroup for AT users to understand what the group controls:
```tsx
<ToggleGroup type="single" aria-label="Text alignment" ... />
```

Without it, AT users only hear individual button labels with no group context.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TG3 | Medium | `type="single"` uses `role="group"` + `aria-pressed` per button; ARIA radiogroup pattern (`role="radiogroup"` + `role="radio"`) is more semantically correct for single-select | **Partially resolved (PR 2728).** Resolved in the `SegmentedControl` alias — it ships `role="radiogroup"` + `role="radio"` + `aria-checked`. **Still open for the canonical `ToggleGroup`**, which retains `role="group"` + `aria-pressed` (accepted-risk; `aria-pressed` is functional). See §4.1. |
| G-TG4 | Low | No roving tabindex — all buttons are in Tab order rather than arrow-key navigation | **Partially resolved (PR 2728).** Resolved in the `SegmentedControl` alias — roving tabindex plus `ArrowLeft`/`ArrowRight`/`ArrowUp`/`ArrowDown`/`Home`/`End`. **Still open for the canonical `ToggleGroup`** (accepted-risk; consistent with standard button-group patterns). See §4.1. |

### 4.1 Divergence — `SegmentedControl` alias vs canonical `ToggleGroup`

`SegmentedControl` is an alias of this contract family
([SegmentedControl.Semantic.md](./SegmentedControl.Semantic.md), catalog row #116), so this
Accessibility contract governs it. As of **PR 2728** ("fix(ui-react): deepen button accessibility",
merged 2026-07-17) the two implementations **intentionally diverge** on the single-select pattern:

| | Canonical `ToggleGroup` | `SegmentedControl` alias |
|---|---|---|
| Reference implementation | `packages/ui-react/src/components/forms/ToggleGroup.tsx` | `packages/ui-react/src/components/buttons/SegmentedControl.tsx` |
| Container role | `role="group"` | `role="radiogroup"` |
| Option role / state | `role="button"` + `aria-pressed` | `role="radio"` + `aria-checked` |
| Keyboard | All options in Tab order | Roving tabindex + arrow keys, `Home` / `End` |

`SegmentedControl` is always single-select, so the radiogroup pattern is unambiguously correct for
it. `ToggleGroup` supports both `type="single"` and `type="multiple"`; migrating only its single
variant would make one component present two different ARIA patterns, so G-TG3 / G-TG4 remain
accepted-risk there pending a decision covering both variants.

# StatusPill — Accessibility Contract

- **Component:** StatusPill
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./StatusPill.Semantic.md) · [Interaction](./StatusPill.Interaction.md) · [Styling](./StatusPill.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/StatusPill.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Attribute | Element | Value |
| --- | --- | --- |
| `title` | `<span>` | `tooltip` prop value (when provided) |

No explicit ARIA role. StatusPill renders as a `<span>` with visible text
content — the status string is read directly by AT.

---

## 2. Color-only differentiation — WCAG 1.4.1 analysis

WCAG 2.1 SC 1.4.1 Use of Color: "Color is not used as the only visual means of conveying information."

StatusPill conveys status via both **text** (the `value` string rendered inside the pill) and **color** (the pill background/text hue). The text IS present for AT users and satisfies 1.4.1 for most `kind` variants.

**However, two kinds have WCAG 1.4.1 risk:**

### `agingBucket` — severity gradient, color is the primary signal

The aging-bucket color scale (gray → yellow → orange → orange-dark → red) conveys a severity gradient that sighted users scan in dense DataGrids. The text values ("Days0To30", "Days31To60", etc.) are machine-style enum keys — they convey range but not urgency. A user who cannot distinguish the orange-to-red gradient (color-blind or printed page) loses the urgency signal.

**Required:** When using `agingBucket` in a DataGrid, the host SHOULD pair each pill with the `icon` prop (see §3) to add a non-color channel:
- `Days61To90` → Warning triangle icon
- `Days90Plus` → Alert/danger icon

### `balanceState` — binary color, no shape difference

`Balanced` (green) and `OutOfBalance` (red) use only color difference. The text labels DO differentiate, but in a color-blind scan the two pills look identical. **The text satisfies 1.4.1**, but usability recommends an icon for density contexts.

---

## 3. Icon-pairing spec

StatusPill SHOULD expose a `leadingIcon?: React.ReactNode` prop (deferred from M1; tracked as G-SP3). When provided, the icon is rendered to the left of the text and MUST be `aria-hidden="true"` — the status text already provides the accessible name. The icon is a shape/symbol redundancy for color-blind users.

Recommended icon pairings per kind:

| `kind` | Value | Icon |
| --- | --- | --- |
| `agingBucket` | `Days61To90` | `TriangleAlert` (warning) |
| `agingBucket` | `Days90Plus` | `OctagonAlert` (danger) |
| `balanceState` | `OutOfBalance` | `CircleAlert` |
| `workOrderStatus` | `Overdue` | `Clock` (time-based urgency) |

Until `leadingIcon` is implemented, hosts can wrap StatusPill in a `<span>` and place an icon before the pill — but this leaks layout responsibility out of the component.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-SP1 | Low | `title` tooltip is not accessible to keyboard users (no focus target on the `<span>`) | Accepted-risk M1 |
| G-SP2 | Low | For `agingBucket` kinds, the value labels (e.g., "Days0To30") are programmatic enums — readable by AT but potentially confusing without display formatting | Accepted-risk M1 |
| G-SP3 | High | No `leadingIcon` prop — color-blind users have no non-color shape channel for `agingBucket` severity gradient and `balanceState` binary distinction | Must fix before v1 production use for `agingBucket` in dense DataGrids; see §3 for icon-pairing spec |

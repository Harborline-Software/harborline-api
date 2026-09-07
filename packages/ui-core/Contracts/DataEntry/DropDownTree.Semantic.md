# DropDownTree — Semantic Contract

- **Component:** DropDownTree
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DropDownTree.Interaction.md) · [Accessibility](./DropDownTree.Accessibility.md) · [Styling](./DropDownTree.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DropDownTree.tsx`
- **Catalog row:** #49 DropDownTree (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled tree selector

---

## 1. Component purpose

**DropDownTree** — a single-select dropdown where the options panel displays a hierarchical tree. Items have parent–child relationships via the `items` array. Supports expand/collapse per node.

---

## 2. Props

```typescript
interface DropDownTreeItem {
  text: string
  value: string | number
  items?: DropDownTreeItem[]  // children
  expanded?: boolean          // initial expand state per node
  disabled?: boolean
}

interface DropDownTreeProps {
  value?: string | number | null    // controlled
  defaultValue?: string | number
  onValueChange?: (value: string | number | null, item: DropDownTreeItem | null) => void
  data: DropDownTreeItem[]          // required; root-level items
  textField?: string                // reserved; M1 uses item.text directly
  valueField?: string               // reserved; M1 uses item.value directly
  childrenField?: string            // reserved; M1 uses item.items directly
  placeholder?: string              // default: 'Select...'
  disabled?: boolean                // default: false
  size?: 'small' | 'medium' | 'large'         // default: 'medium'
  fillMode?: 'solid' | 'outline' | 'flat'     // default: 'solid'
  rounded?: 'small' | 'medium' | 'large' | 'full'  // default: 'medium'
  className?: string
}
```

---

## 3. Tree model

Items are a recursive `DropDownTreeItem[]`. Each node can have `items` (children). The tree is rendered lazily — children appear only when `expanded=true`. Expand state is internal per node.

---

## 4. Selection

Single selection. `value` is a `string | number` — the `item.value` of the selected node. Both leaf and parent nodes are selectable. Clicking a parent node with children expands/collapses AND selects it simultaneously.

---

## 5. `textField`, `valueField`, `childrenField`

These props are accepted but not consumed in M1 (the component reads `item.text`, `item.value`, `item.items` directly). Reserved for a future data-binding approach.

---

## Kendo-parity expansion (2026-06-11 — spec-first; see _shared/design/polish/kendo-spec-audit/dropdowns.md)

**Status:** Draft — promote to Accepted on council review.
**Wave:** wave-E1 (dropdowns validation + event surface pass).
**Audit baseline:** DropDownTree ~33% Kendo-minimum coverage — the weakest in the family.
P1 misses: filtering (client + server), `textField`/`valueField`/`childrenField` dynamic binding,
keyboard navigation (G-DDT5 — entire surface missing), ARIA tree pattern (G-DDT3/G-DDT4/G-DDT5),
trigger keyboard-focusable (G-DDT1), loading state, `valid`/`required`.

> **Architecture note:** DropDownTree's M1 trigger is a `<div>` (G-DDT1). All keyboard and ARIA
> obligations below require the trigger to become a `<button type="button">` with proper role
> assignment. This is a structural change that must land before any keyboard/ARIA work.

---

### §E1-1 FR-1 adoption — validation contract (family ruling FR-1)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-1.

Add to `DropDownTreeProps`:

```typescript
required?: boolean   // default: false
error?: boolean      // default: false
```

`required` drives `aria-required="true"` on the trigger button (§E1-2 changes trigger to `<button>`).
`error` drives `aria-invalid="true"` on the trigger + error ring on wrapper.
`validationMessage` is NOT added — FR-1 §3.

**wave-E1**

---

### §E1-2 FR-2 adoption — focus + popup events + trigger fix (family ruling FR-2; closes G-DDT1)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-2.

Add to `DropDownTreeProps`:

```typescript
onFocus?: React.FocusEventHandler<HTMLButtonElement>
onBlur?: React.FocusEventHandler<HTMLButtonElement>
open?: boolean
onOpenChange?: (open: boolean) => void
```

**Trigger fix (G-DDT1):** The trigger element MUST be changed from `<div>` to
`<button type="button">`. This is a prerequisite for all keyboard and ARIA work.
- `<button>` is natively keyboard-focusable (no `tabIndex` hack needed).
- `<button>` receives Enter/Space natively for open/close.
- `aria-expanded`, `aria-haspopup`, `aria-controls` are legal on `<button>`.

**Closes:** G-DDT1 (trigger keyboard-focusable).

**wave-E1**

---

### §E1-3 FR-3 size-vocabulary migration (family ruling FR-3)

`size` values migrate from `'small' | 'medium' | 'large'` → `'sm' | 'md' | 'lg'`.
`rounded` migrates similarly. Deprecation aliases at runtime; removed at next major.

**wave-E1**

---

### §E1-4 Loading state with popup suppression (audit P1)

Add to `DropDownTreeProps`:

```typescript
loading?: boolean   // default: false
```

**Semantics:**

1. Animated spinner in trigger trailing slot; replaces expand chevron.
2. `aria-busy="true"` on trigger button.
3. **Popup suppression:** tree panel MUST NOT open while `loading={true}`. Trigger button is
   `disabled` (or click/Enter/Space ignored).

**wave-E1**

---

### §E1-5 Dynamic field binding — activate `textField`, `valueField`, `childrenField` (audit P1)

These props are reserved in M1 but not consumed. Wave-E1 activates them:

```typescript
textField?: string       // default: 'text'    — display text property
valueField?: string      // default: 'value'   — value key property
childrenField?: string   // default: 'items'   — children array property
```

Implementation: replace all direct `item.text`, `item.value`, `item.items` reads with
`item[textField]`, `item[valueField]`, `item[childrenField]` (with fallbacks to defaults).

Data type broadens to support arbitrary shapes:

```typescript
// Updated — replaces DropDownTreeItem (which remains supported as the default shape)
type DropDownTreeData = Array<Record<string, unknown>>
```

The `DropDownTreeItem` interface remains the type for `data` when default field names are used.
When custom field names are supplied, `data: Array<Record<string, unknown>>` is the accepted type.

**wave-E1**

---

### §E1-6 Client-side filtering (audit P1)

Add to `DropDownTreeProps`:

```typescript
filterable?: boolean     // default: false — render a filter input above the tree
```

When `filterable={true}`:
- A text input renders above the tree panel (inside the popup).
- Filter text is applied as a case-insensitive substring match against `item[textField]`.
- Nodes whose subtrees contain a match are expanded and rendered; non-matching leaf nodes
  are hidden.
- Filter resets when the popup closes.
- `minLength` defaults to 1.

**wave-E1**

---

### §E1-7 Server-side filtering / `onFilterChange` (audit P1)

Add to `DropDownTreeProps`:

```typescript
onFilterChange?: (value: string) => void
```

When `onFilterChange` is provided: `filterable` defaults to `false` (client-side disabled).
Fires on every keystroke; host updates `data` and sets `loading={true}` during fetch.

**wave-E1**

---

### §E1-8 ARIA tree pattern — full keyboard + ARIA (closes G-DDT3/G-DDT4/G-DDT5; WCAG Level A)

**These are the most critical P1 gaps: G-DDT3/4/5 constitute zero ARIA structure and zero
keyboard access in M1 — WCAG 2.1.1 (keyboard) + 4.1.2 (name/role/value) Level A violations.**

**Trigger ARIA (on the `<button>` from §E1-2):**

| Attribute | Value |
|---|---|
| `role="combobox"` | literal |
| `aria-expanded` | `"true"` when panel open, `"false"` when closed |
| `aria-haspopup="tree"` | literal |
| `aria-controls` | `"{id}-tree"` |
| `aria-labelledby` or `aria-label` | provided by wrapping FormField or host-supplied label |

**Tree panel ARIA:**

| Attribute | Element | Value |
|---|---|---|
| `role="tree"` | Panel container `<div>` | literal |
| `id="{id}-tree"` | Panel container | matches trigger `aria-controls` |
| `aria-label` | Panel container | `"Select an option"` (or FormField label text) |

**Per-node ARIA (applied to each node `<div>`):**

| Attribute | Value | Notes |
|---|---|---|
| `role="treeitem"` | literal | |
| `aria-expanded` | `"true"` / `"false"` | Only present on nodes WITH children |
| `aria-selected` | `"true"` / `"false"` | For the currently selected node |
| `aria-disabled` | `"true"` | When `item.disabled` |
| `tabIndex` | `0` for focused node, `-1` for others | Roving tabindex pattern |

**Keyboard model (full tree keyboard; WCAG 2.1.1 §2.4.3):**

| Key | Behavior |
|---|---|
| `Enter` / `Space` (on trigger) | Open tree panel; move focus into tree |
| `ArrowDown` | Move focus to next visible tree node (depth-first, skip collapsed children) |
| `ArrowUp` | Move focus to previous visible tree node |
| `ArrowRight` | If node is collapsed with children: expand it. If already expanded: move focus to first child. |
| `ArrowLeft` | If node is expanded: collapse it. If collapsed or leaf: move focus to parent. |
| `Enter` (on node) | Select node; close panel; return focus to trigger |
| `Space` (on node) | Select node; close panel; return focus to trigger |
| `Home` | Move focus to first root-level node |
| `End` | Move focus to last visible node (last node in depth-first traversal) |
| `Escape` | Close panel; return focus to trigger without changing selection |
| `Tab` | Close panel; move focus outside component |

Roving tabindex: exactly one node has `tabIndex=0` at a time; all others have `tabIndex=-1`.
When the panel opens, focus moves to the currently-selected node (or first root node if nothing
selected).

**Closes:** G-DDT3 (trigger ARIA), G-DDT4 (node role/aria-expanded), G-DDT5 (keyboard navigation).

**wave-E1** — WCAG Level A, mandatory before v1-ship.

---

### §E1-9 P2 deferred (wave-E2+)

| Feature | Wave |
|---|---|
| `itemRender` (custom node) | wave-E2+ |
| `popupSettings` | wave-E2+ |
| Adaptive mode | wave-E2+ |
| Form integration (`name` + hidden input) | wave-E2+ |

# MultiSelectTree — Semantic Contract

- **Component:** MultiSelectTree
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./MultiSelectTree.Interaction.md) · [Accessibility](./MultiSelectTree.Accessibility.md) · [Styling](./MultiSelectTree.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiSelectTree.tsx`
- **Catalog row:** #87 MultiSelectTree (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled tree with multi-select checkboxes

---

## 1. Component purpose

**MultiSelectTree** — a multi-select dropdown with a hierarchical tree of checkboxes. Items can be expanded/collapsed. Supports a Select All option at the top. Selected values are shown in the trigger as a comma-separated list.

---

## 2. Props

```typescript
interface MultiSelectTreeProps {
  value?: Array<string | number>       // controlled; selected values
  defaultValue?: Array<string | number>  // default: []
  onValueChange?: (values: Array<string | number>) => void
  data: DropDownTreeItem[]             // required; hierarchical items
  textField?: string                   // reserved; M1 reads item.text directly
  valueField?: string                  // reserved; M1 reads item.value directly
  childrenField?: string               // reserved
  checkAll?: boolean                   // default: false; show Select All checkbox
  placeholder?: string                 // default: 'Select items...'
  disabled?: boolean                   // default: false
  className?: string
}
```

---

## 3. Value model

`value` is a flat array of selected `item.value` primitives, regardless of tree depth. Both parent and leaf nodes can be selected independently.

---

## 4. Display in trigger

Trigger shows `selectedLabels.join(', ')` where `selectedLabels` are the `item.text` values of all selected items (searched via `flattenItems`). When nothing selected, shows placeholder.

---

## 5. `textField`, `valueField`, `childrenField`

Accepted but not consumed in M1. Reserved for future data-binding.

---

## Kendo-parity expansion (2026-06-11 — spec-first; see _shared/design/polish/kendo-spec-audit/dropdowns.md)

**Status:** Draft — promote to Accepted on council review.
**Wave:** wave-E1 (dropdowns validation + event surface pass).
**Audit baseline:** MultiSelectTree ~38% Kendo-minimum coverage. P1 misses: filtering (client +
server), full keyboard navigation (G-MST2 + arrow nav), ARIA tree pattern (G-MST3/G-MST4/G-MST5),
click-outside close (G-MST1), `textField`/`valueField`/`childrenField`/`checkField` dynamic binding,
`loading`, `valid`/`required`.

> **Architecture note (same as DropDownTree):** M1 trigger is a `<div>` (G-MST3). All keyboard
> and ARIA work requires the trigger to become a `<button type="button">`. This is a prerequisite.
> Additionally, click-outside close (G-MST1) must be added — the tree panel has no outside-click
> dismissal in M1.

---

### §E1-1 FR-1 adoption — validation contract (family ruling FR-1)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-1.

Add to `MultiSelectTreeProps`:

```typescript
required?: boolean   // default: false
error?: boolean      // default: false
```

`required` → `aria-required="true"` on trigger button (after §E1-2 trigger fix).
`error` → `aria-invalid="true"` on trigger + error ring on wrapper.
`validationMessage` NOT added — FR-1 §3.

**wave-E1**

---

### §E1-2 FR-2 adoption — focus + popup events + trigger fix (family ruling FR-2; closes G-MST3 partial)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-2.

Add to `MultiSelectTreeProps`:

```typescript
onFocus?: React.FocusEventHandler<HTMLButtonElement>
onBlur?: React.FocusEventHandler<HTMLButtonElement>
open?: boolean
onOpenChange?: (open: boolean) => void
```

**Trigger fix:** Change trigger from `<div>` to `<button type="button">` — same rationale
as DropDownTree §E1-2. Prerequisite for all keyboard/ARIA work.

**Click-outside close (closes G-MST1):** Use a `useClickOutside` hook (or Radix Popover wrapper)
to dismiss the tree panel when the user clicks outside the component boundary. Closes G-MST1.

**wave-E1**

---

### §E1-3 FR-3 size axes (audit P2 — wave-E1 aligned)

Add to `MultiSelectTreeProps`:

```typescript
size?: 'sm' | 'md' | 'lg'
fillMode?: 'solid' | 'outline' | 'flat'
rounded?: 'none' | 'sm' | 'md' | 'lg' | 'full'
```

Apply to the trigger button.

**wave-E1**

---

### §E1-4 Loading state with popup suppression (audit P1)

Add to `MultiSelectTreeProps`:

```typescript
loading?: boolean   // default: false
```

1. Animated spinner in trigger trailing slot.
2. `aria-busy="true"` on trigger button.
3. Popup suppression: tree panel does not open while `loading={true}`.

**wave-E1**

---

### §E1-5 Dynamic field binding — activate reserved props (audit P1)

Activate `textField`, `valueField`, `childrenField`. Add `checkField`:

```typescript
textField?: string      // default: 'text'
valueField?: string     // default: 'value'
childrenField?: string  // default: 'items'
checkField?: string     // default: undefined — when set, checkbox checked state reads item[checkField]
```

Replace direct `item.text`, `item.value`, `item.items` reads with field-mapped access.

`checkField` supports external check-state binding (Kendo's `checkIndeterminateField` analog is
wave-E2+ — see §E1-9).

**wave-E1**

---

### §E1-6 Client-side filtering (audit P1)

Add to `MultiSelectTreeProps`:

```typescript
filterable?: boolean   // default: false
```

Filter input renders above the tree panel. Case-insensitive substring match on `item[textField]`.
Matching logic same as DropDownTree §E1-6: nodes on the path to a match are expanded and shown;
non-matching leaf nodes hidden. Filter resets on close.

**wave-E1**

---

### §E1-7 Server-side filtering / `onFilterChange` (audit P1)

Add to `MultiSelectTreeProps`:

```typescript
onFilterChange?: (value: string) => void
```

When provided: client-side filtering disabled; fires on every keystroke; host updates `data`
and sets `loading={true}` during fetch.

**wave-E1**

---

### §E1-8 ARIA tree pattern — full keyboard + checkbox ARIA (closes G-MST3/G-MST4/G-MST5; WCAG Level A)

**Trigger ARIA (on `<button>`):**

| Attribute | Value |
|---|---|
| `role="combobox"` | literal |
| `aria-expanded` | `"true"` / `"false"` |
| `aria-haspopup="tree"` | literal |
| `aria-controls` | `"{id}-tree"` |
| `aria-multiselectable` | `"true"` (in trigger description; the tree itself carries this) |

**Tree panel ARIA:**

| Attribute | Element | Value |
|---|---|---|
| `role="tree"` | Panel container | literal |
| `aria-multiselectable="true"` | Panel container | literal |
| `id="{id}-tree"` | Panel container | matches trigger aria-controls |

**Per-node ARIA:**

| Attribute | Value | Notes |
|---|---|---|
| `role="treeitem"` | literal | on the node `<div>` or `<li>` |
| `aria-expanded` | `"true"` / `"false"` | Only on nodes WITH children |
| `aria-checked` | `"true"` / `"false"` / `"mixed"` | Multi-select uses `aria-checked` (not `aria-selected`) |
| `aria-disabled` | `"true"` | When `item.disabled` |

**Checkbox label fix (closes G-MST4):** Each `<input type="checkbox">` MUST have an associated
label. Options:
1. Use `htmlFor` + `id` linking the checkbox to the adjacent `<span>` text.
2. Add `aria-label={item[textField]}` directly to the checkbox.

Option 2 is simpler for the hand-rolled implementation. Remove any "no label" condition from G-MST4.

**Keyboard model (full tree multi-select; WCAG 2.1.1):**

| Key | Behavior |
|---|---|
| `Enter` / `Space` (on trigger) | Open tree panel; focus first selected node or first root node |
| `ArrowDown` | Move focus to next visible node |
| `ArrowUp` | Move focus to previous visible node |
| `ArrowRight` | Expand collapsed node (if has children); else move to first child |
| `ArrowLeft` | Collapse expanded node; else move to parent |
| `Space` (on node) | Toggle checkbox for that node |
| `Enter` (on node) | Toggle checkbox; does NOT close panel (multi-select stays open for additional picks) |
| `Home` | Focus first root node |
| `End` | Focus last visible node |
| `Escape` | Close panel; return focus to trigger |
| `Tab` | Close panel; move focus outside component |

Roving tabindex: same as DropDownTree (exactly one node `tabIndex=0` at a time).

**Closes:** G-MST2 (trigger keyboard-focusable + arrow nav), G-MST3 (trigger role/ARIA),
G-MST4 (checkbox label association), G-MST5 (tree role on container).

**wave-E1** — WCAG Level A, mandatory before v1-ship.

---

### §E1-9 P2 deferred (wave-E2+)

| Feature | Wave |
|---|---|
| `indeterminate` parent state (`checkIndeterminateField`) | wave-E2+ |
| Adaptive mode | wave-E2+ |
| Form integration (hidden inputs per selected value) | wave-E2+ |

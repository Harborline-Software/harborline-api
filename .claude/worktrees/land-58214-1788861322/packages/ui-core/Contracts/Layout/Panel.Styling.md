# Panel — Styling Contract

- **Component:** Panel
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Panel.Semantic.md) · [Interaction](./Panel.Interaction.md) · [Accessibility](./Panel.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Panel.tsx`
- **Catalog row:** #95 Panel (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 — updated Cohort 1 (2026-06-12) to add `mode="accordion"` + `resizable`

---

## 1. Static mode

### 1.1 Root container

`rounded-lg overflow-hidden` + variant class + `className` passthrough.

**Variant classes:**

| variant | Class |
|---|---|
| `default` | `bg-white border border-gray-200` |
| `filled` | `bg-gray-50 border border-gray-200` |
| `bordered` | `bg-white border-2 border-gray-300` |
| `elevated` | `bg-white shadow-md border border-gray-100` |

> **M1 note:** All colors hardcoded — not design tokens.

### 1.2 Header div (when `header` provided)

`border-b border-gray-200` + padding class.

When `padding='none'`: uses `'sm'` padding fallback (`p-3`).

### 1.3 Body div

Padding class only.

### 1.4 Footer div (when `footer` provided)

`border-t border-gray-200 bg-gray-50` + padding class.

When `padding='none'`: uses `'sm'` padding fallback (`p-3`).

### 1.5 Padding classes

| padding | Class |
|---|---|
| `none` | `''` (empty) |
| `sm` | `p-3` |
| `md` | `p-4` |
| `lg` | `p-6` |

---

## 2. Accordion mode

### 2.1 Root container

`border border-border rounded-md overflow-hidden divide-y divide-border` + `className` passthrough.

### 2.2 Toggle / leaf button

Base: `flex items-center gap-2 w-full text-left text-sm font-medium px-3 py-2.5 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring hover:bg-accent disabled:opacity-50 disabled:cursor-not-allowed`

Nested items: `paddingLeft` inline style at `(depth + 1) * 12px` for depth > 0.

### 2.3 Expand indicator span

`shrink-0 transition-transform duration-200 text-muted-foreground`

When expanded: adds `rotate-90`.

### 2.4 Child container (when expanded)

`border-l-2 border-border ml-3`

### 2.5 Leaf content area (when item has `content` and no children)

`px-4 py-3 text-sm border-t border-border bg-muted/30`

---

## 3. Resize handles

### 3.1 Handle element

Base: `absolute bg-transparent hover:bg-blue-400/30 focus:bg-blue-400/40 focus:outline-none transition-colors`

**Position + cursor by direction:**

| direction | Position classes | Cursor |
|---|---|---|
| `right` | `top-0 right-0 h-full w-2` | `cursor-ew-resize` |
| `bottom` | `bottom-0 left-0 w-full h-2` | `cursor-ns-resize` |
| `corner` | `bottom-0 right-0 w-4 h-4` | `cursor-nwse-resize` |

> **M1 note:** `blue-400` hardcoded — not design token.

---

## 4. Root element when resizable

When `resizable` prop is set: root element gets `position: relative` (inline style) to anchor absolutely-positioned handles. Width/height inline styles are set when Panel has an explicit or internal size.

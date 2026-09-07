# DescriptionList — Styling Contract

- **Component:** DescriptionList
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DescriptionList.Semantic.md) · [Interaction](./DescriptionList.Interaction.md) · [Accessibility](./DescriptionList.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/DescriptionList.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer container (`<dl>`)

`className` passthrough. No base classes applied to `<dl>` itself.

---

## 2. Stacked layout (default)

Item wrapper: `divide-y divide-gray-100`

Each item `<div>`: `py-3` (normal) or `py-2` (compact)

`<dt>`: `text-sm font-medium text-gray-500`

`<dd>`: `mt-1 text-sm text-gray-900`

Striped: odd items get `bg-gray-50` on the wrapper `<div>`.

---

## 3. Inline layout

Item wrapper: `divide-y divide-gray-100`

Each item `<div>`: `flex items-baseline gap-4 py-3` (normal) or `py-2` (compact)

`<dt>`: `text-sm font-medium text-gray-500 w-40 shrink-0`

`<dd>`: `text-sm text-gray-900`

Striped: same as stacked — odd items `bg-gray-50`.

---

## 4. Grid layout

Container: `grid gap-x-6 gap-y-4` + `sm:grid-cols-{columns}` (1, 2, or 3)

Each item `<div>`: no divider

`<dt>`: `text-sm font-medium text-gray-500`

`<dd>`: `mt-1 text-sm text-gray-900`

Striped: not applicable for grid layout (no alternating row structure).

---

## 5. Design token deviation

All colors are hardcoded Tailwind values (`gray-500`, `gray-900`, `gray-100`, `gray-50`). M2 will replace with design tokens (`text-muted-foreground`, `text-foreground`, `border`, `muted`).

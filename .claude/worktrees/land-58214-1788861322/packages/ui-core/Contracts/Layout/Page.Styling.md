# Page — Styling Contract

- **Component:** Page
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Page.Semantic.md) · [Interaction](./Page.Interaction.md) · [Accessibility](./Page.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Page.tsx`
- **Catalog row:** Page (`app-priority: high`, `library-scope: in-scope`)
- **Phase:** ADR 0017-A1 reference implementation (2026-06-18)

---

## 1. Root

`<div>` — `flex h-full min-h-0 flex-col`. Full-height flex column so the body can grow and scroll
inside it. `min-h-0` is load-bearing: without it a flex child won't shrink below its content size
and `overflow-auto` on the body never engages.

---

## 2. Header

Delegated to {@link PageHeader} (see PageHeader.Styling). `sticky` (default `true`) is forwarded so
the header pins while the body scrolls.

---

## 3. Body

`<section>` — `flex-1 overflow-auto` plus a padding class from `bodyPadding`:

| `bodyPadding` | class |
| --- | --- |
| `none` | `p-0` |
| `sm` | `p-3` |
| `md` (default) | `p-6` |
| `lg` | `p-8` |

`bodyClassName` is merged onto the `<section>` for per-route overrides.

---

## 4. className passthrough

`className` is merged onto the root `<div>`; `...rest` is forwarded there too.

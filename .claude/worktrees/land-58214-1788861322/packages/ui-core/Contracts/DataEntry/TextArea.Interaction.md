# TextArea — Interaction Contract

- **Component:** TextArea
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TextArea.Semantic.md) · [Accessibility](./TextArea.Accessibility.md) · [Styling](./TextArea.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/TextArea.tsx`
- **Catalog row:** #133 TextArea (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Change handling

Native textarea `onChange` → extracts `e.target.value` → fires `onChange(string)` → updates `len` state for counter.

---

## 2. Resize behavior

| resize | Effect |
|---|---|
| `none` | `resize-none` |
| `vertical` | `resize-y` |
| `horizontal` | `resize-x` |
| `both` | `resize` |

When `autoResize=true`: always `resize-none` (overrides `resize` prop). Height auto-grow is not implemented.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TA1 | High | `aria-invalid` not set when `invalid=true` — only `border-destructive` class is applied | Accepted-risk M1 |
| G-TA2 | Medium | `autoResize=true` sets `resize-none` but does not auto-grow height — the prop is misleading | Accepted-risk M1 |
| G-TA3 | Low | No `aria-describedby` prop — callers relying on ...props spread can add it, but it's not documented | Accepted-risk M1 |

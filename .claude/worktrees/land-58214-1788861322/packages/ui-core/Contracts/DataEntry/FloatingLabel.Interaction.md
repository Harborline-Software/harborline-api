# FloatingLabel — Interaction Contract

- **Component:** FloatingLabel
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FloatingLabel.Semantic.md) · [Accessibility](./FloatingLabel.Accessibility.md) · [Styling](./FloatingLabel.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FloatingLabel.tsx`
- **Catalog row:** #61 FloatingLabel (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Label animation trigger

Label animation is CSS-only. No JavaScript event handlers are added.

- **Label floated** (up, small): when the input is focused (`peer-focus`) or has a non-empty value (NOT `:placeholder-shown` — the injected `placeholder=" "` ensures the browser triggers `:placeholder-shown` only when the input is truly empty)
- **Label placeholder position** (down, full size): when the input is unfocused AND empty (`:placeholder-shown`)

---

## 2. No interaction state management

FloatingLabel has no local state. All interaction behavior is delegated to the cloned child's own interaction handlers. FloatingLabel only adds CSS classes.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-FL1 | Medium | `placeholder=" "` injection overwrites the child's existing placeholder text — callers cannot display a real placeholder hint | Accepted-risk M1 |
| G-FL2 | Low | No animation when child input has `defaultValue` (uncontrolled initial value) — label starts in placeholder position then snaps on first render | Accepted-risk M1 |

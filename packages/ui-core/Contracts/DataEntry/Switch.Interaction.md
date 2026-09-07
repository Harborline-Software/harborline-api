# Switch — Interaction Contract

- **Component:** Switch
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Switch.Semantic.md) · [Accessibility](./Switch.Accessibility.md) · [Styling](./Switch.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Switch.tsx`
- **Catalog row:** #130 Switch (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M2 (B1 council migration 2026-06-12; M1 superseded)

---

## 1. Toggle action

`toggle()` is called via:
- `onClick` on the `<button role="switch">`
- `onKeyDown` on the `<button>` — Space or Enter keys

When `disabled=true`, `toggle()` returns early.

`toggle()`:
1. Computes `next = !isOn`
2. Updates internal state (uncontrolled only)
3. Calls `onCheckedChange(next)` (canonical name)
4. Calls deprecated `onChange(next)` if provided (one-wave alias)

---

## 2. Keyboard behavior (M2)

The `<button role="switch">` is natively keyboard-focusable. The component adds an explicit `onKeyDown` handler that intercepts:
- `Space` → calls `toggle()` (browser default action on button is `click`, so `preventDefault()` prevents double-fire)
- `Enter` → calls `toggle()` (non-default for `<button type="button">`, but canonical for switch widgets)

This closes G-SWI1 — both Space and Enter now activate the switch directly without relying on a hidden checkbox.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SWI1 | Medium | `role="switch"` on a `<span>` with `onClick` is not keyboard-reachable directly | **CLOSED — M2 migration 2026-06-12** |
| G-SWI2 | Low | No `aria-label` or `aria-describedby` prop — callers cannot link description text directly | Accepted-risk M2; callers use `id`+external `<label>` for association |

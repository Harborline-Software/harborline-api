# FlatColorPicker — Interaction Contract

- **Component:** FlatColorPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FlatColorPicker.Semantic.md) · [Accessibility](./FlatColorPicker.Accessibility.md) · [Styling](./FlatColorPicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FlatColorPicker.tsx`
- **Catalog row:** #62 FlatColorPicker (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine (showActions=true)

```
PREVIEWING
  → interact with gradient/palette → setPending(color) [no commit yet]
  → click Apply → apply(): setInternal(pending), onValueChange(pending)
  → click Cancel → cancel(): setPending(current) [reset pending]
```

---

## 2. State machine (showActions=false)

```
→ interact with gradient/palette → setPending(color) AND onValueChange(color)
[No Apply/Cancel buttons — immediate commit]
```

---

## 3. Tab switching

Clicking tab buttons sets `tab` state. Sub-view switches instantly. `pending` is preserved across view switches.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-FCP1 | Low | Tab buttons have no `aria-selected` or `role="tab"` — tab pattern not ARIA-conformant | Accepted-risk M1 |

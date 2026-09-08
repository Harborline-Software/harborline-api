# FieldWrapper — Interaction Contract

- **Component:** FieldWrapper
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FieldWrapper.Semantic.md) · [Interaction](./FieldWrapper.Interaction.md) · [Accessibility](./FieldWrapper.Accessibility.md) · [Styling](./FieldWrapper.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FieldWrapper.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

FieldWrapper is a purely structural layout shell. It has no interactive
behaviour of its own. This contract documents the small interaction surfaces
that emerge from its composition: the `<label>` click-to-focus linkage, the
`ErrorLabel role="alert"` announcement, and the CSS-selector-based border
treatment.

---

## 2. Label click

`<Label htmlFor={id}>` renders a native `<label>`. Clicking the label focuses
the child input whose `id` matches the `htmlFor`. This is native browser
behaviour — FieldWrapper neither adds nor intercepts it.

---

## 3. Error label announcement

`<ErrorLabel>` renders `<p role="alert">`. When the `error` prop changes from
`undefined` / empty to a non-empty string:

- The `role="alert"` element enters the DOM (or is populated with text).
- AT announces the error text immediately (live region polite equivalent,
  but `alert` is assertive by default — it interrupts current speech).

Hosts should be careful not to flash the error label rapidly; rapid
`role="alert"` population can be disruptive to AT users.

---

## 4. Hint vs error display

FieldWrapper does not animate between hint and error. The transition is
instantaneous (React reconciliation removes one and adds the other).

---

## 5. Validity border treatment

The validity state is computed once at render:

```typescript
const isInvalid = valid === false || Boolean(error)
```

Child inputs receive error or success borders via Tailwind descendant selectors.
This is not triggered by user interaction — it is driven entirely by the props.

---

## 6. No internal state

FieldWrapper has zero internal state. It is a pure render function of its
props.

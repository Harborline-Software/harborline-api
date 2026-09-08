# ValidationMessage — Accessibility Contract

- **Component:** ValidationMessage
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ValidationMessage.Semantic.md) · [Interaction](./ValidationMessage.Interaction.md) · [Styling](./ValidationMessage.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ValidationMessage.tsx`
- **Catalog row:** #145 ValidationMessage (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="alert"` | `<p>` | Assertive live region — announces on mount |
| `id="${for}-error"` | `<p>` | When `for` prop is provided |

---

## 2. Live region behavior

`role="alert"` maps to `aria-live="assertive"` + `aria-atomic="true"`. When this component mounts (message appears), AT immediately reads the message text interrupting the current reading flow.

---

## 3. id for aria-describedby

When `for="fieldId"` is set, the rendered `id="fieldId-error"` allows the associated input to set `aria-describedby="fieldId-error"`. This is the recommended usage pattern.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-VM1 | Low | When message changes (not mount/unmount), `role="alert"` may not re-announce in all browsers/AT combinations | Accepted-risk M1 |

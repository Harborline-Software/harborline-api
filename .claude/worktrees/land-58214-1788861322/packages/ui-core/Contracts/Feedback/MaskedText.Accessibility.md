# MaskedText — Accessibility Contract

- **Component:** MaskedText
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MaskedText.Semantic.md) · [Interaction](./MaskedText.Interaction.md) · [Styling](./MaskedText.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/MaskedText.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Attribute | Element | Value |
| --- | --- | --- |
| `aria-label` | Value `<span>` | `label` prop when provided; `value` when revealed; `'hidden value'` when masked |
| `aria-label` | Toggle `<button>` | **Toggles** — `'Show value'` (masked state) / `'Hide value'` (revealed state) |
| `aria-pressed` | Toggle `<button>` | `false` (masked) / `true` (revealed) |
| `aria-hidden="true"` | Eye / eye-slash SVG icons | Icons are decorative |

### 1.1 Toggle button ARIA requirements

The `aria-label` on the toggle button MUST change with the state:
- Masked → `aria-label="Show value"` (action: clicking will SHOW)
- Revealed → `aria-label="Hide value"` (action: clicking will HIDE)

The `aria-pressed` attribute MUST also toggle:
- Masked → `aria-pressed="false"`
- Revealed → `aria-pressed="true"`

Both signals together ensure AT announces both the action and the current state:
`"Show value, button, not pressed"` (masked) / `"Hide value, button, pressed"` (revealed).

**Do NOT keep `aria-label` as `'Show value'` when the value is already revealed** — this is a critical AT confusion point: activating a button labeled "Show value" while the value is already visible makes no sense to AT users.

---

## 2. Keyboard behavior

| Key | Effect |
| --- | --- |
| `Tab` | Moves focus to the toggle button |
| `Space` / `Enter` | Activates the toggle (show/hide) |

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-MT3 | Low | The masked display string (e.g., "••••7890") is read by AT character-by-character — may be verbose or confusing without a proper `aria-label` | Mitigated by `aria-label` fallback to `'hidden value'` |
| G-MT4 | Low | No `aria-live` on the value span — reveal state change is not announced to AT | Accepted-risk M1 |
| G-MT5 | High | When revealed, the value span `aria-label` is set to the actual `value` prop — screen reader announces the sensitive value (CC number, SSN, etc.) aloud in the AT output stream. In shared environments or with AT output logging enabled, this is a PII exposure vector. Hosts should supply a `label` prop that describes type/purpose (e.g., `"Credit card number"`) so that AT announces the label rather than the raw value. The component MUST document that `label` is preferred over relying on the default value-based aria-label. | Accepted-risk M1; host MUST supply `label` prop for sensitive value components |
| G-MT6 | High | M1 implementation bug: toggle button `aria-label` does not change on state toggle — stays `'Show value'` even when value is revealed. AT users hear "Show value" on a button that would hide the value. | [RESOLVED spec 2026-06-06] §1.1: `aria-label` toggle + `aria-pressed` are now spec'd as required; M1 impl must be fixed to follow spec |

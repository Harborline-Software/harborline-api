# Highlight — Accessibility Contract

- **Component:** Highlight
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Highlight.Semantic.md) · [Interaction](./Highlight.Interaction.md) · [Accessibility](./Highlight.Accessibility.md) · [Styling](./Highlight.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Highlight.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

`Highlight` renders a `<mark>` element. This is semantically significant: `<mark>` carries AT meaning (some screen readers announce "highlighted" or apply a different reading style). This contract pins the correct usage and the cross-channel signal requirement.

---

## 2. Root element — `<mark>`

`Highlight` renders as `<mark>`, not `<span>`. This is the correct HTML semantic for "text marked for reference" or "text matching a search query".

| Attribute | Value | Notes |
|---|---|---|
| Element | `<mark>` | Native HTML; AT may announce "highlighted" depending on SR configuration |
| Implicit role | (none in ARIA spec; `<mark>` is a phrasing element) | Some SR (NVDA) announce `<mark>` as "highlighted ... end highlighted" |
| `aria-label` | Not set by default | Children text is the accessible name |

**Note on SR announcement:** Screen reader announcement of `<mark>` is inconsistent across SR/browser combinations. NVDA+Chrome may announce the highlight; JAWS may not. Hosts relying on AT to announce the highlight for critical information should not rely on `<mark>` alone — they should also provide a non-visual alternative (see §4).

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships.

---

## 3. Cross-channel signal requirement

`Highlight` uses background colour as its primary visual signal. WCAG 2.2 SC 1.4.1 Use of Color applies.

For `SearchHighlight`, the highlighted text is also present as normal text — so the meaning is conveyed by the text content itself, not by colour alone. This is acceptable.

For manually-authored `<Highlight>` where colour indicates a category (e.g., green = "approved", yellow = "pending review"), the host MUST ensure a non-colour channel carries the meaning (tooltip, label, or adjacent icon).

---

## 4. Keyboard

`Highlight` and `SearchHighlight` are non-interactive. No keyboard contract applies.

---

## 5. Color contrast

Highlighted text must contrast sufficiently against the coloured background per WCAG 2.2 SC 1.4.3 (4.5:1 for normal text).

| `color` | Background (Tailwind) | Text (Tailwind) | Approx contrast on bg |
|---|---|---|---|
| `yellow` | `bg-yellow-100` | `text-yellow-900` | ~11:1 — passes |
| `green` | `bg-green-100` | `text-green-900` | ~10:1 — passes |
| `blue` | `bg-blue-100` | `text-blue-900` | ~10:1 — passes |
| `orange` | `bg-orange-100` | `text-orange-900` | ~10:1 — passes |
| `pink` | `bg-pink-100` | `text-pink-900` | ~10:1 — passes |
| `purple` | `bg-purple-100` | `text-purple-900` | ~10:1 — passes |

All standard colour combinations pass WCAG 2.2 SC 1.4.3 at the tinted-palette level. No known contrast gaps.

---

## 6. Known gaps

| # | Item | Notes |
|---|---|---|
| A1 | `<mark>` SR announcement is inconsistent across screen readers | Not fixable at component level; document as known SR limitation |
| A2 | Colour-only semantic use of `Highlight` (e.g., green = approved) is not self-explanatory | Host responsibility to add non-colour channel |

# PropertyCard — Accessibility Contract

- **Component:** PropertyCard
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PropertyCard.Semantic.md) · [Interaction](./PropertyCard.Interaction.md) · [Accessibility](./PropertyCard.Accessibility.md) · [Styling](./PropertyCard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/PropertyCard.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

PropertyCard is a non-interactive display card. Its accessibility contract covers the structure of the card content (headings, labels, status badge) and delegates interaction accessibility to the host's `actions` slot content.

---

## 2. Card structure

PropertyCard renders a `<div>` — not a `<article>` or `<section>`. No landmark role is applied.

**Recommendation:** For property grids where each card is a distinct entity, the card container SHOULD carry `role="article"` or be wrapped in a `<ul>/<li>` structure so AT users can navigate between cards.

**Known gap (A1):** No landmark role on card. SR users cannot distinguish or navigate between cards in a grid.

---

## 3. Content structure

| Content | Element | Notes |
|---|---|---|
| Address | `<p class="truncate ...">` | Primary identifying text; no heading role |
| City/State | `<p class="text-muted-foreground">` | Subtext |
| Status badge | `<span class="rounded-full ...">` | Non-interactive pill; no `role` |
| Unit count | `<span class="text-xs ...">` | Descriptive text |
| Property name | `<span class="font-mono ...">` | Code/identifier |

**Gap:** There is no heading element (`<h2>`, `<h3>`) for the address. In a card-based list, the address is effectively the card's heading. AT users navigating by heading cannot identify individual cards.

**Known gap (A2):** No heading element on the address. Hosts using a grid of PropertyCards should consider wrapping the address in an appropriate heading level.

---

## 4. Status badge colour

The status badge uses colour as a visual differentiator. WCAG 2.2 SC 1.4.1 Use of Color requires a non-colour channel.

The status text IS the label (e.g., "Active", "Vacant") — this satisfies the non-colour requirement. The colour adds visual emphasis only.

---

## 5. Actions slot accessibility

The `actions` slot's accessibility is entirely the host's responsibility. The slot renders after a `border-t` divider. Hosts must ensure:

- Action buttons have accessible labels.
- Buttons meet WCAG 2.2 SC 2.5.8 touch target size (24×24px minimum).
- Focus order within actions is logical.

---

## 6. Keyboard

PropertyCard's card body is non-interactive and not focusable. The `actions` slot's buttons receive focus via Tab in natural DOM order.

---

## 7. Known gaps

| # | Item | Severity | Resolution path |
|---|---|---|---|
| A1 | No landmark role on card root | Medium | Add `role="article"` or use `<li>` in a list structure at the host level |
| A2 | Address is not a heading element | Medium | Host wraps address in appropriate heading level, or component adds a `headingLevel?: 2 \| 3 \| 4` prop |
| A3 | Truncated address text has no `title` tooltip | Low | Add `title={address}` to the address `<p>` for full text on hover |

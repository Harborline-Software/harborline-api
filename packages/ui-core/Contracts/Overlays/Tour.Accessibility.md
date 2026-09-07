# Tour — Accessibility Contract

- **Component:** Tour / GuidedWalkthrough
- **ADR 0017 family:** Overlays
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Tour.Semantic.md) · [Interaction](./Tour.Interaction.md) · [Styling](./Tour.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Tour.tsx`
- **Catalog row:** #A7 Tour / GuidedWalkthrough (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Ant Design Tour baseline)

---

## 1. Popover role

Tour popover: `role="dialog"` with `aria-modal="true"`. `aria-label` or `aria-labelledby` points to the step title.

---

## 2. Focus management

On step entry, focus moves to the popover (or its first focusable child — the Next/Prev button). Focus is trapped within the popover while the step is active. On close, focus returns to the element that had focus before the Tour opened.

---

## 3. Step progress announcement

Step change announces to AT: `aria-live="polite"` region outside the dialog reports "Step {n} of {total}: {title}".

---

## 4. Navigation buttons

Previous / Next / Finish / Close buttons are standard `<button>` elements with descriptive labels. When Previous is disabled on step 0, `disabled` attribute is set.
Step indicators are named buttons with a minimum 24×24 CSS-pixel target (WCAG 2.5.8).

---

## 5. Mask

The mask layer carries `aria-hidden="true"` — it is decorative. The spotlight cutout is also `aria-hidden`.

---

## 6. Keyboard navigation

- `Escape` closes the Tour; focus returns to trigger.
- `Tab` cycles within the popover (trapped).
- `ArrowRight` / `ArrowLeft` navigate steps (described in Interaction contract).

---

## 7. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TOUR-A1 | Medium | Background page content is masked but not inert — screen reader can still navigate behind the Tour overlay | Accepted-risk M1; add `inert` attribute to `<body>` children (excluding Tour portal) in M2 |

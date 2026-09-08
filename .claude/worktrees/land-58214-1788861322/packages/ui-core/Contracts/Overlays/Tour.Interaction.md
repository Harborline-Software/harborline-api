# Tour — Interaction Contract

- **Component:** Tour / GuidedWalkthrough
- **ADR 0017 family:** Overlays
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Tour.Semantic.md) · [Accessibility](./Tour.Accessibility.md) · [Styling](./Tour.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Ant Design Tour baseline)
- **Catalog row:** #A7 Tour / GuidedWalkthrough (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Ant Design Tour baseline)

---

## 1. Open / close

`open=true` mounts the Tour overlay and positions the first step. `onClose` fires on: Escape key, mask click (when `maskClosable=true`), or explicit Close button. Caller toggles `open`.

---

## 2. Step navigation

**Next**: advances to `current + 1`. On the last step, "Finish" replaces "Next" and calls `onFinish` then `onClose`.

**Previous**: retreats to `current - 1`. Disabled on the first step.

**Step indicators**: clicking a step dot jumps directly to that step (calls `onChange`).

---

## 3. Target tracking

When a step has a `target`, the Tour calculates the target element's bounding rect on step entry. The spotlight and popover position update on window resize. The Tour does NOT reposition on scroll (G-TOUR1).

---

## 4. Mask interaction

When `mask=true` and `maskClosable=true`: clicking outside the spotlight calls `onClose`. Clicking inside the spotlight passes through to the underlying element (pointer-events not blocked on the target area).

---

## 5. Keyboard

- `ArrowRight`: advance to next step.
- `ArrowLeft`: go to previous step.
- `Escape`: close tour.
- `Enter` on focused Next/Finish button: advances/finishes.
- `Tab`: cycles focus within the popover (focus trap per WAI-ARIA `role="dialog" aria-modal="true"`). MUST NOT be intercepted for step-advance — doing so breaks focus-trap requirements and leaves AT users unable to reach the Next/Finish button.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TOUR1 | Medium | Tour popover does not reposition on scroll — target element may scroll out of view while tour is open | Accepted-risk M1; consumers should disable page scroll when tour is active; scroll-aware repositioning deferred to M2 |
| G-TOUR2 | Low | Step transitions have no animation — popover jumps between positions instantly | Accepted-risk M1; CSS transition can be added in M2 |

# ErrorCard — Accessibility Contract

- **Component:** ErrorCard
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ErrorCard.Semantic.md) · [Interaction](./ErrorCard.Interaction.md) · [Styling](./ErrorCard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/ErrorCard.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Attribute | Element | Value |
| --- | --- | --- |
| `role="alert"` | Container `<div>` | Assertive live region; AT announces immediately on mount |

---

## 2. Live region behavior — and initial-render gotcha

`role="alert"` is an assertive live region. AT announces the ErrorCard content immediately when it **dynamically mounts** — i.e., when the DOM element is added after page load.

**Critical limitation: `role="alert"` does NOT fire for initially-rendered content.**

If ErrorCard is present in the initial page render (e.g., server-side error state, or synchronous error in first render), AT processes the initial DOM statically and does not trigger the live region. Users who load the page in an already-errored state hear nothing special from the error card — they must navigate to it manually.

**Workaround patterns (in order of preference):**

1. **Delayed mount via `useEffect`:** Render `null` on initial render, then mount ErrorCard in `useEffect(() => setMounted(true), [])`. The `useEffect` runs after hydration, forcing a DOM mutation that triggers `role="alert"`.

```tsx
// ErrorCard implementation:
const [mounted, setMounted] = React.useState(false)
React.useEffect(() => { setMounted(true) }, [])
if (!mounted) return null
return <div role="alert">...</div>
```

2. **`document.title` update:** When ErrorCard mounts, update `document.title` to include the error (e.g., `"Error — Page title"`). AT announces document title changes on most platforms.

3. **Page-level `aria-describedby`:** If the page knows it will load in an error state (e.g., from a server redirect), include `aria-describedby="error-card-id"` on the `<main>` element, and give ErrorCard a static `id`. AT announces described-by content when the region is focused.

The M1 implementation does NOT use any of these patterns — `role="alert"` is on the initial render output. This is a known functional gap.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-EC1 | High | `role="alert"` on initially-rendered content does not fire to AT — error state invisible to AT on page load | [RESOLVED spec 2026-06-06] §2: delayed-mount pattern + two alternative workarounds now spec'd; M1 impl must adopt `useEffect` mount |
| G-EC2 | Low | Retry button has no `aria-label` beyond visible text "Retry" — context from the error title is not associated | Accepted-risk M1 |
| G-EC3 | Low | `page` variant uses `<h2>` which may skip heading levels depending on page structure | Accepted-risk M1 |

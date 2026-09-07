# LoadingState — Accessibility Contract

- **Component:** LoadingState
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./LoadingState.Semantic.md) · [Interaction](./LoadingState.Interaction.md) · [Styling](./LoadingState.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/LoadingState.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Attribute | Element | Value |
| --- | --- | --- |
| `role="status"` | Outer element (`<div>` or `<p>`) | Announces the loading label to AT when the component mounts |

`role="status"` carries implicit `aria-live="polite"` — when LoadingState replaces content (the common pattern: `{loading ? <LoadingState /> : <Content />}`), AT announces the label text after any currently-speaking content finishes.

---

## 2. `aria-busy` placement — critical pattern

**`aria-busy` does NOT belong on the LoadingState component itself.**

`aria-busy="true"` belongs on the **container region that will eventually contain the loaded content**, not on a wrapper or on LoadingState itself. AT uses `aria-busy` to defer reading a region until loading completes; placing it on LoadingState means AT defers reading the spinner label — the exact opposite of intended behavior.

**Correct pattern:**
```tsx
<section aria-busy={loading} aria-label="Properties panel">
  {loading
    ? <LoadingState label="Loading properties…" />
    : <PropertiesTable />
  }
</section>
```

**Incorrect pattern:**
```tsx
<LoadingState label="Loading…" aria-busy="true" />  {/* WRONG */}
```

In the correct pattern:
- `aria-busy="true"` on the `<section>` tells AT "this region is loading; hold off on reading it in full"
- `role="status"` on LoadingState announces "Loading properties…" politely
- When `loading` flips to `false`, `aria-busy="false"` removes the deferral, and AT can read the now-loaded content

LoadingState itself MUST NOT render `aria-busy` — it is a placeholder, not a container for the content being loaded.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-LS1 | Medium | M1 implementation does not set `role="status"` — loading label not announced to AT on mount | [RESOLVED 2026-06-06] §1: `role="status"` is now spec'd |
| G-LS2 | Medium | M1 does not document `aria-busy` placement — risk of implementors placing `aria-busy` on LoadingState instead of the containing region | [RESOLVED 2026-06-06] §2: correct `aria-busy` placement pattern now spec'd |

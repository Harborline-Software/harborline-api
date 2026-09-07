# LoaderContainer — Styling Contract

- **Component:** LoaderContainer
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./LoaderContainer.Semantic.md) · [Interaction](./LoaderContainer.Interaction.md) · [Accessibility](./LoaderContainer.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/LoaderContainer.tsx`
- **Catalog row:** #80 LoaderContainer (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer container

`relative` — always. Provides the positioning context for the absolute overlay.

---

## 2. Overlay (loading=true)

`absolute inset-0 z-10 flex items-center justify-center`

---

## 3. Backdrop (overlay=true)

Applied to the overlay div in addition to §2 classes:
`bg-background/60 backdrop-blur-[1px]`

When `overlay=false`, no backdrop classes are added — the overlay div is transparent and shows only the centered spinner.

---

## 4. Spinner

No direct styling — delegated to `<Loader>` with forwarded `loaderProps`. The Loader renders centered within the overlay flex container via `items-center justify-center`.

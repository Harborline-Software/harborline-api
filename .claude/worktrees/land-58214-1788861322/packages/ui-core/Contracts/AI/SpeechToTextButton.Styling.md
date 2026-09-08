# SpeechToTextButton — Styling Contract

- **Component:** SpeechToTextButton
- **ADR 0017 family:** AI
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./SpeechToTextButton.Semantic.md) · [Interaction](./SpeechToTextButton.Interaction.md) · [Accessibility](./SpeechToTextButton.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A29 SpeechToTextButton (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik SpeechToTextButton baseline)

---

## 1. Idle state

`inline-flex items-center justify-center h-9 w-9 rounded-full border border-border bg-background text-muted-foreground hover:text-foreground hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:opacity-50 disabled:pointer-events-none transition-colors`.

Microphone icon: `h-4 w-4`.

---

## 2. Recording state

When `recording=true`: `border-destructive bg-destructive/10 text-destructive hover:bg-destructive/20`.

Active pulse indicator: outer ring `animate-ping absolute h-full w-full rounded-full bg-destructive opacity-30` (absolutely positioned sibling, `pointer-events-none`). Requires `position: relative` on the root button.

---

## 3. Unavailable state

When Web Speech API is unavailable: `opacity-40 cursor-not-allowed`. The button remains visible (not hidden) to communicate that voice input exists but is unsupported in the current browser.

---

## 4. Design tokens

Uses: `hsl(var(--background))`, `hsl(var(--border))`, `hsl(var(--muted))`, `hsl(var(--foreground))`, `hsl(var(--muted-foreground))`, `hsl(var(--destructive))`, `hsl(var(--ring))`.

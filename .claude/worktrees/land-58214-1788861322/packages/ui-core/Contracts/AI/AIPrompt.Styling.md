# AIPrompt — Styling Contract

- **Component:** AIPrompt
- **ADR 0017 family:** AI
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./AIPrompt.Semantic.md) · [Interaction](./AIPrompt.Interaction.md) · [Accessibility](./AIPrompt.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A25 AIPrompt (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik AIPrompt / KendoReact AI baseline)

---

## 1. Root

`flex flex-col gap-4 w-full`.

---

## 2. Input area

`relative flex flex-col gap-2 border border-border rounded-lg p-3 bg-card shadow-sm focus-within:ring-2 focus-within:ring-primary/30`. Textarea: `resize-none min-h-[80px] max-h-[240px] bg-transparent text-sm text-foreground placeholder:text-muted-foreground border-none outline-none`.

---

## 3. Send button

Positioned bottom-right of the input container. `h-8 w-8 rounded-full bg-primary text-primary-foreground flex items-center justify-center hover:bg-primary/90 disabled:opacity-40 disabled:pointer-events-none`.

---

## 4. Suggestion chips

`flex flex-wrap gap-2`. Each chip: `text-sm border border-border rounded-full px-3 py-1.5 bg-background hover:bg-muted cursor-pointer transition-colors`.

---

## 5. Output panel

`rounded-lg border border-border bg-card p-4`. Response markdown: `prose prose-sm max-w-none text-foreground`. Loading skeleton: `animate-pulse h-4 bg-muted rounded` repeated 3 lines. Streaming cursor: `inline-block w-[2px] h-[1em] bg-foreground align-middle animate-blink`.

---

## 6. Error state

`rounded-lg border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive`.

---

## 7. Design tokens

Uses: `hsl(var(--card))`, `hsl(var(--border))`, `hsl(var(--primary))`, `hsl(var(--primary-foreground))`, `hsl(var(--foreground))`, `hsl(var(--muted))`, `hsl(var(--muted-foreground))`, `hsl(var(--destructive))`.

---

## 8. Wave-N expansion — styling additions (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred).

### 8.1 Toolbar

Toolbar strip (above tab bar or inline for single-view): `flex items-center gap-1 border-b border-border px-3 py-1.5 bg-card`. Each toolbar item button: `h-7 w-7 flex items-center justify-center rounded text-muted-foreground hover:text-foreground hover:bg-muted text-xs transition-colors`. Toolbar is absent when `toolbarItems` is not provided.

### 8.2 Cancel button

Cancel button (rendered alongside or replacing send during `loading`/`streaming`): `h-8 px-3 rounded border border-border bg-background text-sm text-foreground hover:bg-muted transition-colors`. Positioned in the same bottom-right slot as the send button; the send button is hidden (`display: none`) while cancel is shown.

### 8.3 Streaming cursor in responses view

Same `inline-block w-[2px] h-[1em] bg-foreground align-middle animate-blink` pattern as §5. Applied only while `streaming={true}`; the cursor element is removed from the DOM when `streaming` transitions to `false`.

### 8.4 Commands view (active)

Active command item (keyboard focus): `bg-accent text-accent-foreground` (same as hover). Focused item: `ring-2 ring-inset ring-primary/40`.

### 8.5 Tab bar (with `role="tablist"`)

`flex border-b border-border`. Tab: `px-4 py-2 text-xs font-medium capitalize transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/40`. Active tab: `border-b-2 border-primary text-primary`. Inactive tab: `text-muted-foreground hover:text-foreground`. Tab underline uses `border-b-2 -mb-px` to overlap the parent `border-b` (standard tab underline trick).

### 8.6 RTL (`dir="rtl"`)

Send button moves to `self-start` of the flex row (left side in RTL). Tab bar text-alignment uses `text-right`. No other token changes needed — flex layout handles mirroring automatically.

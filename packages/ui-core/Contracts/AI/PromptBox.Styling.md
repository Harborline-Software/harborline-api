# PromptBox — Styling Contract

- **Component:** PromptBox
- **ADR 0017 family:** AI
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PromptBox.Semantic.md) · [Interaction](./PromptBox.Interaction.md) · [Accessibility](./PromptBox.Accessibility.md) · [Styling](./PromptBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/ai/PromptBox.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root wrapper

`flex flex-col gap-2`

## 2. Suggestions row

`flex flex-wrap gap-1.5`

## 3. Suggestion chip

```
text-xs px-2.5 py-1 rounded-full border border-input bg-background
hover:bg-accent transition-colors
```

Uses CSS-variable-backed Tailwind tokens: `border-input`, `bg-background`, `hover:bg-accent`.

## 4. Input wrapper

```
relative flex flex-col rounded-md border border-input bg-background
focus-within:ring-2 focus-within:ring-ring
```

Focus ring is applied at the wrapper level via `focus-within`, not on the `<textarea>` itself.

## 5. Textarea

```
resize-none bg-transparent p-3 text-sm outline-none
disabled:cursor-not-allowed
```

`rows={3}` (3 visible lines). `resize-none` — no user resize.

## 6. Footer bar

`flex items-center justify-between px-3 py-1.5 border-t border-border`

## 7. Character count

`text-xs text-muted-foreground`

## 8. Submit button

```
inline-flex items-center gap-1.5 px-3 py-1 rounded text-xs font-medium
bg-primary text-primary-foreground hover:bg-primary/90
disabled:opacity-50 disabled:cursor-not-allowed transition-colors
```

## 9. CSS variables used

| Variable | Purpose |
|---|---|
| `--border-input` | Input border colour |
| `--background` | Input background |
| `--ring` | Focus ring colour |
| `--border` | Footer divider border |
| `--muted-foreground` | Character count text |
| `--primary` | Submit button background |
| `--primary-foreground` | Submit button text |
| `--accent` | Suggestion chip hover background |

---

## 10. Wave-N expansion — styling additions (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred).

### 10.1 Attachment chips (topAffix zone)

Top affix container: `flex flex-wrap gap-1.5 px-3 pt-2 pb-0` (renders above the textarea, within the input wrapper, above the `border-t` footer). Rendered only when `attachments` is non-empty.

Each attachment chip: `flex items-center gap-1 text-xs bg-muted/60 border border-border rounded-full px-2 py-0.5 max-w-[160px]`. File name: `truncate text-foreground`. Remove button: `ml-0.5 h-4 w-4 flex items-center justify-center rounded-full text-muted-foreground hover:text-foreground hover:bg-muted transition-colors`.

Error chip variant: `border-destructive/40 bg-destructive/5`. Error file name: `text-destructive`.

Upload button (attach-file trigger): integrated as a `startAffix` slot button: `h-7 w-7 flex items-center justify-center rounded text-muted-foreground hover:text-foreground hover:bg-muted transition-colors`.

### 10.2 Speech to text button

Microphone button in `endAffix` slot: `h-7 w-7 flex items-center justify-center rounded text-muted-foreground hover:text-foreground hover:bg-muted transition-colors`. Recording state: `text-primary animate-pulse`.

### 10.3 Affix zones

`startAffix` (left of single-line input): `flex items-center pl-3 shrink-0`.

`endAffix` (right of input, before built-in buttons): `flex items-center pr-1 gap-0.5 shrink-0`.

`topAffix` (above textarea): see §10.1. When `topAffix` is a custom React node (not the default attachment chips), it renders in the same `px-3 pt-2 pb-0` container.

### 10.4 fillMode

`fillMode === 'solid'` (default): existing input wrapper styles (§4) — `border border-input`.

`fillMode === 'flat'`: remove `border border-input`; add `border-b border-input rounded-none` (single underline, no surrounding border). Background remains `bg-background`. Focus ring: `focus-within:ring-0 focus-within:border-b-2 focus-within:border-ring` (border-thickens instead of ring on flat mode).

### 10.5 Auto-grow mode (`mode === 'auto'`)

In auto-grow mode, the textarea gains `overflow-hidden resize-none transition-[height] duration-100` (height is set by JS in Interaction §8.2). The `rows={3}` default is NOT set in auto mode (height is fully JS-controlled).

### 10.6 Single-line mode (`mode === 'single'`)

Input wrapper: same as §4 but uses `flex-row items-center` instead of `flex-col`. The `<textarea>` is replaced by `<input type="text">` with `flex-1 bg-transparent text-sm outline-none py-0 h-9 px-3 disabled:cursor-not-allowed`. Footer bar (§6) is hidden in single-line mode (char count and submit button are in the affix area).

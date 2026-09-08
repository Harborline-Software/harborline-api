# InlineAIPrompt — Styling Contract

- **Component:** InlineAIPrompt
- **ADR 0017 family:** AI
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./InlineAIPrompt.Semantic.md) · [Interaction](./InlineAIPrompt.Interaction.md) · [Accessibility](./InlineAIPrompt.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A27 InlineAIPrompt (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik InlineAIPrompt baseline)

---

## 1. Trigger button (collapsed state)

`inline-flex items-center gap-1.5 h-7 px-3 rounded-full text-xs border border-border bg-background text-muted-foreground hover:text-foreground hover:bg-muted transition-colors`. Spark icon (AI indicator): `h-3.5 w-3.5`.

---

## 2. Input bar (expanded state)

`flex items-center gap-2 h-9 border border-primary/40 rounded-full px-3 bg-background shadow-sm focus-within:ring-2 focus-within:ring-primary/30 transition-all duration-150`. Input: `flex-1 text-sm bg-transparent outline-none text-foreground placeholder:text-muted-foreground`. Send button: `h-5 w-5 text-primary hover:text-primary/80 disabled:opacity-40`.

---

## 3. Response panel

Root container (wraps both trigger+input bar and response panel): add `relative` so the absolutely-positioned close button is contained. Response panel: `mt-1 rounded-lg border border-border bg-popover shadow-md p-3 text-sm text-popover-foreground max-w-[360px]`. Close button: `absolute top-2 right-2 h-5 w-5 text-muted-foreground hover:text-foreground`. Loading state: single `animate-pulse h-3 bg-muted rounded w-3/4`.

Error state variant: `border-destructive/50 bg-destructive/5 text-destructive`. Close button in error state: same absolute positioning; `text-destructive/70 hover:text-destructive`.

---

## 4. Design tokens

Uses: `hsl(var(--background))`, `hsl(var(--border))`, `hsl(var(--primary))`, `hsl(var(--muted))`, `hsl(var(--foreground))`, `hsl(var(--muted-foreground))`, `hsl(var(--popover))`, `hsl(var(--popover-foreground))`.

---

## 5. Wave-N expansion — styling additions (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred).

### 5.1 Output cards

Output panel scroll container: `flex flex-col gap-2 max-h-[300px] overflow-y-auto py-2`.

Each output card: `rounded-lg border border-border bg-card shadow-sm overflow-hidden`. Card header (when present): `flex items-start justify-between px-3 pt-3 pb-1`. Card body: `px-3 pb-2 text-sm text-card-foreground`. Card actions row: `flex items-center gap-1.5 px-3 py-2 border-t border-border bg-card/80`.

**Streaming card** — same as §3 loading state (`animate-pulse h-3 bg-muted rounded w-3/4`) until first token arrives; then progressive text with blinking cursor.

**Error card**: `border-destructive/40 bg-destructive/5`. Body: `text-destructive text-sm`.

### 5.2 Output action buttons

Default icon buttons (copy, discard): `h-6 w-6 flex items-center justify-center rounded text-muted-foreground hover:text-foreground hover:bg-muted text-xs transition-colors`.

Copy confirmed state (1.5s): `text-success` (maps to `hsl(var(--success))` if defined in token set; fallback: `text-green-600`).

### 5.3 Commands context menu

`⌘` trigger: `h-5 w-5 flex items-center justify-center rounded text-muted-foreground hover:text-foreground text-xs`. Located inside the input bar between `endAffix` slot and the generate button.

Commands popup: `min-w-[180px] rounded-lg border border-border bg-popover shadow-md py-1 z-50`. Command item: `flex items-center gap-2 px-3 py-1.5 text-sm text-popover-foreground hover:bg-accent cursor-pointer`. Command icon: `h-4 w-4 shrink-0 text-muted-foreground`.

### 5.4 Cancel/stop button

When replacing the generate button: same `h-5 w-5 text-primary hover:text-primary/80 disabled:opacity-40` styling as §2 send button but with a stop-square icon (`▪`) at 10px.

### 5.5 Multi-output scroll

When `outputs.length > 1`, the output panel shows a scroll gradient at the bottom edge: `after:absolute after:bottom-0 after:inset-x-0 after:h-6 after:bg-gradient-to-t after:from-popover after:to-transparent after:pointer-events-none` (requires `relative overflow-hidden` on the scroll container wrapper).

### 5.6 Sizing props

`width` maps to `style={{ width }}` on the popup root. `height` maps to `style={{ maxHeight: height }}` on the output scroll container (overrides the `max-h-[300px]` default).

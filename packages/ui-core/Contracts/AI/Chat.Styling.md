# Chat — Styling Contract

- **Component:** Chat
- **ADR 0017 family:** AI
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./Chat.Semantic.md) · [Interaction](./Chat.Interaction.md) · [Accessibility](./Chat.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A26 Chat (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Chat / KendoReact AI Chat baseline)

---

## 1. Root

`relative flex flex-col h-full min-h-[400px] border border-border rounded-lg overflow-hidden bg-background`.

---

## 2. Message thread

`flex-1 overflow-y-auto p-4 flex flex-col gap-3`. Scroll anchor: CSS `overflow-anchor: auto` at bottom of list.

---

## 3. Message bubbles

User message: `self-end max-w-[80%] bg-primary text-primary-foreground rounded-2xl rounded-br-sm px-4 py-2 text-sm`. Assistant message: `self-start max-w-[80%] bg-muted text-foreground rounded-2xl rounded-bl-sm px-4 py-2 text-sm prose prose-sm max-w-none`. System message: `self-center text-xs text-muted-foreground bg-muted/50 rounded-full px-3 py-1`.

---

## 4. Typing indicator

`self-start flex items-center gap-1 bg-muted rounded-2xl rounded-bl-sm px-4 py-3`. Three dots: `w-1.5 h-1.5 rounded-full bg-muted-foreground animate-bounce` with staggered delay (0ms / 150ms / 300ms).

> **Tailwind note:** `animation-delay` is not a Tailwind v3 utility. Staggered bounce requires inline `style={{ animationDelay: '150ms' }}` or Tailwind config extension (`theme.extend.animationDelay`). Add `motion-reduce:animate-none` to each dot's class list.

---

## 5. Input area

`border-t border-border p-3 flex items-end gap-2 bg-background`. Textarea: same as AIPrompt §2 textarea. Send button: same as AIPrompt §3.

---

## 6. Suggestion chips

`flex flex-wrap gap-2 px-3 pt-2`. Same chip styling as AIPrompt §4.

---

## 7. Scroll-to-bottom button

`absolute bottom-[70px] right-4 h-8 w-8 rounded-full shadow-md bg-background border border-border flex items-center justify-center hover:bg-muted transition-colors`.

---

## 8. Design tokens

Same as AIPrompt: `hsl(var(--card))`, `hsl(var(--border))`, `hsl(var(--primary))`, `hsl(var(--primary-foreground))`, `hsl(var(--foreground))`, `hsl(var(--muted))`, `hsl(var(--muted-foreground))`.

---

## 9. Wave-N expansion — styling additions (Draft, 2026-06-11)

> Wave tag: **wave-N** (implementation deferred). Adds token-mapped styling rules for all wave-N surfaces.

### 9.1 Per-message toolbar

Toolbar container: `absolute top-0 right-0 flex items-center gap-0.5 bg-background/90 backdrop-blur-sm rounded-lg border border-border shadow-sm px-1 py-0.5 opacity-0 group-hover:opacity-100 focus-within:opacity-100 transition-opacity`.

Each toolbar button: `h-6 w-6 flex items-center justify-center rounded text-muted-foreground hover:text-foreground hover:bg-muted text-xs transition-colors`.

### 9.2 Context menu

Context menu popup: `min-w-[140px] rounded-lg border border-border bg-popover shadow-md py-1 z-50`. Menu item: `flex items-center gap-2 px-3 py-1.5 text-sm text-popover-foreground hover:bg-accent cursor-pointer`. Destructive item variant: `text-destructive hover:bg-destructive/10`.

### 9.3 File attachment bubbles

File list: `flex flex-col gap-1.5 mt-1` (`'vertical'` layout). Wrap layout: `flex flex-wrap gap-1.5`. Horizontal layout: `flex gap-1.5 overflow-x-auto`.

File card: `flex items-center gap-2 rounded-md border border-border bg-background/70 px-2 py-1.5 text-xs`. File name: `truncate max-w-[160px] text-foreground`. File size: `text-muted-foreground whitespace-nowrap`. Action icon button: `h-5 w-5 text-muted-foreground hover:text-foreground`.

### 9.4 Per-message suggested-action chips

Chip row: `flex flex-wrap gap-1.5 mt-1.5` (`quickActionsLayout: 'wrap'`) or `flex gap-1.5 mt-1.5 overflow-x-auto` (`'scroll'`). Each chip: `text-xs px-2.5 py-1 rounded-full border border-primary/40 text-primary hover:bg-primary/10 transition-colors whitespace-nowrap`.

### 9.5 Pin indicator

Pinned-message header strip: `flex items-center gap-1.5 px-3 py-1 text-xs text-muted-foreground bg-muted/40 border-b border-border`. Pin icon: `h-3 w-3 shrink-0`. Unpin button: `ml-auto text-xs text-muted-foreground hover:text-foreground`.

### 9.6 Reply / quote preview

Reply preview inside bubble: `border-l-2 border-primary/60 pl-2 text-xs text-muted-foreground truncate mb-1 cursor-pointer hover:text-foreground`. The "jump to original" affordance uses no additional decoration — the cursor change and hover color are sufficient.

### 9.7 Load-more affordance

`flex justify-center py-2`. Button: `text-xs text-muted-foreground hover:text-foreground underline underline-offset-2 disabled:opacity-40`. Loading spinner while fetching: `h-4 w-4 animate-spin text-muted-foreground`.

### 9.8 Multi-user typing indicator

Each additional typing bubble after the first: `ml-[-8px]` (slight horizontal overlap to signal simultaneity). Container for multiple bubbles: `flex items-end gap-0`.

### 9.9 Delivery status

Status row below own message: `flex items-center gap-1 text-[10px] text-muted-foreground mt-0.5`. Status icons (sent / delivered / read): `h-3 w-3`. Read status uses `text-primary` instead of `text-muted-foreground` when all recipients have read.

### 9.10 RTL layout tokens

When `dir="rtl"`:
- User message bubbles switch to `self-start` with `rounded-bl-sm` (flip of default `rounded-br-sm`).
- Assistant message bubbles switch to `self-end` with `rounded-br-sm`.
- Toolbar anchored `right-0` in LTR switches to `left-0` in RTL.
- All `mr-*` / `ml-*` gaps on avatar + bubble flip accordingly via `rtl:` Tailwind variant prefix.

### 9.11 Appearance prop tokens (FR-3 partial)

Per Semantic §8.9, Chat adopts `dir`/`id`/`style`/`height` but not the full FR-3 input-axis props. The `height` prop maps to `style={{ height }}` on the root when provided (overrides `h-full` default).

`messageWidthMode` maps:
- `'standard'` → `max-w-[80%]` on bubbles (existing default)
- `'wide'` → `max-w-[92%]`
- `'full'` → `max-w-full`

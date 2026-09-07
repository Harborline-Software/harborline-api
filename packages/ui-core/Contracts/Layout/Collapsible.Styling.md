# Collapsible — Styling Contract

- **Component:** Collapsible
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Collapsible.Semantic.md) · [Interaction](./Collapsible.Interaction.md) · [Accessibility](./Collapsible.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Collapsible.tsx`
- **Catalog row:** #A17 Collapsible (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 — updated Cohort 1 (2026-06-12) to add `preset="panel"`

---

## 1. Headless mode

### 1.1 Root

No styling applied by Collapsible root. `className` passthrough to wrapper div.

### 1.2 Trigger

No default styling. Callers style `CollapsibleTrigger` to match their context (e.g., a "Show more" link, a header row, an expand button).

### 1.3 Content

`CollapsibleContent` provides `data-state="open"` / `data-state="closed"` attributes that callers use in CSS selectors for custom transitions.

---

## 2. Panel preset mode (`preset="panel"`)

### 2.1 Outer container

`border border-border rounded-md overflow-hidden` + `className` passthrough.

### 2.2 Header button

Base: `flex items-center justify-between w-full px-4 py-3 bg-background text-left`

Enabled: adds `cursor-pointer hover:bg-muted/40 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring`

Disabled: adds `opacity-50 cursor-not-allowed`

### 2.3 Title area

Container: `flex flex-col`

Title: `text-sm font-semibold`

Subtitle: `text-xs text-muted-foreground`

### 2.4 Header right side

`flex items-center gap-2`

### 2.5 Chevron span

`text-muted-foreground transition-transform duration-200 text-sm`

Open state: adds `rotate-180` (rotates ▾ to ▴).

### 2.6 Content region (when open)

`px-4 py-3 border-t border-border bg-background text-sm`

> **M1 note:** Colors use CSS custom properties via Tailwind (border-border, bg-background, text-muted-foreground) — these resolve correctly when Tailwind CSS variables are configured.

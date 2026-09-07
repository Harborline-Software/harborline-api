# Signature — Styling Contract

- **Component:** Signature
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Signature.Semantic.md) · [Interaction](./Signature.Interaction.md) · [Accessibility](./Signature.Accessibility.md) · [Styling](./Signature.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Signature.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root container

```
relative inline-block
```

CSS `width` is set from the `width` prop (default `'100%'`).

---

## 2. Canvas recipe

```
border border-input rounded-md
```

Enabled cursor: `cursor-crosshair`
Disabled: `cursor-not-allowed opacity-50`

Canvas CSS: `width: '100%'; height: {height}; background: {backgroundColor}; touchAction: 'none'`

---

## 3. Clear button recipe

```
absolute top-1 right-1 text-xs text-muted-foreground hover:text-foreground
px-1.5 py-0.5 rounded bg-white/80 border border-border
```

Focus: `focus:outline-none` (no replacement ring — known accessibility gap).

---

## 4. Visual state inventory

| State | Region | Recipe |
|---|---|---|
| idle | canvas | `border-input rounded-md cursor-crosshair` |
| disabled | canvas | `cursor-not-allowed opacity-50` |
| clear button hover | button text | `hover:text-foreground` |
| typed capture | input | Standard form-control border/background/focus-ring recipe; disabled follows the component |

## 5. Typed-signature recipe

The typed alternative is stacked below the canvas with a small semantic label.
Its input uses the shared form-control surface:

```
w-full rounded-md border border-input bg-background px-3 py-2 text-sm
focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring
disabled:cursor-not-allowed disabled:opacity-50
```

The typed name is rendered into the canvas using the same foreground colour as
the freehand stroke and a cursive presentation font. The stored value remains
SVG/PNG data, not font-dependent text.

---

## 6. Token notes

Signature uses semantic tokens from the design system:
- `border-input` — input border color
- `border-border` — general border color
- `text-muted-foreground` — secondary text
- `bg-white/80` — translucent white overlay

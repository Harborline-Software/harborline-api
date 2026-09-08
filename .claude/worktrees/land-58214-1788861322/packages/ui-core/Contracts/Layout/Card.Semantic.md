# Card — Semantic Contract

- **Component:** Card
- **ADR 0017 family:** Layout
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Card.Interaction.md) · [Styling](./Card.Styling.md) · [Accessibility](./Card.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Card.tsx`
- **Catalog row:** #21 Card (`app-priority: high`, `library-scope: v1`, `Notes: shadcn Card`)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)
- **Foundation:** shadcn/ui Card → composition of `<div>` slots

---

## 1. Purpose

Card is the canonical bordered-container surface of `@harborline-software/ui-react`. It
groups related content into a visually-distinct region with consistent padding,
border, and (optionally) elevation. It is the primary structural primitive for
dashboards, settings panels, summary widgets, list-item containers, and any
"box of content" idiom.

This is a **forward-spec**: Card has not yet been implemented in
`@harborline-software/ui-react`. The contract describes the **intended** public surface
based on the shadcn/ui Card pattern (a root `<div>` plus named sub-components
for Header / Title / Description / Content / Footer) adapted to Harborline's
elevation + density vocabulary.

Card is a **composable container**, not a leaf primitive. The public surface
is a small family of sub-components:

- `<Card>` — root container.
- `<CardHeader>` — optional top region (typically holds Title + Description).
- `<CardTitle>` — heading text inside CardHeader.
- `<CardDescription>` — secondary text inside CardHeader.
- `<CardContent>` — main body region.
- `<CardFooter>` — optional bottom region (typically holds actions).

Hosts compose these explicitly. There is no monolithic `<Card title=… body=…>`
form — the composition pattern lets hosts skip any region they don't need.

---

## 2. Data model

Card has no internal data model. Every sub-component is a styled wrapper
around `<div>` (or `<h3>` for CardTitle, `<p>` for CardDescription) that
spreads HTML attributes and renders `children`.

```typescript
type CardVariant = 'flat' | 'raised' | 'outlined' | 'elevated'
type CardPadding = 'none' | 'sm' | 'md' | 'lg'
type CardOrientation = 'vertical' | 'horizontal'

interface CardProps extends React.HTMLAttributes<HTMLDivElement> {
  variant?: CardVariant
  padding?: CardPadding
  orientation?: CardOrientation
  separators?: boolean
  asChild?: boolean
  children: React.ReactNode
}

interface CardHeaderProps extends React.HTMLAttributes<HTMLDivElement> {
  children: React.ReactNode
}

interface CardTitleProps extends React.HTMLAttributes<HTMLHeadingElement> {
  as?: 'h1' | 'h2' | 'h3' | 'h4' | 'h5' | 'h6'
  children: React.ReactNode
}

interface CardDescriptionProps extends React.HTMLAttributes<HTMLParagraphElement> {
  children: React.ReactNode
}

interface CardContentProps extends React.HTMLAttributes<HTMLDivElement> {
  children: React.ReactNode
}

interface CardFooterProps extends React.HTMLAttributes<HTMLDivElement> {
  children: React.ReactNode
}
```

---

## 3. Props — semantics and defaults

### 3.1 `<Card>` (root)

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `variant` | `'flat' \| 'raised' \| 'outlined' \| 'elevated'` | `'outlined'` | Visual emphasis. `'flat'` is no border + no shadow (a logical group only). `'outlined'` is a bordered card. `'raised'` is bordered + drop shadow. `'elevated'` is a more prominent shadow without a border. |
| `padding` | `'none' \| 'sm' \| 'md' \| 'lg'` | `'md'` | Padding applied to the root container. `'none'` is required when sub-components own their own padding (e.g. a Card wrapping a DataGrid). |
| `orientation` | `'vertical' \| 'horizontal'` | `'vertical'` | Layout axis for the Card's direct children. `'vertical'` (default) stacks Header → Content → Footer top-to-bottom (`flex-col`). `'horizontal'` places them left-to-right (`flex-row`) — CardContent gets `flex-1` to fill remaining space. See §3.8. |
| `separators` | `boolean` | `false` | When `true`, renders visual dividers between CardHeader / CardContent / CardFooter regions. PAO Styling owns the separator token. |
| `asChild` | `boolean` | `false` | Radix `Slot` pattern (Button.Semantic.md §3.3): when `true`, Card composes onto its single child element (e.g. an `<a>` to make the whole card a link). |
| `children` | `ReactNode` | _required_ | Card content. Typically a composition of CardHeader / CardContent / CardFooter, but free-form content is supported. |
| HTML attributes | — | — | Spread onto the root `<div>` (or `asChild` target). |

### 3.2 `<CardHeader>` / `<CardContent>` / `<CardFooter>`

Each is a styled `<div>` wrapper:

| Sub-component | Default padding inside | Default border |
| --- | --- | --- |
| `<CardHeader>` | `space-y-1.5 pb-4` (gap between Title + Description; bottom space before Content) | None |
| `<CardContent>` | `0` (Card root's padding owns the inset) | None |
| `<CardFooter>` | `pt-4 flex items-center` (top space after Content; default flex layout for actions) | `border-t` at top — separates footer from content. **Telerik users:** Telerik Card separates `CardActions` (buttons) from metadata — Harborline uses a single `<CardFooter>` for both. Hosts who need a metadata row AND an actions row compose them inside `<CardFooter>` via flex children. **Closes G-CR4.** |

All three accept arbitrary children, spread HTML attributes, and have no
M2-specific props beyond `children`.

### 3.3 `<CardTitle>` / `<CardDescription>`

| Sub-component | Default element | Notes |
| --- | --- | --- |
| `<CardTitle>` | `<h3>` (overridable via `as` prop, `h1`–`h6`) | Title text. PAO Accessibility owns heading-level guidance. |
| `<CardDescription>` | `<p>` | Secondary text. Typography token owned by PAO Styling. |

**Note for Telerik users (closes G-CR3):** Telerik Card uses `CardSubTitle`
for the secondary text under the title. Harborline uses `<CardDescription>`.
They are equivalent. There is no `CardSubTitle` component; use
`<CardDescription>` in all cases.

### 3.4 Variant semantics

| Variant | Border | Shadow | Background | Typical use |
| --- | --- | --- | --- | --- |
| `flat` | none | none | inherits | Logical grouping where visual emphasis is unwanted (settings sub-section). |
| `outlined` | 1px subtle | none | subtle off-surface | Default. Most cards in the app. |
| `raised` | 1px subtle | small drop-shadow | subtle off-surface | Hero card / featured panel; sparingly used. |
| `elevated` | none | prominent drop-shadow | surface | Floating widget; modal-adjacent surface where a border would be redundant. |

PAO Styling owns the exact tokens. A `'hover'` elevation step (raise on
hover) is deferred (§7).

### 3.5 Padding semantics

| Padding | Inset | Typical use |
| --- | --- | --- |
| `none` | `0` | Card wraps a component that owns its own inset (DataGrid, full-bleed image). |
| `sm` | `p-3` (~12px) | Dense list-item card; small summary widget. |
| `md` | `p-4` to `p-6` (default) | Most cards. |
| `lg` | `p-8` | Hero card; settings page section. |

PAO Styling owns the exact tokens. Padding applies to the root container;
sub-components (CardHeader / CardContent / CardFooter) lay out inside that
padded box.

### 3.6 HTML attribute passthrough

Every sub-component **spreads** additional valid HTML attributes onto its
rendered element (`<div>`, `<h3>`, `<p>`). Following the Button precedent.

### 3.7 `asChild` (Card root only)

When `<Card asChild>` wraps a single element child (e.g. `<a href=…>` or a
routing `<Link>`), the entire card surface becomes the activatable element.
PAO Accessibility owns the focus-ring + hover-state treatment for the
"clickable card" pattern; PAO Styling owns the elevated-on-hover token.

`asChild` is **only on the root `<Card>`**. Sub-components do not support
`asChild` in M2 — there is no use case for "make just the header a link".

### 3.8 Orientation semantics (G-CR1)

This section closes G-CR1 (ONR audit 2026-06-04).

**`orientation='vertical'` (default):**
- Card root uses `flex-col`. Children stack Header → Content → Footer.
- This is the existing behavior; all current usage is unaffected.

**`orientation='horizontal'`:**
- Card root switches to `flex-row`. Children flow left-to-right.
- `CardContent` receives `flex-1` to fill the remaining horizontal space.
- Direct children flow left-to-right as columns. If `CardHeader` is used, it forms the left column. Direct non-subcomponent children (e.g., `<img>`) are also valid as left-column anchors — the Card does not require CardHeader in horizontal mode.

**Typical horizontal use case — media card:**

```tsx
<Card orientation="horizontal">
  <img src={thumb} className="w-40 rounded-l-lg object-cover" />
  <CardContent>
    <CardTitle>Property name</CardTitle>
    <CardDescription>3 bed · 2 bath · Available now</CardDescription>
  </CardContent>
</Card>
```

**Separator interaction:** in `horizontal` mode, `separators` adds `divide-x` (vertical dividing lines between left/right columns). In `vertical` mode, `separators` adds `divide-y` (horizontal lines between rows). The Styling contract §3.5 specifies the direction-conditional divide recipe.

---

## 4. Events — semantics

Card has **no events**. It is a structural container.

When `asChild === true` and the child is an interactive element (`<a>`,
`<button>`, etc.), the child's native events fire — but Card itself does
not define an event surface.

---

## 5. Slots

Card uses **named sub-components** rather than slot props:

| Sub-component | Purpose | Cardinality |
| --- | --- | --- |
| `<CardHeader>` | Top region. Typically holds CardTitle + CardDescription. | 0 or 1 |
| `<CardTitle>` | Heading inside CardHeader. | 0 or 1 (inside CardHeader) |
| `<CardDescription>` | Secondary text inside CardHeader. | 0 or 1 (inside CardHeader) |
| `<CardContent>` | Main body. | 0+ (typically 1) |
| `<CardFooter>` | Bottom action row. | 0 or 1 |

Hosts compose freely:

```tsx
<Card>
  <CardHeader>
    <CardTitle>Outstanding invoices</CardTitle>
    <CardDescription>Across all properties this quarter.</CardDescription>
  </CardHeader>
  <CardContent>
    <DataGrid {...} />
  </CardContent>
  <CardFooter>
    <Button variant="ghost">View all</Button>
  </CardFooter>
</Card>
```

A card with only `<CardContent>` is also valid — the Header and Footer
regions are optional and Card renders fine without them.

---

## 6. Component composition

- **Card-wrapped DataGrid.** A common pattern: `<Card padding="none">
  <CardHeader>…</CardHeader> <DataGrid /> </Card>`. The `padding="none"`
  is required so DataGrid's own gridlines reach the card border.
- **Dashboard summary widgets.** A grid of `<Card variant="outlined">`
  cards each containing a `<CardTitle>` and a single metric.
- **Settings page sections.** `<Card padding="lg">` for top-level settings
  groups; `<Card variant="flat">` for nested sub-sections within.
- **Clickable card.** `<Card asChild><a href="/property/123">…</a></Card>`.
  PAO Accessibility owns focus-ring and ARIA treatment.
- **Empty-state inside Card.** EmptyState (M1) is a valid CardContent
  body — hosts pass `<CardContent><EmptyState … /></CardContent>` for
  "this widget has no data" treatment.
- **Form inside Card.** A form's `<CardFooter>` typically holds Cancel +
  Submit Button instances side-by-side with `gap-2 ml-auto` (right-
  aligned actions). The exact layout is host-owned within CardFooter's
  default `flex items-center`.

---

## 7. Deferred features

Out of scope for the M2 baseline:

- **`hoverElevation`** — automatic elevation increase on hover. Hosts who
  want this today can compose Tailwind utility classes externally.
- **`selected` state** — a "checked" / "active" treatment for card-as-radio
  patterns. Deferred to a future SelectionCard / CardRadio composition.
- **Header-only card** dedicated treatment (no Content). Today's spec
  supports it via composition; no dedicated variant.
- **`<CardMedia>` — inset / full-bleed image region (closes G-CR2)** —
  `<CardMedia>` is the deferred sub-component for top/bottom image areas
  in a Card. Telerik Card exposes `<CardImage>` as a dedicated image slot
  that bleeds to the card's border edges and occupies the top (or bottom)
  of the card. Harborline has no `<CardMedia>` or `<CardImage>` in M2.
  **Workaround:** render a raw `<img>` or `<picture>` as the first child
  of `<Card padding="none">` with Tailwind utilities for rounding
  (`rounded-t-[--radius]`). The Card's `padding="none"` removes inset so
  the image reaches the card border; `<CardContent>` below supplies its
  own internal padding. Deferred to the media-card wave.
- **Drag-and-drop hooks** — moving cards around dashboards. Deferred.
- **Loading skeleton variant** — automatic skeleton treatment when a
  `loading` prop is true. Hosts compose Loader (M2 Batch C) inside
  CardContent for this case.

---

## 8. Open questions (for council)

1. **Default variant.** `'outlined'` (this spec) or `'flat'` (shadcn
   default)? Leaning `'outlined'` — the bordered card is the dominant
   pattern in ERP-style apps; flat is the exception.
2. **Default padding.** `'md'` or `'sm'`? Most cards in dense ERP layouts
   feel large at `md`. Leaning `'md'` — sparser layouts read better; we
   add `sm` for dense lists.
3. **CardTitle default element.** `<h3>` or `<h4>`? Depends on document
   heading hierarchy. Leaning `<h3>` with `as` override for hosts to
   move it up/down per page context.
4. **CardFooter top border.** This spec includes a `border-t` by default.
   shadcn's default Card has no footer border. Leaning **include** —
   visual separation between content and actions feels clearer; hosts
   can override via passthrough.
5. **Asymmetric padding for Card containing DataGrid.** Hosts must
   currently set `padding="none"` and then CardHeader needs internal
   padding to align with the DataGrid below. Should we provide a
   convenience `padding="header-only"` mode? Leaning no — the
   composition with `padding="none"` + explicit Header padding is
   clearer.
6. **`asChild` on the root + interactive content inside.** When the
   whole card is a link (`<Card asChild><a>…`) and `<CardFooter>` holds
   a Button, clicking the button bubbles to the card link. Should Card
   intercept clicks on interactive descendants? Leaning no — hosts must
   `stopPropagation` in the inner Button or avoid mixing clickable-card
   with footer actions.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/cards/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |


---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CR1 | Critical | Orientation (horizontal/vertical) missing | [RESOLVED 2026-06-05] §7 + §3.8: horizontal orientation deferred; lands with CardMedia wave |
| G-CR2 | High | CardImage / media region absent | [RESOLVED 2026-06-05] §7: deferred to media-card wave; high demand noted |
| G-CR3 | High | CardSubTitle vs CardDescription naming | [RESOLVED 2026-06-05] §5 alias note: Telerik CardSubTitle = Harborline CardDescription |
| G-CR4 | High | CardActions vs CardFooter semantic difference | [RESOLVED 2026-06-05] §6 note: CardFooter covers both actions and metadata; for both rows, nest two divs |
| G-CR5 | Medium | CardSeparator vs separators prop | [ACCEPTED-RISK 2026-06-05] §3.1: separators:boolean adds all-position dividers; per-position control deferred |
| G-CR6 | Medium | Deck/auto-height-syncing absent | [ACCEPTED-RISK 2026-06-05] §7: use CSS grid/flex row with items-stretch on the parent |
| G-CR7 | Medium | Clickable card + Footer button stopPropagation recipe | [ACCEPTED-RISK 2026-06-05] §3.7 recipe added: wrap footer button onClick with e.stopPropagation() |

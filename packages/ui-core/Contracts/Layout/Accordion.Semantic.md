# Accordion — Semantic Contract

- **Component:** Accordion
- **ADR 0017 family:** Layout
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** Interaction (PAO) · [Styling](./Accordion.Styling.md) · [Accessibility](./Accordion.Accessibility.md)
- **Related contracts:** [Card.Semantic.md](./Card.Semantic.md) — Accordion panels may be styled similarly to Cards; [TabStrip.Semantic.md](../Navigation/TabStrip.Semantic.md) — alternative for mutually-exclusive panel switching when all tabs are always visible.
- **Reference implementation:** `packages/ui-react/src/components/layout/Accordion.tsx`
- **Catalog rows:** #94 PanelBar (`app-priority: low`, `library-scope: planned`) — Accordion is the shadcn alias; canonical catalog row is PanelBar #94
- **Phase:** ADR 0017-A1 Phase M1 (R8 priority bump)
- **Foundation:** none — hand-rolled (no `@radix-ui/react-accordion`)

---

## 1. Purpose

Accordion is a **vertically-stacked set of collapsible panels**. Each panel
has a clickable header that toggles the panel body open or closed. Accordion
reduces visual noise by hiding content until the user needs it.

**Primary Harborline use cases:**

- Property detail pages (sections: Overview / Tenants / Units / Documents)
- Invoice line-item breakdown (expand to see GL coding details)
- Settings pages (grouped preference sections)
- FAQ / Help content

---

## 2. Data model

```typescript
interface AccordionItem {
  value: string           // unique identifier for this panel
  title: React.ReactNode  // header content (usually a string, may include a badge)
  children: React.ReactNode  // panel body content
  disabled?: boolean      // prevents opening; visually muted
}

interface AccordionProps extends React.HTMLAttributes<HTMLDivElement> {
  items: AccordionItem[]
  type?: 'single' | 'multiple'
  defaultValue?: string[]
  collapsible?: boolean
}
```

### 2.1 Uncontrolled (M1)

M1 exposes an **uncontrolled** Accordion with `defaultValue`. The component
manages open/close state internally. The host sets the initial open panels
via `defaultValue` and does not need to track state.

Controlled mode (`value` + `onValueChange`) is deferred to §7.

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `items` | `AccordionItem[]` | _required_ | The panels to render. Each has a `value` (key), `title` (header), `children` (body), optional `disabled`. |
| `type` | `'single' \| 'multiple'` | `'single'` | `'single'`: only one panel can be open at a time (opening a new one closes the current). `'multiple'`: any number can be open simultaneously. |
| `defaultValue` | `string[]` | `[]` | The `value` strings of panels that start open. For `type='single'`, only the first element is used. |
| `collapsible` | `boolean` | `true` | For `type='single'`: whether clicking the currently-open panel header closes it. When `false`, exactly one panel is always open (accordion stays open). |
| HTML attributes | — | — | Spread onto the root `<div>`. |

### 3.1 `AccordionItem.value`

`value` is the stable identifier for a panel — used as the `id` base for ARIA
attributes and for `defaultValue` matching. Must be unique within the Accordion.

### 3.2 `AccordionItem.title` as `ReactNode`

`title` accepts `ReactNode` so hosts can include:
- Plain strings: `"Tenant details"`
- Strings with badges: `<>Documents <Badge count={3} /></>`
- Icon + text: `<><FolderIcon /> Attachments</>`

### 3.3 `type='single'` with `collapsible=false`

When `type='single'` and `collapsible=false`, the Accordion behaves like a
controlled single-select: exactly one panel is always open. If `defaultValue`
is empty, the first non-disabled item opens automatically.

### 3.4 Keyboard model (WAI-ARIA Accordion pattern)

| Key | Behavior |
| --- | --- |
| `Enter` / `Space` | Toggle the focused panel header |
| `Tab` | Move focus to next focusable element |
| `↓` | Move focus to next header (wraps) |
| `↑` | Move focus to previous header (wraps) |
| `Home` | Focus first header |
| `End` | Focus last header |

### 3.5 ARIA attributes

Each panel header renders:
- `<h3><button aria-expanded={isOpen} aria-controls="{value}-panel">` (heading level configurable via `headingLevel` prop; default `h3`)

Each panel body renders:
- `<div id="{value}-panel" role="region" aria-labelledby="{value}-header">`
- When closed: `hidden` attribute (not `display:none` — hidden preserves the region for AT but removes from tab order)

---

## 4. Events — semantics

| Event | Note |
| --- | --- |
| None (M1) | Accordion is uncontrolled in M1. Controlled mode + `onValueChange` is deferred (§7). |

---

## 5. Slots

Accordion uses items-as-props in M1. Each `AccordionItem.title` and
`AccordionItem.children` are `ReactNode` slots. Compound-component API
(`<Accordion.Item>`, `<Accordion.Trigger>`, `<Accordion.Content>`) is
deferred to §7.

---

## 6. Component composition

### Property detail page

```tsx
<Accordion
  items={[
    {
      value: 'overview',
      title: 'Overview',
      children: <PropertyOverview property={property} />,
    },
    {
      value: 'tenants',
      title: <>Tenants <Badge count={property.activeTenants} /></>,
      children: <TenantList propertyId={property.id} />,
    },
    {
      value: 'documents',
      title: 'Documents',
      children: <DocumentList propertyId={property.id} />,
      disabled: !property.hasDocuments,
    },
  ]}
  defaultValue={['overview']}
/>
```

### Multiple-open FAQ

```tsx
<Accordion
  type="multiple"
  items={faqItems.map(faq => ({
    value: faq.id,
    title: faq.question,
    children: <p>{faq.answer}</p>,
  }))}
/>
```

### Always-one-open (non-collapsible single)

```tsx
<Accordion
  type="single"
  collapsible={false}
  defaultValue={['first-section']}
  items={sections}
/>
```

---

## 7. Deferred features

Out of scope for the M1 baseline:

- **Controlled mode** — `value: string[]` + `onValueChange: (v: string[]) => void`.
  Today the component is uncontrolled (internal state + `defaultValue`).
- **Compound component API** — `<Accordion>` / `<AccordionItem>` / `<AccordionTrigger>` /
  `<AccordionContent>` dot-notation sub-components for more flexible composition.
- **Animation** — animated slide-open/close for panel bodies. Today panels
  appear/disappear instantly. PAO Styling wave.
- **Bordered / separated variants** — visual treatment with dividers between
  items or card-style borders. PAO Styling wave.
- **Nested accordions** — accordion items that contain child accordions.
  Hosts can nest today; no extra API needed.

---

## 8. Open questions (for council)

1. **Items-as-props vs compound components.** The items-as-props API is simpler
   for the common case but limits flexibility when panel body content needs
   context from the accordion state. Leaning items-as-props for M1 — promote
   to compound API in M2 if compound flexibility is needed.
2. **`collapsible` default.** Default `true` allows all panels to close. Should
   the default be `false` for `type='single'` (always-one-open)? Leaning `true`
   — more permissive default; host opts in to always-open.
3. **`defaultValue` as `string` vs `string[]`.** For `type='single'`,
   `defaultValue` could reasonably be `string` (not an array). Leaning
   `string[]` for both types — uniform API is simpler.
4. **`hidden` attribute vs CSS.** Closed panels use the HTML `hidden` attribute.
   This removes them from the accessibility tree. Should closed panels remain
   in the accessibility tree but hidden with CSS? Leaning `hidden` — it's
   simpler and consistent with how most AT handles collapsed accordions.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/layout/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |

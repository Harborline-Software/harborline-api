# PropertySelector — Semantic Contract

- **Component:** PropertySelector
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted (domain-relocated to Harborline App; NOT `@harborline-software/ui-react`)
- **Companion contracts:** [Interaction](./PropertySelector.Interaction.md) · [Accessibility](./PropertySelector.Accessibility.md) · [Styling](./PropertySelector.Styling.md)
- **Reference implementation:** the Harborline App's `src/property/PropertySelector.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — domain-specific property selection widget

---
---

> **SCOPE WARNING (MG3-7):** This component is a **domain-tier component** specific to Harborline App's
> property-management domain. It MUST NOT be added to `@harborline-software/ui-react` (the shared component library).
> The `feedback_ui_react_no_domain_components` fleet rule explicitly prohibits property-management
> and building-systems domain components from the shared library (~500 PRs were closed/reverted
> when this boundary was violated). This spec is retained as app-domain documentation only.
> Its implementation is intentionally located in the Harborline App's `src/property`, not in
> `packages/ui-react`.

---


## 1. Purpose

PropertySelector is a domain-specific combobox for selecting a property from a
list of `PropertyOption` items. It renders a trigger button with a search input
and a `role="listbox"` dropdown. Each option displays a thumbnail (if
available), name, address, and optionally unit count. It is a domain-tier
component — NOT for inclusion in the general `@harborline-software/ui-react` catalog
(see `[No domain components in ui-react]`); present here for reverse-spec only.

---

## 2. Data model

```typescript
interface PropertyOption {
  id: string
  name: string
  address: string
  units?: number
  type?: string
  thumbnail?: string   // image URL for the property
}

interface PropertySelectorProps {
  properties: PropertyOption[]
  value?: string       // selected property id
  onChange?: (id: string) => void
  label?: string
  placeholder?: string
  disabled?: boolean
  id?: string
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `properties` | `PropertyOption[]` | _required_ | Full list of selectable properties. |
| `value` | `string` | — | Selected property id. Controlled. |
| `onChange` | `(id: string) => void` | — | Called when a property is selected from the dropdown. |
| `label` | `string` | — | When provided, renders a `<label>` above the trigger. |
| `placeholder` | `string` | `'Select property'` | Text shown in the trigger when no value is selected. |
| `disabled` | `boolean` | `false` | When `true`, the trigger is non-interactive. |
| `id` | `string` | — | Id for the trigger button; auto-generated with `useId()` if not provided. |
| `className` | `string` | — | Additional CSS classes on the root wrapper. |

### 3.1 Search filtering

The search input in the dropdown filters by:
- `property.name.toLowerCase().includes(query)` OR
- `property.address.toLowerCase().includes(query)`

When `query` is empty, all properties are shown.

### 3.2 Open/close

The dropdown opens on trigger button click. It closes on:
- Property selection.
- `mousedown` outside the component (document-level listener).

There is no keyboard-driven Escape close in M1.

---

## 4. Internal state

| State | Default | Meaning |
|---|---|---|
| `open` | `false` | Dropdown visible |
| `query` | `''` | Search filter text |

---

## 5. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `string` (property id) | User selects an option from the dropdown. |

---

## 6. Composition

PropertySelector is self-contained. The dropdown renders with `position:
absolute` inside the component's `relative` wrapper. It does NOT use a portal
— it may be clipped by overflow containers.

---

## 7. Deferred features (and domain concerns)

- **General-purpose combobox.** SearchableSelect is the domain-agnostic
  version of this component. New code should use SearchableSelect for generic
  combobox needs; PropertySelector is domain-specific.
- **Keyboard navigation in dropdown.** Arrow key item navigation is absent.
- **Escape to close dropdown.**
- **Portal rendering** to escape overflow containers.

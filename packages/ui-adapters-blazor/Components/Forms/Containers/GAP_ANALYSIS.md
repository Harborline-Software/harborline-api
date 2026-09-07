# Forms/Containers Gap Analysis

> Generated: 2026-03-30
>
> Scope: `HarborlineField`, `HarborlineForm`, `HarborlineLabel`, `HarborlineValidation` components in
> `<path>/src/Harborline.Components/Forms/Containers/` compared against
> documentation specs in `docs/component-specs/floatinglabel/`, `docs/component-specs/form/`,
> and `docs/component-specs/validation/`.

---

## Table of Contents

1. [HarborlineField Gap Analysis](#1-marilofield-gap-analysis)
2. [HarborlineForm Gap Analysis](#2-mariloform-gap-analysis)
3. [HarborlineLabel Gap Analysis](#3-marilolabel-gap-analysis)
4. [HarborlineValidation Gap Analysis](#4-marilovalidation-gap-analysis)
5. [Cross-Cutting Concerns](#5-cross-cutting-concerns)

---

## 1. HarborlineField Gap Analysis

### Summary

`HarborlineField.razor` is a minimal wrapper that renders a `<div>` with a CSS class from `CssProvider.FieldClass()` and a `ChildContent` render fragment. The documentation specs (`floatinglabel/overview.md`) describe a **`HarborlineFloatingLabel`** component with significantly richer functionality (floating animation, `Text` parameter, `Id` parameter, component compatibility restrictions, placeholder interaction, prefix adornment integration, and validation-aware styling). `HarborlineField` appears to be a basic field container, not the documented FloatingLabel.

### Spec to Code Gaps (Documented but not correctly implemented)

#### Parameters

| Gap | Severity | Details |
|-----|----------|---------|
| Missing `Text` parameter | **[High]** | Spec requires `Text` (string) to set the floating label text displayed over/above the input. Not implemented. |
| Missing `Id` parameter | **[Medium]** | Spec documents an `Id` parameter that renders on the `<label>` element for accessibility (`aria-labelledby` association). Not implemented. |

#### Behaviors

| Gap | Severity | Details |
|-----|----------|---------|
| No floating/animation behavior | **[High]** | Spec describes a label that floats over empty non-focused components and moves above on focus. The current implementation is a static `<div>` with no label element, no focus tracking, and no animation. |
| No `<label>` element rendered | **[High]** | Spec indicates a `<label>` child element inside `span.k-floating-label-container`. Current implementation renders only a `<div>` with `ChildContent`. |
| No component compatibility enforcement | **[Medium]** | Spec lists 12 compatible Harborline components (AutoComplete, ComboBox, DateInput, etc.) and explicitly states FloatingLabel does not support third-party or generic HTML inputs. No compatibility check exists. |
| No placeholder interaction | **[Medium]** | Spec documents that when the floating label is over the component the placeholder is hidden, and when the label floats away the placeholder becomes visible. Not implemented. |
| No validation-aware styling | **[Medium]** | Spec (`floatinglabel/validation.md`) describes the floating label changing color when the associated field is invalid. Not implemented. |
| No prefix adornment integration | **[Low]** | Spec mentions "Integration with Prefix Adornment" (section header present but empty). Not implemented, though spec content is incomplete. |

### Code to Spec Gaps (Implemented but not documented)

#### Parameters

| Gap | Severity | Details |
|-----|----------|---------|
| `ChildContent` (RenderFragment?) | **[Low]** | The component accepts `ChildContent` to render arbitrary child markup inside the field container. Not documented in the FloatingLabel spec -- the spec expects structured content with a compatible Harborline component, not free-form child content. |

#### Behaviors

| Gap | Severity | Details |
|-----|----------|---------|
| Static `<div>` container | **[Low]** | The component renders a plain `<div>` using `CssProvider.FieldClass()`. This container concept is not part of the FloatingLabel spec. |

### Recommended Changes

#### Implementation Updates

1. **[High]** Either rename/reposition `HarborlineField` as a generic field container (not mapped to FloatingLabel spec), or implement the full `HarborlineFloatingLabel` component with `Text`, `Id`, floating animation, focus-tracking, and `<label>` rendering.
2. **[High]** If intended as FloatingLabel: add `Text` parameter, render a `<label>` element with `for` attribute, and implement JavaScript interop for focus/blur animation.
3. **[Medium]** Add validation-state-aware CSS class toggling.

#### Documentation Updates

1. **[Medium]** If `HarborlineField` is intentionally a simpler wrapper (not FloatingLabel), create dedicated documentation for it and remap the FloatingLabel spec to a future `HarborlineFloatingLabel` component.

### Open Questions / Ambiguities

- Is `HarborlineField` intended to be the `HarborlineFloatingLabel` from the spec, or is it a separate, simpler "form field" container? The naming mismatch (`Field` vs. `FloatingLabel`) strongly suggests these are different concepts, and `HarborlineFloatingLabel` may not yet be implemented.
- The spec's "Integration with Prefix Adornment" section is empty. What is the expected behavior?

---

## 2. HarborlineForm Gap Analysis

### Summary

`HarborlineForm.razor` renders a `<form>` element with a CSS class from `CssProvider.FormClass()` and a `ChildContent` render fragment. The spec (`form/overview.md` and 10+ sub-pages) describes an extremely feature-rich component: model/EditContext binding, automatic field generation, FormItems/FormGroups/FormButtons child components, validation integration, columns/orientation/spacing layout, events (OnSubmit, OnUpdate, OnValidSubmit, OnInvalidSubmit), Size/Width appearance parameters, form reference with `Refresh()` method, FormItemsTemplate rendering system, and WAI-ARIA accessibility. The current implementation has none of these features.

### Spec to Code Gaps (Documented but not correctly implemented)

#### Parameters

| Gap | Severity | Details |
|-----|----------|---------|
| Missing `Model` parameter | **[High]** | Spec requires `Model` (object) for binding the form to a data object and auto-generating editors. Not implemented. |
| Missing `EditContext` parameter | **[High]** | Spec documents `EditContext` as an alternative to `Model` for providing an existing edit context. Not implemented. |
| Missing `ValidationMessageType` parameter | **[High]** | Spec documents `ValidationMessageType` (enum: Inline/Tooltip/None) controlling how validation messages display. Not implemented. |
| Missing `AutoComplete` parameter | **[Medium]** | Spec documents the `autocomplete` HTML attribute pass-through. Not implemented. |
| Missing `Id` parameter | **[Medium]** | Spec documents `Id` for the form's `id` attribute, used for external submit buttons via `Form` parameter on buttons. Not implemented (base class `AdditionalAttributes` could carry it, but no dedicated parameter). |
| Missing `Columns` parameter | **[Medium]** | Spec documents `Columns` (int) for multi-column form layout. Not implemented. |
| Missing `ColumnSpacing` parameter | **[Low]** | Spec documents `ColumnSpacing` (string, default "32px") for horizontal space between columns. Not implemented. |
| Missing `RowSpacing` parameter | **[Low]** | Spec documents `RowSpacing` (string) for vertical space between rows. Not implemented. |
| Missing `Orientation` parameter | **[Medium]** | Spec documents `Orientation` (FormOrientation enum: Horizontal/Vertical). Not implemented. |
| Missing `ButtonsLayout` parameter | **[Low]** | Spec documents `ButtonsLayout` (FormButtonsLayout enum, default Start) for button positioning. Not implemented. |
| Missing `Size` parameter | **[Medium]** | Spec documents `Size` (string: sm/md/lg) for controlling editor size and spacing. Not implemented. |
| Missing `Width` parameter | **[Medium]** | Spec documents `Width` (string) for CSS width of the form. Not implemented. |

#### Events

| Gap | Severity | Details |
|-----|----------|---------|
| Missing `OnSubmit` event | **[High]** | Spec documents `OnSubmit` (`EventCallback<EditContext>`) fired on form submission. Not implemented. |
| Missing `OnValidSubmit` event | **[High]** | Spec documents `OnValidSubmit` (`EventCallback<EditContext>`) fired when form passes validation. Not implemented. |
| Missing `OnInvalidSubmit` event | **[High]** | Spec documents `OnInvalidSubmit` (`EventCallback<EditContext>`) fired when form fails validation. Not implemented. |
| Missing `OnUpdate` event | **[Medium]** | Spec documents `OnUpdate` (`EventCallback<FormUpdateEventArgs>`) fired on field value changes with `Model` and `FieldName` properties. Not implemented. |

#### Methods

| Gap | Severity | Details |
|-----|----------|---------|
| Missing `Refresh()` method | **[Medium]** | Spec documents a `Refresh()` method that triggers `StateHasChanged()` on the form. Not implemented. |
| Missing `EditContext` property (reference) | **[Medium]** | Spec documents accessing `FormRef.EditContext` for programmatic validation. Not implemented. |

#### Behaviors

| Gap | Severity | Details |
|-----|----------|---------|
| No automatic field generation | **[High]** | Spec describes auto-generating editors based on model property types (string->TextBox, int->NumericTextBox, etc.) with data annotation support. Not implemented. |
| No `<FormValidation>` child support | **[High]** | Spec documents a `<FormValidation>` nested tag for adding validators. Not implemented. |
| No `<FormItems>` child support | **[High]** | Spec documents `<FormItems>` with `<FormItem>` children for defining/customizing editors. Not implemented. |
| No `<FormGroups>` support | **[Medium]** | Spec documents `<FormGroup>` for organizing fields with `LabelText`, `Columns`, `ColumnSpacing`, `RowSpacing`. Not implemented. |
| No `<FormButtons>` support | **[Medium]** | Spec documents `<FormButtons>` for custom button rendering. No default submit button rendered. Not implemented. |
| No `<FormItemsTemplate>` support | **[Medium]** | Spec documents `<FormItemsTemplate>` with `FormItemsTemplateContext`, `FormGroupRenderer`, `FormItemRenderer` for custom layouts. Not implemented. |
| No `<FormAutoGeneratedItems>` support | **[Low]** | Spec documents mixing auto-generated and manually defined items. Not implemented. |
| No data annotation attribute support | **[High]** | Spec documents `[Display(Name)]`, `[Display(AutoGenerateField)]`, `[Editable]`, `[Required]`, etc. Not implemented. |
| No WAI-ARIA `role=form` | **[Low]** | Spec accessibility page notes `role=form` or `<form>` element. Current code uses `<form>` element which is correct, but no additional ARIA attributes are managed. |

### Code to Spec Gaps (Implemented but not documented)

#### Parameters

| Gap | Severity | Details |
|-----|----------|---------|
| `ChildContent` (RenderFragment?) | **[Low]** | Component accepts free-form child content. The spec does not document a generic `ChildContent` parameter; instead it expects structured child components (`FormValidation`, `FormItems`, `FormButtons`, `FormItemsTemplate`). |

### Recommended Changes

#### Implementation Updates

1. **[High]** Implement `Model` and `EditContext` parameters with mutual exclusion logic.
2. **[High]** Implement automatic field generation based on model reflection and data type-to-editor mapping.
3. **[High]** Implement `FormValidation`, `FormItems`, `FormGroups`, `FormButtons` render fragment parameters.
4. **[High]** Implement `OnSubmit`, `OnValidSubmit`, `OnInvalidSubmit` events wired to the underlying EditContext.
5. **[Medium]** Implement layout parameters: `Columns`, `ColumnSpacing`, `RowSpacing`, `Orientation`, `Size`, `Width`.
6. **[Medium]** Implement `OnUpdate` event tied to `EditContext.OnFieldChanged`.
7. **[Medium]** Implement `Refresh()` method and expose `EditContext` property.

#### Documentation Updates

1. **[Low]** Document the base class parameters (`Class`, `Style`, `AdditionalAttributes`) which are inherited but not listed in the spec.

### Open Questions / Ambiguities

- The current implementation is essentially an empty `<form>` shell. Is this a placeholder/scaffold, or is the rich form functionality expected to be layered on top separately?
- The spec references `FormEditorType` enum, `FormOrientation` enum, `FormValidationMessageType` enum, `FormButtonsLayout` enum, `FormUpdateEventArgs` class, and `FormItemsTemplateContext` class -- do any of these types exist in the codebase yet?
- The spec documents both `Model` and `EditContext` with the note "using both parameters together is not supported." Should the implementation throw if both are set?

---

## 3. HarborlineLabel Gap Analysis

### Summary

`HarborlineLabel.razor` renders a `<label>` element with a CSS class from `CssProvider.LabelClass()`, a `for` attribute bound to a `For` parameter, and a `ChildContent` render fragment. The documentation maps to `floatinglabel/overview.md` which describes a **`HarborlineFloatingLabel`** component. Similar to `HarborlineField`, there is a fundamental naming and scope mismatch. `HarborlineLabel` is a basic HTML label wrapper, while the spec describes a floating label with animation, text parameter, and validation integration.

### Spec to Code Gaps (Documented but not correctly implemented)

#### Parameters

| Gap | Severity | Details |
|-----|----------|---------|
| Missing `Text` parameter | **[High]** | Spec requires `Text` (string) to define the floating label text. `HarborlineLabel` uses `ChildContent` instead of a `Text` parameter. |
| Parameter mismatch: `For` vs `Id` | **[Medium]** | Spec documents an `Id` parameter that renders on the `<label>` element. `HarborlineLabel` has a `For` parameter that renders as the `for` HTML attribute. These serve different purposes -- `Id` sets the label's own id, while `For` associates the label with an input. The spec's `Id` is missing; the `For` parameter is not documented in the spec. |

#### Behaviors

| Gap | Severity | Details |
|-----|----------|---------|
| No floating behavior | **[High]** | Spec describes the label floating over empty inputs and moving above on focus. `HarborlineLabel` renders a static `<label>`. |
| No validation color change | **[Medium]** | Spec describes the label changing color when the form field is invalid. Not implemented. |
| No animation | **[Medium]** | Spec describes built-in animations. Not implemented. |
| No placeholder interaction management | **[Medium]** | Spec describes coordinated placeholder visibility based on label position. Not implemented. |
| No wrapper container | **[Low]** | Spec indicates the FloatingLabel renders a `span.k-floating-label-container` wrapping the `<label>`. `HarborlineLabel` renders only the `<label>` element. |

### Code to Spec Gaps (Implemented but not documented)

#### Parameters

| Gap | Severity | Details |
|-----|----------|---------|
| `For` (string?) | **[Medium]** | Renders the `for` HTML attribute to associate the label with an input element by id. Not documented in the FloatingLabel spec (the spec auto-derives association from the child component's `Id`). |
| `ChildContent` (RenderFragment?) | **[Low]** | Allows arbitrary child markup as label content. The spec uses a `Text` parameter instead. |

### Recommended Changes

#### Implementation Updates

1. **[High]** Clarify whether `HarborlineLabel` is meant to be the FloatingLabel from the spec or a separate basic label component. If separate, create `HarborlineFloatingLabel` with full spec compliance.
2. **[Medium]** If `HarborlineLabel` remains a basic label, add validation-state CSS class awareness (e.g., toggling an error class when the associated field is invalid).

#### Documentation Updates

1. **[Medium]** If `HarborlineLabel` is a separate component from FloatingLabel, create its own spec page documenting `For`, `ChildContent`, `Class`, `Style`, and `AdditionalAttributes`.
2. **[Low]** Document that `ChildContent` serves as the label text.

### Open Questions / Ambiguities

- Same core question as HarborlineField: is `HarborlineLabel` intended to implement the FloatingLabel spec, or is it a basic `<label>` wrapper that should have its own documentation?
- Should `HarborlineLabel` support both `Text` (string) and `ChildContent` (RenderFragment) for label content, or pick one?
- The spec's `Id` parameter is for the label's own `id` attribute (for `aria-labelledby`). Should `HarborlineLabel` also expose an `Id` parameter separate from `For`?

---

## 4. HarborlineValidation Gap Analysis

### Summary

`HarborlineValidation.razor` renders a `<div>` with a CSS class from `CssProvider.ValidationMessageClass(Severity)` and a `Message` string parameter. The documentation specs (`validation/overview.md`, `validation/message.md`, `validation/tooltip.md`, `validation/summary.md`) describe three separate components: `HarborlineValidationMessage`, `HarborlineValidationSummary`, and `HarborlineValidationTooltip`, each with rich functionality including EditContext integration, `For` expression binding, templates, and positioning. `HarborlineValidation` appears to be a simplified, static validation message display that does not integrate with any of these documented features.

### Spec to Code Gaps (Documented but not correctly implemented)

#### Parameters (HarborlineValidationMessage spec)

| Gap | Severity | Details |
|-----|----------|---------|
| Missing `For` parameter | **[High]** | Spec requires `For` (`Expression<Func<TValue>>`) to bind the validation message to a specific model property via EditContext. Not implemented; the component uses a static `Message` string. |
| Missing `Template` (RenderFragment) | **[Medium]** | Spec documents a `<Template>` child tag with `IEnumerable<string>` context for custom rendering of all messages. Not implemented. |

#### Parameters (HarborlineValidationTooltip spec)

| Gap | Severity | Details |
|-----|----------|---------|
| Missing `TargetSelector` parameter | **[High]** | Spec documents `TargetSelector` (CSS selector) for positioning the tooltip relative to a target element. Not implemented. |
| Missing `Position` parameter | **[Medium]** | Spec documents `Position` (TooltipPosition enum) for controlling which side the tooltip appears on. Not implemented. |
| Missing `For` parameter | **[High]** | Same as ValidationMessage -- tooltip also requires `For` expression. Not implemented. |
| Missing `Template` (RenderFragment) | **[Medium]** | Same as ValidationMessage -- tooltip also supports a template. Not implemented. |

#### Parameters (HarborlineValidationSummary spec)

| Gap | Severity | Details |
|-----|----------|---------|
| No summary implementation at all | **[High]** | Spec describes `HarborlineValidationSummary` as a separate component that displays all validation errors. `HarborlineValidation` is a single-message display, not a summary. The summary component is entirely missing. |
| Missing `Template` (RenderFragment) | **[Medium]** | Spec documents a `<Template>` with `IEnumerable<string>` context for custom rendering of the summary. Not implemented. |

#### Behaviors

| Gap | Severity | Details |
|-----|----------|---------|
| No EditContext integration | **[High]** | All three spec'd components require a cascading `EditContext` to automatically retrieve validation messages. `HarborlineValidation` has no EditContext awareness -- it displays only a manually passed `Message` string. |
| No multi-message support | **[Medium]** | Spec's ValidationMessage/Tooltip show all validation messages for a field (via `IEnumerable<string>`). `HarborlineValidation` shows only a single string. |
| No tooltip rendering | **[High]** | The spec describes `HarborlineValidationTooltip` as a popup that shows on hover. No tooltip/popup behavior exists. |
| No summary rendering | **[High]** | The spec describes `HarborlineValidationSummary` as an aggregate error list. Not implemented. |
| Single component instead of three | **[High]** | Spec defines three distinct components (`HarborlineValidationMessage`, `HarborlineValidationSummary`, `HarborlineValidationTooltip`). Only one component (`HarborlineValidation`) exists. |

### Code to Spec Gaps (Implemented but not documented)

#### Parameters

| Gap | Severity | Details |
|-----|----------|---------|
| `Severity` (ValidationSeverity enum) | **[Medium]** | Accepts `Info`, `Warning`, `Error` to control visual styling. None of the three spec'd validation components document a `Severity` parameter -- they derive their severity/styling from the EditContext validation state. |
| `Message` (string?) | **[Medium]** | Accepts a static message string. The spec'd components derive messages from EditContext, not from a string parameter. This is a fundamentally different API approach. |

### Recommended Changes

#### Implementation Updates

1. **[High]** Implement `HarborlineValidationMessage` as a separate component with `For` expression parameter, cascading `EditContext` integration, `Template` support, and `Class` parameter. This should replace or supplement `HarborlineValidation` for EditContext-driven scenarios.
2. **[High]** Implement `HarborlineValidationSummary` as a separate component with cascading `EditContext`, `Template` support, and `Class` parameter.
3. **[High]** Implement `HarborlineValidationTooltip` as a separate component with `For`, `TargetSelector`, `Position`, `Template`, and `Class` parameters.
4. **[Medium]** Decide whether to keep `HarborlineValidation` as a low-level static validation display (useful for manual/imperative scenarios) or deprecate it in favor of the three spec'd components.

#### Documentation Updates

1. **[Medium]** If `HarborlineValidation` is retained, create documentation for its `Severity` and `Message` parameters and clarify its use case vs. the EditContext-integrated components.
2. **[Low]** Document base class parameters (`Class`, `Style`, `AdditionalAttributes`).

### Open Questions / Ambiguities

- Is `HarborlineValidation` intended as a building block for the three spec'd components, or as a standalone simple validation display?
- Should the `Severity` parameter be added to the spec'd components, or is it strictly an internal concern derived from EditContext state?
- The spec references `HarborlineValidationSummary` being used inside `<FormValidation>` of `HarborlineForm`. Since `HarborlineForm` lacks `<FormValidation>` support, how should the validation summary integrate until the form is fully implemented?

---

## 5. Cross-Cutting Concerns

### Naming Mismatches

| Severity | Issue |
|----------|-------|
| **[High]** | Spec names (`HarborlineFloatingLabel`, `HarborlineValidationMessage`, `HarborlineValidationSummary`, `HarborlineValidationTooltip`) do not match implementation names (`HarborlineField`, `HarborlineLabel`, `HarborlineValidation`). This will cause confusion for developers consulting the documentation. Either rename components or create the documented components as new additions. |

### Missing Components

| Severity | Component | Notes |
|----------|-----------|-------|
| **[High]** | `HarborlineFloatingLabel` | Fully documented but not implemented. `HarborlineField` + `HarborlineLabel` together do not cover its functionality. |
| **[High]** | `HarborlineValidationMessage` | Documented with `For` expression, `Template`, `Class`. Not implemented. |
| **[High]** | `HarborlineValidationSummary` | Documented with `Template`, `Class`. Not implemented. |
| **[High]** | `HarborlineValidationTooltip` | Documented with `For`, `TargetSelector`, `Position`, `Template`, `Class`. Not implemented. |
| **[High]** | `FormItem` | Documented child component of `HarborlineForm`. Not implemented. |
| **[High]** | `FormGroup` | Documented child component of `HarborlineForm`. Not implemented. |
| **[Medium]** | `FormButtons` | Documented child component of `HarborlineForm`. Not implemented. |
| **[Medium]** | `FormValidation` | Documented child component of `HarborlineForm`. Not implemented. |
| **[Medium]** | `FormAutoGeneratedItems` | Documented child component of `HarborlineForm`. Not implemented. |
| **[Medium]** | `FormItemsTemplate` / `FormGroupRenderer` / `FormItemRenderer` | Documented template/renderer system. Not implemented. |

### Base Class Parameters Not in Specs

| Severity | Parameter | Notes |
|----------|-----------|-------|
| **[Low]** | `Class` (string?) | Inherited from `HarborlineComponentBase`. Mentioned in some spec tables but not consistently across all component specs. |
| **[Low]** | `Style` (string?) | Inherited from `HarborlineComponentBase`. Not mentioned in any of the reviewed specs. |
| **[Low]** | `AdditionalAttributes` (Dictionary) | Inherited from `HarborlineComponentBase`. Not mentioned in specs. Provides HTML attribute splatting. |

### Overall Assessment

The four container components represent early-stage scaffolding. Each renders basic HTML elements with CSS provider integration and attribute splatting, but none implement the rich behaviors described in the specifications. The gap between spec and implementation is fundamental -- the specs describe a mature form framework with model binding, auto-generation, validation integration, layout management, and accessibility support, while the implementations are thin HTML wrappers. A prioritized implementation plan starting with `HarborlineForm` model/EditContext binding and `HarborlineValidationMessage` EditContext integration would provide the most value.

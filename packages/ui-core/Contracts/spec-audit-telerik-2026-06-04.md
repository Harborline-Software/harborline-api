# Spec Audit — @harborline-software/ui-react vs Telerik Blazor Components

- **Date:** 2026-06-04
- **Auditor:** ONR (subagent dispatched by Admiral)
- **Baseline:** https://github.com/telerik/blazor-docs/tree/master/components (master @ 2026-06-04)
- **Subject of audit:** Semantic contracts under `packages/ui-core/Contracts/` describing the public shape of `@harborline-software/ui-react` components
- **Companion audit:** prior DataGrid audit (gaps G-C1…G-C5 now closed in `DataDisplay/DataGrid.Semantic.md`); this report follows that depth standard

---

## Methodology

For each in-scope component:

1. Read the Semantic contract on `origin/main` (the **what we describe**).
2. Fetched the matching Telerik Blazor doc tree on `master` — minimum `overview.md` + `events.md` where present, plus `modes.md` / `appearance.md` / etc. when the component family has them (the **what a commercial peer documents**).
3. Identified gaps in three buckets:
   - **Critical (Crit)** — A common-use behavior or trigger surface that the contract does NOT address; would surprise a developer using `@harborline-software/ui-react` for a real workload.
   - **High** — A notable behavior or trigger that the contract does NOT mention even though it IS in the shipping implementation, OR a behavior the contract describes ambiguously enough that integrators could mis-wire it. (Deferred features are NOT high; they are explicit non-gaps.)
   - **Medium** — Edge cases, minor refinements, less-common use cases.
4. Did NOT flag Telerik features that are explicitly listed in our `§7 Deferred features` — those are known-and-named, not gaps.
5. Did NOT flag implementation-internal Telerik concepts that have no Harborline equivalent (e.g. `TelerikRootComponent`, theme constants, `@ref` Blazor pattern).

The audit deliberately focuses on **gaps in the spec's description of what IS built**, not on backlog items we have not built. A deferred feature is fine; an undocumented behavior in what IS built is a gap.

Where the contract is a **forward-spec** (component not yet implemented — Button, Badge, Card, Loader, Notification, Drawer, TabStrip — all M2), gaps are evaluated against the **intended** public surface rather than shipping behavior.

---

## Results by Component

### TextField

- **Telerik page:** https://github.com/telerik/blazor-docs/blob/master/components/textbox/overview.md + `events.md`
- **Our contract:** `DataEntry/TextField.Semantic.md`
- **Coverage:** **Partial** — controlled-input shape is well-documented; the trigger surface (when does change fire?) is materially under-specified vs. a commercial peer.

**Gaps found:**

- **G-TF1 (Crit) — `onChange` fire-timing not disambiguated.** Telerik documents two distinct callbacks: `ValueChanged` (per-keystroke) and `OnChange` (commit — fires on Enter/Tab or blur). Our contract calls our single `onChange` callback "fires on each keystroke" (§4) and adds in passing "debounce / commit policies are host-owned" (§3 props table). The contract does NOT name the distinction between "live" and "commit" semantics, nor confirm that the React component has no commit-on-blur callback. Hosts coming from Telerik will look for an `onCommit` / `onBlur`-with-value and not find one — and the contract doesn't address why. Open question §8.2 hints at this but doesn't resolve it.
- **G-TF2 (Crit) — No documented debounce policy.** Telerik defaults `DebounceDelay=150ms` on the TextBox. Our `onChange` fires synchronously per keystroke with no debounce. The contract says "debounce is host-owned" — but does not state that the default React `onChange` is *zero-debounce* and what that implies for `setState` cost. Hosts integrating with a remote-search pattern will need this called out.
- **G-TF3 (High) — `aria-invalid` wiring described only for `error={true}`.** §3 row "error" states `aria-invalid=true` is set when `error===true`. The contract does NOT state what happens when `error===false` (is the attribute absent? `aria-invalid="false"`? Either is reasonable; the implementation choice should be named so AT-testing hosts can assert).
- **G-TF4 (High) — Native validation interaction with controlled value undefined.** §3.1 lists which native validations the browser applies on submit, but does not address the `event.target.checkValidity()` flow, what fires `invalid`, or whether the React component intercepts. Hosts using HTML5 form validation will need this.
- **G-TF5 (Medium) — `size` token gap mapping ambiguous.** §3.2 lists the current Tailwind utility classes as "implementation values" and says PAO Styling will own token-named replacements. Acceptable as a forward-pointer, but the catalog row pin (#134 TextBox / #72 Input — note the dual catalog) is buried and the relationship to FormField sizing is not addressed (FormField might or might not own size propagation to TextField).
- **G-TF6 (Medium) — IME / composition handling missing.** Telerik docs do not deeply cover IME either, but for a v1 spec the React `onChange` behavior under `compositionstart`/`compositionend` is real-world load-bearing for CJK input. Listed under §7 Deferred — which is correct as a backlog item — but the spec doesn't acknowledge the *current* behavior (likely: keystrokes fire `onChange` during composition, which can corrupt IME). Worth a sentence in §3 noting current behavior even if richer composition handling is deferred.

---

### SelectField

- **Telerik page:** https://github.com/telerik/blazor-docs/blob/master/components/dropdownlist/overview.md + `events.md`
- **Our contract:** `DataEntry/SelectField.Semantic.md`
- **Coverage:** **Partial** — controlled-value shape and Radix dependence are clear; the event vocabulary is materially narrower than Telerik's and the contract doesn't address why most of the difference exists or is deferred.

**Gaps found:**

- **G-SF1 (Crit) — `onOpenChange` / popover lifecycle events undocumented.** Telerik has `OnOpen` and `OnClose` events with cancelable args (`IsCancelled = true` aborts the open). Our contract lists `onOpenChange` in §7 Deferred — acceptable as a backlog item — but does not state the current behavior (the popover opens / closes silently with no callback whatsoever; hosts cannot observe). For real Harborline workloads that need to e.g. lazy-load options when the popover opens, this is a load-bearing missing event AND a missing description of why hosts can't intercept it.
- **G-SF2 (Crit) — No `OnRead` / async-load contract.** Telerik documents `OnRead` for lazy data loading / virtualization. Our `options` prop is described as a flat synchronous array (`SelectOption[]`). Hosts that want to load options async (an `<DataGrid>` reference-data scenario, common in ERP UIs) will read our spec and ask "how do I load on-open / on-filter?" — and find no answer. §7 lists async deferred, but does not name the canonical pattern the host should use *today* (presumably: hold `options` in state, drive load yourself, and live with the open-without-callback limitation per G-SF1).
- **G-SF3 (High) — `value === ''` as "no selection" sentinel surprises typed-value hosts.** §3 says `value` must be one of `options[].value` strings OR empty string. Telerik's `DefaultText` shows when "Value equals 0 (integers) or null (nullable types/strings)" — i.e. Telerik admits an explicit "no value" sentinel per type. Our string-only model with `""` as null-equivalent is a deliberate choice but not justified in the contract. Hosts whose value is naturally numeric (`tenant_id: number`) must serialize at the boundary; this is not called out in §3 or §6.
- **G-SF4 (High) — Filtering / typeahead behavior absent.** Telerik documents `Filterable`, `FilterOperator`, `FilterPlaceholder`, `FilterDebounceDelay` — first-class filter UI. We defer (§7 "searchable / typeahead"). That's a legitimate defer, but the contract should name what `@harborline-software/ui-react` does today when an option list is long — does the Radix popover scroll? Does the user have any way to find an option without scrolling? Confirmed Radix behavior (typing in the popover does match-and-scroll via `Select.Item` value matching the typed prefix) is not in the spec. Hosts may not realize they get partial typeahead for free.
- **G-SF5 (Medium) — Per-option `disabled` deferred but not documented as silent-fail.** §7 lists per-option disabled as deferred. The Radix primitive supports it; our wrapper presumably ignores any `disabled` field on `SelectOption`. The contract should state that adding an extra field to a `SelectOption` is silently no-op (vs. type-error) so hosts know not to "just add it and hope."
- **G-SF6 (Medium) — `aria-invalid` parity.** Same as G-TF3 — the spec names the `error===true → aria-invalid=true` flow but not the `error===false` resting state.

---

### DateField

- **Telerik page:** https://github.com/telerik/blazor-docs/blob/master/components/datepicker/overview.md + `events.md`
- **Our contract:** `DataEntry/DateField.Semantic.md`
- **Coverage:** **Good** — the native-`<input type="date">` choice is well-motivated and the deviation from Telerik's full DatePicker is explicit. Gaps below are mostly edge-case behavior that hosts will hit in practice.

**Gaps found:**

- **G-DF1 (High) — `onChange` semantics during invalid intermediate states.** Our spec says `onChange` fires when the user picks a date or clears the field. Native `<input type="date">` actually fires `change` only on commit (focus leaves the picker, or user picks via the calendar). Browsers differ on partial typed input (`"2026-06-"` mid-typing) — some fire `change`, some don't, some emit `value=""`. The spec is silent on the intermediate-state contract and on cross-browser variance, which is a real footgun.
- **G-DF2 (High) — Invalid-typed-input recovery undocumented.** When the user types `"99/99/9999"` (or whatever the locale parses), the native input either keeps the prior `value` or clears to `""`. The contract is silent on what `onChange` fires in that case. Telerik's DatePicker has explicit doc on parse/format round-tripping with `ValidateOn` — we have none.
- **G-DF3 (Medium) — `min` / `max` enforcement is picker-only.** §3 says "native picker disables earlier/later dates." It does NOT say what happens when the host types a date outside `[min, max]` (browser allows it, fires `change` with the out-of-range value, native validation flags `rangeUnderflow/Overflow` only on submit). Hosts who use min/max as a "hard floor" guarantee will be surprised.
- **G-DF4 (Medium) — Clear-button availability inconsistent across browsers.** §4 says "or clears the field (some browsers expose a clear button)." Worth a stronger callout: Chrome shows one, Safari does not. Hosts who want a guaranteed clear gesture need their own (e.g. a Button next to the field). Telerik's `ShowClearButton` is first-class; we defer (correctly) but should name the consequence.
- **G-DF5 (Medium) — `today` button / quick-shortcut absent.** Telerik DatePicker has a today / clear footer. We defer (§7). Acceptable; flagged because property-management workflows ("Show me today's work orders") commonly want this and hosts will work around it via their own button.

---

### NumberField

- **Telerik page:** https://github.com/telerik/blazor-docs/blob/master/components/numerictextbox/overview.md + `events.md`
- **Our contract:** `DataEntry/NumberField.Semantic.md`
- **Coverage:** **Good** — the `string` payload choice is well-explained and the controlled-state model is clear. Gaps below are mostly about behavior NumberField inherits from the native input that the contract doesn't name.

**Gaps found:**

- **G-NF1 (Crit) — Commit semantics vs. typing semantics not disambiguated.** Identical structural issue to G-TF1. Telerik distinguishes `ValueChanged` (typing) from `OnChange` (commit on Enter/blur). Our single `onChange` fires "on every change" — but for the spinner buttons, paste, and arrow keys, the firing pattern differs. Hosts who want "commit on blur" semantics (very common for monetary fields where mid-typing values should not trigger a re-query) have no documented hook.
- **G-NF2 (Crit) — Wheel-scroll-changes-value not addressed.** Native `<input type="number">` increments on mouse-wheel when focused — a well-known footgun for forms (user scrolls the page, the focused number changes silently). Telerik's NumericTextBox disables wheel by default. Our contract doesn't mention wheel behavior at all; the implementation almost certainly inherits the native behavior. This is a real-world bug source for Harborline forms.
- **G-NF3 (Crit) — Locale + decimal-separator handling missing.** Native `<input type="number">` parses `","` vs `"."` per browser locale; the *string* payload from `e.target.value` may or may not be `Number()`-parseable depending on locale. Our contract says "the host parses" (§3.1) but does not warn that `Number("1,5")` returns `NaN` in en-US, where the user with a de-DE browser may have typed `1,5`. Telerik's NumericTextBox handles this internally. We push it to the host but don't flag the cross-locale parsing pitfall.
- **G-NF4 (High) — `step` and `min`/`max` enforcement scope.** §3.2 says "the native browser handles spinner-bound enforcement; typed input is unrestricted." Good, but the spec doesn't mention that `step` interacts with `min`: the spinner increments are `min + n*step`, not `0 + n*step`, which is a common source of confusion when `min=0.5, step=1` (spinner goes 0.5, 1.5, 2.5…).
- **G-NF5 (High) — `value` mid-typing intermediate states are common in practice.** §2 acknowledges `value: number | string`. §3 column "Default" leaves the host's choice. The contract should give a stronger recommendation: "Use `string` for fields the user types into; use `number` only for programmatically-set values." Otherwise hosts will mix and hit React `valueAsNumber` quirks.
- **G-NF6 (Medium) — `valueAsNumber` field on the DOM event not addressed.** Native input exposes `e.target.valueAsNumber` which is `NaN` for empty / invalid. Our contract passes `e.target.value` (string). Hosts who reach for `e.target.valueAsNumber` get `NaN`; not flagged as a gotcha.
- **G-NF7 (Medium) — `SelectOnFocus` (Telerik default) parity.** Telerik selects the content on focus by default. Our contract doesn't specify; default browser behavior is *not* to select. Worth naming the gap so hosts know they need to add their own select-on-focus handler.

---

### CheckboxField

- **Telerik page:** https://github.com/telerik/blazor-docs/blob/master/components/checkbox/overview.md + `events.md`
- **Our contract:** `DataEntry/CheckboxField.Semantic.md`
- **Coverage:** **Good** — the indeterminate-suppression decision is documented honestly, FormField composition is acknowledged as awkward. Gaps are minor.

**Gaps found:**

- **G-CB1 (High) — Indeterminate is filtered, but not "blocked from being set" externally.** §3.1 says we filter out `'indeterminate'` from `onCheckedChange`. The contract does NOT clarify: can a host set `checked={'indeterminate'}` via Radix prop-drilling (no — type is `boolean`)? Can the *visual* indeterminate state ever be rendered? The current API forces hosts to a `boolean`-only world; this is fine, but the contract should state explicitly: "CheckboxField cannot render an indeterminate visual state in M1; the underlying Radix primitive's indeterminate mode is unreachable through our API." Without that, integrators may think there's a hidden indeterminate toggle.
- **G-CB2 (High) — Open question §8.1 ("explicit `indeterminate?: boolean` prop") collides with the DataGrid select-all use case.** DataGrid §3.2 internally renders a header checkbox that *should* be indeterminate when some-but-not-all rows are selected. The contract treats that as "internal to DataGrid, not a public component." OK — but the spec doesn't say *how* DataGrid does it (does it use a different component? Bypass our CheckboxField? Use Radix directly?). This is a coordinated-spec gap, not a CheckboxField-isolated gap.
- **G-CB3 (Medium) — Label-click toggle works for the bundled label only.** §3.2 confirms that clicking the bundled label toggles the checkbox. The contract doesn't address what happens when CheckboxField is wrapped in a FormField (whose `label` is separately wired to the checkbox via `htmlFor`). Both labels presumably toggle; if so, that's fine — but the FormField's label is `<label htmlFor={name}>` and the CheckboxField's bundled label is also `<label htmlFor={name}>`, which means a screen reader sees two labels. Either AT announces both (verbose) or one wins (which?). §6 hints at this awkwardness but doesn't resolve it.
- **G-CB4 (Medium) — `OnChange` vs `ValueChanged` parity (Telerik distinction) absent.** Telerik documents a distinction between `OnChange` (works with two-way binding) and `ValueChanged` (requires manual update). Our single `onCheckedChange` is the React equivalent of `ValueChanged` (host manages state). The contract doesn't compare to Telerik for hosts migrating from a Blazor reference.
- **G-CB5 (Medium) — `ShowClearButton`-style "uncheck without focus" affordance does not exist.** Telerik doesn't have this for checkbox either, so flag is informational only — not a real gap, but worth noting CheckboxField has no "tri-state with explicit unchecked" affordance for hosts that want a "no opinion" mode (cf. DataGrid filtering: an unchecked checkbox is the same as "off", not "no filter"). This is a Harborline-wide UX policy question, not a CheckboxField bug.

---

### Dialog

- **Telerik page:** https://github.com/telerik/blazor-docs/blob/master/components/dialog/overview.md + `events.md` (also referenced Window for comparison)
- **Our contract:** `Overlays/Dialog.Semantic.md`
- **Coverage:** **Good** — Radix dependence, portal/scroll-lock, the always-on close button, and ARIA wiring are all addressed. Gaps are mostly about callbacks the contract chose not to expose.

**Gaps found:**

- **G-DG1 (Crit) — Single `onOpenChange` collapses three distinct close gestures.** Telerik's Dialog allows the host to cancel a close (`VisibleChanged` with a "don't commit"). Our contract conflates close-button, overlay-click, and Escape into one callback. §7 lists `onPointerDownOutside / onEscapeKeyDown` as deferred; council OQ §8.3 acknowledges this is "important for production form dialogs." This is a near-universal request — fast-follow worthy. Right now hosts cannot suppress overlay-click-during-upload, which is a real production footgun.
- **G-DG2 (High) — Size constraint (`max-w-lg`) is fixed and not parameterized.** §3.3 admits this; OQ §8.1 acknowledges the need for `size`. Telerik's Window component has `Small/Medium/Large` + custom width/height; their Dialog inherits similar via `Width`/`Height`. We are constrained. Flagged because for ERP workloads (a 3-column form, or a data-table inside a dialog) `max-w-lg` is too narrow; hosts will inevitably pass custom CSS classes to override.
- **G-DG3 (High) — Body region scroll behavior undocumented.** Telerik's Dialog gives a max-height + inner scroll. Our spec doesn't say what happens when `children` content overflows the dialog's vertical space. Does the dialog grow? Does the body scroll? Does the page scroll-lock prevent the inner scroll from working? Radix's default is "body grows, page is scroll-locked" — but our spec is silent.
- **G-DG4 (High) — Header height + footer border are implementation-locked.** §3.4 fixes the close button position; §5 fixes footer to have a `border-t`; neither is parameterized. Hosts who want a "dialog without a footer divider" or a "header without a close button" have no escape hatch in M1. Telerik's `ShowCloseButton=false` covers the second case directly.
- **G-DG5 (Medium) — `FocusedElementSelector` / `initialFocusRef` parity.** Telerik has `FocusedElementSelector`. We rely on Radix's default (first focusable). For dialogs containing complex forms, hosts often want focus to land on a specific input. Listed as a deferred-via-omission (not mentioned in §7) — should be explicit.
- **G-DG6 (Medium) — Nested dialogs explicitly untested.** §7 says "the M1 contract is silent and the implementation is not tested for nested behaviour." Worth strengthening to "nested dialogs are unsupported in M1; behaviour is undefined" so hosts don't try.

---

### ConfirmDialog

- **Telerik page:** No direct Telerik counterpart (the spec acknowledges this — ConfirmDialog is Harborline-native composition over Dialog). Compared to Telerik's general Dialog with manually-constructed buttons.
- **Our contract:** `Overlays/ConfirmDialog.Semantic.md`
- **Coverage:** **Good** — the wrapper composition is rigid and the contract is explicit about the trade-offs.

**Gaps found:**

- **G-CD1 (Crit) — `loading` async-confirm omission is acknowledged but the consequence is severe.** §3.3 + §7 + §8.1 all acknowledge `loading?: boolean` is deferred. The auto-close-on-confirm fires *before* the host's async work completes, meaning a host calling `await api.delete(...)` from inside `onConfirm` will see the dialog close, then potentially the operation fails, and the user has no in-dialog feedback. Council OQ §8.1 says "fast-follow once we see real call sites" — this is the kind of footgun that needs an explicit "DO NOT use ConfirmDialog for async work that may fail" warning in §3.3 (currently the advice is "show a separate toast"; that's good but the warning should be stronger).
- **G-CD2 (High) — `variant: warning` open question is unresolved but high-value.** §8.3 raises a `warning` (yellow/amber) variant. Property-management flows have classic warning-but-not-destructive cases ("This will email the tenant — proceed?"). Defaulting to `destructive` (red) overstates the consequence; using `default` (blue) understates. A `warning` middle is real. Flagged because the open question hasn't been resolved.
- **G-CD3 (High) — Auto-close on Cancel undocumented in §4.** §3 says Cancel triggers `onOpenChange(false)`. §4 confirms `onOpenChange` fires on Cancel click. But the contract doesn't explicitly state Cancel "always" closes (vs. Confirm which closes after `onConfirm()`). Hosts may wonder if they need to call `onOpenChange(false)` from a Cancel callback that doesn't exist (there isn't a `onCancel` prop). The clarity is implicit; it should be explicit.
- **G-CD4 (Medium) — `confirmLabel='Confirm'` default is too generic for destructive variant.** When `variant='destructive'`, the canonical label is "Delete" / "Archive" / similar — `'Confirm'` is wrong-feeling for a destructive action. The contract should recommend hosts override `confirmLabel` whenever `variant='destructive'` (or, more aggressively, make `confirmLabel` *required* when `variant='destructive'`).
- **G-CD5 (Medium) — Body slot omission is a recurring request the contract should pre-empt.** §7 lists "Body slot" as deferred. Common case ("Don't show again" checkbox). Worth a `composition recipe` showing how to use Dialog directly for this case rather than waiting on ConfirmDialog body support.

---

### Drawer

- **Telerik page:** https://github.com/telerik/blazor-docs/blob/master/components/drawer/overview.md + `modes.md` + directory listing
- **Our contract:** `Overlays/Drawer.Semantic.md`
- **Coverage:** **Good** — modal/non-modal, side, size, animation, and Radix Dialog composition are well-documented. Gaps are mostly about navigation features Telerik bundles that we don't.

**Gaps found:**

- **G-DR1 (Crit) — Conceptual scope difference with Telerik's Drawer not flagged.** Telerik Drawer is a **navigation drawer** with built-in `Data` / `SelectedItem` / `ItemTemplate` for a list of nav items. Our Drawer is an **overlay panel** (closer to Telerik's `Window` in non-modal mode + slide animation). This is a fundamental shape mismatch that hosts coming from Telerik will hit immediately. The contract acknowledges Harborline AppLayout's overlay-mode side-nav is "conceptually a Drawer" (§1) and OQ §8.3 raises whether AppLayout reuses Drawer. The contract should state explicitly that **`@harborline-software/ui-react` Drawer is a Sheet-pattern overlay panel, NOT a navigation drawer with item rendering** — Telerik users will conflate.
- **G-DR2 (Crit) — `Mode='Push'` (content-resize, non-overlay) is deferred but the deferral should be louder.** Telerik's `Push` mode resizes the main content area to make room for the drawer. We support only overlay-style (modal + non-modal both float; `modal=false` lets the page stay interactive but does NOT resize the page layout). §7 lists "Inline (push) variant" as deferred — this is the correct posture. Worth a stronger callout: hosts who want a persistent inspector panel that lives *alongside* main content (not overlapping it) cannot use Drawer for that; they need AppLayout's future right-rail slot. Currently buried in §7.
- **G-DR3 (High) — `closeOnBackdropClick` / `closeOnEscape` opt-outs OQ §8.4 is unresolved.** Telerik exposes these on its Window. The OQ leans "add both — cheap, useful, addresses the 'accidentally lost my edit' footgun." Recommend resolving in M2 rather than deferring.
- **G-DR4 (High) — `size` per-side asymmetry not enforced.** §3.2 lists pixel-canonical defaults per size. For `side='top'`/`bottom'`, the sizes are heights; for `right`/`left`, widths. Hosts who pass `size='full'` on a `side='bottom'` drawer get `100vh` height — which is also a `right`-slide-full-vw, semantically the same as a full-screen takeover. Worth noting that `size='full'` + any `side` collapses to "full-screen overlay."
- **G-DR5 (Medium) — `MiniMode` (Telerik) deferred via omission.** Telerik supports a collapsed mini-rail mode where the drawer shrinks to a narrow icon strip but stays visible. Not in our §7. Acceptable defer; worth listing for parity awareness.
- **G-DR6 (Medium) — Multiple drawer stacking (§7 deferred) — should state behavior when violated.** §7 says stackable drawers are deferred. What happens if a host opens drawer A then opens drawer B? Radix Dialog stacking *works*; our spec doesn't say what the focus-trap and z-index behavior is. Worth a sentence on "behaviour undefined; do not nest in M2."
- **G-DR7 (Medium) — Animation customization deferred but no escape hatch.** §3.6 says ~300ms canonical; §7 says customization deferred. For hosts who *must* match a different motion system (rare but happens), the contract gives no opt-out — no class passthrough on the animation surface (the root passthrough goes to a wrapper). Listed in §3.7 — but PAO Accessibility / Styling owns ARIA + tokens, not animation. Worth clarifying the "no animation override" door is currently fully closed.

---

### Notification (Toast)

- **Telerik page:** https://github.com/telerik/blazor-docs/blob/master/components/notification/overview.md + `events.md`
- **Our contract:** `Feedback/Notification.Semantic.md`
- **Coverage:** **Good** — Sonner-based provider + imperative API model is clear; dedupe, max, position, and variant vocabularies all addressed.

**Gaps found:**

- **G-NF1 (Crit) — Hover-to-pause is undocumented.** Sonner (which our spec names as the foundation, §header) **pauses the auto-dismiss timer on hover by default**. This is widely-relied-on UX (user hovers to read a long error before it dismisses). Our spec is silent. Telerik's notification has no hover-pause and we may inherit Sonner's behavior — but the contract should state explicitly whether hover-pause is on (recommend: yes, document as inherited from Sonner) so hosts can rely on it.
- **G-NF2 (Crit) — Action click + auto-dismiss timing is council-OQ unresolved.** §8.2 leans auto-dismiss after action click. §3.5 says toasts auto-dismiss after `duration`. The interaction between "user clicks Retry, host's `onClick` fires an async retry that takes 2s, but the toast already dismissed at click-time" is unaddressed. Hosts whose Retry fails need to re-emit the toast. The contract should state this loop explicitly.
- **G-NF3 (High) — `position` per-toast override deferred, but provider-level alternative not described.** §7 lists per-toast position override as deferred. The workaround (mount multiple `<NotificationProvider>` instances at different positions?) is undocumented; that pattern is presumably unsupported (each provider creates its own viewport, and `useNotification()` would bind to the nearest one — context behavior). §8.5 raises singleton enforcement; worth resolving alongside per-toast position.
- **G-NF4 (High) — Animation timing parity with Telerik.** Telerik exposes `AnimationType` and `AnimationDuration` (default 300ms Fade). Sonner has its own enter/exit animation, presumably faster (~150ms slide-in). Our spec defers customization; the *current* animation profile (Sonner's defaults) is not named. Hosts integrating notifications with their motion system can't predict.
- **G-NF5 (High) — `description` is optional but presence vs. absence layout impact undocumented.** §3.2 says `title || description` required (one or both). The visual treatment when both are present vs. title-only vs. description-only is not addressed (does a description-only toast look like a title? Center-align? Larger font?). Hosts will figure this out via Storybook; the spec should at least name the visual hierarchy.
- **G-NF6 (Medium) — Max-stack FIFO eviction announces nothing to AT.** §3.5 says oldest is evicted when `max` is exceeded. AT users may have just heard a toast announced and then it silently disappears. PAO Accessibility owns the resolution; spec should flag that the eviction is silent.
- **G-NF7 (Medium) — Promise-based API (`notify.promise(...)`) is Sonner-canonical and very common.** §7 lists it as deferred. Worth highlighting as a near-universal fast-follow request the moment hosts have a real save-then-confirm flow (Sonner code-examples lead with this pattern).

---

### Button

- **Telerik page:** https://github.com/telerik/blazor-docs/blob/master/components/button/overview.md + `appearance.md`
- **Our contract:** `DataEntry/Button.Semantic.md`
- **Coverage:** **Good** — Radix Slot pattern, variant + size vocab, loading state, and HTML passthrough are all addressed; forward-spec posture is explicit.

**Gaps found:**

- **G-BT1 (Crit) — Variant ↔ FillMode mapping not declared.** Telerik decouples `ThemeColor` (Primary/Secondary/Tertiary/Info/Success/Warning/Error/Base/Dark/Light) and `FillMode` (Solid/Flat/Outline/Link/Clear). Our `variant` ('primary' / 'secondary' / 'tertiary' / 'destructive' / 'ghost') conflates the two: `'ghost'` is a fillmode concept; `'destructive'` is a themecolor concept; `'tertiary'` is a hybrid. Hosts who want "a tertiary button with a destructive color" cannot express it in our model. This is a deliberate design choice (PAO Styling owns the token surface), but the contract should NAME the conflation explicitly so hosts know not to expect the cross-product.
- **G-BT2 (Crit) — `size='icon'` removes the `leadingIcon`/`trailingIcon` slots.** §3 column for `children` says "For `size: 'icon'`, `children` is the icon itself; `leadingIcon`/`trailingIcon` are unused." This is a small surprise: a host iterating over icon names in a loop and passing them all into `leadingIcon` will find icon-buttons render blank when they switch `size` to `'icon'`. Worth a stronger callout that `'icon'` mode is structurally different from text-button mode.
- **G-BT3 (High) — `loading` state behavior on `asChild` is undefined.** §3.2 says when `asChild=true`, `type` is ignored. §3.3 doesn't address what happens to `loading` when `asChild=true`. The `<Link>` or `<a>` child doesn't have a native `disabled` to suppress clicks; presumably we apply `pointer-events: none` via the Slot className. Spec should state this explicitly — or list `asChild + loading` as unsupported.
- **G-BT4 (High) — Type-`'submit'` defaulting-to-`'button'` is a forward-spec opinion that diverges from native HTML.** §3 + §3.2 are clear about the choice. Hosts coming from Telerik (which uses `ButtonType` enum with no default-override) will be surprised by submit-in-a-form not submitting. The §8.2 council OQ acknowledges this — recommend resolving (lean: keep `'button'` default) and add a clearer migration note for Telerik users.
- **G-BT5 (Medium) — `loadingText` deferral, with stronger reasoning.** §7 lists `loadingText` as deferred (shadcn keeps children visible). Worth a forward-pointer: many real Harborline hosts will want "Saving…" vs. their normal label during async; a `loadingChildren` slot could land in fast-follow.
- **G-BT6 (Medium) — `Rounded` (border-radius) treatment is missing.** Telerik exposes a separate `Rounded` parameter (Small/Medium/Large/Full/None). Our Button has no equivalent — radius is implicit per `variant`/`size`. Hosts who want a pill button or a square button must pass className. Acceptable defer; worth listing as a near-term gap because filter-chip-style buttons are very common.
- **G-BT7 (Medium) — Open question §8.5 (icon-only aria-label enforcement) is real risk.** Icon-only Buttons without `aria-label` are a major accessibility regression source. The PAO Accessibility owner is fine; the contract should at least say "PAO Accessibility MAY add a dev-mode runtime check for icon-only buttons missing aria-label."

---

### Badge

- **Telerik page:** https://github.com/telerik/blazor-docs/blob/master/components/badge/overview.md
- **Our contract:** `DataDisplay/Badge.Semantic.md`
- **Coverage:** **Good** — variant/size/appearance/shape/passthrough all addressed. Gaps are around positioning and overlay-on-parent patterns Telerik bundles.

**Gaps found:**

- **G-BD1 (Crit) — Badge-on-parent (overlay positioning) pattern is missing.** Telerik Badge has `Position`/`HorizontalAlign`/`VerticalAlign` for positioning the badge *over* a parent element (the classic notification-dot-on-an-icon pattern). Our Badge is inline-only — §3.6 says renders `<span>` by default. Hosts who want a "23 unread" overlay dot on a notification bell icon must compose it themselves (absolute-position the Badge over the icon). Not flagged as deferred in §7. This is the single biggest functional gap vs. Telerik.
- **G-BD2 (High) — `ShowCutoutBorder` (separation from container) parity absent.** When a Badge overlays a parent (G-BD1), Telerik renders a small cutout border to visually separate the badge from the icon underneath. Our Badge doesn't have an analog (because we don't do overlay). Listed for awareness; flag if G-BD1 is addressed.
- **G-BD3 (High) — `Sign` (numeric badge with min/max truncation) parity absent.** Telerik Badge has numeric mode with min/max/value and auto-truncation to "99+". Our §7 lists "Numeric overflow handling — hosts handle via string formatting" — correct defer for a presentational component, but worth a sentence: "If your badge content is a numeric count and may exceed 2 digits, format outside the Badge."
- **G-BD4 (High) — `variant` taxonomy mismatch with Telerik.** Telerik: Primary/Secondary/Tertiary/Info/Success/Warning/Error/Base. Ours: default/secondary/info/success/warning/danger. The mapping is intuitive but the `danger` vs. `error` naming intentionally diverges (Notification uses `error`, Badge uses `danger`). §8.4 of Notification.Semantic.md addresses the diff. Worth a back-pointer here so the divergence is intentional.
- **G-BD5 (Medium) — Default `appearance: 'subtle'` divergence from shadcn (`'solid'`) is acknowledged in OQ §8.2.** Worth resolving the OQ. Lean: `'subtle'` (per current spec).
- **G-BD6 (Medium) — Open question §8.4 (`accent` semantics) is not in the current variant list.** Confusion in §8.4 ("Is `accent` clear...") refers to a variant `accent` that isn't in the §3 table. Looks like a copy-paste from an earlier draft. Suggest dropping §8.4 or aligning.

---

### Card

- **Telerik page:** https://github.com/telerik/blazor-docs/blob/master/components/card/overview.md
- **Our contract:** `Layout/Card.Semantic.md`
- **Coverage:** **Good** — named sub-component composition is well-aligned with Telerik; the variant/padding vocabulary is more granular than Telerik's.

**Gaps found:**

- **G-CR1 (Crit) — `Orientation` (horizontal vs vertical) is missing.** Telerik Card has `Orientation: Horizontal | Vertical` for laying out CardImage + CardBody side-by-side (classic media-card pattern). Our Card has no orientation prop; all sub-components stack vertically. §7 lists `<CardMedia>` as deferred — orientation would naturally land with media support. Worth listing as a near-term gap because the dashboard summary-card-with-icon-on-the-left pattern needs it.
- **G-CR2 (High) — `CardImage` / media region absent.** §7 acknowledges deferral. Strong gap vs. Telerik. Property-management UI (lease cards, property cards, vendor cards) commonly wants an image. Flagged because the deferral comment ("deferred to media-card wave") undersells how common the request will be.
- **G-CR3 (High) — `CardSubTitle` vs `CardDescription` naming difference.** Telerik uses `CardSubTitle`; we use `CardDescription`. Both are reasonable; if integrators reference Telerik docs they may search for SubTitle. Worth a one-line alias note in §5.
- **G-CR4 (High) — `CardActions` (Telerik) vs. `CardFooter` (ours) semantic difference.** Telerik's CardActions is for action buttons; CardFooter is for metadata. We collapse both into CardFooter (§6 says it "typically holds actions"). Hosts who want metadata *and* actions in two distinct rows have no documented pattern (presumably: nest a custom div in `<CardFooter>`). Worth naming explicitly or splitting in future.
- **G-CR5 (Medium) — `CardSeparator` (Telerik) — handled via our `separators: boolean` (§3.1)?** Our `separators` prop renders dividers between Header / Content / Footer regions. Telerik's CardSeparator is an inline element hosts place explicitly between sub-components. The difference is meaningful: we don't expose per-position separator control. Acceptable for M2; worth naming the difference.
- **G-CR6 (Medium) — Deck / Group / auto-height-syncing patterns deferred.** Telerik has Card Deck for auto-height-syncing card rows. Our spec doesn't address it. Hosts who put 3 cards in a flex row and want them all the same height must do it themselves (`items-stretch`).
- **G-CR7 (Medium) — `asChild` + interactive descendants warning is good but should be code-snipped.** §8.6 raises the issue. §7 doesn't list a deferred fix. Worth a sentence in §3.7 with a small recipe: "When the whole card is clickable AND a Footer button is needed, wrap the button's `onClick` with `e.stopPropagation()`."

---

### Loader

- **Telerik page:** https://github.com/telerik/blazor-docs/blob/master/components/loader/overview.md + https://github.com/telerik/blazor-docs/blob/master/components/loadercontainer/overview.md
- **Our contract:** `Feedback/Loader.Semantic.md`
- **Coverage:** **Good** — separates `<Loader>` (atom) from `<LoaderOverlay>` (panel) cleanly, mirrors Telerik's split. Variants and sizing are well-defined.

**Gaps found:**

- **G-LD1 (Crit) — `LoaderOverlay` scope (fullscreen vs scoped) is undocumented.** Telerik's LoaderContainer "by default fills the browser viewport" and is scoped to a parent only when `position: relative` is set on that parent. Our LoaderOverlay's behavior is implicit ("wraps a region; renders `children` underneath and overlays a Loader + scrim when `active`"). The contract says §3.5 "active flips to true → scrim covers children." OK — but is the scrim absolutely positioned relative to LoaderOverlay's container (yes, presumably)? Does it cover the viewport? Does it require the host to set `position: relative` on the wrapper? Hosts will hit this immediately.
- **G-LD2 (High) — `LoaderOverlay`'s `position: relative` requirement undocumented.** Following from G-LD1: if LoaderOverlay's internal absolute-positioning needs a containing block, hosts must set `position: relative` on the parent (or LoaderOverlay sets one itself on its wrapping div). Telerik documents this requirement clearly. We don't.
- **G-LD3 (High) — `Type` parity — Telerik names Pulsing/InfiniteSpinner/ConvergingSpinner.** Our `variant: 'spinner' | 'dots' | 'bar'` doesn't map 1:1. Telerik's `Pulsing` (default) is closer to our `'dots'`. Our `'spinner'` matches Telerik's `InfiniteSpinner`. We have a `'bar'` Telerik doesn't have. Worth a comparison table in the spec for hosts migrating.
- **G-LD4 (High) — Inline label visibility behavior surprises.** §3.4 says inline mode shows the label next to the spinner; non-inline hides the label (AT-only). Hosts who want a centered spinner WITH a visible "Loading…" caption must use inline (which is left-aligned) or pass `aria-hidden` workarounds. Worth a `caption: ReactNode` slot or similar; Telerik's LoaderContainer has `Text` for this.
- **G-LD5 (Medium) — `OverlayThemeColor` (light/dark scrim) parity absent.** Telerik LoaderContainer's `OverlayThemeColor='light'|'dark'`. Our `blur` is the only customization; the scrim color is implicit (`bg-white/60`). Hosts in dark mode will want a dark scrim. PAO Styling owns; flag for parity.
- **G-LD6 (Medium) — `LoaderPosition` (top/bottom/start/end relative to text) — N/A for us.** Our inline mode is always spinner-then-label. Telerik exposes `LoaderPosition`. Not a major gap, but listed for parity.
- **G-LD7 (Medium) — Delayed-render (no-flash-for-fast-ops) deferral is correct but worth a recipe.** §7 lists this as deferred. The host pattern (`useEffect` + 150ms timer before rendering Loader) is a one-liner; worth a recipe in §6 rather than leaving hosts to roll it themselves.

---

### Pager

- **Telerik page:** https://github.com/telerik/blazor-docs/blob/master/components/pager/overview.md + `events.md`
- **Our contract:** `DataDisplay/Pager.Semantic.md`
- **Coverage:** **Good** — stateless display + emit model is clean; 1-based vs 0-based and host-owns-clamping are explicitly named.

**Gaps found:**

- **G-PG1 (Crit) — Numbered page-link buttons are deferred — but this is the dominant pattern.** §7 lists "Numbered page-link list (1 / 2 / 3 / … / N)" as deferred. Telerik's Pager has `ButtonCount` for exactly this. The Prev/Next-only baseline is a real Harborline constraint, but the vast majority of Telerik-trained users expect numbered buttons. This is the single biggest functional gap. Flagged as Critical because it materially changes the UX vocabulary.
- **G-PG2 (Crit) — `InputType` (text-input page-jumper) absent.** Telerik's `InputType=PagerInputType.Input` renders a text input for direct page entry. §7 lists "Jump to page" direct input as deferred. Same severity argument as G-PG1 — common pattern, deferred.
- **G-PG3 (High) — Responsive / `AdaptiveMode` behavior absent.** Telerik's Pager has `Responsive` (true default) and `AdaptiveMode` for collapsing the controls on narrow screens. Our spec doesn't address what happens on mobile (~400px width). Likely the controls overflow horizontally; the spec is silent.
- **G-PG4 (High) — `pageSizeOptions` `null` / "All" entry parity.** Telerik supports `PageSizes` including `null` for "All" (no pagination). Our `pageSizeOptions: number[]` has no null/"All" affordance. Hosts who want "show all 240 rows" must drive `pageSize=240` themselves.
- **G-PG5 (Medium) — `aria-live` dual-region OQ §8.3 unresolved.** Worth resolving — single live region is recommended for clarity.
- **G-PG6 (Medium) — Locale number formatting in "Showing X–Y of Z" deferred.** §7 lists this; worth a note that the current uses `Intl.NumberFormat` default vs. raw `String()`. Recommend `toLocaleString()` even in M1 — cheap, useful.
- **G-PG7 (Medium) — `Refresh` button (Telerik) is N/A for us.** Telerik has a refresh action in the pager. Listed for parity awareness only.

---

### TabStrip

- **Telerik page:** https://github.com/telerik/blazor-docs/blob/master/components/tabstrip/overview.md + `events.md`
- **Our contract:** `Navigation/TabStrip.Semantic.md`
- **Coverage:** **Good** — Radix-canonical composition (TabList / Tab / TabPanel) is clear, controlled-only stance is explicit, orientation + variant + activationMode all addressed.

**Gaps found:**

- **G-TS1 (Crit) — Overflow handling deferred — material UX gap.** §7 lists "Overflow handling — scrollable horizontal tabs when they exceed available width, with arrow scroll buttons." Telerik has `OverflowMode = None | Scroll | Menu` (default likely Scroll). Our M2 baseline "lets tabs overflow naturally; hosts must keep tab count modest" is a real constraint. Property detail pages with 5–6 tabs (Overview / Leases / Work Orders / Documents / History / Notes) commonly exceed mobile widths. Critical because there's no host-side workaround that's not janky.
- **G-TS2 (High) — Closeable tabs deferred.** §7 lists this. Telerik supports it as a tab-level feature. Listed for awareness — IDE-style tabs not in scope is a fair stance.
- **G-TS3 (High) — `value`/`onValueChange` API name parity.** Radix names it `value` / `onValueChange`. Telerik names it `ActiveTabId` / `ActiveTabIdChanged`. We follow Radix. Worth a one-line note in §3 confirming Harborline follows Radix naming rather than Blazor/Telerik naming.
- **G-TS4 (High) — `<Tab disabled>` arrow-key skipping behavior should be named.** §3.3 says disabled tabs are "skipped in arrow-key navigation." This is Radix default — worth confirming it's deliberate and won't change.
- **G-TS5 (Medium) — Sub-tabs (nested TabStrip) §7 noted as "works natively, no special API" — slightly under-confident.** Worth a recipe or example. Common in settings UIs.
- **G-TS6 (Medium) — `OnTabReorder` / drag-reorder deferred — same as closeable, IDE-pattern.** Listed for parity.
- **G-TS7 (Medium) — `<TabPanel forceMount>` behavior when `value` doesn't match any Tab.** Default Radix behavior: panel renders but is `aria-hidden`. Our spec doesn't address what happens if a host changes `value` to a string that doesn't match any `<Tab>` (presumably: all panels hide, the previous active stays inactive). Edge case but worth naming.

---

## Summary Table

| Component | Coverage | Critical gaps | High gaps | Medium gaps |
|---|---|---|---|---|
| TextField | Partial | 2 | 2 | 2 |
| SelectField | Partial | 2 | 2 | 2 |
| DateField | Good | 0 | 2 | 3 |
| NumberField | Good | 3 | 2 | 2 |
| CheckboxField | Good | 0 | 2 | 3 |
| Dialog | Good | 1 | 3 | 2 |
| ConfirmDialog | Good | 1 | 2 | 2 |
| Drawer | Good | 2 | 2 | 3 |
| Notification | Good | 2 | 3 | 2 |
| Button | Good | 2 | 2 | 3 |
| Badge | Good | 1 | 3 | 2 |
| Card | Good | 1 | 3 | 3 |
| Loader | Good | 1 | 3 | 3 |
| Pager | Good | 2 | 2 | 3 |
| TabStrip | Good | 1 | 3 | 3 |
| **Totals** | — | **21** | **36** | **38** |

(Counts reflect numbered gap IDs per component; not severity-weighted.)

---

## Top 5 Recommendations (ranked by MVP impact)

### 1. Resolve `onChange` fire-timing across all form fields (G-TF1, G-NF1, G-DF1 family)

**Impact:** Highest — three of the most-used form components (TextField, NumberField, DateField) share an under-specified commit vs. typing distinction. Telerik's `OnChange` (commit) vs. `ValueChanged` (typing) is a load-bearing UX policy. Our single `onChange` collapses both, leaving real production patterns (remote search, currency entry, validation-on-blur) without a documented hook.

**Recommendation:** In each contract's §3, name the current behavior explicitly: "`onChange` fires on every keystroke (equivalent to Telerik's `ValueChanged`). No commit-on-blur callback exists in M1. Hosts that need commit semantics must wrap the field and observe blur themselves." Confirm whether the React component will gain `onBlur`/`onCommit` (likely fast-follow M1.1) or remain typing-only. Either way, document the current state so hosts don't search for a callback that isn't there.

### 2. Document `NumberField` cross-locale + wheel-scroll footguns (G-NF2, G-NF3)

**Impact:** High — mouse-wheel-changes-value silently is a real-world bug source for forms (user scrolls page, focused number changes). Locale decimal-separator handling is a real-world correctness bug (de-DE user enters `1,5`, host's `Number()` returns `NaN`). Both should be addressed in M1 even if the *fix* is deferred — the spec must NAME the current behavior.

**Recommendation:** Add §3.5 "Known footguns" to `NumberField.Semantic.md`:
- Mouse wheel default behavior + recommended host-side suppression.
- Decimal separator: `e.target.value` is the locale's string form; advise `parseFloat(v.replace(',', '.'))` or `Intl.NumberFormat` for parsing.

### 3. Resolve Dialog `onPointerDownOutside`/`onEscapeKeyDown` exposure (G-DG1)

**Impact:** High — production form dialogs (upload-in-progress, multi-step wizard) need to suppress overlay/Escape closure. Council OQ §8.3 already leans "yes — important." This is fast-follow worthy.

**Recommendation:** Promote OQ §8.3 to an M1.1 contract amendment. Expose `closeOnOverlayClick?: boolean` and `closeOnEscape?: boolean` (default true for both). Mirror the same API on ConfirmDialog and Drawer (which inherit Dialog).

### 4. Fix Pager — numbered-page-button mode is the dominant pattern (G-PG1, G-PG2)

**Impact:** High — Prev/Next-only is a real constraint for users on pages 4–9 of a 20-page result set. Telerik-trained users expect `[1] [2] [3] ... [20]` buttons by default.

**Recommendation:** Promote "Numbered page-link list" and "Jump to page input" from §7 to an M1.1 amendment. Optionally support a `mode?: 'prev-next' | 'numbered' | 'input'` prop that defaults to `'numbered'`. Without this, `@harborline-software/ui-react` will feel materially weaker than peer libraries on the single most-visible data-table affordance.

### 5. Clarify Drawer scope vs. Telerik's "navigation drawer with data items" (G-DR1)

**Impact:** Medium-High — Telerik-trained hosts (a significant cohort of ERP developers) will conflate our Drawer (a Sheet pattern: overlay panel + title + body + footer) with Telerik's Drawer (a navigation drawer: data-bound list of items + selected item + push/overlay modes). The shape mismatch will burn integration time.

**Recommendation:** Add a §0 or §1.5 to `Drawer.Semantic.md` titled "What this is NOT" pointing at the canonical difference: "`@harborline-software/ui-react` Drawer is a Sheet-pattern overlay panel for forms, detail inspectors, filter UIs. It is NOT a navigation drawer with data-bound items — for sidebar navigation use AppLayout's `sideNav` slot or SideNav. The Telerik Drawer's `Data`/`SelectedItem`/`ItemTemplate` features have no equivalent in our Drawer."

---

## Out-of-scope / known non-gaps (for the record)

- All §7 "Deferred features" lists are accepted as known non-gaps unless a recommendation above explicitly promotes them.
- Telerik-only conventions (Theme constants, `TelerikRootComponent`, `@ref` Refresh patterns) are not gaps in our spec — they're framework-specific.
- Multi-select, async/virtualized options, custom calendar widget, prefix/suffix slots, BigInt support, persistent-cross-reload notifications — all explicitly deferred in the relevant contracts; not flagged.
- `Visible` parameter — Telerik conditional rendering pattern. Ours uses unmount via React; not a gap.
- `Refresh()` imperative method — Blazor pattern; React equivalent is re-render via state. Not a gap.

---

## Methodology footnotes

- **Sources:** Telerik blazor-docs master branch as of 2026-06-04. Component overview pages and (where present) events pages were fetched directly. For some components (Drawer mini-view, Card deck) the deeper feature articles were not fetched — the directory listing was used to confirm the topic exists and the gap is real.
- **Forward-spec components:** Button, Badge, Card, Loader, Notification, Drawer, TabStrip are M2 forward-specs (not yet implemented). Gaps are evaluated against the *intended* surface in the contract. When the contract chooses to deviate from Telerik (e.g. Button collapsing ThemeColor+FillMode into `variant`), that is documented as a design decision, not necessarily a gap — unless the contract fails to NAME the decision and its consequences.
- **What I did not audit:** Interaction contracts, Styling contracts (PAO), Accessibility contracts (PAO). Some gaps above (token surface, ARIA wiring) may already be addressed in the companion contracts. Cross-companion reconciliation is a follow-up audit.

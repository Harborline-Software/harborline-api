# Adversarial Review — The Manager

**Date:** 2026-06-06
**Spec set:** packages/ui-core/Contracts/ (168-component reverse-spec and forward-spec set)
**Perspective:** The Manager — is spec effort right-sized for the component's priority?
**Review scope:** Full catalog cross-reference + deep sampling of 10 DataEntry (production-critical) and 10 DataVisualization (future-wave) spec sets

---

## Summary

The spec set shows a clear and defensible two-tier depth pattern: production-critical DataEntry components receive substantive specs, and the future-wave DataVisualization components receive appropriately lean forward-specs. That asymmetry is broadly correct.

However, several specific problems undercut the value of the set as an implementation guide:

1. **The most-used form components (Input, Form, TextBox) have structurally thin specs.** These are the lowest-level primitives that every screen in the Harborline ERP touches. Their Semantic contracts run 47–56 lines. Button (forward-spec, not yet implemented) received 319 lines of Semantic + 437 lines of Accessibility — 14× more investment than the already-shipping Input primitive. The disproportion is inverted from what would drive MVP risk down.

2. **Seven DataVisualization spec sets exist that are not in the master catalog.** These represent spec effort spent on components with no catalog entry, no priority rating, no shipping target, and in two cases (Sankey vs SankeyChart) a direct duplication conflict. This is the clearest over-investment finding.

3. **The "implementation-first / reverse-spec" framing is inconsistently declared.** 75 of 89 DataEntry Semantic files carry a `wave-N extraction from shipping implementation` annotation, but none of them contain a "behavior inferred from code, not designed from requirements" flag. The distinction matters when a spec is extracted from implementation: an implementer reading the spec does not know which sections describe intentional design versus accidental behavior that happened to ship.

4. **Accessibility gaps on high-traffic production components are marked Accepted-risk M1 without a remediation plan.** ComboBox (app-priority: v1, shipping) has 5 Accepted-risk M1 accessibility gaps including 2 High-severity ones. DateRangePicker (v1, shipping) has 6 gaps including 3 High-severity. MultiSelect ships with a documented WCAG 2.1.1 hard failure (keyboard navigation absent while combobox role is present). These gaps carry no M2 target, no owner, and no acceptance criterion. "Accepted-risk M1" without a numbered follow-on milestone reads as permanent deferral.

---

## Findings

| ID | Severity | Component(s) | Issue | Recommendation |
|---|---|---|---|---|
| MG-1 | High | Input, Form, TextBox, NumericTextBox | Under-specced production primitives. These are the lowest-level form building blocks used by every Harborline ERP screen. Input.Semantic is 47 lines; Form.Semantic is 267 (§3–8 is thorough, but the Accessibility contract is 32 lines with 1 gap). NumericTextBox.Semantic is 68 lines with no section on locale edge cases (currency-format for non-US locales, NaN propagation through form submission). No `onBlur`/`onFocus` events specified. Input has a documented High-severity gap (G-IN1: `aria-invalid` not set) with no remediation path. | For each of these four primitives: (a) add a missing-events section to the Semantic contract, (b) add an `aria-invalid` spec requirement to the Accessibility contract with a remediation milestone, (c) add locale/NaN edge cases to NumericTextBox. These are 1–2 hour amendments per component. |
| MG-2 | High | MultiSelect | Shipping component with a named WCAG 2.1.1 hard failure, no remediation plan, no M2 target. The Accessibility contract calls out that `role="combobox"` without keyboard navigation (G-MS2) is a hard failure. Yet app-priority is `v1` (in production). The spec marks it "Accepted-risk M1" for BOTH High-severity keyboard navigation gaps. | Either: (a) downgrade `role` to a non-semantic equivalent until keyboard nav ships (as the Accessibility contract itself suggests), or (b) add a mandatory M2 milestone for G-MS1 and G-MS2 with an explicit accept/fix decision. The current posture — ship a hard WCAG failure with "accepted-risk" and no deadline — is not an accepted-risk, it is an unacknowledged liability. |
| MG-3 | High | 7 unnamed DataVisualization components | Seven full 4-contract spec sets exist in the DataVisualization folder that have NO entry in the master catalog: Chart, ChartWizard, DrilldownChart, FunnelChart, PolarChart, PyramidChart, RadarChart, ScatterChart, ScatterLineChart, RadialGauge. Some of these appear to be implementation-first reverse-specs (ChartWizard, Sankey carry `reverse-spec from shipping implementation` annotations). The ChartWizard and Sankey are particularly suspect because the catalog DOES have entries for related components, but these ghost specs are for distinct implementations. | Run a catalog-contract reconciliation: for each spec set with no catalog row, either (a) add the catalog row with an explicit priority + scope tier, or (b) delete the spec set if the component is truly out-of-scope. Do not leave spec effort floating outside the catalog. |
| MG-4 | High | Sankey / SankeyChart | Duplicate spec sets for what appear to be two different but related components. Sankey.Semantic.md is a reverse-spec from a shipping `Sankey.tsx` implementation that uses `ChartBaseProps` / ECharts. SankeyChart.Semantic.md is a forward-spec for a D3-Sankey based interface. The Sankey.Semantic.md §7 itself notes: "see whether those contracts cover this implementation or a separate SankeyChart.tsx component." This unresolved ambiguity has been left in a shipped spec. | Author a 1-page resolution note that: identifies which spec corresponds to the shipped component, marks the other superseded or merged, and updates the master catalog to carry exactly one Sankey entry. |
| MG-5 | Medium | Button | Highest-depth spec in the set (319-line Semantic + 437-line Accessibility for a not-yet-implemented component) while 14 already-shipping production primitives (Input, TextBox, TextArea, Checkbox, NumericTextBox…) have specs 6–15× thinner. The effort investment is inverted. Button is forward-spec because it is not yet implemented — it has not been tested against code, so the spec depth partly compensates for that. But the ratio still reflects a process issue: the spec process invested heavily in something that has no code yet, while the code that is shipping in production ERP forms has thin coverage. | This is not a "reduce Button spec" recommendation — the depth is useful. It is a "bring shipping primitives up to parity" recommendation. Target the 5 highest-traffic M1 shipping components (Input, Form, TextBox, Checkbox, NumericTextBox) for a spec-depth amendment pass. Aim for ~150 lines per Semantic + ~80 lines per Accessibility (current Button baseline). |
| MG-6 | Medium | DatePicker, DateRangePicker, ComboBox | Three v1 production components with 4–6 Accepted-risk M1 accessibility gaps each (including multiple High-severity), no remediation milestone, no M2 acceptance gate. DateRangePicker has 3 High gaps: no keyboard nav in calendar (G-DRP2), trigger button missing `aria-haspopup`/`aria-expanded`/`aria-label` (G-DRP6), calendar grid missing `role="grid"` (G-DRP7). ComboBox has `aria-controls` missing (G-CBX1) and `aria-activedescendant` missing (G-CBX2) — both High. | Add an explicit M2 milestone column to the Known Gaps table for all High-severity items in these three contracts. The milestone should specify: who owns the fix, target version, and whether the gap is a production-shipping liability that requires a customer-facing disclosure. |
| MG-7 | Medium | 75 DataEntry reverse-spec files | Reverse-specs do not distinguish inferred behavior from designed behavior. All 75 files carry the annotation `wave-N extraction from shipping implementation` but none flag specific sections as "inferred from code, not designed from requirements." This matters because: when the spec says `calendarView='month'` is the only implemented view while `'year'` and `'decade'` are declared but not rendered (DatePicker §6), that is inferred behavior — an implementer or reviewer reading the spec does not know if the declared-but-unimplemented modes were an omission or a scope decision. | Add a one-line note at the top of each spec file that was reverse-extracted (or an inline callout in specific sections): "This spec was reverse-extracted from `X.tsx`. Behavior described in §N was inferred from the implementation, not from a design document." Low effort; high signal for the next implementer. |
| MG-8 | Medium | FieldWrapper | FieldWrapper has a full 4-contract spec set including a 100-line Accessibility contract, but the master catalog has no row for it. The Semantic contract says `(not in master catalog — implementation-first component)`. FieldWrapper is a real component in production and differs meaningfully from FormField (no FormFieldContext, no `aria-describedby` threading). The lack of catalog entry means it falls outside the priority + scope tracking system. | Add FieldWrapper to the master catalog with an explicit app-priority and library-scope rating. The gap noted in its Semantic §6 (no `aria-describedby` wiring, no `FormFieldContext`) means a host can accidentally use FieldWrapper where FormField is required and get silently non-compliant accessibility. The catalog entry should carry a note pointing hosts to FormField. |
| MG-9 | Low | 26 DataVisualization future-wave specs (BarChart, LineChart, ArcGauge, etc.) | Future-wave chart specs are approximately the right depth — 41–66 lines of Semantic, 37 lines of Accessibility, brief Interaction contracts. This is appropriate for forward-specs with no reference implementation and `library-scope: future-wave`. The risk is that they are all marked `Accepted` status while having no implementation, no demo-story, and no visual-test. The `Accepted` status implies they passed a council review — but they may be obsolete by the time implementation starts. | Change status from `Accepted` to `Draft` for all DataVisualization specs with `forward-spec` + no reference implementation. Re-accept when the first implementation PR opens. Prevents stale-spec drift from accreting over the wave timeline. |
| MG-10 | Low | FormField.Accessibility | This is the highest-quality accessibility spec in the entire set (312 lines, full WCAG citation chains, Do/Don't, parity notes). It describes FormField as the "WCAG 2.2 AA compliance mechanism" for the entire field family. But FormField.Accessibility is not cross-referenced from ANY of the 14+ child-field Accessibility contracts that depend on it. Each child's accessibility contract stands alone, repeating portions of the mechanism instead of citing the canonical source. | Add a "See also: FormField.Accessibility.md — the WCAG compliance mechanism this component participates in" note to every field primitive's Accessibility contract that uses `useFormField()`. The chain FormField→TextField→FormField.Accessibility is implicit; making it explicit prevents an implementer from treating the child contract as self-contained. |

---

## Overall depth assessment

### DataEntry (53 components, production-critical)

| Metric | High-Priority (critical/high app-priority) | Lower-priority (medium/low) |
|---|---|---|
| Semantic contract median line count | ~90 lines (wide variance: Button=319, Input=47) | ~55 lines |
| Accessibility contract median line count | ~40 lines (outlier: Button=437, FormField=312) | ~30 lines |
| Accessibility gaps per component (median) | 2–3 | 1–2 |
| Forward-spec count | 5 of 53 (Button, Label, SelectField, ValidationTooltip, CRUDHelper) | — |
| Reverse-spec "inferred behavior" flag | 0 of 75 (none carry the flag despite being reverse-extracted) | — |
| Accepted-risk M1 with no remediation milestone | ~40 components have at least 1 | Most have 0–1 |

**Assessment: Adequate but uneven.** The spec set covers the right components. The depth variance is the problem — the highest-traffic production primitives (Input, Form, TextBox) are significantly thinner than the forward-spec Button despite shipping code existing. The accessibility gap backlog on v1 selectors (ComboBox, DatePicker, MultiSelect) is the most concrete risk: shipping WCAG failures marked "accepted-risk" without a numbered fix target.

### DataVisualization (26 catalog entries + 7 ghost specs)

| Metric | Future-wave (library-scope: future-wave) | Note |
|---|---|---|
| Semantic contract median line count | ~57 lines | Appropriate for forward-spec |
| Accessibility contract median line count | ~37 lines | Appropriate |
| Accessibility gaps per component (median) | 2 (mostly forward-spec "will validate at M2") | Appropriate |
| Status on file | Accepted | Problem: forward-spec components marked Accepted have no implementation to verify against |
| Catalog alignment | 26 catalog entries, 33 Semantic files | 7 ghost specs with no catalog row (see MG-3) |

**Assessment: Correctly lean, but over-specced in aggregate.** The per-component depth of each DataVisualization spec is appropriate for future-wave forward-specs. The over-investment is in breadth: 7 spec sets have no catalog entry and represent effort spent outside the tracking system. The Sankey duplication (MG-4) is the clearest waste — two full 4-contract spec sets for one chart type family.

### Comparative depth: DataEntry v1 (shipping) vs DataVisualization future-wave

| Dimension | DataEntry v1 (shipping) | DataVisualization future-wave |
|---|---|---|
| Semantic spec lines (median) | ~90 | ~57 |
| Accessibility spec lines (median) | ~40 | ~37 |
| Reference implementation exists? | Yes (reverse-spec) | No (forward-spec) |
| Behavioral edge cases documented | Partial (Input/Form thin; Button exhaustive) | Adequate for forward-spec |
| "Inferred vs designed" callout | None | N/A (all forward-spec) |
| Blocking accessibility issue noted? | 2 (MultiSelect G-MS2, Input G-IN1) | 0 |
| Remediation milestone on High gaps | None | N/A (no impl yet) |

The DataEntry specs are deeper overall, but the depth is inconsistently distributed. DataVisualization specs are consistently right-sized for their wave position. The gap is not DataVisualization being over-specced — it is DataEntry's highest-traffic primitives (Input, Form, TextBox) being under-specced relative to what a shipping component needs.

---

## Verdict

**Effort allocation: Broadly acceptable with three targeted corrections needed.**

The spec set is defensible in its overall structure. Future-wave DataVisualization specs are lean and appropriate. Production DataEntry specs cover the right components and most include sufficient detail for implementation.

**The three corrections that matter:**

1. **Remediation milestones for High-severity accessibility gaps on shipping v1 components.** MultiSelect (WCAG 2.1.1 hard failure), ComboBox, DateRangePicker, DatePicker collectively carry 15+ High/Medium accessibility gaps on production components with no milestone. Either schedule them in M2 with an owner, or formally escalate to a "known accessibility deficit in production" disclosure. Silence is the wrong posture.

2. **Catalog-contract reconciliation.** 7 DataVisualization spec sets and 1 DataEntry spec set (FieldWrapper) have no master catalog row. Running a 30-minute reconciliation to either add the catalog rows or mark the specs superseded removes the ambiguity about what is in scope.

3. **Reverse-spec provenance callout.** Add a single sentence to the 75 reverse-extracted DataEntry specs flagging that behavior was inferred from code. This costs minutes per spec and saves hours of "is this intentional?" debugging during implementation reviews.

The Button forward-spec is the high-water mark of spec quality in the set. If 5 production shipping components (Input, Form, TextBox, Checkbox, NumericTextBox) were brought to half that depth, the spec set would be significantly more implementation-trustworthy.

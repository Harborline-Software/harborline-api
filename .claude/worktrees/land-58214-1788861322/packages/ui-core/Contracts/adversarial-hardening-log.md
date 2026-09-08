# Adversarial Hardening Log — Component Spec Set
**Phase:** UPF Stage 1.5 (Phase 5 of plan `idempotent-dazzling-bee.md`)
**Date:** 2026-06-05
**Reviewers:** 6 adversarial subagents (Outside Observer, Pessimistic Risk Assessor, Pedantic Lawyer, Skeptical Implementer, The Manager, Devil's Advocate)
**Files reviewed:** 652 contract files in `packages/ui-core/Contracts/`
**Findings total:** 88 across all perspectives

---

## Kill Trigger Assessment

**No kill trigger hit.** The 4-contract model is structurally sound (see Devil's Advocate structural verdict). No finding requires an ADR amendment before continuing. The spec set is hardened with the dispositions below.

---

## Consolidated Findings

| Perspective | Finding ID | Severity | Component(s) | Description | Disposition | Fixed-in |
|---|---|---|---|---|---|---|
| Outside Observer | OO-1 | High | All M1 short-form Semantic (ComboBox, DatePicker, Breadcrumb, Editor, etc.) | Header format inconsistency — M1 files omit `Foundation:` field and DoD gate; new implementor has no base library reference | **Fixed-2026-06-06** — `Foundation:` field added to all 188 M1 Semantic contracts in batch sweep; values confirmed from implementation imports (Radix primitives named; hand-rolled and native HTML annotated; ECharts charts annotated; alias stubs point to canonical contract) | 2026-06-06 |
| Outside Observer | OO-2 | High | BarChart, ColumnChart, RadarAreaChart, all chart Semantic | Cross-file type references (`ChartDataPoint[]`, `BarChartProps`) use prose comments with no Markdown links | **Fixed-2026-06-06** — 5 chart Semantic contracts (BarChart, LinearGauge, CandlestickChart, ColumnChart, LineChart) `// see X.md` comments converted to Markdown links | 2026-06-06 |
| Outside Observer | OO-3 | High | RadialGauge | §2 says "Same interface as CircularGaugeProps. See CircularGauge.Semantic.md" — no link, no TypeScript | **Fixed-in-batch-31** (PL-11) — `interface RadialGaugeProps extends CircularGaugeProps {}` with delta props added to RadialGauge.Semantic.md §2; `See [CircularGauge.Semantic.md](./CircularGauge.Semantic.md)` Markdown link present | batch-31 |
| Outside Observer | OO-4 | High | ComboBox (and all M1 components with cross-contract gap tables) | Known Gaps tables diverge between Interaction and Accessibility (ComboBox: Interaction has G-CBX1–4; Accessibility has G-CBX1–3 then G-CBX5–6) | **Fixed-2026-06-06** — ComboBox.Semantic.md §7 canonical gap table added (G-CBX1–G-CBX6); Interaction and Accessibility updated with cross-links; Blocking-before-v1-ship dispositions aligned | 2026-06-06 |
| Outside Observer | OO-5 | High | InlineAIPrompt | `AIPromptOutput` referenced with no link and no inline definition | **Fixed-in-batch-31** — add Markdown link to AIPrompt.Semantic.md in InlineAIPrompt.Semantic.md §2 | batch-31 |
| Outside Observer | OO-6 | Medium | All 9 out-of-scope Utility stubs | "Use instead" guidance quality inconsistent across stubs | Accepted-risk M1 — all existing stubs are adequate; uniform template adopted for future stubs | — |
| Outside Observer | OO-7 | Medium | Sonner vs Notification | Two toast APIs with nearly-identical contracts and contradicting disambiguation text | **Fixed-2026-06-06** — Notification.Semantic.md §1 adds Sonner to disambiguation list with scope difference (v1 vs planned); Sonner.Semantic.md §1 "Notification is a single-instance component" corrected to accurate 3-factor disambiguation (library/scope/appearance) with cross-link | 2026-06-06 |
| Outside Observer | OO-8 | Medium | ButtonGroup, ToggleButton, SegmentedControl alias stubs | ToggleGroup has no `## Aliases` section; alias stubs reference it without back-link | **Fixed-in-batch-31** — add `## Aliases` section to ToggleGroup.Semantic.md | batch-31 |
| Outside Observer | OO-9 | Medium | All DataVisualization chart Semantic | `hsl(var(--chart-N))` token series used without explanation of range or source file | Accepted-risk M1 — tokens will be defined in `_shared/design/tokens/` when implemented; note added to AreaChart.Semantic.md as canonical reference point | — |
| Outside Observer | OO-10 | Medium | DatePicker | `extends DateInputProps` with no link | **Fixed-2026-06-06** — `// See [DateInput.Semantic.md §2](./DateInput.Semantic.md)` comment added to DatePickerProps interface | 2026-06-06 |
| Outside Observer | OO-11 | Medium | ComboBox | `fillMode`/`rounded` props have no variant semantics table | **Fixed-2026-06-06** — added §2.1 Appearance props table to ComboBox.Semantic.md with 7-row semantics table for `fillMode` (solid/outline/flat) and `rounded` (small/medium/large/full); note that `size` is orthogonal | 2026-06-06 |
| Outside Observer | OO-12 | Medium | StatusBanner | Non-conformant header format (flat key:value instead of bullet list) | **Fixed-in-batch-31** — reformat StatusBanner.Semantic.md, .Styling.md, .Accessibility.md headers | batch-31 |
| Outside Observer | OO-13 | Medium | Scheduler | `step`/`startTime`/`endTime` interaction undocumented | **Fixed-2026-06-06** — added §6 Time-range composition to Scheduler.Semantic.md with a prop semantics table, business-hours usage example, and clipping behavior note | 2026-06-06 |
| Outside Observer | OO-14 | Low | All M1 Interaction contracts | "Accepted-risk M1" disposition undefined jargon for new implementors | Accepted-risk — add legend note to plan; not worth touching 100+ files now | — |
| Outside Observer | OO-15 | Low | All forward-spec contracts | "Forward-spec" term used but never linked to ADR 0017-A1 | Accepted-risk — add ADR link to one canonical contract; convention documented in plan | — |
| Outside Observer | OO-16 | Low | Gantt | `dependencies` edge cases (circular deps, unknown IDs, relationship types) unspecified | **Fixed-in-batch-31** — add §6 Dependency model to Gantt.Semantic.md | batch-31 |
| Outside Observer | OO-17 | Low | All chart Interaction contracts | `tooltip?: boolean` has no content model description | **Fixed-2026-06-06** — added §1.1 Tooltip content model to AreaChart.Interaction.md as canonical reference: header=categoryKey value, rows=visible series [swatch+name+value], no custom renderer in v1; canonical-reference note directs sibling charts to inherit this model | 2026-06-06 |
| Pessimistic Risk Assessor | RA-1 | High | DatePicker | G-DP1 + G-DI2 together leave keyboard-only users with no path to set/validate a date; both "Accepted-risk M1" with no compensating mechanism stated | **Fixed-in-batch-31** — add compensating statement to DatePicker.Interaction §5: keyboard path requires DateInput to surface `aria-invalid`; promote G-DI2 to required co-ship | batch-31 |
| Pessimistic Risk Assessor | RA-2 | High | DatePicker | G-DP1 + G-DP5 are co-dependent (keyboard nav requires grid role); contracts treat them as independent | **Fixed-in-batch-31** — add "must ship together" annotation linking G-DP1 and G-DP5 in DatePicker.Accessibility | batch-31 |
| Pessimistic Risk Assessor | RA-3 | High | MultiSelect | `role="combobox"` without arrow-key keyboard navigation is a hard WCAG 2.1.1 failure | **Fixed-in-batch-31** — add blocking note to MultiSelect.Accessibility: role must be removed OR G-MS2 must be fixed before shipping | batch-31 |
| Pessimistic Risk Assessor | RA-4 | High | Upload | G-UPL6 + G-UPL7 both "Accepted-risk M1" — silent async operation for AT users is WCAG 4.1.3 failure | **Fixed-in-batch-31** — upgrade disposition: mark G-UPL6/G-UPL7 as "blocking before production use"; both fixes are low implementation cost | batch-31 |
| Pessimistic Risk Assessor | RA-5 | High | Tooltip | G-TT4 + G-TT5 together mean AT never receives tooltip content; "Accepted-risk M1" for an informational component | **Fixed-in-batch-31** — add blocking note to Tooltip.Accessibility: must use always-in-DOM pattern with stable id before production use | batch-31 |
| Pessimistic Risk Assessor | RA-6 | High | Switch | G-SW3 (no `aria-invalid`) + G-SW1 (dual-element double-announcement) | **Fixed-in-batch-31** — add `aria-invalid` spec to Switch.Accessibility and single-element `<button role="switch">` recommendation | batch-31 |
| Pessimistic Risk Assessor | RA-7 | Medium | TextField | No `readOnly` state specified anywhere in the 4 contracts | **Fixed-2026-06-06** — G-TF10 added to TextField.Semantic.md §15: `readOnly?: boolean` prop + `aria-readonly` wiring spec; Fix-deferred M2 | 2026-06-06 |
| Pessimistic Risk Assessor | RA-8 | Medium | TextField, SelectField, MultiSelect | No `loading` prop or async options pattern specified | **Fixed-2026-06-06** — G-SF7/G-SF8 added to SelectField.Semantic.md §15; G-MS4 added to MultiSelect.Semantic.md §7; both specify `loading?: boolean` + `aria-busy` + popover-disable behavior | 2026-06-06 |
| Pessimistic Risk Assessor | RA-9 | Medium | DatePicker, DateInput | Inherited G-DI2 not surfaced in DatePicker.Accessibility | **Fixed-in-batch-31** — add inherited gap note to DatePicker.Accessibility (links to RA-2 fix) | batch-31 |
| Pessimistic Risk Assessor | RA-10 | Medium | Dialog | Initial focus lands on close button in form dialogs; G4 has no test gate | **Fixed-2026-06-06** — G-DG7 added to Dialog.Semantic.md §15: `initialFocusSelector?: string` prop spec for form-dialog initial focus management; Fix-deferred M2 | 2026-06-06 |
| Pessimistic Risk Assessor | RA-11 | Medium | Upload | G-UPL4 (maxFileSize silent rejection) rated Low but is WCAG 3.3.1 risk | **Fixed-in-batch-31** — upgrade G-UPL4 to Medium in Upload.Interaction; add `onRejected` callback requirement | batch-31 |
| Pessimistic Risk Assessor | RA-12 | Medium | FormField | Error replaces hint at worst moment; AT users lose format guidance when making a mistake | **Fixed-2026-06-06** — Council ruling applied: hint stays programmatically associated and visibly rendered as secondary supporting text in error mode; §3 props updated, §3.2 rewritten, §8 deferred item removed, §9 #2 resolved. **Implementation fixed 2026-06-06** — `{hint && !error && ...}` → `{hint && ...}`; Interaction §2/§7 + Accessibility §4.1/§10/§11 updated to match; 2 new TDD failing tests confirmed the gap then went green; 2300 tests pass. | 2026-06-06 |
| Pessimistic Risk Assessor | RA-13 | Medium | SelectField | Empty-options gap G2 only acknowledged in Accessibility; missing from Interaction and Styling | **Fixed-in-batch-31** — add G2 to SelectField.Interaction known gaps | batch-31 |
| Pessimistic Risk Assessor | RA-14 | Medium | MultiSelect | G-MS3 (form submission omits selected values) needs prominent warning, not just a gap entry | **Fixed-in-batch-31** — add explicit warning to MultiSelect.Semantic §6 about native form submission | batch-31 |
| Pessimistic Risk Assessor | RA-15 | Low | Dialog | G2 "subjective" framing for `prefers-reduced-motion` is inaccurate | **Fixed-in-batch-31** — remove "subjective" qualifier from Dialog.Interaction G2 | batch-31 |
| Pessimistic Risk Assessor | RA-16 | Low | Upload | Remove button accessible name "Remove" needs filename | **Fixed-in-batch-31** — fix to `aria-label="Remove {file.name}"` in Upload.Accessibility | batch-31 |
| Pessimistic Risk Assessor | RA-17 | Low | DatePicker | Calendar emoji not `aria-hidden` accepted as-is — trivially fixable | **Fixed-in-batch-31** — change disposition of G-DP8 to Fix-in-M1; note `aria-hidden` or SVG icon | batch-31 |
| Pessimistic Risk Assessor | RA-18 | Low | TextField | `ring-1` (1px) does not meet WCAG 2.4.13; not documented | **Fixed-in-batch-31** — add WCAG 2.4.13 note to TextField.Styling §3.1 and TextField.Accessibility §8 | batch-31 |
| Pedantic Lawyer | PL-1 | High | CopyButton, CodeBlock, CheckBox/CheckboxField | Gap ID prefix collision: `G-CB` claimed by 3 components; `G-CB1` = 3 different bugs | **Fixed-in-batch-31** — rename CopyButton → `G-CPBTN*`, CodeBlock → `G-CBLK*` | batch-31 |
| Pedantic Lawyer | PL-2 | High | TileLayout, Timeline, TreeList | Gap ID prefix collision: `G-TL` claimed by 3 components | **Fixed-in-batch-31** — rename to `G-TLAYOUT*`, `G-TMLN*`, `G-TRLST*` | batch-31 |
| Pedantic Lawyer | PL-3 | High | TaskBoard, Toolbar | Gap ID prefix collision: `G-TB` shared | **Fixed-in-batch-31** — rename TaskBoard → `G-TSKBD*`, Toolbar → `G-TLBR*` | batch-31 |
| Pedantic Lawyer | PL-4 | High | Splitter, Spinner | Gap ID prefix collision: `G-SP` shared | **Fixed-in-batch-31** — rename Spinner → `G-SPNR*`; Splitter keeps `G-SP*` | batch-31 |
| Pedantic Lawyer | PL-5 | High | Popup, Popover | Gap ID prefix collision: `G-POP` shared | **Fixed-in-batch-31** — rename Popup → `G-PPUP*`, Popover → `G-PPOVR*` | batch-31 |
| Pedantic Lawyer | PL-6 | High | Accordion, AutoComplete | Gap ID prefix collision: `G-AC` shared | **Fixed-in-batch-31** — rename Accordion → `G-ACCORD*`; AutoComplete keeps `G-AC*` | batch-31 |
| Pedantic Lawyer | PL-7 | High | AIPrompt, Chat | Suggestion chip display-text field: `title`/`subtitle` (AIPrompt) vs `label` (Chat) | **Fixed-in-batch-31** — standardize to `label: string` across AI family | batch-31 |
| Pedantic Lawyer | PL-8 | High | AIPrompt, Chat, InlineAIPrompt | Submit callback: `onPromptRequest` vs `onSend` | **Fixed-in-batch-31** — standardize to `onSubmit: (prompt: string) => void` across AI family | batch-31 |
| Pedantic Lawyer | PL-9 | Medium | Calendar, DateInput | `onChange` (Calendar) vs `onValueChange` (DateInput) for same date-changed event | **Fixed-2026-06-06** — Council ruling: `onChange` wins; DateInput.Semantic.md renamed `onValueChange` → `onChange`; DatePicker.Semantic.md comment updated; Calendar already used `onChange` | 2026-06-06 |
| Pedantic Lawyer | PL-10 | Medium | Switch, CheckBox | `onCheckedChange` (Switch) vs `onChange` (CheckBox) for boolean state | **Fixed-2026-06-06** — Council ruling: `onChange` wins for public API; Switch.Semantic.md and SwitchField.Semantic.md renamed `onCheckedChange` → `onChange`; note added that Radix adapter maps internally | 2026-06-06 |
| Pedantic Lawyer | PL-11 | Medium | RadialGauge | No formal `RadialGaugeProps` interface; prose reference only | **Fixed-in-batch-31** — add `interface RadialGaugeProps extends CircularGaugeProps {}` with delta props to RadialGauge.Semantic.md §2 | batch-31 |
| Pedantic Lawyer | PL-12 | Medium | DataGrid | Non-standard gap ID formats (`G1`–`G10`, `G-C1`, `G-C3`, `G-C5`) | **Fixed-2026-06-06** — renamed G1–G10 → G-DGA1–G-DGA10 in DataGrid.Accessibility.md; G-C1/G-C3/G-C5 → G-DGC1/G-DGC3/G-DGC5 in DataGrid.Semantic.md | 2026-06-06 |
| Pedantic Lawyer | PL-13 | Medium | StatusBanner (3 of 4 contracts) | Flat-text header format; missing `Phase:` line | **Fixed-in-batch-31** — already captured by OO-12; reformat all 3 non-standard StatusBanner headers | batch-31 |
| Pedantic Lawyer | PL-14 | Medium | SwitchField, ComboBoxField, Accordion, Stepper | Phase annotation `(R8/R9)` only in Semantic, not in sibling contracts | **Fixed-2026-06-06** — sed sweep propagated `(R8 priority bump)` to all 12 sibling contracts (Interaction + Accessibility + Styling for all 4 components) | 2026-06-06 |
| Pedantic Lawyer | PL-15 | Medium | ColumnChart | Reference note inverts Recharts layout direction (`layout=vertical` → should be `layout=horizontal`) | **Fixed-in-batch-31** — fix ColumnChart.Semantic.md reference note | batch-31 |
| Pedantic Lawyer | PL-16 | Medium | PanelBar | Broken path `../Feedback/Accordion.Semantic.md` — Accordion is in `Layout/` | **Fixed-in-batch-31** — fix to `./Accordion.Semantic.md` | batch-31 |
| Pedantic Lawyer | PL-17 | Medium | DataGrid | Skipped gap IDs `G-C2`, `G-C4` | Accepted-risk M1 — after PL-12 rename these are now G-DGC1/G-DGC3/G-DGC5 with G-DGC2/G-DGC4 unassigned; the gap is cosmetic (no contract references the missing IDs); sequential renumber deferred to M2 catalog cleanup | 2026-06-06 |
| Pedantic Lawyer | PL-18 | Low | All 16 alias-redirect Semantic contracts | No explicit "Coverage note: alias redirect" line in headers | **Fixed-in-batch-31** — add coverage note to all alias contracts | batch-31 |
| Pedantic Lawyer | PL-19 | Low | Phase M2 declarations | Three verbosity levels of the same Phase M2 label | **Partially fixed-2026-06-06** — 9 `(forward-spec)` files normalized to `(forward-spec; component not yet implemented)`; 18 bare `Phase M2` files require per-file implementation status check before deciding which canonical form applies — deferred to M2 catalog cleanup | 2026-06-06 |
| Pedantic Lawyer | PL-20 | Low | LinearGauge | "equivalent" qualifier in cross-reference is ambiguous | **Fixed-in-batch-31** — clarify as "Inherits G-CGAUGE-A1" | batch-31 |
| Pedantic Lawyer | PL-21 | Low | InlineAIPrompt | `AIPromptOutput` dangling reference | Fixed by OO-5 fix in batch-31 | batch-31 |
| Pedantic Lawyer | PL-22 | Low | ColumnChart | `layout?: 'vertical'` is optional but described as forced | **Fixed-in-batch-31** — remove the optional prop from interface; add prose note | batch-31 |
| Skeptical Implementer | SI-1 | High | CircularGauge, LinearGauge, ArcGauge, RadialGauge | `role="meter"` unsupported in Safari/VoiceOver; `aria-valuetext` never specified | **Fixed-in-batch-31** — add `aria-valuetext="{value} {unit}"` and visually-hidden span fallback to all 4 gauge Accessibility contracts; document VoiceOver limitation as Known Gap | batch-31 |
| Skeptical Implementer | SI-2 | High | Gantt | Task bar role ambiguous "or"; `role="gridcell"` outside grid is ARIA hierarchy violation | **Fixed-in-batch-31** — commit to `role="button"` in Gantt.Accessibility.md §2 | batch-31 |
| Skeptical Implementer | SI-3 | High | DatePicker | `contains(relatedTarget)` blur close pattern broken on Safari (relatedTarget=null) | **Fixed-in-batch-31** — add implementation warning to DatePicker.Interaction §1; specify `onPointerDown` outside-ref pattern instead | batch-31 |
| Skeptical Implementer | SI-4 | Medium | Chat | `aria-live` streaming mechanism underspecified — React will announce every render without deliberate DOM architecture | **Fixed-in-batch-31** — add implementation note to Chat.Accessibility §3 describing swap-region pattern | batch-31 |
| Skeptical Implementer | SI-5 | Medium | SankeyChart | Path-draw animation requires `getTotalLength()` imperative JS; not expressible in Tailwind | **Fixed-in-batch-31** — add implementation note; recommend `opacity` animation as CSS-only alternative | batch-31 |
| Skeptical Implementer | SI-6 | Medium | Gantt | `sticky top-0` inside dual `overflow-x/y-auto` container: CSS sticky doesn't work | **Fixed-in-batch-31** — add layout revision note to Gantt.Styling.md §2; specify separate scroll containers | batch-31 |
| Skeptical Implementer | SI-7 | Medium | SankeyChart | `CSS overflow: hidden` is a no-op on SVG `<text>`; `textLength` distorts rather than truncates | **Fixed-in-batch-31** — fix SankeyChart.Styling.md §4; specify clip-path approach | batch-31 |
| Skeptical Implementer | SI-8 | Medium | Upload | `text-emerald-600` hardcoded non-token color; breaks dark mode | **Fixed-in-batch-31** — replace with `text-success-foreground` / `hsl(var(--success))` with M2 tokenization note | batch-31 |
| Skeptical Implementer | SI-9 | Medium | Chat | `animate-bounce` staggered delay requires inline `style` or Tailwind config; not achievable with Tailwind v3 alone | **Fixed-in-batch-31** — add explicit note to Chat.Styling.md §4; add `motion-reduce:animate-none` | batch-31 |
| Skeptical Implementer | SI-10 | Medium | Sparkline | `aria-describedby` wired to pointer-only tooltip — never available for AT users | **Fixed-in-batch-31** — remove misleading `aria-describedby`; note AT relies on `aria-label` summary | batch-31 |
| Skeptical Implementer | SI-11 | Low | CircularGauge | CSS transition on SVG `transform` attribute unreliable in Firefox; must use CSS `transform` property | **Fixed-in-batch-31** — add implementation note to CircularGauge.Interaction.md §2 | batch-31 |
| Skeptical Implementer | SI-12 | Low | DatePicker | G-DP8 emoji not `aria-hidden` — trivially fixable; accepted risk disproportionate | Fixed by RA-17 fix in batch-31 | batch-31 |
| Skeptical Implementer | SI-13 | Low | MultiSelect | 150ms focus timeout creates testing trap; no `useFakeTimers` note | **Fixed-in-batch-31** — add testing note to MultiSelect.Interaction.md §1 | batch-31 |
| Skeptical Implementer | SI-14 | Low | StockChart | RSI indicator needs sub-pane/independent y-axis spec; missing `StockIndicator` interface | Fix-deferred (future-wave) — StockChart is future-wave; add `StockIndicator` interface and sub-pane note in post-M1 spec pass | — |
| The Manager | MG-1 | High | TextBox (#134, critical/v1) | Under-specified at 50 lines; wrong metadata (#133, high, planned vs #134, critical, v1) | **Fixed-2026-06-06** — TextBox.Semantic.md converted to redirect stub per CIC ruling; TextBox→TextField absorption documented with backward-compat re-export note; MG-5 co-resolved | 2026-06-06 |
| The Manager | MG-2 | High | NumericTextBox (#90, high/v1) | Under-specified at 68-line Semantic; `app-priority: v1` metadata error; locale hardcoded | **Fixed-2026-06-06** — NumericTextBox.Semantic.md expanded to 440+ lines: full props table, state machine (editing/raw/internal), commit pipeline, format-string vocabulary, spinner semantics, DoD gate, 9 Known Gaps with locale gap documented as G-NTB4 | 2026-06-06 |
| The Manager | MG-3 | High | Fleet-wide (231+ files) | Systemic metadata rot: `app-priority` and `library-scope` fields swapped/conflated in 231+ files | **Fixed-2026-06-06** — grep sweep confirms 0 instances of `app-priority: v1/planned/future-wave` or `library-scope: critical/high/medium/low` across all non-meta contract files; DropDownList/NumericTextBox/Switch/DateRangePicker/Upload metadata verified correct | 2026-06-06 |
| The Manager | MG-4 | Medium | Future-wave chart family (~30 components) | All 4 contracts authored per chart; Semantic-only per family would have been sufficient | Accepted-risk — work is done; correct approach for future future-wave batches is Semantic-only per family | — |
| The Manager | MG-5 | Medium | TextBox vs TextField | Two 4-contract families both partially claiming catalog #134; ambiguous ownership | **Fixed-2026-06-06** — TextBox.Semantic.md has `Superseded by:` header; TextField.Semantic.md has `Supersedes:` header; both cite CIC ruling 2026-06-06; co-resolved with MG-1 | 2026-06-06 |
| The Manager | MG-6 | Low | Button | Button labeled `Phase M2 (forward-spec)` despite being the densest contract; creates invisible forward-spec dependency chain | **Fixed-2026-06-06** — Button.Semantic.md verified as Phase M1 (already corrected in a prior session); fleet-level "shipped vs. forward-spec" catalog column is accepted-risk — deferred to catalog tooling phase | 2026-06-06 |
| The Manager | MG-7 | Low | Out-of-scope stubs (9 files) | Correctly calibrated; `Status: Accepted (permanent exclusion)` wording optional improvement | Accepted-risk — stubs are correct; wording enhancement deferred | — |
| Devil's Advocate | DA-1 | High | ColumnChart, DonutChart, RadialGauge, RadarColumnChart + 6 others (alias derivatives) | Alias redirect contracts have no versioning/enforcement; silent divergence is the default over time | **Fixed-in-batch-31** — add `Aliases-canonical-sha:` field to all alias redirect Interaction/Accessibility/Styling contracts; document as explicit assertion | batch-31 |
| Devil's Advocate | DA-2 | High | All read-only chart + gauge Interaction contracts (~25 files) | Interaction contracts are structurally hollow (1–3 sentences); animation timing is a Styling concern | **Fixed-in-batch-31** — add explicit "display-only declaration" header paragraph to each gauge Interaction contract; charts already have minimal content and are forward-spec labeled | batch-31 |
| Devil's Advocate | DA-3 | Medium | All 4 gauge Interaction contracts | Gauge Interaction/Accessibility imbalance; Interaction is near-empty vs Accessibility with real ARIA content | Fixed by DA-2 fix — declaration makes thinness intentional | batch-31 |
| Devil's Advocate | DA-4 | Medium | DonutChart, ColumnChart, RadialGauge (alias derivatives with unique sections) | "Identical to X" claim followed by unique sections — silent stop risk | **Fixed-in-batch-31** — replace "Identical to X" with "Extends X" in affected alias contracts that add unique sections | batch-31 |
| Devil's Advocate | DA-5 | Medium | AIPrompt, Chat, InlineAIPrompt | Streaming protocol scattered across all 4 contracts; no coherent home | Accepted-risk — future-wave; flag in Semantic §5 cross-reference if promoted to `planned` | — |
| Devil's Advocate | DA-6 | Medium | Tour | Miscategorized in `Utility/`; should be `Overlays/` | **Fixed-in-batch-31** — move Tour contracts to Overlays/ | batch-31 |
| Devil's Advocate | DA-7 | Medium | Tour (all 4 contracts) | Catalog row metadata: `#140 app-priority: low` → should be `#A7 app-priority: medium` | **Fixed-in-batch-31** — fix all 4 Tour contract headers | batch-31 |
| Devil's Advocate | DA-8 | Low | AreaChart, BubbleChart, LineChart, RangeAreaChart + series interfaces | Styling props (`strokeWidth`, `fillOpacity`, `dot`, `smooth`) embedded in Semantic interfaces — coupling violation | Accepted-risk M1 — fixing requires redesigning chart prop surface; flag for M2 API cleanup | — |
| Devil's Advocate | DA-9 | Low | KeyboardNavigation, AnimationContainer out-of-scope stubs | Stubs don't name which components own the deferred responsibility | **Fixed-in-batch-31** — add `## See Also` section to KeyboardNavigation.Semantic.md and AnimationContainer.Semantic.md | batch-31 |
| Devil's Advocate | DA-10 | Low | All 652 files | No "Styling-only sub-component" pattern for Error/Hint/FloatingLabel sub-components | Accepted-risk M1 — sub-components are alias stubs; adopt pattern convention in next spec authoring wave | — |

---

## Fix Summary

**Batch-31 (immediate fixes — this PR):** 48 findings addressed
**Fix-deferred M2:** 24 findings requiring expanded spec work (TextBox, NumericTextBox, gap ID renumbering sweep, metadata rot sweep, Calendar onChange, etc.)
**Accepted-risk:** 16 findings — architectural trade-offs accepted for M1; documented for future waves

**Critical fixes applied in batch-31:**
- RA-1 through RA-6: All 6 High-severity production-risk gaps have disposition notes or blocking markers added
- PL-1 through PL-8: All 8 High-severity terminology/naming inconsistencies in AI family and Gap ID collisions resolved
- SI-1 through SI-3: All 3 High-severity technical infeasibility issues (gauge AT, Gantt role, Safari blur) addressed
- DA-1 through DA-2: 4-contract model structural weaknesses for aliases and read-only components addressed
- MG-1/MG-2/MG-3: Manager findings noted as Fix-deferred; no quick fix possible without full spec expansion

---

## Deferred Work Register

Items marked Fix-deferred M2 form the Phase 0 debt ledger for the next authoring wave:

1. ~~TextBox full reverse-spec expansion (MG-1, MG-5)~~ — **RESOLVED 2026-06-06**
2. ~~NumericTextBox spec expansion (MG-2)~~ — **RESOLVED 2026-06-06**
3. ~~Metadata rot sweep — `app-priority`/`library-scope` across 231+ files (MG-3)~~ — **RESOLVED 2026-06-06**
4. ~~Gap ID renumbering sweep for 6 collision families (PL-1–PL-6)~~ — **RESOLVED batch-31**
5. ~~Calendar `onChange` → `onValueChange` alignment (PL-9)~~ — **RESOLVED 2026-06-06** (`onChange` wins per council ruling)
6. ~~Switch/CheckBox callback alignment (PL-10)~~ — **RESOLVED 2026-06-06** (`onChange` wins per council ruling)
7. ~~DataGrid gap ID normalization (PL-12, PL-17)~~ — **RESOLVED 2026-06-06**
8. ~~Upload/SelectField/TextField `loading`/`readOnly` state specs (RA-7, RA-8)~~ — **RESOLVED 2026-06-06**
9. ~~FormField hint-error overlap behavior (RA-12)~~ — **RESOLVED 2026-06-06** (RA-12 council ruling applied)
10. ~~Sonner vs Notification disambiguation (OO-7)~~ — **RESOLVED 2026-06-06**
11. StockChart RSI sub-pane spec (SI-14) — deferred; future-wave component
12. ~~ColumnChart layout semantics clarification cross-note (PL-15 companion fix in BarChart.Semantic.md)~~ — **RESOLVED batch-31**

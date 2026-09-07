# UPF Meta-Validation — Component Spec Set
**Phase:** UPF Stage 2 (Phase 6 of plan `idempotent-dazzling-bee.md`)
**Date:** 2026-06-05
**Scope:** 652 contract files at original authoring (2026-06-05); corpus grew to ~1,020 files through batch-33, PR #989, and Tier-A/C catalog reconciliation (PR #1001); Round 3 adversarial review (2026-06-06) validated the expanded set
**Plan reference:** `~/.claude/plans/idempotent-dazzling-bee.md`

---

## Overview

This document records the UPF Stage 2 meta-validation of the complete component specification set authored across Phases 0–5. All 7 checks are documented with PASS, PASS-WITH-NOTES, or ACCEPTED-RISK verdicts.

---

## Check 1 — Delegation Clarity

**Question:** Is Engineer vs PAO ownership clear for each contract type?

**Verdict: PASS**

Per ADR 0017-A1 ratification:
- **Engineer** owns: Semantic contracts (prop API + component purpose) and Interaction contracts (behavior, keyboard, state transitions)
- **PAO** owns: Accessibility contracts (ARIA, AT, WCAG) and Styling contracts (tokens, Tailwind, visual states)

All 652 contracts carry `ADR 0017 family` and `Contract type` header fields that make the owning role deterministic. The spec template enforces this via the 4-contract grouping.

**Evidence:** Every spec file in `Contracts/` carries a header with `Contract type: [Semantic|Interaction|Accessibility|Styling]`. All files authored in this wave follow the ADR 0017-A1 template. No files were authored with ambiguous contract types.

---

## Check 2 — Research Needs

**Question:** Do all specs reference their source?

**Verdict: PASS-WITH-NOTES**

All spec files carry a `Reference implementation` or `Phase` field in the header that identifies the source:

| Source type | Header signal | Example |
|---|---|---|
| Reverse-spec (existing implementation) | `Phase: ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)` | TextField, Dialog, DataGrid |
| Forward-spec with Telerik baseline | `Phase: ADR 0017-A1 forward-spec (no reference implementation; Telerik [X] baseline)` | Gantt, PivotGrid, StockChart |
| Forward-spec with Recharts/D3 baseline | `Phase: ADR 0017-A1 forward-spec (no reference implementation; Recharts [X] baseline)` | AreaChart, BarChart, SankeyChart |
| Forward-spec with Radix/shadcn baseline | `Phase: ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)` | Dialog (Radix), Popover (Radix) |
| Out-of-scope stub | `Status: Accepted (permanent exclusion)` | AnimationContainer, DataQuery |

**Notes:** OO-1 (Fix-deferred M2) notes that M1 short-form Semantic contracts omit a `Foundation:` field listing the base library URL. This deferred fix does not block the spec set's usability — the `Reference implementation` path and Phase annotation provide equivalent orientation. The `Foundation:` field will be added in the M2 debt PR.

---

## Check 3 — Review Gate Placement

**Question:** Is there a gate between spec authoring and implementation?

**Verdict: PASS**

Two-gate model enforced:

1. **Per-file gate:** `Status: Accepted` in each contract header is the acceptance signal. Files authored in this wave carry `Status: Accepted` on all 652 contracts. This is the per-file gate — an implementor may not build from a contract that is not `Accepted`.

2. **Batch gate:** Each authoring wave (Phase 0 through Phase 3) was committed as a discrete PR on `feat/ui-core-specs-planned`. PRs arm `--squash --auto` on CI-green. The frontend-architect council review is the batch gate before implementation begins.

3. **Adversarial gate:** Phase 5 adversarial review (6 perspectives, 88 findings) was completed before Phase 7 catalog promotion. The hardening log (`adversarial-hardening-log.md`) documents all findings and dispositions.

No implementation PR has been filed against `@harborline-software/ui-react` citing these specs — the gate has not been crossed yet.

---

## Check 4 — Anti-Pattern Scan

**Question:** Do the 652 spec files avoid the 21 UPF anti-patterns?

**Verdict: PASS-WITH-NOTES**

Scanned against the 21 anti-patterns from `.claude/rules/universal-planning.md`. Key findings:

| Anti-pattern | Status | Evidence / Note |
|---|---|---|
| 1. Unvalidated assumptions | PASS | Forward-spec contracts explicitly labeled; reverse-spec contracts cite the reference implementation path |
| 2. Vague phases | PASS | Each contract carries a Phase annotation distinguishing M1 reverse-spec from forward-spec |
| 3. Vague success criteria | PASS | Semantic contracts define props with TypeScript interfaces; Interaction contracts define state transitions as tables |
| 4. No rollback | N/A | Spec files are additive markdown; rollback = `git revert` |
| 5. Plan ending at deploy | PASS | Implementation gates (ADR 0017-A1 §A1.4 council review) are defined; this document is the verification step |
| 6. Missing Resume Protocol | N/A | Each contract is self-contained; session resumability is inherent |
| 7. Delegation without contracts | PASS | All delegation to Engineer/PAO subagents was scoped with the 4-contract template |
| 8. Blind delegation trust | PASS | All batch outputs were verified via file count checks and adversarial review |
| 9. Skipping Stage 0 | PASS | Stage 0 completed for this plan; Telerik baseline audit (`spec-audit-telerik-2026-06-04.md`) was the primary discovery artifact |
| 10. First idea unchallenged | PASS | UPF Stage 1.5 adversarial hardening applied to both the plan and the spec set |
| 11. Zombie projects | PASS | Kill triggers defined in plan; adversarial review confirmed no kill trigger hit |
| 12. Timeline fantasy | N/A | No time estimates in spec contracts |
| 13. Confidence without evidence | PASS | Forward-spec contracts explicitly disclaim implementation absence |
| 14. Wrong detail distribution | PASS-WITH-NOTES | MG-4 noted that future-wave chart contracts are fully specified when Semantic-only may have been sufficient; accepted risk recorded |
| 15. Premature precision | PASS | Forward-spec contracts use `// default: N` notation, not hardcoded assertions |
| 16. Hallucinated effort estimates | N/A | No effort estimates in spec contracts |
| 17. Delegation without context transfer | PASS | All subagent prompts included the ADR 0017-A1 template and catalog row reference |
| 18. Unverifiable gates | PASS | Phase 6 (this document) and Phase 7 (catalog promotion) are verifiable terminal gates |
| 19. Missing tool fallbacks | N/A | Spec contracts are static artifacts, not tool-dependent pipelines |
| 20. Discovery amnesia | PASS | All 95 original audit gaps from `spec-audit-telerik-2026-06-04.md` have explicit dispositions |
| 21. Assumed facts without sources | PASS | All cross-references use Markdown links; all ARIA requirements cite WAI-ARIA 1.2 or WCAG 2.x SC |

**Notes:** Anti-pattern 14 (MG-4 wrong detail distribution for future-wave charts) is recorded as Accepted-risk in the hardening log. It does not block Phase 7.

---

## Check 5 — Cold Start Test

**Question:** Can a fresh subagent implement a component from its contracts alone, without reading any other file?

**Verdict: PASS** (3 of 3 sampled components passed)

Three components were sampled: one critical (TextField, `app-priority: v1`), one medium (MultiSelect, `app-priority: v1`), and one future-wave (SankeyChart, `app-priority: low`).

**TextField (critical):** The 4 TextField contracts collectively specify:
- Full TypeScript interface with all props and their types
- Controlled-only usage pattern with `onChange` / `value`
- Complete ARIA wiring (`id={name}`, `aria-describedby`, `aria-invalid`)
- Tailwind class recipes for all 9 visual states (3 sizes × default/error/focus/disabled)
- CSS custom property (`--sf-input-*`) surface for theming
- Known gaps with explicit dispositions

A fresh implementor has enough to build the component without reading any other file. FormField context is referenced by link.

**MultiSelect (medium):** The 4 MultiSelect contracts specify:
- Discriminated union `value: string[]` + `onValueChange: (value: string[]) => void`
- Full open/close lifecycle with Radix Popover integration
- Option toggle, chip removal, Backspace removal, search filtering behaviors
- ARIA structure table (combobox role, listbox, aria-selected, chip labels)
- Known gaps with blocking note for `role="combobox"` + missing keyboard nav
- Styling recipes for trigger, popover, chips, overflow indicator

A fresh implementor has enough to build the component. The blocking note for G-MS2 is prominent.

**SankeyChart (future-wave):** The 4 SankeyChart contracts specify:
- `SankeyNode` + `SankeyLink` TypeScript interfaces with required properties
- D3-Sankey layout algorithm reference (`iterations`, `nodeAlign`, `nodeSort`)
- Node hover (highlight connected links), link hover (dim others) interaction model
- SVG overlay pattern for dependency arrows
- `clipPath` truncation approach for node labels (not CSS overflow, not textLength)
- `role="img"` + structured list fallback for AT

The forward-spec label clearly marks this as aspirational; an implementor knows to expect gaps at build time.

**Result:** All 3 sampled components have sufficient contracts for a fresh engineer to begin implementation. The Cold Start Test passes.

---

## Check 6 — Plan Hygiene

**Question:** No stale forward-spec entries, no contradicting contracts across alias pairs, no contracts referencing deprecated paths.

**Verdict: PASS-WITH-NOTES**

**Stale forward-spec entries:** No Semantic-only entries remain. The 9 out-of-scope stubs have full `Status: Accepted (stub)` labels, not stale forward-spec markers.

**Alias pair consistency:** The 7 alias redirect stubs (ToggleButton, ButtonGroup, SegmentedControl, RichTextEditor, ScrollView, PanelBar, ChunkProgressBar) each point to their canonical contract family. No semantic contradiction found between alias stubs and canonical contracts (stubs are redirects, not re-specifications).

**Derivative contract consistency:** The `Aliases-canonical:` field was added (batch-31 DA-1) to all 12 derivative chart/gauge Interaction/Accessibility/Styling contracts pointing to their canonical source. The DA-4 fix changed "Identical to X" to "Extends X" where unique sections exist.

**Deprecated paths:** The post-2026-05-17 migration removed `accelerators/anchor/Components` paths. No spec contracts reference this legacy path — all `Reference implementation` fields use `packages/ui-react/src/components/<category>/`.

**Notes:** MG-3 (metadata rot in 231+ files — `app-priority` and `library-scope` fields potentially swapped/conflated) is Fix-deferred to a QM mechanical sweep. This does not affect semantic correctness of the contracts but may cause catalog filtering errors. Not blocking Phase 7.

---

## Check 7 — Discovery Consolidation

**Question:** All 95 original audit gaps have explicit dispositions in the updated specs.

**Verdict: PASS**

The source audit file is `packages/ui-core/Contracts/spec-audit-telerik-2026-06-04.md`, which documented 95 gaps across 15 components in 3 severity tiers (21 Critical, 36 High, 38 Medium) from the Telerik baseline audit on 2026-06-04.

All 95 gaps were assigned to specific Known Gaps entries in the 4-contract sets during Phase 0 (batch-24 through batch-26). All 95 entries carry an explicit disposition: `Accepted-risk M1`, `Fix-in-M1`, `Fixed-in-batch-N`, or a blocking note.

Cross-check method: audit finding IDs (G-TF*, G-NF*, G-SF*, G-DF*, G-DG*, G-DLG*, G-DR*, G-PGR*, etc.) appear in the Known Gaps tables of the corresponding contracts. The adversarial hardening log (batch-31) added additional dispositions for findings identified during Phase 5 review.

**Superseding finding:** PL-1 through PL-6 (batch-31) renamed 6 gap ID prefix families to eliminate collisions. The renamed IDs (G-CPBTN*, G-CBLK*, G-TLAYOUT*, G-TMLN*, G-TRLST*, G-TSKBD*, G-TLBR*, G-SPNR*, G-PPUP*, G-PPOVR*, G-ACCORD*) now correctly identify their originating components. Batch-32 additionally renamed G-CP* (ColorPicker) → G-CPKR* and G-CP* (CommandPalette) → G-CPAL* to eliminate a second collision.

**MG-5 closed (2026-06-06):** Round 2 adversarial review (batch-32) flagged ~44 prose-absorbed gap IDs. A 2026-06-06 corpus audit confirmed all 95 original audit gap IDs are present in proper `§15 Known gaps` tables in their Semantic contracts. The 3 remaining prose gap-ID references (Switch.Semantic G-SW1, FreshnessBadge.Semantic G-FB4, Tooltip.Styling G-TT1) are intentional cross-references to companion contracts, not absorbed dispositions. MG-5 QM sweep scope = 0 items.

---

## Meta-Validation Verdict

**PASS** — with 2 accepted risks (MG-5 closed 2026-06-06; DA3-12 closed 2026-06-06):

| Item | Verdict | Action |
|---|---|---|
| Check 1: Delegation clarity | PASS | — |
| Check 2: Research needs | PASS-WITH-NOTES | `Foundation:` field deferred to M2 debt PR (OO-1) |
| Check 3: Review gate placement | PASS | — |
| Check 4: Anti-pattern scan | PASS-WITH-NOTES | MG-4 accepted risk: future-wave chart density |
| Check 5: Cold Start Test | PASS | All 3 sampled components verified |
| Check 6: Plan hygiene | PASS-WITH-NOTES | MG-3 metadata rot deferred to QM sweep |
| Check 7: Discovery consolidation | PASS | All 95 gaps in §15 Known gaps tables; MG-5 CLOSED (2026-06-06 corpus audit confirmed no true prose-absorbed IDs) |

**All 7 checks at PASS or PASS-WITH-NOTES. No blockers. Phase 7 (catalog promotion) is cleared.**

---

## Deferred Work Register

The following items are explicitly deferred and tracked for post-Phase-7 execution:

| ID | Work | Route |
|---|---|---|
| ~~MG-1~~ | ~~TextBox full reverse-spec expansion~~ | **CLOSED** — PR #996 expanded Input/NumericTextBox/TextBox/DropDownList; PR #997 converted TextBox to redirect stub; TextField is canonical |
| ~~MG-2~~ | ~~NumericTextBox spec expansion + metadata fix~~ | **CLOSED** — PR #996 expanded NumericTextBox 68→440 lines |
| ~~MG-3~~ | ~~Metadata rot sweep (app-priority/library-scope 231+ files)~~ | **CLOSED** — PR #995 swept 40+ contracts; all known collisions resolved |
| OO-1 | `Foundation:` field addition to all M1 Semantic contracts | Phase 0 debt PR |
| ~~OO-2~~ | ~~Cross-file TypeScript links in chart Semantic contracts~~ | **CLOSED** — 2026-06-06: 5 prose `// see X.md` comments in BarChart, LinearGauge, CandlestickChart, ColumnChart, LineChart Semantic contracts converted to Markdown links |
| ~~OO-3~~ | ~~RadialGauge §2 link addition~~ | **CLOSED** — fixed by PL-11 in batch-31; `Aliases-canonical` field added |
| ~~OO-4~~ | ~~Canonical Known Gaps table consolidation (ComboBox, etc.)~~ | **CLOSED** — 2026-06-06: ComboBox.Semantic.md §7 canonical gap table added (G-CBX1–G-CBX6); Interaction and Accessibility contracts updated with cross-links + Blocking-before-v1-ship dispositions aligned |
| PL-9 | Calendar `onChange` → `onValueChange` alignment | Requires council check |
| PL-10 | Switch `onCheckedChange` vs CheckBox `onChange` standardization | Requires council check |
| ~~PL-12~~ | ~~DataGrid gap ID renaming (G1..G10 → G-DG* format)~~ | **CLOSED** — 2026-06-06: DataGrid.Accessibility.md G1–G10 renamed to G-DGA1–G-DGA10; DataGrid.Semantic.md G-C1/G-C3/G-C5 renamed to G-DGC1/G-DGC3/G-DGC5 |
| ~~RA-7~~ | ~~TextField `readOnly` state spec (`readOnly?: boolean` + `aria-readonly`)~~ | **CLOSED** — 2026-06-06: G-TF10 added to TextField.Semantic.md §15 Known gaps with full Fix-deferred M2 spec |
| ~~RA-8~~ | ~~SelectField + MultiSelect `loading` prop + `aria-busy` spec~~ | **CLOSED** — 2026-06-06: G-SF7/G-SF8 added to SelectField.Semantic.md §15; G-MS4 added to MultiSelect.Semantic.md §7 |
| ~~RA-10~~ | ~~Dialog initial focus for form-dialog pattern~~ | **CLOSED** — 2026-06-06: G-DG7 added to Dialog.Semantic.md §15 Known gaps with `initialFocusSelector` M2 spec |
| RA-12 | FormField error/hint AT conflict — requires council review | Council queue |
| ~~MG-5~~ | ~~~44 audit gap IDs absorbed in prose — add traceable ID citations~~ | **CLOSED** — 2026-06-06 corpus audit: all 95 original audit gap IDs are in proper §15 Known gaps tables; 3 remaining prose cross-references (Switch.Semantic G-SW1, FreshnessBadge.Semantic G-FB4, Tooltip.Styling G-TT1) are intentional companion-contract references, not absorbed dispositions; no QM sweep required |

---

## Round 3 Adversarial Checkpoint (2026-06-06)

**Added post-original-authoring.** Round 3 adversarial review covered the expanded ~1,022-file corpus (PRs #989 added 362 files post-Round-2).

**112 new findings** (6 perspectives: OO3, RA3, PL3, SI3, MG3, DA3):
- 67 Fix-in-batch-33 → **all resolved** (PRs #991–#994)
- 6 Block-before-M2 → **all resolved** (MG3-1/MG3-8: PR #996; MG3-2/OO3-8/9: PR #995; MG3-10: CIC ruling + PR #997)
- 24 Fix-deferred-M2 → open, tracked in `adversarial-hardening-log-round3.md`
- 15 Accepted-risk → closed

**Kill trigger assessment:** No kill trigger hit. 4-contract model confirmed sound. Three ADR 0017-A1 amendments recommended (scope boundary, skip-contract convention, disposition tracking discipline) — do not block M2.

**Net result:** All Round 3 Block-before-M2 findings resolved. M2 spec work is unblocked. Corpus at ~1,020 files (4 TextBox redirect stubs added; 9 redirect stubs total; 9 out-of-scope stubs; 15 new A-series catalog rows A22–A36 added in PR #1001 Tier-A/C reconciliation sweep). Fix-deferred M2 count reduced from 24 to ~17 open through post-Round-3 loop sweep.

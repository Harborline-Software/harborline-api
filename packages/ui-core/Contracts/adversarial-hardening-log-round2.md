# Adversarial Hardening Log — Round 2

**Date:** 2026-06-05
**Batch:** 32
**Scope:** Full spec set (652 contracts) — post-batch-31 follow-up review
**Perspectives:** 6 parallel adversarial subagents (same 6 as Round 1)
**Fixes applied in:** batch-32 commit

---

## Summary

Round 2 identified 29 actionable findings across the 6 perspectives. 20 were applied as batch-32 fixes; 9 are accepted-risk or explicitly deferred.

---

## Findings Table

| Finding ID | Perspective | Severity | Component(s) | Description | Disposition | Fixed-in |
|---|---|---|---|---|---|---|
| FV-1 | Fix Verification | Critical | AIPrompt.Semantic.md | §3 prose still fires `onPromptRequest` after PL-8 only fixed the interface | Fixed — §3 prose changed to `onSubmit` | batch-32 |
| FV-2 | Fix Verification | High | ColumnChart.Semantic.md | §3 still says `layout='vertical'` after PL-15 only fixed §1 and interface | Fixed — §3 + header changed to `layout='horizontal'` | batch-32 |
| FV-3 | Fix Verification | High | Tooltip.Accessibility.md | RA-5 logged as Fixed-in-batch-31 but blocking note was not present | NOT-A-BUG — blocking note confirmed present in §4 on re-read; RA-5 was correctly applied | — |
| FV-NEW-1 | Fix Verification | Medium | AIPrompt.Semantic.md / Chat.Semantic.md | PL-7 resolution: hardening log says standardize to `label`; actual fix used `title`/`subtitle` | ACCEPTED-AS-APPLIED — `title`/`subtitle` is the Telerik baseline vocabulary; apply consistently; hardening log updated | batch-32 |
| FV-NEW-2 | Fix Verification | Medium | ColorPicker / CommandPalette | G-CP gap ID collision not addressed in batch-31 | Fixed — ColorPicker renamed G-CPKR1..4; CommandPalette renamed G-CPAL1..4 | batch-32 |
| AI-C1 | AI + Scheduling Review | Critical | SmartPasteButton | No contracts exist despite catalog #A28 entry | Fixed — 4 contracts authored | batch-32 |
| AI-C2 | AI + Scheduling Review | Critical | SpeechToTextButton | No contracts exist despite catalog #A29 entry | Fixed — 4 contracts authored | batch-32 |
| AI-H1 | AI + Scheduling Review | High | InlineAIPrompt | Error state has no dismissal path in Interaction; error panel close button escapes positioned ancestor | Fixed — Interaction §3 extended; Styling §3 root gets `relative`; Accessibility §3a error ARIA added | batch-32 |
| AI-H2 | AI + Scheduling Review | High | Chat.Styling.md | Scroll-to-bottom `absolute` button lacks `relative` on root container | Fixed — root classes in §1 now include `relative` | batch-32 |
| AI-M1 | AI + Scheduling Review | Medium | Gantt.Semantic.md | rowHeight default 32 (Semantic) contradicts 36 (Styling §3) | Fixed — Semantic default updated to 36 | batch-32 |
| AI-M2 | AI + Scheduling Review | Medium | Gantt.Interaction.md | Column-width resize handle styled (cursor-col-resize) but no behavior defined | Fixed — G-GANTT3 added as Accepted-risk M1 gap | batch-32 |
| AI-M3 | AI + Scheduling Review | Medium | Scheduler.Interaction.md | Keyboard Delete for event deletion mentioned in Accessibility but missing from Interaction | Fixed — §7 Event keyboard delete added; §8 Known Gaps created with G-SCHED1/G-SCHED2 | batch-32 |
| AI-M4 | AI + Scheduling Review | Medium | Scheduler.Semantic.md | "Selected event" state undefined — no `selectedEvent` prop or `onEventSelect` | ACCEPTED — G-SCHED2 added as Fix-deferred M2; selection model added as a known gap | batch-32 |
| AI-L1 | AI + Scheduling Review | Low | Chat.Interaction.md | Streaming DOM pattern note is informational; no action needed | No action needed | — |
| GAP-H1 | Gap ID + Severity Audit | High | Upload.Accessibility.md | G-UPL6/G-UPL7 severity column says Medium but disposition says Blocking | Fixed — severity upgraded to High | batch-32 |
| GAP-H2 | Gap ID + Severity Audit | High | Tooltip.Accessibility.md | RA-5 not applied (see FV-3 above) | NOT-A-BUG — already applied | — |
| GAP-M1 | Gap ID + Severity Audit | Medium | DataGrid.Accessibility.md | §10 cites WCAG 3.2.2 On Input — wrong SC for focus-lost-to-body on button click | Fixed — changed to WCAG 2.4.3 Focus Order | batch-32 |
| GAP-M2 | Gap ID + Severity Audit | Medium | DataGrid.Styling.md | §1 and §2 and References self-reference `[DataGrid.Styling.md](./DataGrid.Styling.md)` | Fixed — self-referential links replaced with token-file references | batch-32 |
| DIS-1 | Deferred Re-triage | High | Scheduler | Keyboard Delete was in Accessibility but not Interaction | Fixed via AI-M3 | batch-32 |
| DIS-2 | Deferred Re-triage | Medium | Gantt | Column-width resizer undefined in Interaction | Fixed via AI-M2 | batch-32 |
| DIS-3 | Deferred Re-triage | Medium | Menu.Accessibility.md | DIS-11: No declaration of which ARIA pattern the implementation targets | Fixed — §3 ARIA target pattern section added | batch-32 |
| DIS-4 | Deferred Re-triage | Medium | TextArea.Semantic.md | INCON-2: `invalid?: boolean` does not match `error?: boolean` convention used by all other DataEntry components | Fixed — `invalid` renamed `error` | batch-32 |
| DIS-5 | Deferred Re-triage | Medium | Breadcrumb.Semantic.md | `Omit<..., 'aria-label'>` with no replacement prop — no way to provide custom nav label | Fixed — `ariaLabel?: string` added (default: `"Breadcrumb"`) | batch-32 |
| DIS-6 | Deferred Re-triage | Low | Menu.Styling.md | Caret `▾`/`▸` spans need `aria-hidden="true"` | DEFERRED — not applied in batch-32; Styling contracts are PAO territory; route to PAO M2 pass |
| UPF-1 | UPF Meta-validation | Medium | upf-meta-validation.md | Check 7 verdict PASS is inaccurate — ~44 of 90 original gap IDs not cited by ID in contracts | Fixed — verdict updated to PASS-WITH-NOTES; MG-5 registered | batch-32 |
| UPF-2 | UPF Meta-validation | Low | upf-meta-validation.md | OO-3 still listed as Fix-deferred M2 despite being fixed by PL-11 in batch-31 | Fixed — OO-3 marked CLOSED | batch-32 |
| INCON-1 | DataEntry Consistency | Medium | Multiple DataEntry | `onChange` vs `onValueChange` split has no documented rationale | DEFERRED — API standardization requires council review (PL-10 covers Switch/CheckBox; extend scope to INCON-1 tracking) |
| INCON-3 | DataEntry Consistency | Medium | Multiple DataEntry | FormFieldContext consumption inconsistent across CheckBox/Switch/TextArea/DateInput | DEFERRED — requires FormField council review; track with RA-12 |
| INCON-4 | DataEntry Consistency | Medium | DatePicker / MultiSelect | WCAG 4.1.2 / 2.1.1 hard failures noted as Accepted-risk M1 | ACCEPTED — gaps G-DP* and G-MS* carry explicit dispositions; production blocking note added to MultiSelect already |

---

## Net file count after batch-32

- 8 new files: SmartPasteButton (×4) + SpeechToTextButton (×4)
- All new files carry `Status: Accepted`
- New total: **660 files** (652 + 8)
- Component catalog entries with full contracts: now includes #A28 and #A29

---

## Open items after batch-32

| Item | Tracking |
|---|---|
| DIS-6 | Menu caret `aria-hidden` — route to PAO Styling M2 pass |
| INCON-1 | `onChange` vs `onValueChange` rationale — extend PL-10 council check scope |
| INCON-3 | FormFieldContext consumption standardization — attach to RA-12 council queue |
| MG-5 | ~44 prose-absorbed gap IDs — QM sweep post-M2 |

---
component: Button, ButtonGroup, DropDownButton, FloatingActionButton, SplitButton
phase: parity
status: complete
complexity: medium
priority: medium
owner: bosun-b
last-updated: 2026-07-16
depends-on: []
---

# Resolution status: buttons

The React buttons category has been verified against the accepted UI Core contracts. The
contract-backed Blazor tranche is complete.

## Completed

- [x] Button and ButtonGroup contract parity
- [x] DropDownButton implementation, gallery demo, contract tests, and axe coverage
- [x] FloatingActionButton theme, accessible-name, and RTL-aware alignment parity
- [x] SplitButton loading and typed-item parity
- [x] Gallery overview parity for the contract-backed category
- [x] Blazor unit and WCAG 2.2 AA button gates

## Deferred by contract boundary

ActionMenu, CopyButton, and DataExportButton are React convenience components without accepted UI
Core contracts. They are not candidates for cross-framework API work until those contracts exist.
See the parent `GAP_ANALYSIS.md` for the verified matrix.

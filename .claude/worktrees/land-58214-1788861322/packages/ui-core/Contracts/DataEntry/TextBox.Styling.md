# TextBox — Styling Contract (Redirect Stub)

- **Component:** TextBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted (stub — superseded by TextField)
- **Superseded by:** [TextField.Styling.md](./TextField.Styling.md)
- **Catalog row:** #134 TextField (`app-priority: critical`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1

---

TextBox has been superseded by **TextField** (CIC ruling 2026-06-06).

TextField absorbs all TextBox capabilities: controlled/uncontrolled modes,
clear button (`clearButton` prop), password reveal (`showReveal` prop), and
the suffix slot composition that merges those affordances with caller-supplied
suffix content.

**Use [TextField](./TextField.Styling.md) for all new code.** The
`TextBox` export will remain as a re-export alias of `TextField` for
backwards compatibility during the transition period.

See [TextField.Styling.md](./TextField.Styling.md) for the full contract.

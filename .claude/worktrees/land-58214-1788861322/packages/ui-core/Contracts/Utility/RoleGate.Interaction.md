# RoleGate — Interaction Contract

- **Component:** RoleGate
- **ADR 0017 family:** Utility
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RoleGate.Semantic.md) · [Interaction](./RoleGate.Interaction.md) · [Accessibility](./RoleGate.Accessibility.md) · [Styling](./RoleGate.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/RoleGate.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. RoleGate is non-interactive

RoleGate is a zero-UI conditional rendering utility. It has no interactive behaviours, event handlers, keyboard interactions, or visual states. It renders its children or fallback transparently.

---

## 2. Rendering behaviour

RoleGate re-renders when `role`, `allow`, `children`, or `fallback` props change. Re-evaluation of the `allow.includes(role)` check is synchronous and O(n) in the length of `allow`.

---

## 3. Known gaps

| Gap | Description |
|---|---|
| No memoisation of `allow` | If `allow` is declared inline (`allow={['admin', 'manager']}`), a new array is created on each render. This does not cause a correctness issue but means the `includes` check always runs. Hosts should hoist `allow` outside the render path for performance in hot render paths. |
| No support for multi-role current users | `role` is a single string. Users with multiple roles (e.g. both 'admin' and 'accountant') cannot be expressed. Hosts must pick the primary role or use nested RoleGates. |
| No loading/unknown state | There is no provision for a `role` that is not yet known (e.g. auth still loading). Passing `role=""` with `allow={[...]}` will not match any role; `fallback` renders. Hosts must handle the loading case externally. |

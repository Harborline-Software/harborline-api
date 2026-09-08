# RoleGate — Semantic Contract

- **Component:** RoleGate
- **ADR 0017 family:** Utility
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./RoleGate.Interaction.md) · [Accessibility](./RoleGate.Accessibility.md) · [Styling](./RoleGate.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/RoleGate.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — render-prop guard component (no DOM primitive)

---

## 1. Component purpose

**RoleGate** — a zero-overhead conditional rendering utility that shows `children` when the current user's `role` is in the `allow` list, and shows an optional `fallback` otherwise. Provides a declarative, readable wrapper around role-based UI visibility guards. Does not render any DOM element of its own.

---

## 2. Props

```typescript
interface RoleGateProps {
  role: string           // the current user's role string
  allow: string[]        // roles that are permitted to see children
  children: ReactNode    // content shown when role is allowed
  fallback?: ReactNode   // content shown when role is not allowed; default: null
}
```

---

## 3. Rendering logic

```typescript
if (!allow.includes(role)) return <>{fallback}</>
return <>{children}</>
```

- When `role` is in `allow`: renders `children` wrapped in a React fragment.
- When `role` is NOT in `allow`: renders `fallback` (default `null` — nothing rendered).
- No DOM wrapper element is added in either case (React fragment only).

---

## 4. Role matching

Matching is exact string equality via `Array.includes`. Role comparisons are case-sensitive. Empty `allow` array means no role is permitted (`children` is never rendered).

---

## 5. Usage patterns

**Hiding UI for unauthorised roles:**
```tsx
<RoleGate role={user.role} allow={['admin', 'manager']}>
  <DeleteButton />
</RoleGate>
```

**Showing a fallback for unauthorised roles:**
```tsx
<RoleGate role={user.role} allow={['admin']} fallback={<ReadOnlyView />}>
  <EditForm />
</RoleGate>
```

**Inline check with no fallback:**
```tsx
<RoleGate role={user.role} allow={['accountant']}>
  <GLCodingPanel />
</RoleGate>
```

---

## 6. Security note

RoleGate is a **UI convenience utility** only. It controls rendering of UI elements, not access to data or server-side resources. Authoritative access control must be enforced at the API/server layer (Bridge endpoints, service methods) independently of RoleGate. A user who bypasses the browser can still call unauthorised API endpoints if server-side guards are absent.

---

## 7. Related components

- **Bridge authorization** — server-side role/policy enforcement (ADR 0091 `IAuthorizationContext`).
- **RoleGate** is the client-side rendering complement to server-side guards.

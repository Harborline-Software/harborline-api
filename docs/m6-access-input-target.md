# Access action targets without holder rows

The initial Access seed is now `1.1.3`, with `access.holders@1.0.2`.
Its narrow and revoke actions require the pack-authored `targetGrant` text
input, displayed as **Grant ID**. Their registered request descriptor target
binds to `input:/targetGrant`; narrow also requires `input:/scope`. Descriptor
IDs, correlation bindings, capability gates, and transport stay unchanged.
This lets an operator enter a known grant identity when a holders read is
empty or refused, and lets the authorization decider return the native
mutation refusal with its audit evidence.

The field name deliberately avoids `grantId`: the pack admission guard reserves
that property name for grant instances at any JSON depth. No exception to the
grant-instance prohibition is introduced.

The signed replacement fixture is `1.1.4`, with `access.holders@1.0.3` and the
scoped review workflow; the late-refusal probe is `1.1.4-atomicity-probe.0`.
Generate both with:

```powershell
dotnet run --project tooling/conformance/access-replacement -- .
```

The manifests pin the resulting bytes. Released `1.1.1` source and `1.1.2`
signed fixture bytes remain available for immutable-predecessor upgrade tests.
Acceptance consumers must use the new manifest versions and supply the pinned
grant UUID as `targetGrant` in both UI lanes.

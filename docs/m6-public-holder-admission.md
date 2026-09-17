# Public holder admission

An administrator can provision a holder through the existing selected-session
invitation endpoint, then let that person redeem the invitation and sign in:

```json
{
  "requestedPermissions": [],
  "idempotencyKey": "holder-invitation-1",
  "initialRole": "tax.roles/admitted-user"
}
```

POST this body to `/api/session/admin/invitations` with the administrator's
selected cookie and antiforgery token. The Access administration `1.1.3` seed
installs `access.admitted-user@1.0.0` as a role vocabulary entry with no offers.
The invitation stores the qualified role and a digest of its installed identity
and effective permission atoms. The command fingerprint and installation audit
bind those facts. Only the issuer selects the role; the anonymous redemption
command has no role field.

Issuance resolves the installed role and its closure and checks attenuation.
An empty permission request requires an explicitly selected role whose closure
is empty. Omitting the role preserves the Member default and the existing
nonempty request requirement. Acceptance resolves the persisted role again,
refuses a missing or changed role/closure, and asks AuthorizationGate to decide
the inviter's current `members:manage` authority, including signed-roster
membership/ejection, before minting any joiner state.
An empty role does not exempt the inviter from that decision.

The existing initial grant writer creates the admission anchor at `/`. Separate
Member grants may then be issued, narrowed and revoked while the anchor and
membership persist. Ordinary reauthentication refreshes the selected-session
authorization epoch after those grant changes. A current anchor with a resolved
empty closure returns a successful empty permission set; an unknown role, stale
epoch, stale grant pin, ejection or unavailable reader remains unresolved.

The signed `1.1.4` replacement retains the admitted-user role. Its current SHA256
is `1fd2920df204e2330e3d533e220ea42ad825fd0e7f48f78dccab68be03f27852`.
The atomicity probe SHA256 is
`263e363a0224a7205520920ac776e8ef99e7a72cff5db01db3c0291dffafe7fc`;
the probe's late refusal pointer is `/contents/7/contentBase64`.

Focused verification passed 166/166 tests with zero skips. The receipt is
`artifacts/m6-platform-form-title/invitation-role-focused.trx`, with its console
log at `artifacts/m6-platform-form-title/invitation-role-focused-final.log`.
Coverage includes issuance, persistence, migrations, replay, mandate
loss, unknown or changed roles, role escalation, public redemption/sign-in,
resolved-empty permission responses and stale selections, plus Access preload
and signed replacement producer/consumer contracts. The route E2E retains its
documented substituted Party/membership fixture; clean-node acceptance must
still record the full public holder lifecycle against the composed host.

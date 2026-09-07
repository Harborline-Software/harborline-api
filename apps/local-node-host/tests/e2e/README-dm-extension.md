# C2 DM extension — two-process E2E harness wiring

This directory holds the **C2 DM extension** (`two-process-dm-e2e-extension.sh`) for the two-process
two-user comms E2E harness (`two-process-comms-e2e.sh`).

## Status / why a separate fragment

The Tier-1 shell harness `two-process-comms-e2e.sh` is **in-flight on a sibling branch**
(`test/comms-two-process-e2e-harness`) and is NOT yet on `origin/main`. C2 ships off `origin/main`, so the
DM seam body is shipped here as a **sourced fragment** rather than duplicating / conflicting with the
in-flight 534-line harness. The harness reserves an inert `RUN_DM_EXTENSION` block precisely as this
extension point.

## How the harness wires this in (once both branches merge)

Replace the harness's inert `RUN_DM_EXTENSION` block body with:

```bash
if [[ "${RUN_DM_EXTENSION:-0}" == "1" ]]; then
  source "$(dirname "$0")/two-process-dm-e2e-extension.sh"
  run_dm_extension                       # C2 identity + scoping
  run_dm_extension_c5                    # C5 leak-proof routing/seal hard gate (needs a 3rd node C)
  run_dm_extension_c5_substitution_gate  # C5 key-distribution (substitution) gate — records the data-layer proof
fi
```

The DM resolve route (`CommsRoutes.DmResolveRoute` = `/api/local-node/comms/dm/{otherPartyId}`) ships ON by
default (`CommsDmFeatureFlag`, default ON) now that the body is sealed end-to-end (C4 content seal + C5
roster-bound, node-secret keys). The harness needs no env var — just leave the kill-switch
`HARBORLINE_COMMS_DM_DISABLED` UNSET. Setting that kill-switch truthy removes the DM route (the incident posture).

## What the extension proves (C2 scope)

1. **Deterministic dm: id (A==B).** A and B each POST to `dm/{otherParty}`; both nodes derive the SAME
   `dm:<hash>` id server-side with zero coordination (the no-handshake property). The fragment asserts
   `DM_ID_A == DM_ID_B`.
2. **A↔B DM round-trip, attributed.** A→B converges to B attributed to A; B→A reply converges to A
   attributed to B.
3. **Scoped.** The DM body does NOT appear on B's team channel.

## The honest "leaks until C4/C5" note

The canonical E2E scenario's load-bearing assertion is **"C (a team member, NOT a DM participant) NEVER sees
the A↔B DM."** At **C2 that assertion is EXPECTED-RED and is DEFERRED** — the DM body is **plaintext** (C4
seals it) and there is **no participant-scoped routing** (C5 filters it), so a non-participant team member on
the same sync plane *would* receive and read a C2 DM. The fragment **logs the deferral explicitly** rather
than silently skipping it; the leak-proof assertion (cryptographic: C holds only ciphertext **and** C never
receives the delta) **flips to a hard gate when C4 + C5 land**.

This is why C2 is **dev/test-gated**: a plaintext DM must never reach the shipped user surface before C4.

## The authoritative C2 A↔B DM proof on `origin/main`

The shell extension above runs the cross-machine flow once both branches land. The **merged, CI-running**
Tier-1 A↔B DM convergence proof is the C# test
`apps/local-node-host/tests/Entities/CommsDmConvergenceTests.cs` — two in-process replicas converging a
`dm:`-scoped DM (both directions, attributed, scoped from the team channel).

**C4 update (the seal CONSTRUCTION landed — NOT the confidentiality guarantee).** The DM body is now SEALED. The
prior honest C2 leak test (`C2_Dm_Leaks_To_NonParticipant_Until_C4_C5`) has been reframed to
`C4_Dm_HonestNonParticipant_Holds_Only_Ciphertext_Construction` — using a clearly-labeled TEST-DOUBLE resolver, an
HONEST non-participant team member (Carol, reporting "carol") holds ONLY opaque ciphertext, while A↔B read the
plaintext. **Per the security-engineering deep-review of PR #1325, this proves the CONSTRUCTION, not the production
guarantee:** with the test double a non-participant who LIES about their party id can still derive the key (it is
party-id-derivable). Production therefore registers a FAIL-CLOSED key provider (`NoDmConversationKeyProvider`, no DM
key derivable) and the dev-gate stays OFF. The REAL no-leak guarantee — a FORGED party id still cannot derive the
key — lands in **C5** with the roster-bound resolver (node-secret, root-seed-bound keys), alongside
participant-scoped routing. See ADR 0136 (DM content confidentiality).

## C5 update — the leak-proof guarantee LANDED (roster-bound keys + participant-scoped routing)

**C5 delivers DM confidentiality.** DM keys are now **roster-bound**: each node's DM **private** key is
HKDF(node-root-secret, teamId) — **node-secret, NOT party-id-derivable** (`NodeDmKeyDerivation` /
`RosterDmKeyResolver`); the DM **public** key rides the synced roster record + the enrollment wire (the #1310
transport-key pattern extended). Production now registers the roster-bound resolver (overriding the C4
fail-closed `NoDmConversationKeyProvider` via `AddRosterBoundDmKeyProvider`), so the arch-fence asserts the
shipped resolver is the node-secret one — never party-id-derivable.

**The REAL forged-id leak test** (`C5_Dm_ForgedId_NonParticipant_Cannot_Derive_Key` +
`C5_Dm_NonParticipant_Node_Holds_Only_Ciphertext_RosterBound` in `CommsDmConvergenceTests.cs`) is GREEN for the
RIGHT reason: a non-participant team member (Carol) — given A's + B's party ids, the `dm:<hash>`, the ciphertext,
**and lying that she IS "alice"** — STILL cannot derive `K_dm`, because the ECDH needs alice's node-secret
seed-derived DM private key (which Carol does not have), not alice's party id. The forged id yields a different,
useless key. This is the test the C4 construction test deliberately was not.

**Participant-scoped routing** (the metadata defence-in-depth): a `dm:` stream registers on the delta router with
a recipient filter, so the gossip daemon ships it ONLY toward its two participants (outbound); and a node
fail-closed **drops** a `dm:` conversation it is not a participant of (inbound). The body is already sealed; routing
limits who even receives the ciphertext.

**The shell hard gate.** `run_dm_extension_c5` (in `two-process-dm-e2e-extension.sh`) **flips the C2 deferred
assertion to a HARD gate**: with a 3rd enrolled team member C, the A↔B DM body NEVER appears on C (not on C's team
channel, not on any conversation C can address), while the team channel IS visible to all three. The dev-gate stays
CLOSED until C5 is clean + the real leak test is GREEN + sec-eng APPROVE → then C6 (Mac↔Surface HW verify) → flip
the gate (expose DMs).

## C5 revision — the DM key-SUBSTITUTION break (sec-eng deep-review BLOCKER, PR #1326)

The first C5 cut passed its forged-id leak test but the sec-eng deep-review caught a deeper hole: the DM
**public** key rode **outside** the signed admission and `RebuildLiveRoster` harvested it **last-carried-wins**
from any record naming a live member (no author check). A trusted-but-malicious member (Mallory) could emit a
roster record `{PartyId:"alice", DmPublicKey: Mallory's}` → a victim derived `K_dm = ECDH(victim, Mallory_pub)` →
Mallory read the DM. The transport-key "unsigned-by-association" pattern was reused **without its precondition**:
a transport key substitution just makes the trusted handshake FAIL (proof-of-possession backstop), but a DM key
is used in a **non-interactive** ECDH with **no PoP**, so substituting it directly hands over the seal key.

**The fix.** The DM public key is now **bound INTO the signed admission envelope**
(`AdmissionRecord.AdmittedDmPublicKey`): `RosterSigning.SignAdmission` signs it, `VerifyAdmission` re-checks it,
`Genesis`/`StableGenesis`/`Admit` + `AdmitOverInvite`/`AdmitOverProximity` thread it, the enrollment-wire
`EnrollmentRequest.JoiningDmPublicKey` already bound it into B's PoP, and the host's `RebuildLiveRoster` +
`WireEnrollment.ValidateAndPlanAdoption` now harvest the DM key **only from the chain-validated rebuilt roster**
(`MemberRoster.DmPublicKeyOf` — the SIGNED key), never the raw snapshot/unsigned hint. A substituted DM key fails
signature verification → the record is dropped → the victim's resolver only ever uses the AUTHENTIC signed key.

**The substitution gate.** `run_dm_extension_c5_substitution_gate` records the gate; the authoritative
regression guards are the data-layer tests (the attack needs a malicious peer, not stageable over a well-behaved
node's HTTP API):

- foundation `RosterSyncRecordsTests.Substituted_Dm_Public_Key_Is_Rejected_Authentic_Survives`
- host `RosterCrdtConvergenceTests.Substituted_Dm_Key_Synced_Delta_Is_Rejected_On_Victim` (refute-verified: FAILS
  with the old last-carried-wins harvest)
- arch-fence `CommsConversationScopeArchTests.Roster_Honors_Dm_Key_Only_From_Signed_Admission`

**Residual (documented, not closed by C5).** `AuthorPartyId` + the `dm:<hash>` are still plaintext WIRE fields —
they reveal which two members DM (brute-forceable from the roster pairs). C5 closes DELIVERY (participant-scoped
routing) + key-distribution (signed-admission DM-key binding); the `dm:<hash>` derivability is a documented
residual (future: opaque conversation ids / encrypted routing headers). KCI / no-forward-secrecy also apply
(static-static ECDH: a node-root-secret compromise lets the holder retro-read that node's DMs). See ADR 0136.

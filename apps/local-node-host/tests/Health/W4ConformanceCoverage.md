# W4 transport conformance coverage

`LocalNodeLanTransportTests` now drives the real LAN gate over HTTP with the per-connection listener
marker, and boots an enabled host to assert the exact loopback-plus-LAN socket set. It proves:

- C1–C5: opt-in defaults, startup validation, TLS/certificate shape, and bind scope;
- C15: the sealed endpoint snapshot's denial set is rejected before handlers, with a hard-coded canary
  for the complete §8.1 allowlist;
- C16–C18: desktop-plane and non-redeem admission routes refuse with `lan.route.unavailable`, only the
  exact POST `/api/local-node/admission/redeem` crosses the sessionless arm, and the loopback bearer is
  not accepted on LAN;
- C22: the authorization fence refuses under a bound device scope (the two named route-level refusals
  remain a device-session-card follow-up); and
- C24: cookie-bearing LAN requests are refused by the middleware, while authenticated requests are not
  charged to the unauthenticated LAN caps.

The existing pairing-substrate suites remain the authority for the pre-existing criteria:

- `ConnectDeviceRoutesTests` covers C8 token TTL defaults and clamping.
- `PairingTokenGatedAdmitterTests` covers C9 proof-before-consume and pin validation.
- `PairingRedeemRouteTests` and `DurableAdmissionTokenStoreTests` cover the durable C10–C12
  single-use, expiry, replay, and audience/session-binding substrate where implemented.
- Session issuance/revocation coverage for C13–C14 is deferred; this PR does not implement the LAN
  device-session seam.

No `ILanDeviceSessionAuthority` implementation exists yet. Consequently, a LAN-enabled node currently
serves only the sessionless `/api/local-node/admission/redeem` route (m1); allowlisted data requests
fail closed until the device-session card supplies the authority. LAN requests also bind no tenant
context yet (m2); that is deferred to the same device-session card.

The following criteria still require a provisioned W4 leaf/CA and a real second device (or equivalent
isolated network namespace) for release evidence: C6, C7, C13, C14, and C19–C21. They prove mobile
trust, discovery, server-side device-session issuance/revocation, firewall posture, secret hygiene,
and the shipped mobile trust instructions. C5's certificate shape and TLS floor are deterministic here;
issuance, OS-store provisioning, and the 90-day operational policy require the deployment check.

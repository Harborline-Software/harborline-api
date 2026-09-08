# Update feed — `harborline-dogfood` (DOGFOOD-DEV grade)

A static, signed update feed (design note `update-feed-design-2026-07-07.md`, slice U1).
Every byte a consumer reads carries a signature that verifies against the **pinned
channel root** — never against the URL that served it — so **a mirror is a dumb
byte-for-byte copy** (design note §2.3): copy this whole tree to an intranet host or a
USB stick and it verifies identically, with no re-signing and no new trust.

## This is a DOGFOOD / DEV feed

The channel root below is a **dogfood-grade DEV root**, generated for the dogfood
channel. It is **NOT** the production official Harborline channel root — that root is a
**human CP signing ceremony** owned by CIC (design note §7.3, slice U7), never minted by
tooling or CI. Do not treat this key as production trust.

## Pinned channel root (out-of-band trust anchor)

- Channel: `harborline-dogfood`
- Key-id: `ed25519:1S_fuTCz2FPyE-_jo1YDyHeATRjU2UiX23WNX1zXaLs`
- Fingerprint (SHA-256): `6B:3A:80:D0:3A:82:3F:90:C2:89:EC:EC:2D:8C:F0:5D:17:6D:E5:29:9E:C8:48:80:9B:F6:AF:51:86:27:1A:38`
- Publisher epoch: `1`
- Grade: `dogfood-dev`

The pinned public root is in `channel-root.pub.json`. In a real deployment the official
root ships pinned in the signed binary; here it is committed beside the feed purely so a
reviewer can run the offline verifier.

## Verify offline (the mirror-is-a-dumb-copy proof)

```sh
# from the earlier repository's tooling/update-feed
dotnet run -- verify --feed <this-dir> --root-file <this-dir>/channel-root.pub.json
# or the wrapper:  ./verify-feed.sh <this-dir> <this-dir>/channel-root.pub.json
```

A clean verify checks: the channel-root signature on `channel.json` /
`packs/*/index.json` / `revocations.json` / `feed-policy.json`; the CID chain down
to each manifest + artifact; the index-coupled policy CID and regulatory class;
the signed `validUntil` freshness fence (channel-typed); the monotonic `sequence`; the
`revocationsCid` coupling; and the publisher signature + merkle binding on the artifact
via the existing pack verifier. In v1, each publisher issuer must equal the channel root.

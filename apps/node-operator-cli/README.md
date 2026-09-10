# Harborline node operator CLI

`harborline-node` calls the local node's existing loopback HTTP listener. Supply the listener URL and
the same per-boot caller-auth token used by other node clients either as options or environment
variables:

```text
harborline-node --url http://127.0.0.1:5050 --token <token> --json health
harborline-node --url http://127.0.0.1:5050 --token <token> --json tenant list
harborline-node --url http://127.0.0.1:5050 --token <token> --json pack install --file general.pack
harborline-node --url http://127.0.0.1:5050 --token <token> --json pack activate --pack-key general --version 1.0.0
harborline-node --url http://127.0.0.1:5050 --token <token> --json pack deactivate --pack-key general --version 1.0.0
harborline-node --url http://127.0.0.1:5050 --token <token> --json pack verify --file general.pack
harborline-node --url http://127.0.0.1:5050 --token <token> --json pack export --request general.export.json --out general.pack
harborline-node --url http://127.0.0.1:5050 --token <token> --json export --scope forms
harborline-node --url http://127.0.0.1:5050 --token <token> --json record create --file entity.json
```

`pack verify` uploads the file byte-exact (like `pack install`) and prints the node's verdict.
`pack export` posts the composition request document verbatim — the node's route owns validation —
and writes the signed pack bytes to `--out`, printing `{"file":…,"bytes":…}`.

`record create` posts the record body verbatim to `POST /api/local-node/entities` — the node owns
validation, so the headless write goes through the same authorization gate and the same real
validator (ticket 151, L1418) as an interactive client. A refused body comes back as the node's 422
verbatim on stderr: the dotted reason code and the RFC 6901 pointers of the failing members, never
the body that was refused.

## Coverage manifest

`cli-coverage.json` beside this README maps every desktop-operator route on the node to its CLI
verb or to an exemption with a rationale. `CliCoverageReconciliationTests` (ticket 072 in
harborline-control) reconciles it against the node's sealed endpoint graph in both directions, so
adding an operator route without a verb — or keeping an entry for a route that no longer exists —
fails the build by name. Add the verb, its command-contract test, the manifest entry, and the README
line in the same change.

The equivalent environment variables are `HARBORLINE_NODE_URL` and `HARBORLINE_NODE_TOKEN`;
explicit `--url`/`--token` flags override them. The CLI never persists the caller-auth token.

## Exit codes and error contract

The CLI is the agent client (ADR 0020 in harborline-control), so its failure surface is part of the
contract, not incidental:

| exit | meaning | stderr with `--json` |
|---|---|---|
| 0 | success | — (body on stdout; non-JSON success bodies are wrapped: `{"status":…}` for `health`, `{"result":…}` otherwise) |
| 1 | the node answered with a non-success HTTP status | the node's JSON error body **verbatim** when it sent one; otherwise `{"error":"http_error","status":<code>,"message":<body>}` |
| 2 | invalid arguments, unknown command, or a missing pack file | `{"error":<code>,"message":…}` — no HTTP request is made |
| 3 | transport failure — the node was unreachable (`connection_failed`) or the request timed out (`request_timeout`) | `{"error":<code>,"message":…}` |

Without `--json`, stdout carries the raw response body and stderr carries plain-text messages; exit
codes are identical in both modes.

Tenant creation, general key rotation, and migration execution are intentionally absent until the
node exposes those operations over HTTP. The current node only lists tenants, records compromised-
device key disposition, and previews ERPNext imports; presenting any of those as the missing write
operation would create a false operator contract.

# First start: running the reference node from a published artefact

Run a self-contained local node and its operator CLI from a published directory. The target machine does not need a source checkout or .NET SDK.

This is a pre-release, manually started local artifact. It does not include an installer, operating-system service or production-hosting procedure. For candidate validation and recovery limitations, see the [operator guide](../operations/first-release-operator-guide.md).

## 1. Publish the artefact

On a machine that does have the checkout and the SDK:

```bash
bash eng/publish-local-node.sh
```

It writes a self-contained directory — the .NET runtime, the ASP.NET Core shared framework, the host, and the operator CLI (`harborline-node`) — to `artifacts/publish/local-node-<rid>`, and prints the start command at the end. The RID comes from the host you run it on; `--rid osx-arm64` (or `win-x64`, `linux-x64`) cross-publishes, and `--out <dir>` chooses the directory.

Copy the complete directory to the target machine. The runtime is included; you must still supply the boot configuration and preserve the installation identity described below.

## 2. Start it

Two boot inputs and, for the browser front door, the founder inputs. All of them are environment variables; none of them is a file you edit.

| variable | what it is |
|---|---|
| `LocalNode__RootSeedHex` | the install's 32-byte root identity, 64 hex characters. Everything derives from it, including the genesis team id (the first 16 bytes). **Generate it once per install and keep it** — a new seed is a new install, and the old node's data is sealed under the old one. |
| `LocalNode__SessionToken` | the per-boot caller-auth token. Every client — the operator CLI included — presents it on the loopback routes. Generate a fresh high-entropy value for each boot; treat it as a credential. |
| `LocalNode__HealthPort` | the loopback port to bind. `0` (the default) lets the OS pick one, which is fine for a supervisor and useless for a person. |
| `LocalNode__DataDirectory` | where the encrypted node store lives. Defaults to the install footprint under your profile. |
| `LocalNode__WebClient__Enabled` | `true` serves the browser front door and the per-user session routes. Leave it off if you only want the API. |
| `LocalNode__WebClient__FounderUsername` | the founder's login name. |
| `LocalNode__WebClient__FounderPasswordHash` | the founder's password as an Argon2id PHC hash. **The plaintext never goes in an environment variable or a config file.** Mint the hash with the artefact itself (below). |

Mint the founder hash first — the host has a subcommand for it that runs and exits before any of the node composes:

```bash
./Harborline.Api.LocalNodeHost hash-web-password
# $argon2id$v=19$m=19456,t=2,p=1$…$…
```

Enter the password on standard input and press Enter. The tool reads one line; it does not mask terminal input. Keep the terminal private and do not capture the plaintext in a transcript. Store only the resulting PHC hash in the configuration.

For a new disposable installation, run the following in Bash from the published directory. For an existing installation, supply its retained root seed instead of generating another one:

```bash
export LocalNode__RootSeedHex=$(openssl rand -hex 32)   # retain this seed for subsequent starts
export LocalNode__SessionToken=$(openssl rand -hex 16)
LocalNode__HealthPort=5050 \
LocalNode__WebClient__Enabled=true \
LocalNode__WebClient__FounderUsername=founder \
LocalNode__WebClient__FounderPasswordHash='$argon2id$v=19$m=…' \
./Harborline.Api.LocalNodeHost
```

The commands above use Bash syntax. On Windows, the executable is `Harborline.Api.LocalNodeHost.exe`; PowerShell uses `$env:VariableName` to set process environment variables. Do not paste the Bash assignments into PowerShell. Stop it with Ctrl+C — the worker traps cancellation and tears the node down through `Running → Stopping → Stopped`.

Capture the seed and the token you generated. The seed is the install; the token is what every client on this machine must present until the next restart.

## 3. What `/health` should answer

```bash
curl http://127.0.0.1:5050/health
```

```text
Healthy
Active team <genesis team id> is materialized and gossip is running (0 known peer(s)).
All active declarative role gates resolve to permitted owners.
The node process is responsive.
```

`Healthy` on the first line is the verdict; the lines under it are the individual checks. The genesis team id is derived from the root seed, so it is the same on every start with the same seed — if it changed, the seed changed. `/health`, `/live` and `/ready` are the only unauthenticated routes; everything else needs the session token.

A node that never answers is not a node that is starting slowly: read its stdout. A container that refuses to build the service graph says so there, and names the service.

## 4. Point the operator CLI at it

`harborline-node` ships **inside** this directory (ADR 0020: it is only useful on the machine whose loopback listener it can reach, and it has to hold that machine's per-boot token). Give it the URL and the token, as flags or as `HARBORLINE_NODE_URL` / `HARBORLINE_NODE_TOKEN`:

```bash
./harborline-node --url http://127.0.0.1:5050 --token "$LocalNode__SessionToken" --json health
{"status":"Healthy"}

./harborline-node --url http://127.0.0.1:5050 --token "$LocalNode__SessionToken" --json tenant list
[{"teamId":"…","name":"Team …","isActive":true,"memberCount":1}]
```

The single team in that list is the genesis team, seeded at first boot from the root seed — seeing it is how you know genesis ran. `apps/node-operator-cli/README.md` has the rest of the verbs and the exit-code contract.

## 5. What the first-boot operator may do

Genesis does not hand out an administrator. The installer's authorization seed issues exactly one holding to the desktop operator party — principal `local`, the party every loopback call carrying `LocalNode__SessionToken` resolves to — and that holding is the sealed `node-operator` package role at scope `/`. It offers only the operations the desktop route families actually resolve at the gate, `packages:operate` and `packages:author` among them, which is why the founder can author, export, verify, install and activate a pack on a freshly started node and cannot, for example, grant permissions to anyone (`grant:permissions` is deliberately not offered to it: that is an Administrator's act, requested through the Access forms). The grant is durable, recorded and revocable like any other — it is in the grant store under the source reference `authorization-seed:node-operator`, not an ambient bypass — and every pack route decides each command through the one `AuthorizationGate`, about that principal. Nothing in the first-install path is allowed because a caller said so.

## Verification

[The install-artifact check](../../eng/verify-install-artefact.sh) publishes a self-contained artifact, copies it to a temporary directory, starts it, checks health and the seed-derived tenant, exercises the published CLI and stops the node. Its result applies to the tested commit and environment. It does not establish completion of the App onboarding workflow or every instruction on this page.

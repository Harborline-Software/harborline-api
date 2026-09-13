# First-release operator guide

> **Candidate guide:** The procedures have not completed a selected-candidate walkthrough. Check the [validation record](first-release-validation.md) before using them.

This guide gives operators one task-oriented path through the current published-artifact, local-node host, CLI, and recovery documentation without requiring Toolbox.

Toolbox is optional. This guide uses the host executable, the bundled `harborline-node` CLI and loopback health interfaces.

## Reference instructions

The [published-artifact first-start guide](../install/first-start.md) is the authority for artifact creation, boot inputs, configuration defaults, expected first health response, and initial CLI connection.

The [node operator CLI reference](../../apps/node-operator-cli/README.md) is the authority for supported verbs, options, environment variables, exit codes, and error output.

The [local-node host guide](../../apps/local-node-host/README.md) is the authority for process lifecycle and the current absence of service-manager integration.

The [backup and restore boundary](../../apps/local-node-host/BackupRestore/README.md) is the authority for the recoverable set, restore authorization, and excluded local state.

## Prepare the installation record

Use the release-supplied published directory; the current source process for producing that directory is documented in [first start](../install/first-start.md#1-publish-the-artefact), but an operator should not build an unpinned checkout and call it a release.

Create an installation record containing the release label, source or distribution revision supplied by the release owner, original artifact location, host operating system and architecture, installation directory, data-directory choice, host-binary SHA-256, and CLI-binary SHA-256.

On Linux, calculate a binary digest with `sha256sum Harborline.Api.LocalNodeHost`; on macOS, use `shasum -a 256 Harborline.Api.LocalNodeHost`; on Windows PowerShell, use `Get-FileHash .\Harborline.Api.LocalNodeHost.exe -Algorithm SHA256`.

Keep the root seed, session token, founder password, founder password hash, private keys, and record bodies out of the installation record.

## Configure the node

Use the configuration table in [first start](../install/first-start.md#2-start-it) rather than copying defaults into another maintained checklist.

Confirm that the root seed was generated once for this installation and placed in approved secret custody, because changing it creates a different installation and leaves existing data sealed under the old identity.

Generate a new caller-auth session token for each boot, choose an explicit loopback port for a person-operated process, and select the data directory according to the release source and local retention policy.

Enable the web client only when the founder inputs have been prepared as documented, and mint the password hash with the shipped host subcommand rather than storing a plaintext founder password.

Treat environment variables as credentials-bearing process inputs, restrict who can inspect the launching process environment, and clear temporary shell variables after a verified stop.

## Start and recognize the node

Start from inside the recorded published directory using the executable and environment inputs in [first start](../install/first-start.md#2-start-it); the executable is `Harborline.Api.LocalNodeHost.exe` on Windows and `./Harborline.Api.LocalNodeHost` on Linux and macOS.

Keep the process attached to a terminal or a supervisor that captures stdout and stderr, because the current host documents a headless process and does not ship an operating-system service unit or installer.

Query `http://127.0.0.1:<port>/live` for process liveness, `http://127.0.0.1:<port>/ready` through the CLI `health` verb for readiness, and `http://127.0.0.1:<port>/health` for the multiline health report; these are the documented unauthenticated health interfaces, while the CLI operations below require the session token.

Run the bundled client from the same published directory after readiness answers:

```sh
./harborline-node --url http://127.0.0.1:<port> --token "$LocalNode__SessionToken" --json health
./harborline-node --url http://127.0.0.1:<port> --token "$LocalNode__SessionToken" --json tenant list
```

On a healthy first start, `health` exits zero and returns a healthy status, while `tenant list` returns the active tenant described in [first start](../install/first-start.md#4-point-the-operator-cli-at-it); record the response’s `teamId` as the tenant identifier, retaining its wire name in diagnostic evidence.

Do not interpret a process that has not answered as slow startup; inspect captured stdout and stderr, where service-composition failures are reported.

## Identify what is running

The current host and CLI expose no supported runtime-version command or HTTP version route, so `health` identifies state but not the release that produced the process.

Correlate the running process with the installation record by retaining the supervisor's exact executable path and the pre-start binary digest; if the path cannot be established, stop gracefully and restart the recorded binary instead of asserting a version from memory.

## Stop

Send an interactive interrupt with `Ctrl+C`, or configure the supervisor to send the equivalent graceful termination signal, and allow the host to move through `Running`, `Stopping`, and `Stopped`.

Wait for the process to exit and retain the final lifecycle log lines before rotating the session token or moving the published directory.

Forced termination has no documented clean-stop guarantee, so record it as an abnormal stop and preserve the data directory for investigation.

## Logs and expected states

The supported source-reviewed log location is the host process's stdout and stderr; no durable product log directory is documented, so configure terminal or supervisor capture before start.

Expected healthy evidence is a zero exit from CLI `health`, a `Healthy` health verdict, a stable active tenant identifier for a stable root seed, and a graceful lifecycle ending in `Stopped`.

Use the maintained [exit-code contract](../../apps/node-operator-cli/README.md#exit-codes-and-error-contract) to distinguish a node refusal, invalid local inputs and a transport failure. Preserve the node’s JSON error body when one is returned.

A refusal is an expected governed state when its code and context explain that the caller, input, or requested operation is not admitted; do not turn it into an availability incident unless the documented interface itself is unavailable or contradicts its contract.

## Collect sanitized diagnostic evidence

Record the UTC start and failure times, installation-record identifier, executable path, binary digests, operating system and architecture, configured loopback port, data-directory path classification, command name, CLI exit code, health verdict, and the smallest relevant stdout or stderr window.

Replace session tokens, root seeds, founder credentials and hashes, bearer values, private keys, signed grants, nonces, user names, tenant names, record bodies, file contents, and sensitive path components with typed markers such as `<session-token-redacted>` before sharing evidence.

Do not edit the stable error code, HTTP status, JSON pointer, lifecycle state, or ordering of relevant log lines, because those values distinguish authorization, validation, configuration, and transport failures.

Run the smallest read-only reproducer first, normally CLI `health` followed by `tenant list`, and attach the exact sanitized command with secrets replaced rather than a paraphrase.

## Backup and restore

Consult the [backup and restore boundary](../../apps/local-node-host/BackupRestore/README.md) before deciding what can be recovered. Continuous selective sync covers canonical documents supplied by canonical holders; it does not provide a point-in-time archive or make a package export a backup.

There is no documented operator command or HTTP route for the re-host ceremony. If a node is lost, preserve diagnostic evidence and custody records and contact [support](../../SUPPORT.md). Do not generate a replacement seed and expect the old encrypted data to open.

The retention table below identifies state outside the executable footprint. The recovery reference owns the exact recoverable set, excluded state, trustee requirements and single-use grant behavior.

## Upgrade, rollback, and uninstall

No supported node-binary upgrade command or operator procedure is present in the current host or CLI documentation, so replacing the executable while retaining a data directory is unverified and must not be offered as an upgrade path.

Pack install, verify, activate, and deactivate are supported CLI operations for packs and do not upgrade the node binary; the presence of update-feed code or database migrations does not create an operator upgrade promise.

No supported node-binary rollback procedure is present, and the host guide explicitly says the admission and roster schemas are one operational migration set and that partial rollback is unsupported.

No installer or uninstaller is shipped; stopping the process and removing a copied published directory removes that executable footprint but does not prove removal of the data directory, root-seed custody copies, environment configuration, supervisor configuration, logs, canonical-holder copies, or operating-system keystore material.

Complete data or key destruction therefore needs an approved retention-and-erasure procedure outside the current release documentation, and an operator must not improvise it with recursive deletion.

| Item | Retained by binary-directory removal? | Current documented recovery or removal boundary |
|---|---:|---|
| Published host and CLI files | No, if that exact copied directory is removed | No installer inventory exists; verify the exact recorded path |
| Encrypted node data | Yes when stored outside the copied directory | Requires the original root identity; no CLI restore/import contract |
| Root seed custody copies | Yes | Governed by external secret custody; changing the seed creates a different install |
| Per-boot session token | May remain in process environment, shell state, or logs | Rotate at next boot and clear temporary shell state after stop |
| OS-keystore material | Yes | Excluded from continuous selective sync; no uninstall cleanup is documented |
| Canonical-holder copies | Yes | Part of selective-sync recovery scope and not local uninstall state |
| Node-local drafts and invitation records | May remain in local data | Excluded from the canonical recoverable set |

## Failure routing

Use the approved [public support channel](../../SUPPORT.md), and keep security-sensitive reports out of public issue content according to the repository's [security policy](../../SECURITY.md).

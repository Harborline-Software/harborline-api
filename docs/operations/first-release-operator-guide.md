# First-release operator guide

> **Source-reviewed candidate draft:** the selected M4 candidate is still active, no immutable release pin has been supplied, and no operator walkthrough or successful recovery has been performed for this guide.

This guide gives operators one task-oriented path through the current published-artifact, local-node host, CLI, and recovery documentation without requiring Toolbox.

Toolbox may assist a future release, but every essential operation documented here uses the published host executable, the bundled `harborline-node` CLI, or the three unauthenticated loopback health endpoints.

## Source authority and release gate

The [published-artifact first-start guide](../install/first-start.md) is the authority for artifact creation, boot inputs, configuration defaults, expected first health response, and initial CLI connection.

The [node operator CLI reference](../../apps/node-operator-cli/README.md) is the authority for supported verbs, options, environment variables, exit codes, and error output.

The [local-node host guide](../../apps/local-node-host/README.md) is the authority for process lifecycle and the current absence of service-manager integration.

The [backup and restore boundary](../../apps/local-node-host/BackupRestore/README.md) is the authority for the recoverable set, restore authorization, and excluded local state.

Before publication, the release owner must identify an immutable candidate, confirm that these linked sources are the versions shipped in it, and complete the walkthrough record at the end of this page.

## Prepare the installation record

Use the release-supplied published directory; the current source process for producing that directory is documented in [first start](../install/first-start.md#1-publish-the-artefact), but an operator should not build an unpinned checkout and call it a release.

Create an installation record containing the release label, source or distribution revision supplied by the release owner, original artifact location, host operating system and architecture, installation directory, data-directory choice, host-binary SHA-256, and CLI-binary SHA-256.

On Linux, calculate a binary digest with `sha256sum Harborline.Api.LocalNodeHost`; on macOS, use `shasum -a 256 Harborline.Api.LocalNodeHost`; on Windows PowerShell, use `Get-FileHash .\Harborline.Api.LocalNodeHost.exe -Algorithm SHA256`.

Keep the root seed, session token, founder password, founder password hash, private keys, and record bodies out of the installation record.

## Configure without copying defaults

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

On a healthy first start, `health` exits zero and returns a healthy status, while `tenant list` returns the active tenant described in [first start](../install/first-start.md#4-point-the-operator-cli-at-it); the current JSON retains the source-era field name `teamId`, which operators should record as the tenant identifier without renaming the wire response.

Do not interpret a process that has not answered as slow startup; inspect captured stdout and stderr, where service-composition failures are reported.

## Identify what is running

The current host and CLI expose no supported runtime-version command or HTTP version route, so `health` identifies state but not the release that produced the process.

Correlate the running process with the installation record by retaining the supervisor's exact executable path and the pre-start binary digest; if the path cannot be established, stop gracefully and restart the recorded binary instead of asserting a version from memory.

This identification limitation is documented for the operator, and the release owner must assess whether the installation-record correlation is sufficient for the selected candidate.

## Stop

Send an interactive interrupt with `Ctrl+C`, or configure the supervisor to send the equivalent graceful termination signal, and allow the host to move through `Running`, `Stopping`, and `Stopped`.

Wait for the process to exit and retain the final lifecycle log lines before rotating the session token or moving the published directory.

Forced termination has no documented clean-stop guarantee, so record it as an abnormal stop and preserve the data directory for investigation.

## Logs and expected states

The supported source-reviewed log location is the host process's stdout and stderr; no durable product log directory is documented, so configure terminal or supervisor capture before start.

Expected healthy evidence is a zero exit from CLI `health`, a `Healthy` health verdict, a stable active tenant identifier for a stable root seed, and a graceful lifecycle ending in `Stopped`.

CLI exit `1` means the node returned a non-success HTTP response, exit `2` means invalid local arguments or a missing input file, and exit `3` means connection failure or timeout; preserve the node's JSON error body when one is returned and consult the maintained [exit-code contract](../../apps/node-operator-cli/README.md#exit-codes-and-error-contract).

A refusal is an expected governed state when its code and context explain that the caller, input, or requested operation is not admitted; do not turn it into an availability incident unless the documented interface itself is unavailable or contradicts its contract.

## Collect sanitized diagnostic evidence

Record the UTC start and failure times, installation-record identifier, executable path, binary digests, operating system and architecture, configured loopback port, data-directory path classification, command name, CLI exit code, health verdict, and the smallest relevant stdout or stderr window.

Replace session tokens, root seeds, founder credentials and hashes, bearer values, private keys, signed grants, nonces, user names, tenant names, record bodies, file contents, and sensitive path components with typed markers such as `<session-token-redacted>` before sharing evidence.

Do not edit the stable error code, HTTP status, JSON pointer, lifecycle state, or ordering of relevant log lines, because those values distinguish authorization, validation, configuration, and transport failures.

Run the smallest read-only reproducer first, normally CLI `health` followed by `tenant list`, and attach the exact sanitized command with secrets replaced rather than a paraphrase.

## Backup and restore

The current backup model is continuous selective sync of canonical documents to canonical holders; it is not a point-in-time archive, filesystem snapshot, or CLI backup command.

The recoverable set is only the canonical documents returned by the re-host source, so any store without an explicit canonical projection or separately documented recovery procedure is outside the backup claim.

Node-local submission drafts, single-use admission invitation records, machine configuration, installation files, local paths, cached or rebuildable projections, and operating-system keystore material are explicitly excluded from continuous selective sync.

The current restore implementation is an in-process `NodeRehostService.RestoreAsync` ceremony requiring a replacement identity, trustee key recovery, canonical holders, promotion authority, authenticated caller, and signed single-use grant; there is no HTTP restore route, CLI restore verb, or archive importer.

Because no first-release operator entry point for that ceremony is documented, this guide cannot provide a runnable restore command or claim a successful recovery; a node-loss event must be routed to the recovery implementation owner with the preserved diagnostic evidence and custody records.

Do not copy an encrypted data directory into a replacement installation, generate a new seed and expect old data to open, replay a consumed re-host grant, or represent export output as backup.

If a restore attempt is eventually authorized through a supported interface, remember that grant redemption is single-use and a later failure does not unburn it, while omitted node-local state must be re-created or reissued through its owning procedure.

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

Route packaging, startup, readiness, CLI transport, unexpected error-contract, persistence, and recovery-ceremony failures to their existing implementation owners with the sanitized evidence bundle; do not change milestone exit criteria or create a competing recovery path from this guide.

Use the approved [public support channel](../../SUPPORT.md), and keep security-sensitive reports out of public issue content according to the repository's [security policy](../../SECURITY.md).

## Documentation walkthrough record

**Status: not run.** No safe disposable-environment walkthrough, selected-candidate operator run, upgrade, rollback, uninstall, or successful restore has been completed for this document.

The release owner should run the following read-only and lifecycle sequence against the immutable candidate: verify artifact digests, start with disposable secrets and data, observe `/live`, run CLI `health`, run `tenant list`, collect sanitized logs, stop gracefully, and confirm the recorded executable path and final lifecycle state.

Record candidate identity, artifact digests, environment, UTC start and end, each command and exit code, observed health states, sanitized evidence location, deviations, functional-failure owner, document edits, reviewer, and disposition.

Upgrade, rollback, uninstall cleanup, and restore require separately approved disposable-environment scenarios because the current source supplies no supported operator procedure; leave each result **not performed** rather than inferring success from build or unit-test evidence.

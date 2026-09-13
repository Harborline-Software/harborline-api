# Operator documentation validation

This record tracks validation of the [first-release operator guide](first-release-operator-guide.md). It does not replace the release acceptance runbook or authorize unsupported recovery procedures.

## Candidate evidence

Before publication, identify the immutable candidate and confirm that the linked installation, host, CLI and recovery references describe its shipped artifacts. Assess whether executable-path and digest correlation is sufficient to identify the running version.

## Walkthrough


**Status: not run.** No safe disposable-environment walkthrough, selected-candidate operator run, upgrade, rollback, uninstall, or successful restore has been completed for this document.

The release owner should run the following read-only and lifecycle sequence against the immutable candidate: verify artifact digests, start with disposable secrets and data, observe `/live`, run CLI `health`, run `tenant list`, collect sanitized logs, stop gracefully, and confirm the recorded executable path and final lifecycle state.

Record candidate identity, artifact digests, environment, UTC start and end, each command and exit code, observed health states, sanitized evidence location, deviations, functional-failure owner, document edits, reviewer, and disposition.

Upgrade, rollback, uninstall cleanup, and restore require separately approved disposable-environment scenarios because the current source supplies no supported operator procedure; leave each result **not performed** rather than inferring success from build or unit-test evidence.

# Harborline API

This repository begins with a fresh public history as of September 2026. The earlier private history is kept, unchanged, in the private archive repository, and every design decision it records is carried forward in the Harborline control tickets. Nothing was rewritten; the history simply starts here.


> **Status: pre-release.** Harborline is under active development and is not ready for production use. APIs, schemas, storage formats and package names change without notice, and there are no supported installs yet. Source is licensed under [Apache-2.0](LICENSE); see [NOTICE](NOTICE) and the [trademark policy](TRADEMARKS.md).

This destination repository is the Harborline API release envelope. It currently supplies a small,
consumer-neutral transport interface while Capability and local-node-host capabilities are migrated:

- `Harborline.Api.Contracts` — request context, error envelope, adapter safety, and client interface.
- `Harborline.Api.Client` — production-capable `HttpClient` transport adapter.
- `Harborline.Api.Testing` — an explicit development-only fixture adapter.
- `Harborline.Api.MockHost` — a Development/Test-only fixture host that refuses Production startup.

Package IDs are prerelease-only until `repository.yaml` names distribution authority. No project in
this repository may reference Harborline, the migration control plane, or a sibling destination.
The publishing workflow also requires the approved remote to be marked `active` and otherwise fails
before registry authentication.

```sh
dotnet restore Harborline.Api.slnx
dotnet test Harborline.Api.slnx
bash eng/verify-boundaries.sh
bash eng/verify-packages.sh
```

## Verify

GitHub Actions is switched off in this repository until it is public (see the `ACTIONS_ENABLED`
block at the top of `.github/workflows/packages.yml`: the org is on the free plan and private-repo
minutes ran out on 2026-08-24). `eng/verify.sh` is what verifies a change instead. It runs the same
steps those workflows ran, in the same order, and on success records a receipt that
`.githooks/pre-push` requires before it will let a push through.

```sh
bash eng/verify.sh                    # run on a clean tree; the receipt attests to HEAD
```

`eng/verify.sh` points `core.hooksPath` at `.githooks` itself on first run, so verifying once arms
the hook from then on. Arm it before your first verification if you prefer:

```sh
git config core.hooksPath .githooks
```

**A clone that runs neither is unprotected and will not say so.** `core.hooksPath` is local
configuration that no clone carries, and git skips a missing hooks path *without an error*. That is
a deliberate defence against remote code execution, not something this repository can fix — no
in-repo change can make a fresh clone enforce on its first push. The reachable invariant is that a
clone enforces once bootstrapped, and that is asserted end-to-end by harborline-control
`tools/check-fresh-clone-verification.sh`, which also reports the residual.

The receipt is refused if the working tree is dirty: it attests to HEAD, while `eng/verify.sh` runs
against the working tree, so on a dirty tree it would vouch for code the run never saw. Commit
first, then verify.

Individual steps, to run one on its own:

```sh
dotnet test Harborline.Api.slnx
bash eng/verify-boundaries.sh
bash eng/verify-packages.sh
node eng/run-exact-clone.mjs
cargo test --manifest-path packages/contracts/rust/Cargo.toml
```

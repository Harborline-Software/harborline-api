# Contributing

Harborline is not currently accepting outside contributions or pull requests.

The project will update this file if the policy changes after the first release and a separate decision to accept outside contributions.

For usage help or a bug report, follow [SUPPORT.md](SUPPORT.md).

## Maintainer work

Harborline is pre-release and moves fast; read the README banner first.

- **Tickets first.** Work is tracked in the private Control repository, and each pull request names its ticket in the title (`NNN: what changed`).
- **Gate before push.** Run this repository's local gate, and do not bypass its hooks.
- **Test baselines are identity-checked, not hand-pinned.** `eng/baselines/host-test-baseline*.json` and `hull-test-baseline*.json` compare test IDENTITY (T-724 ruling 119): adding a test needs no baseline edit at all. Only an intentional test removal or rename needs a `policyRemovals` row naming the old identity, and `knownTests` is refreshed by `node eng/run-exact-clone.mjs --write-known-tests` (or the nightly/land tooling), never by hand. Exception: while a baseline's `knownTests` roster is still empty (true of the macOS/Ubuntu host baselines until their first refresh), the gate falls back to the old exact-total rule, so adding a test there needs the pinned total bumped too, or the roster populated first.
- **Small slices.** Keep each pull request to one coherent change of roughly two hundred production lines or fewer.
- **Tests that mean it.** Integration tests boot the real composition; fences discover their inventory by symbol; a mutation claimed as killed was actually run.
- **Names.** Do not introduce source-era names in new identities; "Harborline" and the logos are trademarks (see TRADEMARKS.md).

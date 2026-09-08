# Contributing

Harborline is pre-release and moves fast; read the README banner first.

- **License.** Contributions are accepted under Apache-2.0 (see LICENSE and NOTICE). By opening a
  pull request you confirm you have the right to contribute the code under that license.
- **Tickets first.** Work is tracked in `harborline-control/tickets`. A pull request names its
  ticket in the title (`NNN: what changed`) and the ticket carries the decision record.
- **Gate before push.** Each repository has a local gate (`bash eng/verify.sh` here for the API,
  `npm run gate:phase4` for Platform); the pre-push or pre-commit hook requires its receipt. Do not
  bypass hooks.
- **Small slices.** One coherent change of roughly two hundred production lines or fewer per pull
  request. Split before you open it, not after review.
- **Tests that mean it.** Integration tests boot the real composition; fences discover their
  inventory by symbol; a mutation you claim to kill was actually run.
- **Names.** No source-era names (see ticket 063 in control) in new identities; "Harborline" and the
  logos are trademarks (TRADEMARKS.md).

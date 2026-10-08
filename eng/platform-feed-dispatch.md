# Manual qualification registration

GitHub requires a `workflow_dispatch` workflow file on the default branch. An
active workflow identifier discovered from a branch run does not establish that
the file is present on main. The original registration-only workflow established the
entrypoint without merging the experimental platform feed implementation.

Before the dormant prototype lands, the registration version has no checkout, repository permissions, cache access,
qualification verdict, or benchmark result. Running it once on main confirms
registration. Its successful completion means only that the entrypoint ran.

After the separate disposable cache experiment establishes the effective write
denial and namespace isolation, a maintainer can select the independently reviewed
benchmark branch using `gh workflow run platform-feed-qualification.yml --ref
<reviewed-branch> -f benchmark=true`. GitHub executes the workflow version at that
ref. Confirm the remote branch head immediately before dispatch and the resulting
run's `head_sha` against the reviewed candidate before accepting its evidence.
An unexpected head invalidates the measurement; review and rerun the correct
candidate. Do not treat this registration as authentication of branch code.

The benchmark candidate retains its complete cold/warm/forced-miss harness,
immutable image, identical fresh validation work, and disposable cache keys.
Neither the production reuse PR nor the benchmark PR needs to merge to run this
experiment. No production reuse authorization follows from registration or from
the benchmark alone.

Sources:

- [GitHub workflow_dispatch requirements](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#workflow_dispatch)
- [GitHub CLI ref selects the workflow version](https://cli.github.com/manual/gh_workflow_run)

The dormant prototype replaces the registration placeholder with real production-builder
qualification on this entrypoint. Benchmark tooling stays in the separately reviewed
benchmark PR; its benchmark=true branch selection must not be mistaken for production
reuse authorization. Ordinary API CI enables neither reuse nor same-job handoff.

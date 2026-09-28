# Agent guide — harborline-api

## Mutation testing (Stryker.NET)

Read this before you add or change a test project's Stryker setup, or claim mutation evidence for a ticket. `eng/mutation-report.mjs` is the entry point; its header documents `--check`, PR mode and `--full`. Per-project scores and floors live in `eng/baselines/mutation-baseline.json`.

- **`--only` takes the exact test project path.** Pass the repository-relative `.csproj` path, for example `--only packages/foundation-localfirst/tests/Harborline.Foundation.LocalFirst.Tests.csproj`. A name fragment matches nothing and prints an empty table.
- **Linked worktrees need `--full`.** Since-mode in a linked worktree marks every mutant Ignored and exits 0, so use `--full --only <path>` there, or run from a plain clone.
- **One config mutates one direct reference.** A test project's config mutates exactly one project that test project references directly; Stryker cannot mutate a project reached only transitively.
- **Keep whole-host reports out of the tracked tree.** A report from the host test project is about 10 MB and embeds every host test source. The retired-prefix scan, the identity-r3 era-token scan and the quality gate's input-size limit all reject it. Archive the raw report on an `archive/*` branch, and pin its SHA-256 in the evidence note, as `docs/evidence/T-772-real-submit-gate-timeout-2026-09-28.md` does.
- **The report is the evidence, not the exit code.** Stryker can exit 0 having mutated nothing. Read the JSON report and name the killing test for each mutant a ticket lists (T-724 ruling 35). Mutant ids change between runs, so match survivors by file, line and mutator.
- **Run Stryker natively.** Inside the Codex sandbox its restore fails for lack of network; record that and leave the run to the controller.

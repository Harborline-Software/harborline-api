# T-670 api seed intake verification, 2026-11-19

API PR #479 merged origin/main without conflicts in d6ef2b368218a2511f7f7db284e9fc474d9654e8. This round changes no production code or tests.

The local feed was reused from the api #516 worktree at /Users/christopherwood/.t3/worktrees/harborline-api/t3-d9664edd-dbb56ee1959dd9d4/.feed. Directory.Packages.props is byte-identical. The complete platform-pin.json diff is this branch's extra T-670 explanatory entry in why; all other fields, including platform commit c9787df2b2b1daf38d7971c85b9b073962919293 and producer identities, are identical. Package versions therefore match. No feed files are tracked.

The unanswered capability-requirements finding is covered by PlatformSeedIntake.ResolveForInstall, called by PlatformPackPreloadHostedService.ReadExportRequest after conversion. PlatformSeedIntakeTests.The_intake_carries_the_package_level_capability_requirements_to_the_installed_pack exercises that production path and asserts the literal ["forms.catalogue-field-source"]. Oracle: seed-released-navigation.md:58. No new mutation evidence is claimed.

Focused host tests passed 274/274, zero failed or skipped: PlatformSeedIntakeTests, CatalogueRouteTests, CatalogueTests, SeededHealthBrowseViewTests, CatalogueFieldSourceContractTests, CascadeDefaultsTests, AccessAdministrationPreloadTests, PackSeedProjectionRouteTests and T433ReplacementAtomicityTests. Local TRX: /private/tmp/api479-focused-d6ef2b36/api479-focused.trx; log: /private/tmp/api479-focused-d6ef2b36.log. Restore reported NU1608 constraints between Npgsql EF Core RC and EF Core 11 GA.

Focused-mode classification against origin/main selected coverage-off and coverage-on; all changed paths have categories. The required quality checkout and bin directory setup check passed. Full-gate results and any receipt are recorded in the PR verification notes after the committed candidate is run.

The latest hosted verify failure at the original PR head is the draft guard: draft PR cannot satisfy required verification. Draft status is retained.

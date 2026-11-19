# T-738/api-generated-pack-enums — focused evidence

2026-11-19, claim base `138708cea336319f4db297c539b53e9bcb9347bc`.
API main checked before delivery: `63bd15165c8627654e984ed1fa42c1dbbc930aba`;
no intervening shared-file changes. Platform package built at the recorded pin
`c9787df2b2b1daf38d7971c85b9b073962919293` through
`eng/exact-clone-platform-feed.mjs`, which invoked `eng/build-local-feed.mjs`.
SDK: `/usr/local/share/dotnet`, `11.0.100`. Solution restore passed.

## Tests and oracles

```sh
DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet test apps/local-node-host/tests/tests.csproj --filter 'FullyQualifiedName~PackEnumCodegenTests|FullyQualifiedName~PackEnumWireCompatibilityTests|FullyQualifiedName~PackContentKindRegistryConsumptionTests'
```

Final result: **45 passed, 0 failed, 0 skipped**. The eight existing consumption
methods and their theories are unchanged. The two new classes add ten cases.
Existing NU1608 and analyzer warnings remain visible.

- Wire oracle: literal names, values and pillar mappings from api `5b313eaf`
  `PackEnums.cs` / `PackPillar.cs`, plus T-738's 2026-09-29 numbering contract.
  Codec output is asserted against each literal name before round-trip assertions
  against each literal number, avoiding a round-trip-only oracle.
- Generator oracle: synthetic JSON rows and documentation in `PackEnumCodegenTests`,
  including a documented Reserved row. Expected values, stale paths and failure
  messages are literals. Checks also prove the property that files stay unchanged.
- Existing consumption oracle: the pinned package registry; existing literal
  pillar, Reserved/undefined and generic-noun theories are retained.

The first renderer fixture was red against the empty generator (missing output
key); the check fixture was red against the no-op runner (missing output file).
All 21 kind and 15 pillar member XML comments were compared to api `5b313eaf`
and preserved verbatim. The L675 no-grant comment is retained in documentation
and generated output.

## Reproduction and drift

Two generator runs produced identical bytes and these SHA-256 values:

- `PackContentKind.g.cs`: `bfd37dc443750e4a74f8cbeee3677f51d38e7d973f6d0b303d913bd2d3056f7e`
- `PackPillar.g.cs`: `895b7a5184878e97a7459cde90e4916a5ccebc6e8f055b6ae0c2d20434b20b14`

Standalone `dotnet run --project tooling/pack-enum-codegen --no-restore -- --check`
passed. Appending a temporary comment to both generated files made it exit 1,
naming both relative paths; their bytes and mtimes stayed unchanged during the
check. Both were restored from copies in `/private/tmp/api525-work/`, and
`git diff --exit-code` passed. The restored standalone check and the exact combined
`codegen-check` command from `eng/verify.sh` both passed. An earlier overlapping
generation/drift probe was discarded; the recorded final probe ran sequentially.

## Hand mutations

Every plant was copied to `/private/tmp/api525-work/` before editing, never staged,
run through `dotnet test ... --filter FullyQualifiedName~<method>`, then restored.
`git diff --exit-code` passed after every restore. Every run below exited 1 from
an assertion failure, rather than a compilation failure. Test paths are under
`apps/local-node-host/tests/Packs/`.

| Plant | Killing method | Failure line |
| --- | --- | --- |
| FormDefinition 0 → 22 | `PackEnumWireCompatibilityTests.Content_kind_names_and_numbers_keep_the_literal_wire_contract_without_aliases` | `:45`, `Assert.Equal() Failure: Collections differ` |
| Add AssistanceDefinition = 20 | `PackContentKindRegistryConsumptionTests.Reserved_and_undefined_values_stay_other_and_contribution(20)` | `:138`, `Assert.False() Failure` |
| Add explicit alias to FormDefinition | `PackEnumWireCompatibilityTests.Content_kind_names_and_numbers_keep_the_literal_wire_contract_without_aliases` | `:45`, `Assert.Equal() Failure: Collections differ` |
| Other 99 → 98 | `PackEnumWireCompatibilityTests.Pillar_names_and_numbers_keep_the_literal_wire_contract_without_aliases` | `:63`, `Assert.Equal() Failure: Collections differ` |
| Remove shipped-only generator filter | `PackEnumCodegenTests.Fixture_rows_generate_literal_values_documentation_and_reserved_list` | `:40`, `Assert.DoesNotContain() Failure: Sub-string found` |
| Substitute fallback documentation | `PackEnumCodegenTests.Undocumented_member_refuses_generation` (both cases) | `:57`, `Assert.Throws() Failure: No exception was thrown` |
| Return success for stale files | `PackEnumCodegenTests.Changed_fixture_value_fails_check_names_the_file_and_writes_nothing` (both cases) | `:77`, expected 1, actual 0 |
| Return Other for every shipped kind | `PackEnumWireCompatibilityTests.Every_content_kind_keeps_its_literal_pillar_mapping` | `:71`, expected 1, actual 99 |

Raw logs and their `log-sha256.json` manifest remain under `/private/tmp/api525-work/`.
No Stryker score is claimed; `node eng/mutation-report.mjs --check` passed.
Shell syntax and the two `eng/tests/verify-receipt.test.mjs` tests passed.

## Outstanding machine work

**full api gate pending (MacBook)**. `eng/verify.sh` was not run in full here.
The slice's final baseline refresh through `eng/run-exact-clone.mjs --write-known-tests`
also remains with that run: the script installs package dependencies and executes
both full lanes. Existing host rosters are populated; no baseline totals or
`knownTests` were hand-edited.

An optional dependency-ledger invocation initially lacked the Control path; a
second invocation with it passed. Inspection then identified the script's internal
`git -c safe.directory` argument, contrary to this task's limit. No persistent Git
configuration changed; no further invocation was made.

Temporary folders retained: `/private/tmp/api525-work/`, including its tool-created
Platform checkout, source backups, fixture directories, orchestration script and logs.

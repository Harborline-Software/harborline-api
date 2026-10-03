# Validation reuse: shadow implementation

The existing required gate still executes in full. `run-exact-clone.mjs` captures
`.claude/gate-evidence/validation-inputs-shadow.json` before deleting scratch.
Existing host evidence uploads carry this observation without workflow changes.
It is unsigned diagnostic data and is never accepted as authority to skip work.

## What is implemented

- Canonical SHA-256 input fingerprints with lane, candidate **tree**, tracked
  workflow/action/script/build/dependency files, restored NuGet target/library
  closure, resolved JavaScript lock files, native binary hashes, SDK policy and
  observed SDK/npm/pnpm/Node versions, OS/architecture/image information,
  resolved platform/quality/control checkout commits, selection and coverage mode.
- Explicit unknown-input blockers and field-level differences, including OFF/ON
  coverage mismatches. Collection failures cannot erase the existing gate result.
- A read-only GitHub broker checks successful run attempt and host job/step,
  source repository, artifact identity/expiry and GitHub's archive SHA-256. It
  also requires artifact creation inside the current job attempt's time window;
  a previous attempt's same-run/same-head artifact is not current evidence. CLI
  failures use a fixed diagnostic rather than credential-bearing exception text.
  reads only two bounded ZIP entries without extraction or execution and checks
  the candidate tree/receipt against GitHub commit metadata. Synthetic PR merge
  checkouts must contain the run's source commit as a parent.
- A separate signed-receipt verifier supports an externally supplied reviewed
  producer/key policy, exact invocation completeness and artifact/consumption
  binding. There are **no default trusted keys**, signing commands or credentials.
  This alternative is tested infrastructure, not a requirement to create keys.
  The consumer supplies authenticated commit metadata: `observation.candidateTree`
  must be the actual tree of its observed producer `headSha`. The signed input
  tree must match it. `shadowVerdict` separately requires `currentObservation`
  with the actual current candidate SHA/tree. Receipt fields never provide these
  independent observations. Package/mutation candidate commits and mutation base
  commits also bind to their independently observed commits. Unsupported or
  missing commit/tree metadata refuses trust even with a valid signature.
- Every verdict is candidate-specific and shadow-only: `reuseAuthorized:false`
  and `requiredWorkSkipped:false`, including a cryptographically valid match.

Completeness requires typed dependency closures and native/lock digests, unique
producer file digests, observed tools/Node/SDK policy, OS/architecture/release,
resolved pins, explicit selections/coverage and commit-dependent boundaries.
Nonempty placeholder objects are incomplete evidence.

Run the lightweight contract suite:

```sh
node --test eng/tests/validation-reuse.test.mjs eng/tests/validation-inputs.test.mjs eng/tests/validation-github-shadow.test.mjs eng/tests/exact-clone-evidence.test.mjs
```

Compare two completed runs using reviewed broker code and an existing read-only
GitHub token (`GH_TOKEN`, Actions/Contents read; no persistent access change):

```sh
node eng/validation-github-shadow.mjs CURRENT_RUN_ID PRIOR_RUN_ID OUTPUT_JSON
```

Python is used only to read bounded ZIP entries. Never run the broker from a
downloaded candidate/artifact checkout. Existing pre-implementation artifacts
lack observations and report missing evidence rather than a matching verdict.

For previously downloaded `gh run download` directories:

```sh
node eng/validation-shadow-report.mjs CURRENT_DIRECTORY PRIOR_DIRECTORY OUTPUT_JSON
```

Artifact folders such as `verify-macos-evidence-123` and
`verify-macos-evidence-122` compare as the same host lane. Both reports always
enumerate the three expected host lanes. Missing evidence on either side,
empty directories and corrupt observations remain explicit unknown evidence;
duplicate lane observations are refused. Presence does not establish complete
inputs or authorize reuse. Each observation must match its expected host OS and
architecture and host baseline; copying Windows evidence into Mac/Linux folders is unknown
evidence. Missing or invalid candidate SHAs also remain unknown evidence.
Candidate SHA/tree identities must be literal hexadecimal strings; JSON arrays
and objects cannot qualify through JavaScript string coercion.
Successful broker entries report no lane problems; failed entries
preserve their actual diagnostics. Coverage profile selection uses the authenticated
GitHub run event, including `merge_group`, retained as current/prior report fields.

## What prevents actual reuse

GitHub artifact metadata authenticates the **transported bytes**, not the truth
of an artifact's claims. A branch can execute arbitrary code during its tests;
a JSON receipt or an unsigned matching observation is not a trusted producer.
Signing that same branch-produced claim alone does not resolve this problem.

The collector explicitly marks evaluated compiler/build inputs and an immutable
runner toolchain image as unobserved. Restored assets and source hashes do not
prove every evaluated MSBuild input or ambient dependency. Commit/history reads
in tests and tools must also be classified before allowing cross-commit reuse.
Package and mutation lanes have distinct commit-dependent inputs and are not
covered by this host collector. Control is resolved to its actual commit rather
than assumed stable because the workflow checks out control `main`.

## Smallest next reviewed implementation

The dependent producer-shadow phase adds a read-only `workflow_run` consumer
loaded from its own default-branch workflow commit, with no persisted checkout
credential and no candidate checkout or artifact execution. It independently
resolves main and compares producer/verifier/workflow/action definitions using
GitHub tree blob identities, rejecting changed or truncated definitions. It also
checks whether producer source is already on protected-main ancestry; same-repo
successful PR artifacts do not establish that trust. Reports bind candidate SHA,
tree, input fingerprint and expected coverage profile and require all three host
lanes to agree on the candidate.

An opt-in MSBuild hook observes `CscCommandLineArgs` after actual CoreCompile and
captures project/framework/configuration/execution context. The reader hashes
sources, references, analyzers and other explicit compiler file inputs, refusing
unknown switches, unapproved paths, missing files, nested responses and
design-time/skipped execution. This is not a hermetic build attestation: arbitrary
MSBuild tasks, generated-input timing, ambient runtime dependencies and candidate
execution can still influence observations. Native execution was exercised under
the coordinated Windows slot in a minimal Release library: fresh capture observed
193 files, changed source invalidated the stored snapshot, a fresh rebuild produced
a valid capture, an unchanged incremental build emitted no compiler arguments and
was refused, and capture-off compilation passed. The reader requires the collector's
independent build-session identifier and matching argument/context and pre/post
file hashes; timestamps alone prove nothing. Reference aliases and resource
name/access metadata remain in the fingerprint. Named host lanes are bound to
expected OS, architecture, baseline and host scope as well as coverage/quality.
Projects with NET analyzers disabled remain unobserved rather than invoking a
missing SDK analyzer preparation target.

The exact-clone parent queries `dotnet nuget locals global-packages --list
--force-english-output` in the checkout before restore/build, then shares that
resolved `NUGET_PACKAGES` root with its children and collector. Snapshot metadata
records the child root but never grants a new approved root; a mismatch refuses
completeness. Missing, relative or ambiguous native query output remains
unobserved. Bounded native proofs exercised both an initially unset override
(NuGet's actual default) and an explicit isolated override; both parent/child
roots matched independent native queries and both fresh captures contained 193
files. Package-file root/hash mismatches also have independent Node fixtures.
The standard `macos-15` profile is ARM64, with its macOS host baseline.

No signer or check-writing token was added. The consumer publishes a shadow
artifact using the existing Actions runtime mechanism and cannot skip gates or
write required candidate check verdicts. Promoting it to an authoritative required
wrapper is a later explicit reviewed workflow/policy change; a `checks:write`
capability, if chosen for that design, must be approved before activation.

Keep the shadow observer running, inspect real comparison reports and resolve
the unknown input dimensions. Add a default-branch, independently reviewed
producer/broker job that obtains GitHub run/job/artifact metadata itself and
captures the evaluated build/test closure in isolation from candidate execution.
Use GitHub's existing artifact digest and authenticated job provenance where that
trusted workflow boundary is sufficient; introduce signing only if that design
needs an additional trust boundary. Producer identity and required invocations
must be consumer policy, not artifact fields.

Then, in a separate reviewed change, make the required candidate wrapper choose
between fresh validation and verified matching evidence. It must deny reuse for
changed/unknown inputs, expired/missing evidence, nonpassing/skipped/cancelled
invocations and unmatched OS/architecture/selection/coverage. Package promotion
must preserve the bytes actually consumed. No landing matrix or protection
change is included here. Focused OFF/ON completion policy remains a separate
contract owned by the focused-mode implementation.

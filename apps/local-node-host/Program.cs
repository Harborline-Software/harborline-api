using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Reflection;

using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.FinancialLedger.DependencyInjection;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.Reports.DependencyInjection;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.FinancialAp.Data;
using Harborline.Api.Blocks.FinancialAr.Data;
using Harborline.Api.Blocks.Banking.Data;
using Harborline.Api.Blocks.Docs.Data;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Blocks.FinancialPayments.Data;
using Harborline.Api.Blocks.People.Foundation.Data;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.LocalFirst;
using Harborline.Api.Foundation.LocalFirst.Installation;
using Harborline.Api.Foundation.Packs.DependencyInjection;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Taxonomy.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.Foundation.ReportDefinitions;
using Harborline.Api.Foundation.ReportDefinitions.DependencyInjection;
using Harborline.Api.Foundation.DataExchangeDefinitions;
using Harborline.Api.Foundation.DataExchangeDefinitions.DependencyInjection;
using Harborline.Api.Foundation.ScheduleDefinitions;
using Harborline.Api.Foundation.ScheduleDefinitions.DependencyInjection;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.Foundation.ViewDefinitions.DependencyInjection;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Runtime.DependencyInjection;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.DependencyInjection;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.DependencyInjection;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.LocalNodeHost;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Banking;
using Harborline.Api.LocalNodeHost.Data.Calendar;
using Harborline.Api.LocalNodeHost.Data.Comms;
using Harborline.Api.LocalNodeHost.Data.Compose;
using Harborline.Api.LocalNodeHost.Data.OrgBranding;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Data.KeyDistribution;
using Harborline.Api.LocalNodeHost.Data.Storage;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.Foundation.Recovery.DependencyInjection;
using Harborline.Api.Foundation.Recovery.Blobs;
using Harborline.Api.Foundation.Recovery.LegalHold;
using Harborline.Api.Documents.PdfSharp;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Generation;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Data.Docs;
using Harborline.Api.LocalNodeHost.Data.Drafts;
using Harborline.Api.LocalNodeHost.Data.Payroll;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Governance;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.Foundation.PasswordHashing.DependencyInjection;
using Harborline.Api.Foundation.Session.DependencyInjection;

public static class LocalNodeHostComposition
{
    public static async Task RunAsync(
        string[] args,
        string? sessionTokenOverride = null,
        string? dataDirectory = null,
        TimeProvider? kernelClock = null,
        Action<IServiceCollection>? finalServiceRegistration = null,
        string? installFootprintRootOverride = null,
        Action<IServiceCollection, IServiceProviderFactory<IServiceCollection>>? finalServiceProviderProbe = null)
    {
var rootTimeProvider = kernelClock ?? TimeProvider.System;
// Admin subcommand: mint an Argon2id hash for a WEB-CLIENT founder password so the plaintext never
// lands in config/env — only the hash does (provisioned as LocalNode__WebClient__FounderPasswordHash).
// Runs and exits BEFORE any host composition. Usage:
//   Harborline.Api.LocalNodeHost hash-web-password 'the-password'
//   echo 'the-password' | Harborline.Api.LocalNodeHost hash-web-password
if (args.Length >= 1 &&
    string.Equals(args[0], "hash-web-password", StringComparison.OrdinalIgnoreCase))
{
    Harborline.Api.LocalNodeHost.Health.WebSession.WebPasswordHashTool.Run(args);
    return;
}

// ADR 0066 clause 8 — the OFFLINE administrator-recovery path. Dispatched HERE, before any host composition,
// because the whole point is that it runs with the node STOPPED and derives its authority from ownership of
// the data directory rather than from a request. It is deliberately NOT an HTTP route and deliberately not a
// verb on the operator CLI, which talks to a running node.
//
// It is what keeps the last-administrator invariant from being able to brick a node, and it is a DISTINCT
// path from the installer: it writes provenance `recovery`, and it never re-arms the installer state.
if (args.Length >= 1 &&
    string.Equals(
        args[0],
        Harborline.Api.LocalNodeHost.Data.Identity.AdministratorRecoveryCommand.Verb,
        StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = await Harborline.Api.LocalNodeHost.Data.Identity.AdministratorRecoveryCommand
        .RunAsync(args, Console.Out, Console.Error, rootTimeProvider)
        .ConfigureAwait(false);
    return;
}

// Composition root for the Harborline local-node host process.
//
// Paper §4 + §5.1: this is the persistent background service that owns the
// kernel runtime. It is intentionally headless — the application shell
// (Anchor, kitchen-sink, third-party UIs) connects to the already-running
// stack over the sync-daemon transport. Wave 6.3.E.2 reshaped the composition
// root: per-team services (event log, encrypted store, quarantine queue,
// gossip, lease coordinator, bucket registry) live inside each team's
// TeamContext.Services. The install-level surface here owns only:
//
//   * Plugin discovery / lifecycle   → AddHarborlineKernelRuntime           (Wave 1.1)
//   * Security primitives + KDFs     → AddHarborlineKernelSecurity          (Wave 1.6)
//   * Keystore-backed root seed      → AddHarborlineRootSeedProvider        (Wave 6.7.A)
//   * Per-tick gossip cap            → AddHarborlineResourceGovernor        (Wave 6.4, ADR 0032)
//   * Per-team service registrar     → AddHarborlineDefaultTeamRegistrar    (Wave 6.3.E)
//   * Per-team store activator       → AddHarborlineTeamStoreActivator      (Wave 6.3.E.1)
//   * Team bootstrap hosted service  → MultiTeamBootstrapHostedService   (Wave 6.3.E.2)
//   * Node-host lifecycle            → LocalNodeWorker                   (Wave 1.1 + Wave 6.3.E.2)

var builder = WebApplication.CreateBuilder(args);
ResilientWindowsEventLogRegistration.Add(builder.Services);
var authorizationSeedProfile = builder.Environment.IsDevelopment()
    ? AuthorizationSeedProfile.Development
    : AuthorizationSeedProfile.Production;
builder.Services.AddSingleton(authorizationSeedProfile);
if (sessionTokenOverride is not null || dataDirectory is not null)
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["LocalNode:SessionToken"] = sessionTokenOverride,
        ["LocalNode:DataDirectory"] = dataDirectory,
    });
}
// Endpoint registrars must complete before SharedHostedWebApp seals and binds the exact route graph.
// The shared app also fails closed under a concurrent mapper, but the production host keeps the
// reviewed sequential startup order explicit rather than relying on HostOptions' framework default.
builder.Services.Configure<HostOptions>(options => options.ServicesStartConcurrently = false);

// Ticket 216: the composition root owns the process's sole wall clock. Packages may consume this
// abstraction but may neither register nor fall back to a system clock of their own.
builder.Services.TryAddSingleton(rootTimeProvider);

// Bind host-wide configuration (node id, team id, data directory, multi-team
// bootstrap). We need the DataDirectory + MultiTeam section available NOW —
// before service registration — so the default team registrar closure can
// capture it.
builder.Services.Configure<LocalNodeOptions>(
    builder.Configuration.GetSection("LocalNode"));

var localNodeOptions = new LocalNodeOptions();
builder.Configuration.GetSection("LocalNode").Bind(localNodeOptions);

builder.Services.AddSingleton<Harborline.Api.Kernel.Sync.Network.INetworkTrustState>(
    new Harborline.Api.Kernel.Sync.Network.ConfiguredNetworkTrustState(localNodeOptions.Sync.NetworkTrust));
Console.WriteLine($"[local-node-host] network trust: {localNodeOptions.Sync.NetworkTrust} (explicit configuration).");

// Ticket 047 — resolve every machine-fixed filesystem path from the SAME durable install identity.
// The first/default install atomically claims the historical paths in place; later co-resident installs
// resolve beneath installs/{identity}. An explicit LocalNode:DataDirectory remains an operator override.
InstallFootprint installFootprint;
IInstallIdentityProvider installIdentityProvider;
IInstallFootprintProvider installFootprintProvider;
// Test-harness only: set by the in-repo host tests on the process they start. No operator or script
// surface, so the pre-rename spelling is not kept.
var isolatedInstallFootprintRoot = installFootprintRootOverride ?? Environment.GetEnvironmentVariable(
    "HARBORLINE_TEST_INSTALL_FOOTPRINT_ROOT");
var configuredDataRoot = builder.Configuration["LocalNode:DataDirectory"];
var installFootprintRoot = !string.IsNullOrWhiteSpace(isolatedInstallFootprintRoot)
    ? isolatedInstallFootprintRoot
    : string.IsNullOrWhiteSpace(configuredDataRoot) ? null : configuredDataRoot;
var installIdentityPath = !string.IsNullOrWhiteSpace(isolatedInstallFootprintRoot)
    ? Path.Combine(isolatedInstallFootprintRoot, "install.identity")
    : string.IsNullOrWhiteSpace(configuredDataRoot)
        ? null
        : Path.Combine(
            configuredDataRoot,
            "install-identities",
            Path.GetFileName(InstallIdentityPaths.GetDefaultIdentityFilePath()));
using (var footprintServices = new ServiceCollection()
    .AddHarborlineInstallFootprint(installFootprintRoot, installIdentityPath)
    .BuildServiceProvider())
{
    installIdentityProvider = footprintServices.GetRequiredService<IInstallIdentityProvider>();
    installFootprintProvider = footprintServices.GetRequiredService<IInstallFootprintProvider>();
    installFootprint = installFootprintProvider
        .GetInstallFootprintAsync(CancellationToken.None)
        .AsTask()
        .GetAwaiter()
        .GetResult();
    localNodeOptions.ApplyInstallFootprint(
        installFootprint,
        dataDirectoryIsConfigured: builder.Configuration["LocalNode:DataDirectory"] is not null);
}
builder.Services.AddSingleton(installIdentityProvider);
builder.Services.AddSingleton(installFootprintProvider);
builder.Services.PostConfigure<LocalNodeOptions>(options =>
    options.ApplyInstallFootprint(
        installFootprint,
        dataDirectoryIsConfigured: builder.Configuration["LocalNode:DataDirectory"] is not null));

// DEV / troubleshooting COMMS DIAGNOSTICS (default-on pre-release). Register the single CommsDiagnostics seam from
// the resolved LocalNode:Diagnostics:CommsDiagnosticLogging flag (default true) + a host logger. ADDITIVE: it only
// emits structured [comms-diag] lines at the comms/enrollment/peer-trust/roster decision points that otherwise hide
// the real reason — it NEVER affects enrollment/admission/trust control flow and NEVER logs a secret value (lengths
// + id prefixes + teamIds + party ids + boolean decisions only). It flips OFF for the production release by setting
// LocalNode:Diagnostics:CommsDiagnosticLogging=false.
var commsDiagEnabled = localNodeOptions.Diagnostics.CommsDiagnosticLogging;
builder.Services.AddSingleton(sp => new Harborline.Api.LocalNodeHost.Diagnostics.CommsDiagnostics(
    sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
        ?.CreateLogger("Harborline.Api.LocalNodeHost.CommsDiagnostics"),
    commsDiagEnabled));
// The peer-trust observer bridge — resolved by the per-team registrar (from the OUTER provider) to wrap the trust
// policy in the behaviour-neutral diagnostic decorator, surfacing PEER_UNTRUSTED-vs-trusted in the console. When
// the flag is OFF the CommsDiagnostics no-ops, so this bridge is inert in the production-release posture.
builder.Services.AddSingleton<Harborline.Api.Kernel.Sync.Handshake.IPeerTrustDiagnosticObserver>(sp =>
    new Harborline.Api.LocalNodeHost.Diagnostics.CommsPeerTrustObserver(
        sp.GetRequiredService<Harborline.Api.LocalNodeHost.Diagnostics.CommsDiagnostics>()));
Console.WriteLine(
    $"[local-node-host] Comms diagnostics: CommsDiagnosticLogging={(commsDiagEnabled ? "ON" : "OFF")} "
    + "(dev/troubleshooting flag, DEFAULT-ON pre-release; flips OFF for the production release; LocalNode:"
    + "Diagnostics:CommsDiagnosticLogging). Emits structured [comms-diag] lines for enrollment/admission/peer-trust/"
    + "roster decisions — NO secret values (lengths + id prefixes + teamIds + party ids only).");

// Root seed: either an injected per-tenant seed (Bridge supervisor path) or a
// keystore-backed IRootSeedProvider (direct-install path).
//
// W5.2 stop-work #1: when Bridge spawns a tenant child, it HKDF-derives a
// per-tenant 32-byte seed from its own install-level root seed and passes the
// hex string via LocalNode__RootSeedHex. The child honors it and skips the
// keystore lookup entirely — two tenants on one Bridge host therefore derive
// cryptographically independent Ed25519 + SQLCipher keys.
//
// Direct installs (Anchor, standalone dotnet run) leave RootSeedHex null and
// fall back to the keystore path: the provider generates a 32-byte RNG seed
// on first launch and persists it to the platform keystore (Windows DPAPI
// today; mac/Linux Wave-2 stubs). Blocking on GetAwaiter().GetResult() is
// acceptable here — no ambient SynchronizationContext, single-threaded
// bootstrap, well before any hosted service begins its StartAsync.
byte[] rootSeed;
if (!string.IsNullOrWhiteSpace(localNodeOptions.RootSeedHex))
{
    // Parse + validate the injected hex seed. Convert.FromHexString throws
    // FormatException on malformed input — we catch and rethrow with a clearer
    // message so misconfigured environments fail the host fast.
    try
    {
        rootSeed = Convert.FromHexString(localNodeOptions.RootSeedHex);
    }
    catch (FormatException ex)
    {
        throw new InvalidOperationException(
            "LocalNode:RootSeedHex is set but is not valid hex. Expected a 64-character hex string (32 bytes).",
            ex);
    }
    if (rootSeed.Length != 32)
    {
        throw new InvalidOperationException(
            $"LocalNode:RootSeedHex decoded to {rootSeed.Length} bytes; expected exactly 32.");
    }

    // SECURITY: log the seed LENGTH only — NEVER the seed value/hex. Promoting
    // this to log the actual seed would leak the root key material (sec-eng
    // advisory, council-verdict-sec-eng-2026-06-13).
    Console.WriteLine(
        $"[local-node-host] Using injected root seed (length={rootSeed.Length}B) — keystore bypass enabled.");
}
else
{
    rootSeed = InstallRootSeedResolver.ResolveAsync(
            installIdentityProvider,
            installFootprintProvider,
            installFootprint.KeystoreDirectory,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();
}

var keyHierarchy = LocalNodeKeyHierarchy.Resolve(localNodeOptions, rootSeed);
byte[]? legacyPerTeamStoreRoot = keyHierarchy.PerTeamKvStoreIsEnvelopeExtended
    ? keyHierarchy.IdentityRootKey.ToArray()
    : null;

// Derive the root Ed25519 identity from the seed. Kernel-security's
// IEd25519Signer is stateless, so we instantiate it directly here rather
// than spin up a mini-provider to resolve it.
//
// The SQLCipher key derivation is hoisted out of the inner block so the EF
// data-plane registration below can share the same instance (it derives the
// relational-store DEK; the registrar/activator derive per-team KV-store keys).
//
// The root public-key FINGERPRINT is likewise hoisted out: ADR 0160 R3-H binds the
// installation's stable identity to a versioned root Ed25519 fingerprint, and the
// founder-bootstrap ceremony registered far below needs the same one this block
// derives — the node must not carry two answers to "which root is this install".
string installationRootPublicKeyFingerprint;
var sqlCipherKeyDerivation = new SqlCipherKeyDerivation();
{
    var signer = new Ed25519Signer();
    var (rootPublicKey, rootPrivateKey) = signer.GenerateFromSeed(rootSeed);
    installationRootPublicKeyFingerprint =
        Harborline.Api.Foundation.Crypto.KeyFingerprint.FromPublicKey(rootPublicKey).Value;

    // Multi-device INC-5 — the node id MUST be a hex string. NodeIdentity.NodeIdBytes
    // does Convert.FromHexString(NodeId) when the HELLO frame is built; a non-hex id
    // throws FormatException that the gossip round loop SILENTLY classifies as a
    // generic GossipError (peers connect, but NO contact crosses — the bug the
    // cross-machine harness run surfaced, bug-syncharness-nodeid-hex). Deriving the
    // id from Convert.ToHexString of the public key is hex by construction; this
    // assertion makes a future regression (e.g. someone swapping in a GUID/label id)
    // fail LOUDLY at composition time instead of silently breaking sync at runtime.
    var nodeId = Convert.ToHexString(rootPublicKey.AsSpan(0, 16)).ToLowerInvariant();
    try
    {
        _ = Convert.FromHexString(nodeId);
    }
    catch (FormatException ex)
    {
        throw new InvalidOperationException(
            $"Derived node id '{nodeId}' is not valid hex — NodeIdentity.NodeIdBytes would throw " +
            "during the sync HELLO and silently break cross-machine sync (bug-syncharness-nodeid-hex). " +
            "The node id must be hex (the team key is the trust anchor, not the node id).",
            ex);
    }
    var rootIdentity = new NodeIdentity(
        NodeId: nodeId,
        PublicKey: rootPublicKey,
        PrivateKey: rootPrivateKey);

    // Pure, side-effect-free KDF utilities. Constructed directly so the default
    // registrar closure captures the exact derivation instance the activator also
    // observes later.
    var subkeyDerivation = new TeamSubkeyDerivation(signer);

    // ADR 0115 — gate the per-team LISTENING sync transport (inbound peer
    // sync). Default semantics: a multi-team install listens for peers; a
    // single-device single-team node runs outbound-only. The Tauri sidecar
    // boot contract sets LocalNode:MultiTeam:Enabled=false, so the embedded
    // node defaults to outbound-only — which sidesteps the macOS 104-char
    // sun_path limit on the listening Unix-domain socket (bug-2847). An
    // explicit LocalNode:Sync:ListenForPeers wins over the default when set.
    var listenForPeers = localNodeOptions.Sync.ListenForPeers
        ?? (localNodeOptions.MultiTeam?.Enabled ?? true);
    Console.WriteLine(
        $"[local-node-host] Peer-sync listening transport: {(listenForPeers ? "ENABLED" : "outbound-only (single-device)")}.");

    // Multi-device INC-5 — the cross-machine LISTEN bind. When listening is on,
    // resolve the per-team transport's bind endpoint from LocalNode:Sync:BindAddress.
    // An explicit value (e.g. "0.0.0.0:7473") makes the listener LAN-routable so a
    // remote peer can reach it; the daemon is trust-gated + DoS-capped, so binding
    // 0.0.0.0 is the intended production shape. When unset, the registrar keeps the
    // safe ephemeral loopback default (tcp://127.0.0.1:0) — the Bridge multi-tenant
    // posture, where no remote peer dials in. Only consulted when listenForPeers.
    var listenBindEndpoint = listenForPeers && !string.IsNullOrWhiteSpace(localNodeOptions.Sync.BindAddress)
        ? SyncEndpoint.NormalizeTcpEndpoint(localNodeOptions.Sync.BindAddress!)
        : null;
    if (listenBindEndpoint is not null)
    {
        Console.WriteLine(
            $"[local-node-host] Cross-machine sync LISTEN bind: {listenBindEndpoint} (trust-gated + DoS-capped).");
    }

    // Snappy-convergence follow-up — the periodic anti-entropy round interval. A
    // local edit triggers an immediate authenticated push (push-on-change,
    // ContactCrdtProjection → IGossipDaemon.TriggerPushAsync), so the periodic round
    // is only the missed-push backstop. The host default (5s) is snappier than the
    // library's paper-§6.1 30s; an explicit LocalNode:Sync:RoundIntervalSeconds wins
    // (e.g. 30 to restore the paper cadence). null is passed through as the host
    // default so the Bridge multi-tenant path — which does not call this registrar
    // helper — is unaffected.
    var roundIntervalSeconds = localNodeOptions.Sync.RoundIntervalSeconds
        ?? SyncTransportOptions.DefaultRoundIntervalSeconds;
    Console.WriteLine(
        $"[local-node-host] Gossip anti-entropy round interval: {roundIntervalSeconds}s " +
        "(push-on-change is the primary path; this is the backstop cadence).");

    builder.Services
        .AddHarborlineKernelRuntime()              // plugin registry + INodeHost          (Wave 1.1)
        .AddHarborlineKernelSecurity()             // Ed25519 + X25519 + KDFs              (Wave 1.6)
        .AddHarborlineRootSeedProvider()           // keystore-backed install seed         (Wave 6.7.A)
        .AddHarborlineResourceGovernor()           // per-tick gossip cap                  (Wave 6.4)
        // Container-bridge (ONR survey 2026-06-19): the per-team daemon this registrar
        // composes in the CHILD container now bridges its IDeltaProducer/IDeltaSink to the
        // install-level delta router (AddNodeContacts → AddHarborlineDeltaRouter, OUTER
        // container) — so the production per-team daemon resolves the REAL contacts
        // projection, not the Noop. The bridge resolves the router via the outer
        // IServiceProvider the widened TeamServiceRegistrar delegate now threads through.
        .AddHarborlineDefaultTeamRegistrar(        // per-team service wiring              (Wave 6.3.E)
            dataDirectory: localNodeOptions.DataDirectory,
            rootIdentity: rootIdentity,
            subkeyDerivation: subkeyDerivation,
            sqlCipherKeyDerivation: sqlCipherKeyDerivation,
            listenForPeers: listenForPeers,
            listenBindEndpoint: listenBindEndpoint,
            roundIntervalSeconds: roundIntervalSeconds)
        .AddHarborlineTeamStoreActivator(
            keyHierarchy.AtRestRootKey,
            legacyPerTeamStoreRoot);              // recoverable per-team store + legacy re-key
}

builder.Services.AddLocalNodeTeamSwitcher();

// ADR 0134 P1b-2/P2 — the CANONICAL node principal signer. Built ONCE from the same
// resolved root seed as the node's Ed25519/SQLCipher identity above, wired into the
// foundation IOperationSigner (CanonicalJson/SignedOperation form, #1254). The
// HostedCurrentPrincipalSignatureApiEndpoint resolves THIS to sign the host-resolved
// current OS-user principal on the loopback signing route — producing an envelope the
// TS verifier (apps/capability-host signed-principal.ts) accepts with the node public key pinned.
// SECURITY: the secret key lives ONLY inside the owned KeyPair (NSec zeroes on dispose);
// it is NEVER exported/logged/handed to the Harborline App — the Harborline App reaches a SIGNATURE by
// calling the loopback route (inc-4), never the key. Singleton so the node public key
// (trust anchor) is stable for the process lifetime.
builder.Services.AddSingleton(new Harborline.Api.LocalNodeHost.Health.NodePrincipalSigner(rootSeed));
// Customer-zero dogfood deploy (2026-07-06) found this host-composition-root gap: the comment
// above (and the identical claim at line ~962/~1318 below) has always asserted "the host already
// registered IOperationSigner (NodePrincipalSigner...)" but no such registration ever existed —
// only the CONCRETE NodePrincipalSigner type was added to the container. Any consumer that
// resolves the foundation IOperationSigner interface directly (e.g. the dynamic-forms engine's
// AddHarborlineRecoveryCoordinator, which only materializes lazily on first real multi-team
// bootstrap) throws "No service for type 'IOperationSigner' has been registered" and the host
// never starts. This single line is the fix the comments already describe: expose the signer
// NodePrincipalSigner already built (Harborline.Api.Foundation.Crypto.Ed25519Signer over the same
// root-seed-derived KeyPair) under its interface, changing no signing semantics.
builder.Services.AddSingleton<Harborline.Api.Foundation.Crypto.IOperationSigner>(
    sp => sp.GetRequiredService<Harborline.Api.LocalNodeHost.Health.NodePrincipalSigner>().Signer);

builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.BackupRestore.IRosterRehostGrantProvider,
    Harborline.Api.LocalNodeHost.BackupRestore.SignedRosterRehostGrantProvider>();
builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.BackupRestore.NodeRehostService>();

// ── Enrollment Phase B — PRODUCTION trust roster (closes #1277-B1 end-to-end). ───────────────────────
//
// The install-level NodeTeamRoster, seeded HERE with the GENESIS self-admission: the single-office
// operator admits its OWN (party, principal-key) pair, signed by the node's canonical principal signer
// (NodePrincipalSigner). This is the #1288 M1 carry-forward — Phase A built the forge-proof mechanism but
// left it UNWIRED; seeding the roster here makes it LIVE:
//
//   * AddNodeComms wires the comms merge gate's rosterBinding to this roster's ForgeProofBinding, so a
//     forged-foreign-party peer message is DROPPED on the real merge path in production (not just a unit
//     test). #1277-B1b closed end-to-end.
//   * The genesis (self) is in its OWN roster, so a fresh SINGLE-USER node forge-proves its own messages
//     and is NOT bricked by an empty roster.
//   * Registered ALSO as ITrustedMemberKeyProvider so the per-team MemberSetTrustPolicy (registrar) unions
//     admitted-member TRANSPORT subkeys with its own-subkey floor. On a single-user node the provider is
//     empty (no admitted peers); the registrar's own team subkey is the trust floor (same posture the
//     retired SharedRootTrustPolicy gave) — so single-user trust is non-regressing and never bricks.
//
// GAP #2 — the genesis party id is the COMMS AUTHOR id (HostedCommsApiEndpoint stamps Current.GenesisPartyId
// as every message's authorPartyId). It MUST be PER-NODE-DISTINCT, or two nodes would both stamp the old
// install-constant "local" (distinct signing keys but the SAME party id — the two-user-test residual). So
// the founder party id is derived as os:<user>#<node-key8>: the OS-user principal (the scope-fence "the
// OS-user is the enrolled genesis member" — mirrors CurrentPrincipalSignatureRoutes' os:<user> format) made
// distinct by an 8-hex suffix of the NODE PRINCIPAL public key. The signing key is distinct per root by
// construction, so the suffix guarantees distinctness even when two installs share an OS username; and
// because the genesis self-admission binds THIS party id to THIS signer's key, the comms author↔signing-key
// consistency the forge-proof gate checks holds by construction (the operator's own messages pass). This is
// the ATTRIBUTION/party axis only — the financial actor axis (ActiveTeamAuthorizationContext.LocalUserId, used by the
// soft-close bypass + ActiveTeamAuthorizationContext + the membership edge) is a SEPARATE concern, untouched.
//
// The genesis team id uses the configured LocalNode:TeamId when present (else a synthesized stable value);
// it scopes the signed AdmissionRecord. The party→key binding the comms gate consumes does not depend on
// the team id matching, so a later team-id reconciliation does not break attribution.
//
// C5 — capture the genesis team id + the genesis (= comms author) party id out of the block so the
// roster-bound DM key provider (below, AddRosterBoundDmKeyProvider) + the founder's own DM key seeding can
// reference them. They are assigned inside the block where they are computed.
string capturedGenesisTeamId = string.Empty;
string capturedGenesisPartyId = string.Empty;
var sqlitePath = Path.Combine(
    localNodeOptions.DataDirectory ?? Path.Combine(AppContext.BaseDirectory, "data"), "local-node.db");
void AddInstallStore(IServiceCollection services, bool pooling = true)
{
    if (!string.IsNullOrWhiteSpace(localNodeOptions.StoreDekHex))
        services.AddSqlCipherLocalNodeDbContextWithStoreDek(keyHierarchy.AtRestRootKey.Span, sqlitePath, pooling);
    else
        services.AddSqlCipherLocalNodeDbContext(rootSeed, sqlitePath, sqlCipherKeyDerivation, pooling);
}
{
    var genesisSigner = new Harborline.Api.LocalNodeHost.Health.NodePrincipalSigner(rootSeed);
    // SINGLE SOURCE OF TRUTH for the genesis team id (GenesisTeamId.Resolve): the configured LocalNode:TeamId
    // when present, else the seed-derived fallback (rootSeed[0..16] → Guid). The SAME helper resolves the
    // bootstrap's active team (MultiTeamBootstrapHostedService consumes the GenesisTeamIdProvider registered
    // below), so daemon-active-team == this roster-genesis-team == the /admission/invites trust-anchor team —
    // the divergence the cross-machine verify caught (the bootstrap minted a RANDOM Guid.NewGuid() active team
    // while the invite advertised this seed-derived genesis, so a remote joiner joined a team the daemon was
    // not gossiping on). The override stays idempotent — both paths resolve the configured id identically.
    // The MultiTeam section is passed so the multi-team branch cannot diverge from this genesis
    // (earlier repository ticket #3450): it activates TeamBootstraps[] directly and never consulted this helper, so with
    // multi-team on and LocalNode:TeamId unset the node served one team while the signed roster genesis and
    // the invite anchor were seeded under another. Configured id still wins; single-team is untouched.
    var resolvedGenesisTeam = GenesisTeamId.Resolve(
        rootSeed, localNodeOptions.TeamId, localNodeOptions.MultiTeam);
    var genesisTeamId = resolvedGenesisTeam.Value;
    builder.Services.AddSingleton(new GenesisTeamIdProvider(resolvedGenesisTeam));
    var genesisVerifier = new Harborline.Api.Foundation.Crypto.Ed25519Verifier();
    // Ticket 296: resolve the signed durable identity before any hosted service can publish.
    // A renamed account is not a new principal: only a tenant without a log consults the shell.
    MemberRoster? storedGenesisRoster;
    // Ticket 294 slice 2a — the party key minted for a first boot with no stored genesis. It is the
    // founder's CANONICAL TENANT PRINCIPAL id, the one key the grant store, the closure reader and the
    // admin surface already speak (see NodeGatePrincipal); there is no shell-derived fallback.
    string? mintedGenesisPartyId = null;
    var genesisStoreServices = new ServiceCollection();
    // This short-lived probe owns its native connections: disposing its contexts must close
    // the file even when later composition refuses startup, before a host owns the store.
    AddInstallStore(genesisStoreServices, pooling: false);
    await using (var genesisStore = genesisStoreServices.BuildServiceProvider())
    {
        // Ticket 294 slice 2b — signed roster records cannot be migrated honestly: changing PartyId would
        // invalidate the admission signature. Inspect this install tenant once on the probe composition before
        // any genesis can publish, and require a re-found current-format log rather than mixing key spaces.
        var rosterFactory = genesisStore.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using (var rosterStore = await rosterFactory.CreateDbContextAsync(CancellationToken.None))
        {
            await rosterStore.Database.MigrateAsync(CancellationToken.None).ConfigureAwait(false);
            var maximumWireFormat = await rosterStore.RosterRecords.AsNoTracking()
                .Where(row => row.TeamId == genesisTeamId.ToString("D"))
                .Select(row => (int?)row.WireFormatVersion)
                .MaxAsync(CancellationToken.None).ConfigureAwait(false);
            if (maximumWireFormat is <= 3)
                throw new InvalidOperationException(GenesisStartupMessages.RosterWireFormatPre294);
        }

        storedGenesisRoster = await DurableGenesisIdentity.ReadAsync(
            rosterFactory,
            genesisTeamId, genesisSigner.Signer.IssuerId, genesisVerifier, CancellationToken.None);

        if (storedGenesisRoster is null)
        {
            // The founder's canonical tenant principal is DETERMINISTIC in the genesis tenant and the
            // founder ceremony correlation — FounderTenantMembershipAttachService derives the identical
            // value when it writes the founder's People binding, so the roster edge and the grant store
            // are born on one key instead of being reconciled afterwards.
            var genesisTenant = ActiveTeamTenantContext.ProjectTenantId(resolvedGenesisTeam);
            var founderPrincipal = FounderTenantMembershipAttachService.DerivePrincipal(
                genesisTenant, InstallationFounderBootstrapCeremony.CorrelationId);

            // The refusal. A missing genesis roster is normal ONLY before the founder bootstrap ceremony
            // has run — the founder attach then adopts this very principal as the install's canonical
            // party. Once the ceremony HAS completed and this node still has no genesis of its own, the
            // install's founder identity is half-written; minting anything here would put the roster on a
            // key the grant store never reads, which is the defect that produced two key spaces. There is
            // no shell-derived fallback: refuse, name the code, publish nothing.
            //
            // The probe cannot ask ICanonicalPrincipalPartyReader: this short-lived composition registers
            // the store graph only, not the People entity model, so a Party read here throws rather than
            // refusing. It does not need to — the reader echoes the principal it is given
            // (NodeEfCanonicalPrincipalPartyReader), so it can never answer a DIFFERENT key than the
            // derivation above; it only answers whether a binding exists yet. This check answers the same
            // question one layer earlier and on a store the probe already owns.
            await using var identity = await genesisStore
                .GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>()
                .CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            await identity.Database.MigrateAsync(CancellationToken.None).ConfigureAwait(false);
            if (await identity.InstallationAccessGrants.AsNoTracking().AnyAsync(
                    row => row.AuditCorrelationId == InstallationFounderBootstrapCeremony.CorrelationId,
                    CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException(GenesisStartupMessages.PartyUnresolved);
            }

            mintedGenesisPartyId = founderPrincipal.Value;
        }
    }

    // Keep the install's admitted party when its configured tenant was founded by another node.
    var genesisPartyId = storedGenesisRoster?.Members
        .First(member => member.PublicKey.Equals(genesisSigner.Signer.IssuerId)).PartyId
        ?? mintedGenesisPartyId!;

    // C5 (DM key-substitution fix) — derive the founder's OWN team-scoped DM PUBLIC key (HKDF(root, genesisTeamId)
    // over the DM domain) BEFORE the genesis self-admission, so it can be SIGNED INTO the genesis admission envelope
    // (StableGenesis takes the DM key). This makes the founder's (party → DM-pubkey) binding forge-proof from the
    // chain root — a roster writer cannot substitute the founder's DM key (the substituted record fails signature
    // verification). The DM key is deterministic from the root seed, so the genesis record stays restart-stable.
    var ownGenesisDmKey = Harborline.Api.LocalNodeHost.Enrollment.NodeDmKeyDerivation
        .DeriveDmPublicKey(rootSeed, genesisTeamId.ToString("D"));
    var ownGenesisDmKeyB64 = Harborline.Api.Foundation.Crypto.PrincipalId.FromBytes(ownGenesisDmKey).ToBase64Url();

    var genesisRoster = storedGenesisRoster ?? MemberRoster.StableGenesis(
        teamId: genesisTeamId,
        founderPartyId: genesisPartyId,
        founderSigner: genesisSigner.Signer,
        verifier: genesisVerifier,
        founderDmPublicKey: ownGenesisDmKeyB64);
    var nodeRoster = new Harborline.Api.LocalNodeHost.Enrollment.NodeTeamRoster(genesisRoster);
    builder.Services.AddSingleton(nodeRoster);
    builder.Services.AddSingleton<Harborline.Api.Kernel.Sync.Handshake.ITrustedMemberKeyProvider>(nodeRoster);

    // C5 — capture the genesis team id + party id for the roster-bound DM key provider (registered after AddNodeComms).
    capturedGenesisTeamId = genesisTeamId.ToString("D");
    capturedGenesisPartyId = genesisPartyId;

    // C5 — (a) seed the founder's own DM PUBLIC key into the live roster's DM-key map (the own-key floor — so a fresh
    // single-user node holds its own DM key before any peer admit; the roster also surfaces it off the SIGNED genesis
    // admission via DmPublicKeyOf), and (b) register a NodeOwnDmKey holder. The wire DM field on the synced genesis
    // record is now derived from the SIGNED genesis admission (RosterRecordCrdtState.FromAdmission), so every
    // converging member harvests the founder's forge-proof DM public key from the chain-validated converged roster
    // (roster-derived DM keys). The PRIVATE half stays node-secret (in the RosterDmKeyResolver, derived from the
    // same root).
    {
        nodeRoster.SetOwnDmPublicKey(genesisPartyId, ownGenesisDmKey);
        builder.Services.AddSingleton(
            new Harborline.Api.LocalNodeHost.Enrollment.NodeOwnDmKey(ownGenesisDmKey));
    }
    // INFO-2 (≥3-node mesh) — the founder's OWN team-scoped transport key for the genesis team, derived the SAME
    // way the per-team registrar's own-subkey floor is (HKDF(root, genesisTeamId)). Reconstructed here from the SAME
    // rootSeed the per-team registrar uses (this is a sibling block; rootIdentity/subkeyDerivation from the boot
    // block above are out of scope — mirror the joiner-half reconstruction below), so the key is byte-identical to
    // what the registrar's INodeIdentityProvider presents on the genesis-team wire HELLO.
    // RosterSyncBootstrapHostedService STAMPS it onto the genesis self-admission it publishes to the synced roster
    // doctype, so every converging member harvests the founder's transport key from the converged roster
    // (roster-derived trust — the chain root).
    {
        var ownTransportSigner = new Harborline.Api.Kernel.Security.Crypto.Ed25519Signer();
        var (ownTransportRootPub, ownTransportRootPriv) = ownTransportSigner.GenerateFromSeed(rootSeed);
        var ownTransportNodeId = Convert.ToHexString(ownTransportRootPub.AsSpan(0, 16)).ToLowerInvariant();
        var ownTransportRootIdentity = new NodeIdentity(ownTransportNodeId, ownTransportRootPub, ownTransportRootPriv);
        var ownTransportSubkeyDerivation = new TeamSubkeyDerivation(ownTransportSigner);
        var ownGenesisTransportKey = Harborline.Api.Kernel.Sync.Identity.TeamScopedNodeIdentity
            .Derive(ownTransportRootIdentity, genesisTeamId.ToString("D"), ownTransportSubkeyDerivation).PublicKey;
        builder.Services.AddSingleton(
            new Harborline.Api.LocalNodeHost.Enrollment.NodeOwnTransportKey(ownGenesisTransportKey));
    }
    // The admission coordinator + single-use invite store — the peer-to-peer admission protocol seam the
    // Harborline App QR-scan / invite-entry UI (FED follow-on) drives. Registered so the admission surface can
    // resolve them; the protocol itself is host-agnostic (foundation-identity-atlas).
    //
    // DURABLE token store (cerebrum [2026-06-21] in-memory admission token store follow-on). The store now
    // persists minted invites to the recoverable SQLCipher store (NodeLocalAdmissionDbContext) so a minted
    // invite SURVIVES a node restart within its TTL — the in-memory v1 lost invites on every host recycle.
    // The IDbContextFactory it depends on is registered (unconditionally, both key paths) further below by
    // AddSqlCipherLocalNodeDbContext*; DI is lazy, so registering the store here ahead of its factory is fine
    // (both are in the container before builder.Build()). Single-use + TTL + fail-closed semantics are
    // unchanged from the in-memory store.
    builder.Services.AddSingleton<Harborline.Api.Foundation.IdentityAtlas.Enrollment.IAdmissionTokenStore>(sp =>
        new Harborline.Api.LocalNodeHost.Data.Admission.DurableAdmissionTokenStore(
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<
                Harborline.Api.LocalNodeHost.Data.Admission.NodeLocalAdmissionDbContext>>()));
    builder.Services.AddSingleton(sp => new Harborline.Api.Foundation.IdentityAtlas.Enrollment.AdmissionCoordinator(
        sp.GetRequiredService<Harborline.Api.Foundation.Crypto.IOperationVerifier>(),
        sp.GetRequiredService<Harborline.Api.Foundation.IdentityAtlas.Enrollment.IAdmissionTokenStore>(),
        sp.GetRequiredService<TimeProvider>()));

    // ── MTW-2 #3167 — the WEB-ADMITTED PAIRING path (mode-exclusive with plain enrollment per R2). ────────
    // Durable pairing-binding store (SQLCipher; the binding survives a restart within the token TTL), the
    // pin-re-verifying atlas bridge, the "connect your device" mint (the front door — wired into the WebSession
    // endpoint), the R2 web-plane predicate, the R5 rate limiter, and the token-gated pairing admitter (R1.3 — a
    // DISTINCT signer instance, NOT the standing WireEnrollmentAdmitter signer, reachable only through a successful
    // redemption). All bundled into PairingRedeemDispatch, injected into HostedAdmissionApiEndpoint (the redeem
    // route). The pairing admitter party = this node's genesis founder (the same admitter the plain path uses).
    var pairingAdmitterPartyId = genesisPartyId;
    builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.Data.Identity.IWebPairingInviteBindingStore>(sp =>
        new Harborline.Api.LocalNodeHost.Data.Admission.DurableWebPairingInviteBindingStore(
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<
                Harborline.Api.LocalNodeHost.Data.Admission.NodeLocalAdmissionDbContext>>()));
    builder.Services.AddSingleton(sp => new Harborline.Api.LocalNodeHost.Data.Identity.WebAdmittedMemberAtlasBridge(
        sp.GetRequiredService<Harborline.Api.Foundation.Authorization.ICanonicalPrincipalPartyReader>(),
        sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<
            Harborline.Api.LocalNodeHost.Data.Search.NodeLocalSearchDbContext>>(),
        sp.GetRequiredService<Harborline.Api.Foundation.IdentityAtlas.Enrollment.AdmissionCoordinator>(),
        sp.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Identity.IWebPairingInviteBindingStore>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<Harborline.Api.Blocks.AccessGrant.IAuthorizationClosureReader>()));
    builder.Services.AddSingleton(sp => new Harborline.Api.LocalNodeHost.Data.Identity.WebAdmittedMemberPairingTokenMint(
        sp.GetRequiredService<Harborline.Api.Foundation.IdentityAtlas.Enrollment.AdmissionCoordinator>(),
        sp.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Identity.IWebPairingInviteBindingStore>()));
    builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.Data.Identity.IWebPlaneAdmissionState>(sp =>
        new Harborline.Api.LocalNodeHost.Data.Identity.LiveWebPlaneAdmissionState(
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<
                Harborline.Api.LocalNodeHost.Data.Search.NodeLocalSearchDbContext>>(),
            sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton(sp => new Harborline.Api.LocalNodeHost.Enrollment.PairingRedeemRateLimiter(
        sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton(sp =>
    {
        // R1.3 — a DISTINCT pairing admitter signer (same founder key, distinct instance), NOT the standing
        // WireEnrollmentAdmitter._admitterSigner. Held privately by the gated admitter; reachable only via a redeem.
        var distinctPairingSigner =
            new Harborline.Api.LocalNodeHost.Health.NodePrincipalSigner(rootSeed).Signer;
        var gatedAdmitter = new Harborline.Api.LocalNodeHost.Enrollment.PairingTokenGatedAdmitter(
            sp.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Identity.WebAdmittedMemberAtlasBridge>(),
            sp.GetRequiredService<Harborline.Api.LocalNodeHost.Enrollment.NodeTeamRoster>(),
            distinctPairingSigner,
            pairingAdmitterPartyId,
            sp.GetRequiredService<Harborline.Api.Foundation.Crypto.IOperationVerifier>(),
            sp.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Roster.RosterCrdtProjection>(),
            sp.GetRequiredService<Harborline.Api.Foundation.IdentityAtlas.Enrollment.IEnrollmentCompensatingControlRecorder>(),
            sp.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Identity.IWebPairingInviteBindingStore>(),
            sp.GetRequiredService<Harborline.Api.Kernel.Runtime.Teams.ITeamContextFactory>(),
            sp.GetService<Harborline.Api.LocalNodeHost.Diagnostics.CommsDiagnostics>());
        return new Harborline.Api.LocalNodeHost.Enrollment.PairingRedeemDispatch(
            gatedAdmitter,
            sp.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Identity.IWebPairingInviteBindingStore>(),
            sp.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Identity.IWebPlaneAdmissionState>(),
            sp.GetRequiredService<Harborline.Api.LocalNodeHost.Enrollment.PairingRedeemRateLimiter>(),
            sp.GetRequiredService<Harborline.Api.Kernel.Runtime.Teams.IActiveTeamAccessor>());
    });

    Console.WriteLine(
        $"[local-node-host] Enrollment Phase B: trust roster SEEDED (genesis self-admission, party '{genesisPartyId}') "
        + "— comms author = this genesis member (per-node-distinct, gap #2); merge gate is FORGE-PROOF "
        + "(rosterBinding live); MemberSetTrustPolicy active (own-subkey floor + admitted peers).");

    // ── TWO-SIDED WIRE ENROLLMENT — the JOINER half, WIRED LIVE (cerebrum [2026-06-21]). ──────────────────
    //
    // The shipping admission was ADMITTER-NODE-LOCAL: A's /redeem admitted B into A's roster + trust map but the
    // admission never reached B, so two fresh nodes never established mutual trust over the wire (PEER_UNTRUSTED).
    // The JOINER half (NodeWireEnrollmentClient) closes it: B derives its A-team-scoped transport key, signs +
    // sends the enroll request to A's /redeem, validates A's response against the out-of-band invite anchor, and
    // ADOPTS A's team into THIS node's NodeTeamRoster (+ wires A's transport key into MemberSetTrustPolicy). The
    // A-side response (the bootstrap payload) is built by the redeem route above; this is the B-side runtime that
    // consumes it. Registered LIVE in the shipping graph (not test-only — the recurring "mechanism built but not
    // wired" lesson): the Harborline App invite-entry UI (FED follow-on) drives EnrollAsync over the HTTP transport.
    //
    // rootIdentity + subkeyDerivation are reconstructed here from the SAME rootSeed the per-team registrar uses,
    // so B's derived transport key for A's team is byte-identical to what its adopted team's INodeIdentityProvider
    // will present on the wire HELLO (the #1296-F2 JOINED-team scope).
    {
        var joinerSigner = new Harborline.Api.Kernel.Security.Crypto.Ed25519Signer();
        var (joinerRootPub, joinerRootPriv) = joinerSigner.GenerateFromSeed(rootSeed);
        var joinerNodeId = Convert.ToHexString(joinerRootPub.AsSpan(0, 16)).ToLowerInvariant();
        var joinerRootIdentity = new NodeIdentity(joinerNodeId, joinerRootPub, joinerRootPriv);
        var joinerSubkeyDerivation = new TeamSubkeyDerivation(joinerSigner);

        builder.Services.AddHttpClient();
        builder.Services.AddSingleton(sp => new NodeWireEnrollmentClient(
            rootIdentity: joinerRootIdentity,
            subkeyDerivation: joinerSubkeyDerivation,
            xwingSubkeyDerivation:
                sp.GetRequiredService<Harborline.Api.Kernel.Security.Keys.IXWingSubkeyDerivation>(),
            principalSigner: genesisSigner.Signer,
            selfPartyId: genesisPartyId,
            roster: nodeRoster,
            verifier: sp.GetRequiredService<Harborline.Api.Foundation.Crypto.IOperationVerifier>(),
            // BLOCKER-2 (cerebrum [2026-06-21] DECISIVE cross-machine verify): the synced roster doctype's
            // adopt-time self-supersession — on join, B retracts its OWN superseded genesis team's records from
            // the synced doctype so the dual-genesis injection guard does not fail-close the converged roster.
            rosterSupersession: sp.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Roster.RosterCrdtProjection>(),
            clock: sp.GetRequiredService<TimeProvider>(),
            // 293 s3c: the joiner's own grant view decides each admitter's members:admit on the adopted chain.
            rosterAuthority: sp.GetService<Harborline.Api.Foundation.IdentityAtlas.IRosterAuthority>()));

        // The B-side wire transport to the ADMITTER. #1301 F-1 — PREFER the SOCKET transport over the admitter's
        // 7473 sync listener (the cross-machine path: a remote joiner CAN reach 0.0.0.0:7473, but CANNOT reach the
        // loopback-bound + caller-auth-gated HTTP /redeem route). When LocalNode:Enrollment:AdmitterSyncEndpoint is
        // set, the joiner dials the pre-trust enrollment phase on that socket; otherwise it falls back to the
        // loopback HTTP transport via AdmitterUrl (same-node / local-app use). Operator-supplied; resolved per
        // call so a config change is picked up. Registered even when both are unset (EnrollAsync is only invoked
        // when the operator drives a join, at which point one must be present — a missing endpoint fails closed at
        // call time).
        var admitterTokenCfg = localNodeOptions.Enrollment?.AdmitterToken;
        builder.Services.AddSingleton<IEnrollmentTransport>(sp =>
        {
            var syncEndpoint = localNodeOptions.Enrollment?.AdmitterSyncEndpoint;
            if (!string.IsNullOrWhiteSpace(syncEndpoint))
            {
                // CROSS-MACHINE: the invite-gated pre-trust enrollment over the admitter's 7473 sync listener.
                return new SocketEnrollmentTransport(
                    admitterEndpoint: () => localNodeOptions.Enrollment?.AdmitterSyncEndpoint
                        ?? throw new InvalidOperationException(
                            "Two-sided wire enrollment: LocalNode:Enrollment:AdmitterSyncEndpoint is not "
                            + "configured — cannot reach the admitter's sync listener to join."),
                    logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<SocketEnrollmentTransport>>());
            }

            // FALLBACK: loopback HTTP /redeem (same-node / local-app; not reachable cross-machine).
            return new HttpEnrollmentTransport(
                sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
                admitterBaseUrl: () =>
                {
                    var url = localNodeOptions.Enrollment?.AdmitterUrl;
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        throw new InvalidOperationException(
                            "Two-sided wire enrollment: neither LocalNode:Enrollment:AdmitterSyncEndpoint nor "
                            + "AdmitterUrl is configured — cannot reach the admitter node to join. The operator "
                            + "supplies the admitter's reachable address (prefer AdmitterSyncEndpoint for "
                            + "cross-machine).");
                    }
                    return new Uri(url, UriKind.Absolute);
                },
                sp.GetService<Microsoft.Extensions.Logging.ILogger<HttpEnrollmentTransport>>())
            {
                AdmitterToken = admitterTokenCfg,
            };
        });
        // ── The B-SIDE JOIN ORCHESTRATOR — the PRODUCTION TRIGGER for the joiner half (cerebrum [2026-06-21]
        //    gap #1+#2+#3), WIRED LIVE. ─────────────────────────────────────────────────────────────────────────
        //
        // NodeWireEnrollmentClient.EnrollAsync had ZERO production callers (the cross-machine re-verify blocker):
        // it adopted A's roster but nothing (a) TRIGGERED it from a route, (b) switched B's active team to A's, or
        // (c) rebound B's gossip daemon to its A-team HELLO key — so B kept presenting its OWN-team key and the
        // trusted handshake failed PEER_UNTRUSTED from B's side. NodeEnrollmentJoinService closes all three: it
        // drives EnrollAsync, then MATERIALIZES + activates A's team (so the per-team child container derives B's
        // A-team identity), then SetActiveAsync(A.teamId) — which fires IActiveTeamAccessor.ActiveChanged, which
        // LocalNodeWorker observes to STOP B's old-team daemon + START A's-team daemon (the DAEMON-REBIND, gap #3).
        // The route POST /api/local-node/admission/join (F1-caller-auth'd) drives this; the Harborline App invite-entry UI
        // (FED follow-on) calls that route. Registered live in the shipping graph (the recurring "mechanism built
        // but not wired" lesson — the very thing that left EnrollAsync caller-less).
        builder.Services.AddSingleton(sp => new NodeEnrollmentJoinService(
            client: sp.GetRequiredService<NodeWireEnrollmentClient>(),
            transport: sp.GetRequiredService<IEnrollmentTransport>(),
            teamContextFactory: sp.GetRequiredService<Harborline.Api.Kernel.Runtime.Teams.ITeamContextFactory>(),
            storeActivator: sp.GetRequiredService<Harborline.Api.Kernel.Runtime.Teams.ITeamStoreActivator>(),
            activeTeam: sp.GetRequiredService<Harborline.Api.Kernel.Runtime.Teams.IActiveTeamAccessor>(),
            logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<NodeEnrollmentJoinService>>(),
            diag: sp.GetService<Harborline.Api.LocalNodeHost.Diagnostics.CommsDiagnostics>()));

        var hasSyncEndpoint = !string.IsNullOrWhiteSpace(localNodeOptions.Enrollment?.AdmitterSyncEndpoint);
        var hasHttpUrl = !string.IsNullOrWhiteSpace(localNodeOptions.Enrollment?.AdmitterUrl);
        Console.WriteLine(
            "[local-node-host] Two-sided wire enrollment: JOINER client + JOIN orchestrator WIRED LIVE "
            + "(NodeWireEnrollmentClient + NodeEnrollmentJoinService + "
            + (hasSyncEndpoint ? "SocketEnrollmentTransport over 7473 [cross-machine]" : "HttpEnrollmentTransport [loopback]")
            + ") — this node can ENROLL into a peer team from an invite, ADOPT it, SWITCH its active team, and "
            + "REBIND its gossip daemon to the A-team key (POST /api/local-node/admission/join). Admitter "
            + (hasSyncEndpoint ? "sync endpoint configured" : hasHttpUrl ? "HTTP URL configured" : "NOT set (join inactive until configured)")
            + ".");
    }

    // ── TWO-SIDED WIRE ENROLLMENT — the A-side NETWORK pre-trust enrollment handler, WIRED LIVE (#1301 F-1). ──
    //
    // The A-side admit-and-respond core (WireEnrollmentAdmitter) + the network handler (NodeEnrollmentAdmitter,
    // an IPreTrustEnrollmentHandler). The per-team registrar resolves the handler from THIS outer provider and
    // wires it into the per-team gossip daemon, so the daemon's 7473 sync listener offers an INVITE-GATED pre-trust
    // enrollment phase BEFORE the trusted HELLO — the cross-machine transport the loopback /redeem route could
    // never be (loopback-bound + caller-auth-gated). The handler is the SAME admit core the loopback route uses;
    // AUTH is the invite (single-use / roster-admin-signed / TTL), NOT the F1 session token or roster membership.
    {
        builder.Services.AddSingleton(sp => new WireEnrollmentAdmitter(
            coordinator: sp.GetRequiredService<Harborline.Api.Foundation.IdentityAtlas.Enrollment.AdmissionCoordinator>(),
            roster: nodeRoster,
            admitterSigner: genesisSigner.Signer,
            admitterPartyId: genesisPartyId,
            verifier: sp.GetRequiredService<Harborline.Api.Foundation.Crypto.IOperationVerifier>(),
            projection: sp.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Roster.RosterCrdtProjection>(),
            sodAudit: sp.GetRequiredService<Harborline.Api.Foundation.IdentityAtlas.Enrollment.IEnrollmentCompensatingControlRecorder>(),
            activeTeam: sp.GetRequiredService<Harborline.Api.Kernel.Runtime.Teams.IActiveTeamAccessor>(),
            diag: sp.GetService<Harborline.Api.LocalNodeHost.Diagnostics.CommsDiagnostics>()));

        builder.Services.AddSingleton<Harborline.Api.Kernel.Sync.Handshake.IPreTrustEnrollmentHandler>(sp =>
            new NodeEnrollmentAdmitter(
                sp.GetRequiredService<WireEnrollmentAdmitter>(),
                sp.GetRequiredService<Harborline.Api.LocalNodeHost.Enrollment.PairingRedeemDispatch>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<NodeEnrollmentAdmitter>>(),
                sp.GetService<Harborline.Api.LocalNodeHost.Diagnostics.CommsDiagnostics>()));

        Console.WriteLine(
            "[local-node-host] Two-sided wire enrollment: A-SIDE NETWORK pre-trust enrollment handler WIRED LIVE "
            + "(NodeEnrollmentAdmitter → IPreTrustEnrollmentHandler) — the 7473 sync listener now offers an "
            + "invite-gated pre-trust enrollment phase a REMOTE joiner can reach (#1301 F-1 cross-machine "
            + "transport). The loopback /redeem route stays for same-node use; both call the SAME admit core.");
    }

    // ── Enrollment Phase C — the SoD COMPENSATING-CONTROL audit, WIRED LIVE (#1295 F1/F2). ───────────────
    // Registers IEnrollmentCompensatingControlRecorder → KernelAuditEnrollmentCompensatingControlRecorder over a singleton in-memory kernel audit trail
    // (signed/tamper-evident/undeletable; SEPARATE from the SC4-guarded EF financial-audit path) + the node's
    // canonical principal signer, with a fail-safe-but-LOUD onFault (WARN/degraded-mode, escalated to Error for
    // OwnershipTransferred). With this, the admission-redeem route's RecordMemberAdmittedAsync call lands a SoD
    // audit record at RUNTIME — the "second set of eyes" control is LIVE, not test-only (closes the F1 gap that
    // the seam + adapter existed but nothing invoked them and nothing was DI-registered).
    builder.Services.AddEnrollmentCompensatingControlAudit();
    Console.WriteLine(
        "[local-node-host] Enrollment Phase C: SoD compensating-control audit WIRED LIVE — IEnrollmentCompensatingControlRecorder → "
        + "KernelAuditEnrollmentCompensatingControlRecorder (signed, tamper-evident trail) + fail-safe-but-LOUD onFault; a runtime member "
        + "admit now records a SoD audit event (the second-set-of-eyes control).");
}

// inc-4 cross-process CALLER AUTH — the per-boot session token the Tauri Harborline App shell
// injects (LocalNode__SessionToken) + presents on the loopback routes. A loopback bind
// authenticates the HOST, not the calling PROCESS, so this token is the per-process caller
// gate. inc-4 F1: the gate is LISTENER-LEVEL — the SharedHostedWebApp middleware requires a
// matching `Authorization: Bearer` for EVERY route by default (gate-all-by-default), with a
// tiny explicit allowlist (/health, /live, /ready probes + /ws peer-sync, each authenticated by its own
// layer). A configured token therefore makes ALL non-allowlisted routes — including every
// financial-cluster write route — REJECT a stranger fail-closed (401), closing the F1 gap
// where the original per-route opt-in left financial writes ungated. When NO token is
// configured (direct dotnet run / a Bridge-spawned tenant child) the guard is
// dev/single-host-trusted (permits the call) — the SHIPPED Harborline App always injects a token, so
// the production Harborline App path is always authenticated. SECURITY: log presence/length only,
// never the value (a token in a log is a credential leak).
var sessionToken = localNodeOptions.SessionToken;
var callerSessionToken = new Harborline.Api.LocalNodeHost.Health.NodeCallerSessionToken(sessionToken);
builder.Services.AddSingleton(callerSessionToken);
Console.WriteLine(
    string.IsNullOrWhiteSpace(sessionToken)
        ? "[local-node-host] inc-4 caller-auth: NO session token configured — dev/single-host-trusted mode (all loopback routes permit local callers)."
        : $"[local-node-host] inc-4 caller-auth: session token configured (length={sessionToken.Trim().Length}) — ALL loopback routes REQUIRE Authorization: Bearer by default (fail-closed; allowlist: /health, /live, /ready, /ws, and exact session bootstrap routes).");

// WEB-CLIENT profile (dogfood browser UI; DOGFOOD.md gap #1). Wired ONLY when
// LocalNode:WebClient:Enabled — otherwise NOTHING here touches the composition and the listener
// caller-auth gate behaves exactly as before (bootstrap token only). Reuses the ADR-0097 Argon2id
// hasher + the ADR-0099 session store/TTL (search-before-substrate) — only the bearer transport is
// node-specific (the node serves over plain-HTTP Tailscale where the ADR-0099 Secure cookie is not
// sent). Registered here (before SharedHostedWebApp) so the hosted endpoints map their routes and
// SharedHostedWebApp resolves the authority + options at construction.
var webClientSection = builder.Configuration.GetSection("LocalNode:WebClient");
builder.Services.Configure<Harborline.Api.LocalNodeHost.NodeWebClientOptions>(webClientSection);
var webClientOptions =
    webClientSection.Get<Harborline.Api.LocalNodeHost.NodeWebClientOptions>()
    ?? new Harborline.Api.LocalNodeHost.NodeWebClientOptions();
// SECURITY (deep-review #1842, major finding): couple WebClient:Enabled to caller-auth enforcement,
// fail-closed. The listener gate fast-paths (permits every route) when NO session token is configured,
// so enabling the browser front door without LocalNode__SessionToken set would make the login screen
// decorative and leave every node route open. Refuse to start rather than serve an unguarded node.
// Bound to the SAME IsEnforced predicate the middleware uses (no A4 drift). No-op when disabled.
webClientOptions.EnsureCallerAuthEnforcedIfEnabled(callerSessionToken.IsEnforced);
if (webClientOptions.Enabled)
{
    builder.Services.AddHarborlinePasswordHashingSubstrate();
    builder.Services.AddHarborlinePasswordHashing<Harborline.Api.LocalNodeHost.Health.WebSession.NodeWebUser>();
    builder.Services.AddHarborlinePasswordHashing<
        Harborline.Api.LocalNodeHost.Data.Identity.InstallationAccountRecord>();
    builder.Services.AddHarborlineSessionEstablishment(); // reused: ISessionStore (in-memory) + SessionOptions (TTL floors)
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IWebAccountAccessChallengeIssuer,
        Harborline.Api.LocalNodeHost.Data.Identity.WebAccountAccessChallengeIssuer>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.WebAntiforgeryStateStore>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.AccountSetupInvitationStore>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IAccountSetupInvitationIssuer,
        Harborline.Api.LocalNodeHost.Data.Identity.AccountSetupInvitationIssuer>();
    // MTW-2 card 2615: contract-driven initial member-grant issuance. The service is composed now so
    // invitation acceptance can invoke it in the following slice; this card deliberately adds no
    // acceptance route or trigger. IGrantStore is resolved lazily after the durable vector composition
    // replaces the in-memory fallback below.
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.InitialGrantIssuanceService>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IInvitationAcceptanceGrantWriter>(provider =>
            provider.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Identity.InitialGrantIssuanceService>());
    // MTW-2 card 2614: invitation acceptance. The web-plane granter-authority provider re-verifies the
    // inviter's live mandate (members:manage + active grants) at accept time — the duty transferred to
    // acceptance by council-verdict-2026-07-22T1124Z. The joiner account minter is deliberately
    // non-singleton (admiral-ruling-2026-07-22T2310Z): a fresh instance is resolved per acceptance so no
    // mint state is shared across requests. The acceptance saga is a singleton (closed over by the hosted
    // endpoint, bug-2849) and resolves the minter from a per-acceptance scope.
    builder.Services.AddTransient<
        Harborline.Api.LocalNodeHost.Data.Identity.WebJoinerAccountMinter>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IWebJoinerPartyBindingMinter,
        Harborline.Api.LocalNodeHost.Data.Identity.WebJoinerPartyBindingMinter>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IAccountSetupAcceptanceAuthority,
        Harborline.Api.LocalNodeHost.Data.Identity.AccountSetupAcceptanceService>();
    // MTW-2 card 3338: the node mints the joiner's credential artifact from the password they chose
    // (ADR 0160 D3 — "Redemption chooses a password"). It resolves the SAME registered
    // IPasswordHasher<InstallationAccountRecord> the challenge issuer verifies against, so there is
    // one implementation and no parameter drift between minting and sign-in.
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IWebChosenCredentialFactory,
        Harborline.Api.LocalNodeHost.Data.Identity.WebChosenCredentialFactory>();
    // MTW-2 card 3013: account recovery (credential change + all-tenant session revocation, ADR 0160
    // R3-E/R3-F/D3). Separate recovery-invitation substrate (target-account-pinned, isolated from the
    // AccountSetup path); the members:manage-gated issuer mirrors the AccountSetup issuer's gate. The
    // recovery saga is a singleton (closed over by the hosted endpoint, bug-2849).
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.RecoveryInvitationStore>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.RecoverySessionRevoker>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IRecoveryInvitationIssuer,
        Harborline.Api.LocalNodeHost.Data.Identity.RecoveryInvitationIssuer>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IAccountRecoveryAuthority,
        Harborline.Api.LocalNodeHost.Data.Identity.AccountCredentialRecoveryService>();
    // MTW-2 card 2617: admin Team & access surface. Members:manage-gated list (roster UNION grant-anchored
    // web members per admiral-ruling-2026-07-23T0045Z Option A) + pending-invitation list + grant
    // revocation (the ruled web-plane revocation lever). Reuses the invitation issuer for the issue action.
    // IGrantStore is resolved lazily after the durable vector composition (same as the acceptance saga).
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IAuthorizedGrantRevocationWriter,
        Harborline.Api.LocalNodeHost.Data.Identity.AuthorizedGrantRevocationWriter>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IAdminTeamAccessAuthority,
        Harborline.Api.LocalNodeHost.Data.Identity.AdminTeamAccessAuthority>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Health.WebSession.IWebAntiforgeryPolicy,
        Harborline.Api.LocalNodeHost.Health.WebSession.WebAntiforgeryPolicy>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IInstallationIdentityHomeDecisionAuthority,
        Harborline.Api.LocalNodeHost.Data.Identity.InstallationIdentityHomeDecisionAuthority>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.ITenantIdentityAuthorityPartitionResolver,
        Harborline.Api.LocalNodeHost.Data.Identity.TeamContextTenantIdentityAuthorityPartitionResolver>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.ITenantMembershipAuthorityAdmission,
        Harborline.Api.LocalNodeHost.Data.Identity.LiveTenantMembershipAuthorityAdmission>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.InstallationIdentityCoordinatorService>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IInvitationAcceptanceMembershipWriter>(provider =>
            provider.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Identity.InstallationIdentityCoordinatorService>());
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.InstallationIdentityCoordinatorRecoveryService>();
    builder.Services.AddInstallationTenantCandidateClassification();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IWebTenantSelectionAuthority,
        Harborline.Api.LocalNodeHost.Data.Identity.WebTenantSelectionAuthority>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IWebTenantSwitchAuthority,
        Harborline.Api.LocalNodeHost.Data.Identity.WebTenantSwitchAuthority>();
    builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.Data.Identity.WebSelectedSessionStore>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IWebSelectedSessionPrincipalAuthority,
        Harborline.Api.LocalNodeHost.Data.Identity.WebSelectedSessionPrincipalAuthority>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IWebSelectedSessionLogoutAuthority,
        Harborline.Api.LocalNodeHost.Data.Identity.WebSelectedSessionLogoutAuthority>();
    // MTW-2 card 3329 step 1: the selected audience's own whoami. Identity only — the account id,
    // Party, People label, tenant + its label, founder-vs-member standing, and an advisory expiry.
    // The label reader is the narrow People seam (a display name and nothing else); a miss leaves
    // the name absent rather than substituting one.
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.ISelectedSessionMemberLabelReader,
        Harborline.Api.LocalNodeHost.Data.People.NodeEfSelectedSessionMemberLabelReader>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IWebSelectedSessionIdentityAuthority,
        Harborline.Api.LocalNodeHost.Data.Identity.WebSelectedSessionIdentityAuthority>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IWebFounderBindAuthority,
        Harborline.Api.LocalNodeHost.Data.Identity.WebFounderBindAuthority>();
    // Authority-version policies. Each version's readiness, admission and retirement rules live in
    // its own policy; the orchestrator owns only the durable stage machine and asks the registry.
    // Adding V3 means adding a policy here, not editing the stage machine or any caller.
    //
    // V2's readiness depends on IInstallationAuthorityV2SignInPath, which NOTHING implements yet.
    // That is not an omission: committing the authority marker retires every v1 cookie audience
    // permanently, and until a successor sign-in path is registered the honest answer to "is v2
    // ready" is no. The enumerable resolves empty and the cutover refuses, fail-closed.
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IInstallationAuthorityVersionPolicy,
        Harborline.Api.LocalNodeHost.Data.Identity.V1InstallationAuthorityVersionPolicy>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IInstallationAuthorityVersionPolicy,
        Harborline.Api.LocalNodeHost.Data.Identity.V2InstallationAuthorityVersionPolicy>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IInstallationAuthorityVersionRegistry,
        Harborline.Api.LocalNodeHost.Data.Identity.InstallationAuthorityVersionRegistry>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.InstallationIdentityCutoverOrchestrator>();
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Data.Identity.IInstallationIdentityV1AuthorityGate>(
            services => services.GetRequiredService<
                Harborline.Api.LocalNodeHost.Data.Identity.InstallationIdentityCutoverOrchestrator>());
    builder.Services.AddSingleton<
        Harborline.Api.LocalNodeHost.Health.WebSession.INodeWebSessionAuthority,
        Harborline.Api.LocalNodeHost.Health.WebSession.NodeWebSessionAuthority>();
    // S11 — the login rate limiter / lockout (in-process, fail-closed) the login route enforces.
    builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.Health.WebSession.WebLoginRateLimiter>();
if (!string.IsNullOrWhiteSpace(webClientOptions.LlmUpstreamBase))
    {
}

    var credentialState =
        string.IsNullOrWhiteSpace(webClientOptions.FounderUsername) ||
        string.IsNullOrWhiteSpace(webClientOptions.FounderPasswordHash)
            ? "NO founder credential provisioned (login FAIL-CLOSED — set LocalNode__WebClient__FounderUsername/__FounderPasswordHash)"
            : "founder credential provisioned";
    Console.WriteLine(
        $"[local-node-host] web-client profile: ENABLED — bundle={webClientOptions.BundleRoot ?? "(none)"}, " +
        $"llm={webClientOptions.LlmUpstreamBase ?? "(off)"}, {credentialState}.");
}

// The forms package supplies an in-memory projection outbox only for Development. Register its
// startup assertion before the storage guard so operational startup ordering remains fail-closed.
builder.Services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IHostedService, Harborline.Api.Foundation.Forms.Submission.InMemoryFormSubmitOutboxGuardAssertion>());

// Multi-device INC-5 — mDNS peer discovery (paper §6.1 tier-1, zero-config LAN).
// Registered into the install-level (outer) container only when enabled; the
// LocalNodeWorker resolves it from the OUTER provider after the per-team daemon is
// live and bridges discovered peers into the team daemon via AttachDiscovery. mDNS
// is link-local and cannot cross subnets — the fleet's cross-subnet/Tailscale
// topology uses LocalNode:Sync:Peers (static dial) instead, wired in the worker.
if (localNodeOptions.Sync.EnableMdns &&
    localNodeOptions.Sync.NetworkTrust is Harborline.Api.Kernel.Sync.Network.NetworkTrustLevel.Known)
{
    builder.Services.AddMdnsPeerDiscovery();
    Console.WriteLine("[local-node-host] mDNS peer discovery: ENABLED (same-subnet auto-discovery).");
}
else if (localNodeOptions.Sync.EnableMdns)
{
    Console.WriteLine("[local-node-host] mDNS peer discovery: SUPPRESSED (current network is Unknown).");
}
if (localNodeOptions.Sync.Peers.Count > 0)
{
    Console.WriteLine(
        $"[local-node-host] Static sync peers configured: {localNodeOptions.Sync.Peers.Count} " +
        "(dialed at startup via AddPeer — the cross-subnet/Tailscale path).");
}

// ADR 0114 — SQLite EF data plane for the embedded local node (SC-1 encrypted).
//
// All registered IHarborlineEntityModule implementations (contributed by block
// packages) are injected into LocalNodeDbContext so its OnModelCreating can
// configure every entity in a single model build. The per-provider guards
// in LocalNodeDbContext (and in each module's Configure() method) ensure the
// model is correct for both SQLite and Npgsql without provider-specific forks.
//
// SQLCipher encryption (ADR 0113 SC-1, FAIL-CLOSED). SQLitePCLRaw.bundle_e_sqlcipher
// replaces the default SQLite native library. The financial relational store is
// keyed from the install's root-seed-derived DEK BEFORE any read/write/migration:
//   * AddSqlCipherLocalNodeDbContext derives the DEK from the SAME root seed
//     resolved above (HKDF, domain-separated from per-team KV keys) and installs
//     SqlCipherConnectionInterceptor, which runs PRAGMA key + a verification probe
//     on every connection open — a wrong/absent key throws InvalidKeyException
//     (no silent plaintext fallback).
//   * LocalNodeStoreEncryptionGuard (a hosted service) proves the keyed open
//     succeeds at startup and aborts the host otherwise — SC-1's non-bypassable
//     enforcement invariant. There is NO plaintext registration anywhere.
//   * If the DEK cannot be derived (root seed not exactly 32 bytes) the
//     registration throws at composition time, so the host fails closed before
//     it can create a plaintext file.
//
// MigrationsAssembly: migrations live in this host assembly. Generate via:
//   dotnet ef migrations add <Name> -p apps/local-node-host -c LocalNodeDbContext
// ADR 0115 SC-4 amendment (boundary Option A1). If the Tauri shell injected a
// resolved Store DEK via LocalNode__StoreDekHex, key the relational store with
// THAT DEK verbatim (the shell owns the envelope wrap/unwrap + Argon2id +
// keyslot IO and resolved the DEK from whichever slot was available — Keychain
// fast-path or passphrase recovery). Otherwise fall back to the legacy
// root-seed-derived key (Bridge-spawned children + any non-SC-4 install). The
// SC-1 interceptor + startup guard are identical on both paths.
if (!string.IsNullOrWhiteSpace(localNodeOptions.StoreDekHex))
{
    Console.WriteLine(
        $"[local-node-host] Using injected Store DEK (length={keyHierarchy.AtRestRootKey.Length}B) — " +
        "SC-4 envelope recovery enabled for relational and per-team stores.");
}
AddInstallStore(builder.Services);

// ADR 0115 SC-4 amendment — fail-closed recoverability guard (SPOT-CHECK S1).
//
// The resolved hierarchy extends the recoverable Store-DEK root to every per-team
// encrypted store. Existing seed-keyed team stores are opened with their legacy key
// and rotated in place before use. Passing the hierarchy itself prevents this guard's
// safety input from drifting away from the key material wired into the activator.
Sc4RecoverabilityGuard.Validate(localNodeOptions, keyHierarchy);

// MTW-2 card 3342 — ADR 0160 R3-E/R3-F installation founder-account bootstrap. Until this line the
// v2 identity plane had no production path to its FIRST installation account, so identity.Accounts
// was empty on every real install and /api/session/account-challenge refused every actor, which made
// select / admin / founder-bind / connect-device / logout structurally unreachable.
//
// The ceremony is driven by LOCAL bootstrap authority (the console-provisioned founder credential in
// the node's own environment), not by a listener route: the authority it drives already records the
// actor as local-bootstrap-authority / local-installation-console, and a route would put the
// installation-root mint behind something the network can reach. It writes nothing unless the web
// profile is enabled AND a complete, policy-conformant founder credential is present, and its two
// ceremony coordinates are derived rather than generated so a restart replays the same command.
//
// Registered HERE, after the SQLCipher registration, on purpose: that call registers the encryption
// guard whose StartAsync applies the installation-identity schema, and HostOptions pins
// ServicesStartConcurrently=false at the top of this file, so registration order IS start order.
if (webClientOptions.Enabled)
{
    builder.Services.AddInstallationFounderBootstrapCeremony(
        installationRootPublicKeyFingerprint,
        authorizationSeedProfile);
}

// ADR 0114/0115 Pattern-A entity modules. Runtime, EF design-time scaffolding, and migration-path
// verification consume one code-owned catalog so a module cannot enter the executable model while being
// omitted from committed migrations. Shared financial/people/docs modules and host-local audit,
// home-epoch, and workflow modules all map into this same LocalNodeDbContext transaction boundary.
builder.Services.AddLocalNodePatternAModules();
// LocalNodeDbContext's constructor takes IEnumerable<IHarborlineEntityModule>; the
// DI container auto-composes that from the AddSingleton<IHarborlineEntityModule, X>() registrations.
// Do NOT add an explicit
// AddSingleton<IEnumerable<IHarborlineEntityModule>>(sp => sp.GetServices<IHarborlineEntityModule>())
// — GetServices<T>() resolves IEnumerable<T>, so that registration is
// SELF-REFERENTIAL and throws "A circular dependency was detected for the
// service of type IEnumerable<IHarborlineEntityModule>" the first time the
// DbContextFactory builds a context (LocalNodeStoreEncryptionGuard.StartAsync).
// The direct-construction tests (C2 drift, SqlCipher) never hit this because
// they pass the module set explicitly; only the composed running host does.
// bug-2796.

// Wave 5.2.D health surface. Registered before LocalNodeWorker so the
// endpoint is bound as soon as possible after team bootstrap — Bridge's
// TenantHealthMonitor begins polling once the child process is spawned and
// will see "active team not yet materialized" (Unhealthy) during the
// bootstrap window until MultiTeamBootstrapHostedService completes.
Harborline.Api.Foundation.EngineRoom.EngineRoomServiceCollectionExtensions.AddHarborlineEngineRoom(
    builder.Services);
builder.Services.AddTransient<LocalNodeHealthCheck>();
ResilientWindowsEventLogRegistration.AddAvailabilityCheck(
    builder.Services.AddHealthChecks()
        .AddCheck<LocalNodeHealthCheck>("local-node")
        .AddCheck<AuthorizationHealthCheck>("authorization")
        .AddCheck<LocalNodeLivenessCheck>("local-node-liveness", tags: ["live"])
        .AddCheck<LocalNodeReadinessCheck>("local-node-readiness", tags: ["ready"]));

// Wave 5.3.C shared Kestrel-backed WebApplication. Singleton so
// HostedHealthEndpoint and HostedWebSocketEndpoint can register their paths
// on the same listener. Registered as a singleton FIRST so its constructor
// (which builds the WebApplication) runs before anything else resolves it.
builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry>();

// Wave 5.3.C sync-daemon accept surface. LoggingSyncDaemonAcceptor is a
// stub that logs "received WS connection, CBOR-reading not yet wired" and
// closes the WebSocket cleanly; Wave 5.3.D replaces it with the real
// session pipeline.
builder.Services.AddSingleton<ISyncDaemonAcceptor, LoggingSyncDaemonAcceptor>();

// Container-bridge a2 (ONR survey 2026-06-19): register the contacts projection on the
// install-level delta router (the first/default synced doctype) and cold-start-hydrate
// the CRDT doc from local-node.db. MUST run BEFORE MultiTeamBootstrapHostedService —
// materializing the first team invokes the per-team registrar, which bridges the
// per-team daemon's delta plane to THIS router; the route + hydration must exist first.
builder.Services.AddHostedService<ContactSyncBootstrapHostedService>();

// Comms append-log pilot — register the comms projection on the SAME install-level delta router as the
// SECOND synced doctype (the #1265 additive seam; contacts stays the default route), and cold-start-hydrate
// the CRDT comms list from the recoverable local-node.db. MUST run AFTER ContactSyncBootstrapHostedService
// (so contacts keeps the daemon's Phase-1 "default" route) and BEFORE MultiTeamBootstrapHostedService (the
// per-team daemon bridge); registration order guarantees both. The projection is composed by AddNodeComms()
// below.
builder.Services.AddHostedService<CommsSyncBootstrapHostedService>();

// Roster-sync doctype (production-wiring gap #1) — register the trust-roster projection on the SAME
// install-level delta router as a third synced doctype (the #1265 additive seam), seed the local genesis
// self-admission AS a synced record, and cold-start-hydrate the CRDT roster list from the recoverable
// local-node.db. MUST run AFTER contacts + comms (so they keep their routes) and BEFORE
// MultiTeamBootstrapHostedService (the per-team daemon bridge); registration order guarantees both. The
// projection is composed by AddNodeRoster() below. This makes the roster propagate across nodes (admit/revoke
// records ride the delta-router + wire-fan-out, validate to genesis on merge) so membership converges with no
// central authority — the two-user harness no longer has to FAKE it by seeding both nodes.
builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.Data.Roster.RosterAdmissionGrantBackfill>();
builder.Services.AddHostedService<RosterSyncBootstrapHostedService>();

// ADR 0032 identity layer — the team-membership store (the many-to-many ActorId↔TeamId
// edge, role on the edge). The bootstrap below PROJECTS the installation's established administrator
// onto the OS-user's membership edge so a single office works out of the box (GetMembershipsAsync
// returns a real membership) and the per-org role resolution has a real edge to resolve.
//
// ADR 0066 clause 9 — it no longer MINTS one. The bootstrap used to re-upsert the OS user as full
// Admin on every boot with no key, no admission signature and no reading of prior authority state,
// which made revoking an administrator un-stick on the next restart. Authority is now established
// ONCE, durably, into NodeAdministratorAuthority; the boot only projects it.
builder.Services.AddHarborlineTeamMembershipStore();

// ADR 0066 migration step 1 — the durable administrator-authority log. It is simultaneously the
// canonical state the installer principal's gate is evaluated against (clause 5, re-checked inside the
// writing transaction) and the permanent audit of every establishment, removal and expiry (clause 4).
// Backed by NodeLocalRosterDbContext, i.e. the SQLCipher-keyed local-node.db registered above.
builder.Services.AddSingleton(provider => new Harborline.Api.LocalNodeHost.Data.Identity.NodeAdministratorAuthority(
    provider.GetRequiredService<
        Microsoft.EntityFrameworkCore.IDbContextFactory<
            Harborline.Api.LocalNodeHost.Data.Roster.NodeLocalRosterDbContext>>(),
    provider.GetRequiredService<TimeProvider>(),
    provider.GetRequiredService<Harborline.Api.Foundation.Authorization.AuthorizationGate>()));

// The liveness evidence the OFFLINE recovery command (clause 8) checks. Held for the node's lifetime;
// released by the kernel however the process dies, so a crash cannot lock recovery out permanently.
builder.Services.AddHostedService<Harborline.Api.LocalNodeHost.Data.Identity.NodeRunLock>();

// Multi-team bootstrap runs next so the node-host worker sees a materialized
// active team on StartAsync. Registration order matters — the .NET generic
// host starts hosted services in registration order. The per-team daemon it
// materializes now bridges to the install-level contacts router (above).
builder.Services.AddHostedService<MultiTeamBootstrapHostedService>();

// Seed the authorization catalogue after tenant materialization but before any founder path can
// publish the first Administrator grant. Hosted services start in registration order.
builder.Services.AddHostedService<AuthorizationSeedHostedService>();

// earlier repository ticket #3448 — attach the bootstrap founder to the genesis tenant. Registered AFTER the team
// bootstrap because ServicesStartConcurrently=false makes registration order = start order, and this
// binds to the tenant that bootstrap materializes. Web-client profile only: it exists to make the v2
// selected-audience flow reachable for the founder, which is meaningless without the web plane.
if (webClientOptions.Enabled)
{
    builder.Services.AddFounderTenantMembershipAttach(capturedGenesisPartyId);
}
// BLOCKER-1 (cerebrum [2026-06-21] DECISIVE cross-machine verify) + MAJOR-1: register LocalNodeWorker ONCE
// as a concrete singleton and bridge BOTH facets — the hosted-service role AND the readable
// IEnrollmentRebindStatus — to that single instance (the cycle-free wiring; see
// LocalNodeWorkerRegistration). The prior wiring resolved IEnrollmentRebindStatus through
// `sp.GetServices<IHostedService>().First(...)`; because HostedSyncStatusApiEndpoint (itself an
// IHostedService) takes an optional IEnrollmentRebindStatus, the host's enumeration of
// IEnumerable<IHostedService> at StartAsync re-entered that same enumeration via the factory → a circular
// dependency that crashed Host.StartAsync UNCONDITIONALLY on both platforms (the host did not boot). The
// helper is the SINGLE registration the host-boot smoke test also calls, so a future regression is caught.
builder.Services.AddLocalNodeWorkerAndRebindStatus();
// R3-H recovery backstop — initiate the installation identity-coordinator drain after the team
// bootstrap (so tenant partitions exist) and before the shared listener. The pass starts before
// identity routes can serve, but completes asynchronously; a wedged store must not block startup.
// The daemon is web-profile-only because the coordinator itself is composed only for that profile.
// Its coordinator resume path already owns the tenant leases, durable state transitions, and grant
// source-reference idempotency; this service adds no second lock around live request-path coordination.
if (webClientOptions.Enabled)
{
    var identityRecoverySweepInterval = TimeSpan.FromSeconds(
        localNodeOptions.IdentityCoordinator.RecoverySweepIntervalSeconds is { } seconds && seconds > 0
            ? seconds
            : Harborline.Api.LocalNodeHost.IdentityCoordinatorOptions.DefaultRecoverySweepIntervalSeconds);
    builder.Services.AddHostedService<
        Harborline.Api.LocalNodeHost.Data.Identity.InstallationIdentityCoordinatorRecoveryDaemon>(sp =>
        new Harborline.Api.LocalNodeHost.Data.Identity.InstallationIdentityCoordinatorRecoveryDaemon(
            sp.GetRequiredService<
                Harborline.Api.LocalNodeHost.Data.Identity.InstallationIdentityCoordinatorRecoveryService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<
                Harborline.Api.LocalNodeHost.Data.Identity.InstallationIdentityCoordinatorRecoveryDaemon>>(),
            identityRecoverySweepInterval));
}
// Wave 5.3.C: HostedHealthEndpoint + HostedWebSocketEndpoint register their
// paths on the shared app during their StartAsync; the shared app itself is
// registered LAST so it starts Kestrel after every path has been mapped.
// ADR 0114/0115 cohort 1b: validation API surface — /api/local-node/status + /api/local-node/tables.
// Registered before SharedHostedWebApp so paths are mapped before Kestrel starts.
// Sync-status read-model (Phase A — sync-status-read-model survey 2026-06-19; PAO design):
// GET /api/local-node/sync-status — the four-state (HAS/WILL/SHOULD/COULDN'T) multi-device
// sync surface the Harborline App UI reads, projected from the active team's gossip daemon by the
// team-scoped ISyncStatusReadModel. Registered before SharedHostedWebApp so the path is mapped
// before Kestrel starts.
// Tenant portability dump. This is deliberately separate from Data Exchange and from backup:
// the default contributor exports only tenant-keyed local-first store entries.
builder.Services.AddHarborlineLocalFirst();
// Instance-lifecycle governance (ADR 0144 AD.1 — setup-phase + workshop:unlock mechanics, slice B5):
// GET /api/local-node/governance/lifecycle (phase + workshop-unlock decision) +
// POST .../finish-setup (founder-declared setup -> operating, the unguarded safe direction). The Harborline App's
// lifecycle-aware Build fold (useLifecyclePhase.ts) reads this. The durable store persists to
// governance-lifecycle.json under the node data dir; the workshop-unlock authority resolves over the node's
// IAuthorizationContext. Registered before SharedHostedWebApp so the path is mapped before Kestrel starts.
builder.Services.AddNodeTenantGovernance(
    localNodeOptions.DataDirectory ?? System.IO.Path.Combine(AppContext.BaseDirectory, "data"));
// Genesis backstop: after MultiTeamBootstrap seeds the active team, ensure the tenant is born in setup
// phase (idempotent). Runs after MultiTeamBootstrapHostedService (registered above) so the active team is
// set by its StartAsync.
builder.Services.AddHostedService<Harborline.Api.LocalNodeHost.Data.Governance.HostedGovernanceGenesisService>();
// Teams chrome (Harborline App teams UI): GET /api/local-node/teams (joined-team list) +
// GET /api/local-node/teams/active (current active team). The Harborline App teamsClient.ts
// reads these to render the REAL teams instead of the mock Alpha/Beta fallback; the
// joined-team list is the materialized ITeamContextFactory.Active snapshot, isActive
// from IActiveTeamAccessor, memberCount from the per-team roster (IMutableTeamRegistry).
// Registered before SharedHostedWebApp so the paths are mapped before Kestrel starts.
// ADR 0115 D8 Stage 2: node-local maintenance CRUD — /api/local-node/maintenance.
// Backed by NodeLocalMaintenanceDbContext (a SEPARATE node-exclusive context on
// the same SQLCipher file, NOT a shared IHarborlineEntityModule), so the C2
// both-provider parity test never sees it. PM doctype, client-local-authoritative
// (Admiral ruling 2026-06-13 Option B). Registered before SharedHostedWebApp so
// paths are mapped before Kestrel starts.
// ADR 0134 P1b-2/P2: canonical-node-key principal-signing route —
// GET /api/local-node/current-principal-signature. Signs the host-resolved current
// OS-user principal with the node identity (CanonicalJson/SignedOperation, #1254) so a
// TS/remote verifier accepts it with the node public key pinned. BOUNDED (host-resolved
// principal only — no signing oracle), loopback-bound; inc-4 adds cross-process caller
// auth (session token). Registered before SharedHostedWebApp so the path is mapped
// before Kestrel starts.
// ADR 0115 gap C4: offline first-run entity + chart-of-accounts seed routes.
// GET+POST /api/local-node/entities — create/list the initial LegalEntity offline.
// POST /api/local-node/chart-of-accounts/seed-from-template — seed CoA from bundled template.
// Both backed by LocalNodeDbContext (SQLCipher SC-1, FinancialLedgerEntityModule).
// Registered before SharedHostedWebApp so paths are mapped before Kestrel starts.
// Pack Composer B-1a — the domain-pack EXPORT + VERIFY surface (design note 2026-07-02). Registers
// the stateless exporter/verifier/validator/PII-scanner/codec, then maps POST
// /api/local-node/packs/{export,verify} on the shared listener. Own-roster trust only in v1 (the
// node principal signer); NO install route + NO seed writes (that is B-1b). Registered before
// SharedHostedWebApp so paths are mapped before Kestrel starts.
builder.Services.AddPackComposerExportVerify();
// Pack Composer B-1b — the INSTALL engine (design note 2026-07-02 Rev 2; the security-concentrated half).
// Bind the REAL adapters BEFORE AddPackComposerInstall so its TryAdd defaults are skipped: the durable
// kernel-audit sink (closes B-1a's structured-log floor) + the real ADR 0143 admission validator (S-9 / A7
// — same validator, no pack-scoped subset). Then the install engine (verify-gate → atomic seed layer →
// S-8 watermark → S-10 re-attach) + the install routes on the shared listener (own-roster + Harborline
// channel trust only; no third-party). Registered before SharedHostedWebApp so paths map before Kestrel.
// Ticket 214 slice 2: the audit sink for an authorization refusal. The renderer keeps the classified
// reading on the refusal's Diagnostic; this writes it to the refusal's audit row while the response
// stays redacted.
builder.Services.AddAuthorizationRefusalAudit();
builder.Services.AddSingleton<IPackInstallAudit, KernelAuditPackInstallAudit>();
builder.Services.AddSingleton<IPackContentAdmission, PackWorkflowAdmissionAdapter>();
var runningPackPlatformVersion =
    (typeof(PackInstaller).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(PackInstaller).Assembly.GetName().Version?.ToString()
        ?? "0.0.0")
    .Split('+', 2)[0];
builder.Services.AddSingleton<IPackPlatformCompatibility>(new PackPlatformCompatibility(
    runningPackPlatformVersion,
    Harborline.Api.LocalNodeHost.Data.PackProjection.PackSeedProjector.RegisteredCases));
// F5 (migration-update-architecture D5.2 / D5.3) — bind the DURABLE SQLCipher-backed IPackInstallStore BEFORE
// AddPackComposerInstall so its TryAdd default (InMemoryPackInstallStore) is skipped. This is what makes an
// installed+activated pack + its S-8 watermark + tenant overrides SURVIVE a node restart / deploy-dogfood
// redeploy: boot re-projects the still-installed packs through the existing PackSeedProjector path (no re-seed).
// Backed by NodeLocalPacksDbContext (registered by AddSqlCipherLocalNodeDbContext — same encrypted local-node.db).
var durablePackStores = new ConditionalWeakTable<IServiceProvider, Lazy<Harborline.Api.LocalNodeHost.Data.Packs.DurablePackInstallStore>>();
Harborline.Api.LocalNodeHost.Data.Packs.DurablePackInstallStore DurablePackStore(IServiceProvider provider) =>
    durablePackStores.GetValue(provider, static sp => new Lazy<Harborline.Api.LocalNodeHost.Data.Packs.DurablePackInstallStore>(
        () => new Harborline.Api.LocalNodeHost.Data.Packs.DurablePackInstallStore(
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Harborline.Api.LocalNodeHost.Data.Packs.NodeLocalPacksDbContext>>()),
        LazyThreadSafetyMode.ExecutionAndPublication)).Value;
builder.Services.AddSingleton<Harborline.Api.Foundation.Packs.Install.IPackInstallStore>(sp =>
    new Harborline.Api.LocalNodeHost.Data.Packs.DurablePackInstallStoreReader(
        DurablePackStore(sp)));
builder.Services.AddSingleton<IPackInstaller>(sp => new PackInstaller(
    sp.GetRequiredService<Harborline.Api.Foundation.Packs.Verify.IPackVerifier>(),
    sp.GetRequiredService<Harborline.Api.Foundation.Packs.Install.IPackInstallStore>(),
    DurablePackStore(sp),
    DurablePackStore(sp),
    sp.GetRequiredService<IPackContentAdmission>(),
    sp.GetRequiredService<IPackInstallAudit>(),
    sp.GetRequiredService<Harborline.Api.Foundation.Authorization.AuthorizationGate>(),
    sp.GetRequiredService<Harborline.Api.LocalNodeHost.Data.PackProjection.IPackSeedProjector>(),
    sp.GetRequiredService<IPackPlatformCompatibility>()));
builder.Services.AddPackComposerInstall();

// Ticket 208 fix 1: the node's pack TRUST SURFACE is composed once and shared. Both the install routes
// (HostedPackInstallApiEndpoint) and the Access administration preload resolve THESE, so a revoked or
// untrusted signature is refused on every install path — no path may root trust in its own key.
builder.Services.AddSingleton<Harborline.Api.Foundation.Packs.Trust.IPackTrustStore>(sp =>
    Harborline.Api.LocalNodeHost.Health.HostedPackInstallApiEndpoint.BuildTrustStore(
        sp.GetRequiredService<Harborline.Api.LocalNodeHost.Health.NodePrincipalSigner>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Harborline.Packs.Trust")));
// Until a signed channel-distributed list is provisioned this is the offline-tolerant Empty list: it
// revokes nothing and reports itself STALE, rather than silently claiming "nothing revoked" (S-11).
builder.Services.AddSingleton<Harborline.Api.Foundation.Packs.Install.Trust.IPackRevocationList>(
    _ => Harborline.Api.Foundation.Packs.Install.Trust.PackRevocationList.Empty);
// App-layer FEATURE GRAPH (G1 keystone; design note app-layer-feature-graph-2026-07-07). The rebuildable
// content-edge-index provider + the read-model that assembles per-app contributions (grouped by pillar) +
// cross-app edges from install state, surfaced read-only at GET /packs/graph. Registered here so the seed
// projector's optional IPackContentEdgeIndexProvider ctor param resolves (a projection pass warms the
// index); no store + no manifest change — a projection over the install state above (the single truth).
builder.Services.AddPackFeatureGraph();
builder.Services.AddInMemoryTaxonomy();
builder.Services.AddSingleton<IReportDefinitionDescriptorRegistry, HostReportKindDescriptorRegistry>();
builder.Services.AddInMemoryReportDefinitions();
// Ticket 076 descriptor admission adapts the wired statement-file parsers + bank-feed provider
// plus the ERPNext dump mode. The ADR 0014 portability-export path is not an exchange kind and
// stays unwired here.
builder.Services.AddSingleton<IDataExchangeDefinitionDescriptorRegistry, HostDataExchangeKindDescriptorRegistry>();
builder.Services.AddInMemoryDataExchangeDefinitions();
builder.Services.AddSingleton<IScheduleDefinitionDescriptorRegistry, HostScheduleKindDescriptorRegistry>();
builder.Services.AddInMemoryScheduleDefinitions();
// Ticket 074: the descriptor's IEntityTypeRegistry dependency resolves lazily; AddNodeAssetRegistry
// registers it later, matching the registration-order dependency the projector wiring already relies on.
builder.Services.AddSingleton<IViewDefinitionDescriptorRegistry, HostViewKindDescriptorRegistry>();
builder.Services.AddInMemoryViewDefinitions();
// ADR 0047/0069 record standings: installed standing rules are ordinary immutable definition rows.
// The evaluator remains a seam for ticket 205; it is deliberately not wired into the kernel gate here.
builder.Services.AddSingleton<
    Harborline.Api.Foundation.RuleEngine.Standings.IStandingRuleDefinitionStore,
    Harborline.Api.Foundation.RuleEngine.Standings.InMemoryStandingRuleDefinitionStore>();
builder.Services.AddSingleton<Harborline.Api.Foundation.RuleEngine.Standings.StandingEvaluator>();
builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.Data.Authorization.StandingCatalogue>();
// Pack seed PROJECTION (BUILD #127 slice 1) — the seam that makes an installed+activated pack's declarative
// content LIVE in the runtime registries the read APIs consume (AssetTypeDefinition → the shared
// IEntityTypeRegistry the Type Manager dropdown reads). Without it, a pack installs into the immutable seed
// layer yet GET /asset-registry/types stays empty (the dead-end this closes). Singleton (deps IPackInstallStore
// [above] + IEntityTypeRegistry [AddNodeAssetRegistry, later — resolved lazily, so registration order is fine]);
// invoked on Draft→Active by the install routes, and re-projected at startup by the hosted service below.
// Documents & Templates pillar (#111 D2): the document-template registry the pack seed projector publishes
// TemplateDefinition content into (and the render pipeline resolves). Registered BEFORE the projector so the
// optional IDocumentTemplateRegistry ctor param resolves — the projector then publishes pack templates
// (e.g. the General invoice) rather than deferring them. In-memory today; a durable store is a Wave-2 axis.
builder.Services.AddSingleton<
    Harborline.Api.Foundation.Documents.Issuance.IDocumentTemplateRegistry,
    Harborline.Api.Foundation.Documents.Issuance.InMemoryDocumentTemplateRegistry>();
builder.Services.AddSingleton<
    Harborline.Api.LocalNodeHost.Data.PackProjection.IPackSeedProjector,
    Harborline.Api.LocalNodeHost.Data.PackProjection.PackSeedProjector>();
// Update feed U2 (design note #155) — the node-side channel feed CLIENT: on an EXPLICIT user-initiated
// check (the scheduled/auto-check opt-in is U6) it fetches a channel feed over HTTP, materializes it, runs
// the SHARED FeedTreeVerifier (channel-root sig → sequence monotonicity vs the durable floor → validUntil
// per channel type → revocation coupling → per-pack index sigs → CID addressing → pack verify), then STAGES
// the verified artifact to the EXISTING /packs/preview engine (no new install path). The channel table pins
// the DOGFOOD root in the binary (F6 — config may sync, the trust pin never does); the F2 anti-rollback
// high-water persists in the durable DurableChannelSequenceStore (beside the S-8 watermark), with a
// build-time first-contact floor pinned beside the root. Registered before SharedHostedWebApp so the routes
// map before Kestrel.
builder.Services.AddHttpClient(
    Harborline.Api.LocalNodeHost.Feed.HttpFeedFetcher.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.Feed.HttpFeedFetcher>();
builder.Services.AddSingleton<
    Harborline.Api.LocalNodeHost.Data.Packs.IChannelSequenceStore,
    Harborline.Api.LocalNodeHost.Data.Packs.DurableChannelSequenceStore>();
builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.Feed.IChannelRegistry>(
    _ => Harborline.Api.LocalNodeHost.Feed.BinaryPinnedChannelRegistry.CreateDefault());
builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.Feed.IChannelFeedClient>(sp =>
    new Harborline.Api.LocalNodeHost.Feed.ChannelFeedClient(
        sp.GetRequiredService<Harborline.Api.LocalNodeHost.Feed.IChannelRegistry>(),
        sp.GetRequiredService<Harborline.Api.LocalNodeHost.Feed.HttpFeedFetcher>(),
        sp.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Packs.IChannelSequenceStore>(),
        sp.GetRequiredService<TimeProvider>()));
// Pack Composer B-2a — the guarded COMPOSE ceremony (design note 2026-07-06; council fold cerebrum
// 2026-07-06). Snapshot-at-compose (Q3) → human PII review affirmation bound to the snapshot hash
// SERVER-SIDE (S-1) → re-hash + own-roster sign through the exporter's fail-closed DCP gate (Q1). Reads
// the live asset registry ONCE at compose (never at export). Registered after the exporter + asset
// registry (its collaborators), before SharedHostedWebApp so the ceremony routes map before Kestrel.
builder.Services.AddPackComposeCeremony();
// T1 local-first sweep: node-local accounting-periods (list + open/close writes) + the
// fresh-install open-period seed. GET/POST /api/local-node/accounting-periods over the
// EXISTING fiscal_periods/fiscal_years tables (FinancialLedgerEntityModule; no new schema,
// UPF F0). Node-direct EF over LocalNodeDbContext (same store NodeEfPeriodResolver reads for
// the posting Phase-4 gate) — writes only the recoverable local-node.db, so SC4-T9(b) stays
// green. Registered before SharedHostedWebApp so paths are mapped before Kestrel starts.
builder.Services.AddSingleton<NodeAccountingPeriodService>();
// ADR 0115 D8 Stage 2 Cohort C: node-local-authoritative properties / leases.
// GET/POST /api/local-node/properties + /api/local-node/leases. Each backed by a
// SEPARATE node-exclusive DbContext (NodeLocalPropertyDbContext /
// NodeLocalLeaseDbContext) on the same SQLCipher file, NOT a shared
// IHarborlineEntityModule, so the C2 both-provider parity test never sees them.
// Registered before SharedHostedWebApp so paths are mapped before Kestrel starts.
// PM-doctype offline rebind for payments (Admiral ruling 2026-06-14): node-local
// READ over the kernel-ledger payments. GET /api/local-node/payments[?chartId=&partyId=]
// + GET /api/local-node/payments/{id}. A projection over LocalNodeDbContext's
// Payment table (the C1-durable financial source of truth, contributed by
// PaymentsEntityModule) — NOT a flat duplicate table, and read-plane only (the
// payment POSTING path is owned by the financial cluster's posting services and is
// untouched). Registered before SharedHostedWebApp so paths are mapped before Kestrel starts.
// Cohort D Step 1 (ADR 0113 ABSOLUTE local-first; ADR 0121 the JE read-model this relocates):
// the embedded node now OWNS journal-entry data. NodeEfJournalStore is an EF-backed IJournalStore
// over the SAME SQLCipher LocalNodeDbContext the payments read-plane projects over (JournalEntry
// is mapped by FinancialLedgerEntityModule, Lines as JSONB — single SELECT, no Include). The
// host-agnostic IJournalEntryQueryReadModel (earlier repository ticket #1161) composes over that store for the read
// surface; routes receive the concrete store (including ReplaceEntryAsync for reversal) from the
// composition root. ADDITIVE — the Bridge JE path is untouched; the frontend flip + Rust sc5 fail-close
// are a sequenced follow-up after the security SPOT-CHECK. Registered before SharedHostedWebApp so
// paths are mapped before Kestrel starts.
builder.Services.AddSingleton<NodeEfJournalStore>();
builder.Services.AddSingleton<IJournalStore>(sp => sp.GetRequiredService<NodeEfJournalStore>());
builder.Services.AddInMemoryJournalEntryQueryReadModel(); // IJournalEntryQueryReadModel over IJournalStore

// Cohort D Step 2a — node-side financial POSTING foundation (security-engineering
// SC4-C2 verdict 2026-06-15, GREEN with conditions (a)-(d)).
//
// Wire JournalPostingService on the node so the manual JE create/reverse routes run the
// full six-phase posting algorithm (preconditions → balance → account-validity →
// period-gating → atomic commit → result) instead of bypassing it and validating only
// balance. The node-resident resolvers read ONLY the recoverable, Store-DEK-enveloped
// local-node.db (gl_accounts / fiscal_periods); the posting service writes ONLY via the
// recoverable IJournalStore == NodeEfJournalStore registered above.
//
// SC4-C2 conditions this satisfies (enforced by the Layer-2 runtime DI-graph assertion in
// Sc4RecoverabilityGuardTests):
//   (a) IJournalStore resolves to NodeEfJournalStore (recoverable local-node.db) — the
//       singleton above; PostAsync's ONLY persistence call is _store.SaveAtomicAsync.
//   (b) IDomainEventPublisher is left at the cluster default NoopDomainEventPublisher —
//       this host NEVER calls AddFoundationEvents(), so NO IDomainEventStore is registered
//       (no cross-cluster event bus). JournalPostingService itself takes no event publisher.
//   (c) NO kernel CRDT writer (PostingEngine / ILedgerEventStream) and NO per-team
//       FileBackedEventLog / IEventLog is wired into the posting composition.
//   (d) the account/period resolvers resolve to node EF reads over local-node.db, never a
//       seed-keyed per-team KV store.
//
// TimeProvider is the posting service's clock (PostedAtUtc stamp). No user context is registered:
// the soft-close override is one AuthorizationGate decision at the posting service's point of use,
// naming the fiscal period it addresses (ticket 205 slice 5, ticket 194). The posting service is
// exposed to the route handlers via an accessor (the routes map onto the inner shared-app
// container whose provider lacks the outer registrations — bug-2849, same pattern as the
// journal-store accessor). The exact registration set lives in AddNodeFinancialPosting — the
// SAME method the SC4-T9(b) Layer-2 runtime DI-graph assertion builds, so the gate verifies
// the real composition with no test/prod drift.
builder.Services.AddNodeFinancialPosting();

// ADR 0126 T4 — node audit system-of-record flip onto the recoverable local-node.db.
//
// AddNodeAuditWrites registers the INodeAuditWriteEnlister, which NodeEfJournalStore resolves via its
// OPTIONAL constructor parameter (registered ABOVE; resolution happens when the provider is built, so
// registration order is irrelevant). Once wired, a posted JE's Financial.JournalPosted audit row is
// STAGED onto the SAME LocalNodeDbContext the JE write commits — the two commit in one SQLite
// transaction (OQ2 = ATOMIC). Because the enlister is optional, every pre-T4 composition (incl. the
// SC4-T9(b) guard's posting/bill providers) is unaffected — existing posting tests stay green.
//
// AddNodeAuditReads registers the NodeAuditEventReader over the recoverable node_audit_events table.
//
// ADR 0135 — per-event signed event-log (the binding pre-multi-device PASS-gate; SEC-A2). The audit
// enlister picks up the node IOperationSigner (NodePrincipalSigner, registered at line ~261 — the
// RootSeedHex→Ed25519 identity under the ADR 0118 custody ladder) so each Financial.JournalPosted
// audit row is per-event SIGNED, and the reader picks up the IOperationVerifier (registered just below)
// so signed rows are REALLY re-verified at read time (a tampered signed row reads VerificationFailed).
// The signer/verifier are pulled OPTIONALLY inside the composition factories (GetService, not
// GetRequiredService), so the SC4-T9(b) minimal audit graph — which wires neither — still resolves the
// enlister + reader unchanged (unsigned, conservative). Foundation crypto is NOT an IEventLog / CRDT /
// KV sink, so SC4-C2 below is unaffected. Registered here (TryAdd) so the audit verifier is independent
// of whether AddNodeComms/AddNodeRoster (which also TryAdd it) are composed.
builder.Services.TryAddSingleton<Harborline.Api.Foundation.Crypto.IOperationVerifier,
    Harborline.Api.Foundation.Crypto.Ed25519Verifier>();
//
// SC4-C2 (the SC4-T9(b) Layer-2 DI-graph test asserts these over AddNodeAuditWrites/AddNodeAuditReads):
//   (a) the ONLY persistence sink is the recoverable local-node.db (the enlister stages onto the JE's
//       LocalNodeDbContext; the reader reads local-node.db) — no audit row reaches a seed-keyed KV.
//   (b) NO IDomainEventPublisher/IDomainEventStore is registered by the audit composition.
//   (c) NO kernel CRDT writer / per-team FileBackedEventLog / IEventLog is wired — the enlister + reader
//       are pure compute + recoverable EF, taking NO IEventLog dependency (the orphan-vector closer).
//   (d) the reader resolves node EF over local-node.db, never a seed-keyed per-team KV store.
builder.Services.AddNodeAuditWrites();
builder.Services.AddNodeAuditReads();
// ── Cohort D Step 2c — node-side AP BILL write + posting (additive) ──────────────────
//
// Bills are the primary AUTO-POSTED journal-entry source: creating + recording a bill posts a
// balanced JE (Debit each line's expense/asset account, Credit the AP control account) through the
// node-resident IJournalPostingService wired by AddNodeFinancialPosting above. AddNodeBillWrites
// registers exactly the BillPostingService slice — the recoverable NodeEfBillRepository (over the
// SAME SQLCipher LocalNodeDbContext; Bill mapped by ApEntityModule, Lines as JSONB), the active-team-
// derived ActiveTeamTenantContext (ADR 0032 — the ambient tenant follows the active org, not a fixed
// "local"), the cluster-default NoOpTaxCalculator, the explicit
// NoopDomainEventPublisher (SC4-C2 (b): no cross-cluster event bus — the host never calls
// AddFoundationEvents), and the posting-service + repository accessors (bug-2849, same inner-shared-
// app-container pattern as the JE accessors).
//
// SC4-C2 (security-engineering verdict 2026-06-15, extended to the AP write path, asserted by the
// SC4-T9(b) Layer-2 DI-graph test in Sc4RecoverabilityGuardTests):
//   (a) the ONLY persistence sinks are the recoverable local-node.db — NodeEfBillRepository (bills)
//       + NodeEfJournalStore (the auto-JE, via the Step-2a posting service).
//   (b) IDomainEventPublisher is the NoopDomainEventPublisher; no IDomainEventStore is registered.
//   (c) NO kernel CRDT writer / per-team event log is wired into the bill-write composition.
//   (d) the bill repository + tenant context resolve to node EF reads/writes over local-node.db.
//
// ADDITIVE — the Bridge AP path is untouched; the frontend flip + Rust sc5 fail-close are a
// sequenced follow-up after the security SPOT-CHECK. The exact registration set lives in
// AddNodeBillWrites — the SAME method the SC4-T9(b) Layer-2 bill assertion builds (no test/prod
// drift). Registered before SharedHostedWebApp so paths are mapped before Kestrel starts.
builder.Services.AddNodeBillWrites();
// ── Cohort D Step 2b — node-side AR INVOICE write + posting (additive) ───────────────
//
// AR invoices are a TWO-step lifecycle on the node (vs the AP bill single-step create+record):
// POST /api/local-node/invoices creates a Draft (mints the canonical INV-YYYY-MM-DD-{Replica}-{NNNN}
// number AT CREATE time, no GL post), and POST /{id}/issue posts the balanced JE (Debit the AR
// control account, Credit each line's income account) through the node-resident IJournalPostingService
// wired by AddNodeFinancialPosting above. /{id}/void posts a reversing JE; /{id}/write-off posts a
// bad-debt JE (Debit BadDebtExpense / Credit AR). AddNodeInvoiceWrites registers exactly the
// InvoicePostingService slice — the recoverable NodeEfInvoiceRepository (over the SAME SQLCipher
// LocalNodeDbContext; Invoice mapped by ArEntityModule, Lines as JSONB), a durable restart-safe
// NodeEfInvoiceNumberingService (derives the next sequence from the MAX existing number in the same
// store, so a restart never re-mints a colliding -0001), the active-team-derived ActiveTeamTenantContext
// (ADR 0032 — ambient tenant follows the active org, shared with the bill slice via TryAdd), the
// cluster-default NoOpTaxCalculator,
// the explicit NoopDomainEventPublisher (SC4-C2 (b): no cross-cluster event bus — the host never calls
// AddFoundationEvents), the recoverable NodeEfJournalStore as the posting service's optional journal
// store, and the posting-service + repository + numbering accessors (bug-2849).
//
// SC4-C2 (security-engineering verdict 2026-06-15, extended to the AR write path, asserted by the
// SC4-T9(b) Layer-2 invoice DI-graph test in Sc4RecoverabilityGuardTests):
//   (a) the ONLY persistence sinks are the recoverable local-node.db — NodeEfInvoiceRepository
//       (invoices) + NodeEfJournalStore (the issue/void/write-off JEs, via the Step-2a posting service).
//   (b) IDomainEventPublisher is the NoopDomainEventPublisher; no IDomainEventStore is registered.
//   (c) NO kernel CRDT writer / per-team event log is wired into the invoice-write composition.
//   (d) the invoice repository + tenant context resolve to node EF reads/writes over local-node.db;
//       the numbering service derives its sequence from the same recoverable store.
//
// ADDITIVE — the Bridge AR path is untouched; the frontend flip + Rust sc5 fail-close are a sequenced
// follow-up after the security SPOT-CHECK. The exact registration set lives in AddNodeInvoiceWrites —
// the SAME method the SC4-T9(b) Layer-2 invoice assertion builds (no test/prod drift). Registered
// before SharedHostedWebApp so paths are mapped before Kestrel starts.
builder.Services.AddNodeInvoiceWrites();
// ── T2b local-first sweep — node-side recurring-invoice schedules + generation ───────────────────────
//
// The recurring-invoice / rent-run GENERATION engine lives in the Harborline blocks (NOT bridge-local):
// IRruleExpansionService (foundation-scheduling) expands the RRULE; the draft → issue → balanced-JE flow
// goes through the node-resident IInvoicePostingService wired by AddNodeInvoiceWrites above (over the
// recoverable NodeEfJournalStore); drafts are upserted via the node IInvoiceRepository. So generating a
// schedule's invoice node-side produces a node-resident AR invoice + balanced JE — the prerequisite for
// the T2b rent-recording composition (record rent lease-centric against the lease's open node invoice).
//
// Schedule persistence is the recoverable LocalNodeDbContext: RecurringInvoiceSchedule is mapped by the
// shared ArEntityModule and the recurring_invoice_schedules table ALREADY exists in the node InitialSchema
// migration — so this flip adds NO new schema (no migration on either LocalNodeDbContext or
// SignalBridgeDbContext). Generation is idempotent: the schedule's GeneratedInvoices map (occurrenceDate →
// InvoiceId) is checked before any draft is created, so a re-run never double-issues (ADR 0122
// SourceReference posture satisfied at the schedule-occurrence level; the dedupe state IS the recoverable
// generated_invoices_json column, never a seed-keyed KV).
//
// SC4-C2: every persistence sink reachable from this slice is the recoverable local-node.db (schedule
// rows + invoice drafts + the issue JEs via the node posting service); no kernel CRDT writer / per-team
// event log is reachable, so SC4-T9(b) stays green.
//
// ADDITIVE — the Bridge recurring path (/api/v1/recurring-invoices, EfRecurringInvoiceService over
// Postgres) is untouched; the frontend flip + Rust sc5 fail-close are a sequenced follow-up after the
// pattern-009 security SPOT-CHECK. MUST run AFTER AddNodeInvoiceWrites (depends on the node
// IInvoiceRepository + IInvoicePostingService). Registered before SharedHostedWebApp so paths are mapped
// before Kestrel starts.
builder.Services.AddNodeRecurringInvoices();
// ── MD-2 — home-failover fencing-epoch store + the G-4 in-transaction fence ───────────────────────────
//
// Registers the durable, tenant-keyed, roster-signed HomeEpochRecord store (the bump-on-promotion path)
// and the G-4 in-transaction fence adapter. NodeEfJournalStore resolves IWriteEnlistment registrations
// through its declared registry, so a JE post inside an ambient HomeEpochWriteScope is fenced against the
// tenant's current home epoch IN ITS OWN TRANSACTION (a superseded home cannot commit; no split-brain
// double-commit).
//
// The fence is a NO-OP until a doctype is activated to multi-home (MD-3/MD-4, gated by G-7 + the open Q3):
// no production write path opens a HomeEpochWriteScope yet, so registering this slice changes NO existing
// single-device posting behaviour — it only makes the fence mechanism AVAILABLE for the separately-gated
// multi-home activation. Built + exercised by tests in isolation here per MD-2 scope.
builder.Services.AddNodeHomeEpochFence();

// ADR 0101 Rev 3.2 precondition 2 / ADR 0168 D2-A4(a) — the GENESIS home-epoch write. On the first
// start with an active team (install / first enrollment) this backstop writes the signed genesis
// HomeEpochRecord (epoch 1, home = THIS node's stable public key) for the active tenant through the
// SAME verified AdvanceAsync path every later promotion uses — so a single-device install is a
// RECORDED, signed fact, never an inferred absence. Gated on a POSITIVE origin claim (the active
// team's roster genesis founder key must BE this node's principal, and the roster's team must be the
// active team) — that closes the WIRE-JOIN vector: an adopter of an inviter's team is refused by
// name, never a silent self-genesis. Idempotent (an existing epoch means no-op), so a
// restart or a re-run of enrollment never mints a second genesis. Genesis write ONLY — no multi-home
// promotion is triggered (MD-3/MD-4 deferred). Registered AFTER MultiTeamBootstrapHostedService (hosted
// services start in registration order) so the active team is set when it runs, mirroring
// HostedGovernanceGenesisService.
builder.Services.AddHostedService<Harborline.Api.LocalNodeHost.Data.HomeEpoch.HostedHomeEpochGenesisService>();

// ── ADR 0135 — durable PROCESS ENGINE (slice 1 core + slice 2 handlers), WIRED LIVE ──────────────────
//
// The durable process engine (ADR 0135 D1/D2): the 3-table store (instances / events / idempotency) on the
// recoverable LocalNodeDbContext + atomic advance (build invariant #1 — a step's JE effect co-commits with
// the advance in ONE SQLite transaction) + the 4-trigger dispatcher. Wired together with its two v1 typed
// handlers (the rule-of-three scope — no general step-graph executor):
//   * Handler A — invoice > $5k → approve → post: under-threshold auto-posts; over-threshold PARKS a human-
//     task carrying the FE-1 basis (posting preview + the decision-table row/version that fired, D7); on
//     approve advances to the CP post step (the human approval IS the CP-park gate, ADR 0135 §Prerequisites).
//   * Handler B — recurring generation on the schedule trigger: the daemon (NodeRecurringScheduleSource,
//     RRULE over the recoverable schedule rows) advances each occurrence via the atomic advance, reusing the
//     bug-1337 deterministic (scheduleId, occurrenceDate) derivation so a crash-resume never double-posts.
//
// SC4-C2 (unchanged by this wiring — the SC4-T9(b) forbidden-set IL scan + the per-cluster Layer-2 DI-graph
// assertions stay green): the engine's ONLY persistence sink is the recoverable local-node.db (the workflow
// store rides LocalNodeDbContext via WorkflowEntityModule; the step effects stage a JournalEntry onto the
// SAME context). It takes NO IEventLog / kernel-CRDT-writer dependency (blocks-workflow references none), so
// the orphan-vector forbidden-set is unaffected. AddNodeWorkflowEngine runs AFTER AddNodeFinancialPosting
// (TimeProvider) + the SQLCipher LocalNodeDbContext registration above. AddNodeWorkflowHandlers registers the
// two handlers + their host financial contexts + the schedule source; AddNodeWorkflowScheduleDaemon takes the
// engine live on the schedule trigger.
builder.Services.AddNodeWorkflowEngine();
builder.Services.AddNodeWorkflowHandlers();
builder.Services.AddAccessGrantFormSubmission();
builder.Services.AddNodeWorkflowScheduleDaemon();

// ── ADR 0135 invoice-approval VERTICAL FLOW — the CP human-task (Ask-bar Inbox) API ──────────────────
//
// Wires the parked-approval-task surface (ADR 0135 §2.8.2): GET /api/local-node/approval-tasks lists the
// parked over-threshold invoice-approval human-tasks WITH their FE-1 basis payload; POST
// /api/local-node/approval-tasks/{instanceId}/action resumes the engine (approve → posts EXACTLY ONCE via
// the live approval context's IssueAsync effect; reject → no post; send-back → round-trip). The cutover the
// invoice-issue route consults (an over-threshold issue routes here instead of posting inline — the
// no-double-post construction) + the parked-task read model are registered by AddNodeWorkflowHandlers above.
// MUST run AFTER AddNodeWorkflowHandlers + AddNodeInvoiceWrites. Registered before SharedHostedWebApp so the
// paths are mapped before Kestrel starts.
// ── T2b local-first sweep — node-side contacts (People) CRUD ─────────────────────────────────────────
//
// Flip the tenant-wide contacts surface onto the embedded node so ContactsPage + ContactDetailPage list/
// view/create/update contacts fully offline (signal-bridge STOPPED). Contacts have NO entity dimension
// (the People read model is tenant-wide), so there is no entity header; the node serves the active-team-
// derived tenant (ADR 0032 — ActiveTeamTenantContext, not a fixed "local"). NodeEfPartyRepository is a
// near-verbatim port of the Bridge
// EfPartyRepository over the recoverable LocalNodeDbContext — both IPartyReadModel + IPartyWriteService.
//
// No new schema: the Party + contact-history tables (parties, party_email_addresses, party_phone_numbers,
// party_addresses, party_roles) are mapped by the shared PeopleEntityModule (registered above) and
// already exist in the node InitialSchema migration. So this flip adds no migration on either
// LocalNodeDbContext or SignalBridgeDbContext.
//
// SC4-C2: the repository's ONLY persistence sink is the recoverable local-node.db; the event publisher
// is the explicit NoopDomainEventPublisher (no cross-cluster event bus), and no kernel CRDT-writer /
// per-team event log is reachable, so SC4-T9(b) stays green. ADDITIVE — the Bridge contacts path is
// untouched; the frontend flip is a sequenced follow-up after the pattern-009 SPOT-CHECK. Registered
// before SharedHostedWebApp so paths are mapped before Kestrel starts.
builder.Services.AddNodeContacts();
// ── Calendar thin-slice — node-side read-only agenda + free/busy (ONR app-calendar survey 2026-06-24) ──
//
// Surfaces blocks-calendar's scheduling primitives in the Harborline App over HTTP loopback. AddNodeCalendar()
// registers the calendar block (AddBlocksCalendar — ZERO consumers on main) then OVERRIDES its in-memory
// ICalendarEventStore / IResourceAvailabilityStore with the durable NodeEf stores over the recoverable
// SQLCipher local-node.db (the IDbContextFactory<NodeLocalCalendarDbContext> is registered by
// AddSqlCipherLocalNodeDbContext above; the migration is applied by the encryption guard). The thin slice
// is READ-ONLY: GET .../calendar/occurrences (the agenda) + GET .../calendar/free-busy (free slots + busy
// intervals). Tenant resolves server-side per call (NodeTenant.Resolve — the Harborline App sends no tenant id);
// closed-over deps (no [FromServices], bug-2849). NO write/book path (inc-2) + NO visibility clip (inc-3) —
// both deferred. Registered before SharedHostedWebApp so paths are mapped before Kestrel starts.
builder.Services.AddNodeCalendar();
// First scheduling product flight: compiled into the binary, but its executable draft API is
// admitted only for the customer-zero dogfood profile. Ordinary releases default this setting OFF
// in appsettings.json; the WinHub dogfood template opts in explicitly. The additive encrypted-table
// migration may exist while inert so a later pointer flip never needs an ad-hoc schema path.
var schedulingDogfoodEnabled =
    builder.Configuration.GetValue<bool>("LocalNode:SchedulingDogfood:Enabled");
if (schedulingDogfoodEnabled)
{
    Harborline.Api.LocalNodeHost.Data.Scheduling.NodeSchedulingComposition.AddNodeSchedulingAuthoring(builder.Services);
}
// ── Owned-calendar collection surface (calendar productization #149, C1) — list + create calendars ────
//
// The calendar-collection is the primary organizing unit (design §1.5): GET/POST
// .../calendar/calendars over the durable NodeEfCalendarStore (registered by AddNodeCalendar above).
// A WRITE surface, so it carries per-route caller-auth (defence-in-depth over the authoritative
// listener-level gate — CIC 2026-07-07 / #138 / PR-1851 posture: writes carry the marker from day one).
// Registered before SharedHostedWebApp so paths are mapped before Kestrel starts.
// ── Default-calendar provisioner (calendar productization #149, C1; design §1.4) — STRUCTURAL, not seed
//
// On the clean path in EVERY environment (NOT dev-gated), ensures the founder tenant owns exactly one
// default "My calendar" (idempotent — provisions only when the tenant has zero calendars). Bound to the
// founder principal (single-founder MVP; per-principal deferred to #118). Its name is the i18n key
// (calendar.defaultName), resolved to a localized "My calendar" by the Harborline App — never a hardcoded seed
// literal (§1.6). Registered AFTER the encryption guard (calendar context migrated) +
// MultiTeamBootstrapHostedService (active team seeded) and BEFORE CalendarDevSeeder (so the dev seed can
// assign its events to this default calendar — populating CalendarEvent.CalendarId).
builder.Services.AddHostedService<DefaultCalendarProvisioningService>();

// ── Org-branding (tenant-branding slice T1) ──────────────────────────────────────────────────────────
// The node STORES + SERVES the active tenant's OrgBrandingProfile (org name / logo refs / AA-gated accent).
// AddNodeOrgBranding registers the durable EF IOrgBrandingStore + the fallback-ladder IOrgBrandingResolver
// over the recoverable SQLCipher local-node.db (the IDbContextFactory<NodeLocalOrgBrandingDbContext> is
// registered by AddSqlCipherLocalNodeDbContext above; the migration is applied by the encryption guard). The
// hosted endpoint maps GET/PUT .../org-branding + GET/POST .../org-branding/logo — tenant resolves server-side
// per call (NodeTenant.Resolve); closed-over deps (no [FromServices], bug-2849); logo bytes land on the
// node IBlobStore. Registered before SharedHostedWebApp so paths are mapped before Kestrel starts.
builder.Services.AddNodeOrgBranding();
// ── DEV-ONLY calendar seed — populate the durable calendar so the REAL Harborline App path shows data ───────
//
// On a DEVELOPMENT node only, CalendarDevSeeder writes a Mon–Fri 9–5 availability window + a populated
// current week of events (a couple on "today") into the SAME (tenant, resource) the Harborline App calendar
// route queries — the active-team-derived tenant (NodeTenant.Resolve) + party:party-dr-smith (the
// Harborline App MOCK_RESOURCE default) — so the real Tauri Harborline App path renders a populated calendar, not just
// the browser-preview mock. The gate is AIRTIGHT + fail-safe OFF: it seeds ONLY when
// IHostEnvironment.IsDevelopment(); external flags cannot opt a non-Development environment in, and it
// NEVER runs in Production (so a real tenant's calendar is never polluted). Idempotent: it seeds only
// when the tenant's calendar event store is empty (a restart is a no-op). Registered AFTER the
// encryption guard (which migrates the calendar context) + MultiTeamBootstrapHostedService (which seeds
// the active team) so the schema exists + a tenant is resolvable when it fires; WRITE-only — it does not
// touch the read endpoints or the clip/auth path.
builder.Services.AddHostedService<CalendarDevSeeder>();

// ── Comms append-log pilot — node-side team-comms messaging (FIRST messaging doctype) ────────────────
//
// A team-wide, append-only, ordered message log built on the ADR 0032 identity foundation, mirroring the
// contacts doctype but with an ICrdtList (append-log) instead of an ICrdtMap. AddNodeComms registers the
// CommsCrdtProjection (the CRDT↔delta bridge over the recoverable NodeLocalCommsDbContext) + the
// (idempotent) install-level delta router; the comms doctype registers on that router as doctype #2 via
// CommsSyncBootstrapHostedService (above). The POST route signs each message with the node's canonical
// operator identity so the converged log attributes each message to its author cryptographically; the GET
// route returns the active team's log in authored order. Tenant-scoped via NodeTenant.Resolve(activeTeam)
// (ADR 0032) — a different active team is a different log.
//
// SC4-C2: the projection's ONLY durable sink is the recoverable NodeLocalCommsDbContext (SQLCipher
// local-node.db); the CRDT document is in-memory convergence state, never a seed-keyed per-team KV store.
// Append-only — no edit/delete (the GL's shape). Channels/DMs/per-channel-access are OUT of scope (later).
// Registered before SharedHostedWebApp so paths are mapped before Kestrel starts.
builder.Services.AddNodeComms();
// ── C5 — ROSTER-BOUND DM KEYS (DM content confidentiality). ───────────────────────────────────────────
//
// AddNodeComms registers the FAIL-CLOSED NoDmConversationKeyProvider (no DM key derivable in a minimal graph —
// the B1 structural fence from PR #1325). HERE, in the FULL host, OVERRIDE it with the real roster-bound
// resolver: the active member's DM PRIVATE key is HKDF(node-root-secret, genesisTeamId) (node-secret, NOT
// party-id-derivable — the fix for the C4 false-positive), and a DM peer's DM PUBLIC key is resolved from the
// VERIFIED team roster (distributed on the synced roster record + the enrollment wire). A non-participant — even
// a team member who knows both party ids + the dm: hash — holds neither participant's DM private key and CANNOT
// derive the per-conversation seal key (the real no-leak guarantee). The C4 derived-ECDH + sign-then-encrypt +
// AAD + ChaCha20-Poly1305 construction is UNCHANGED — only the key SOURCE swaps (NoDm → roster-bound). With the
// crypto proven (three sec-eng deep-review rounds; the non-participant leak property GREEN in CI against the REAL
// roster-bound keys) and post-C5 cross-machine enrollment wire-verified, CIC approved exposing DMs (2026-06-22):
// the DM route now SHIPS (CommsDmFeatureFlag default ON; kill-switch HARBORLINE_COMMS_DM_DISABLED). Confidentiality
// rests on THIS roster-bound resolver, not on the route flag.
builder.Services.AddRosterBoundDmKeyProvider(rootSeed, capturedGenesisTeamId, capturedGenesisPartyId);
// ── MD-1 TENANT-DEK PAIRING (joint ADR 0113+0117 amendment) — distribute the tenant DEK to an admitted party ──
//
// G-1 (the no-mock-crypto fence): AddNodeTenantDekPairing registers the FAIL-CLOSED NoTenantDekPairingResolver
// (a minimal graph can wrap NO DEK to anyone — the same B1 posture the DM key provider uses). HERE, in the FULL
// host, OVERRIDE it with the roster-bound resolver: a tenant DEK may be wrapped ONLY to a recipient key the
// VERIFIED roster bound for an admitted party (MemberRoster.DmPublicKeyOf — the signed, genesis-rooted DM key),
// never to an identity-derivable stand-in. An unadmitted/rogue party resolves null → no DEK is minted for it
// (fail-closed). The wrap itself carries an outer roster-admin signature + context-binding (tenantId,
// recipientPartyId, homeEpoch, purpose) so a forged/replayed/context-mismatched wrap is rejected (F-A). The
// DekPairingResolverArchFence asserts the shipped assembly declares ZERO identity-derivable DEK resolvers and
// that production resolves THIS roster-bound provider; the leak test runs against the REAL resolver (the
// bug-1312 lesson — a green leak test against a stand-in would be a FALSE POSITIVE).
builder.Services.AddNodeTenantDekPairing();
builder.Services.AddRosterBoundTenantDekPairingResolver();

// ── PQC 2c-iii-c — the hybrid-write CUTOVER enable (ADR 0004 Amendment 2 GATE condition 5) ────────────
//
// kernel-security registers the fail-closed DisabledHybridKemWritePolicy by default (suite #1 only — the
// pre-cutover state). HERE the host turns the cutover ON per the per-install config flag
// LocalNode:HybridKemWrite:Enabled: when true (and the env kill-switch is NOT engaged) the tenant-DEK +
// role-key writers box the post-quantum hybrid suite #3 (standard X-Wing) for X-Wing-CAPABLE recipients —
// closing the HNDL hole. A NON-capable recipient still safely degrades to suite #1 (the per-recipient
// capability gate in TenantDekWrapper / RoleKeyManager). Read-before-write (2c-iii-a, #1488) already merged,
// so emitted suite-#3 boxes are openable; the read path keeps accepting #3 regardless of this flag.
//
// STAGED ROLLOUT (local-first): the fleet is per-install hosts, so this flag IS the rollout knob — opt a
// CANARY cohort of installs in first, observe GATE-6 offline round-trip + unwrap health, then widen. Default
// OFF, so an un-opted-in install stays on suite #1 (no all-installs-on-this-build flip).
//
// KILL-SWITCH (GATE condition 5): set LocalNode:HybridKemWrite:Enabled=false + restart, OR — the no-redeploy
// incident path — set HARBORLINE_PQC_HYBRID_WRITE_DISABLED truthy, which FORCES the policy off even when config
// enables it (AddNodeHybridKemWritePolicy ANDs config with NOT-kill-switch). Either reverts the writer to
// suite #1 with nothing stranded (the read path still opens previously-emitted #3 boxes).
builder.Services.AddNodeHybridKemWritePolicy(localNodeOptions.HybridKemWrite.Enabled);

// ── Roster-sync doctype (production-wiring gap #1) — the TRUST ROSTER propagates across nodes ─────────
//
// The trust roster (the MemberRoster signed admission log) was local-only/in-memory, seeded from LOCAL
// genesis only — it never crossed the wire (the two-user harness FAKED it by seeding both nodes). AddNodeRoster
// registers the RosterCrdtProjection (the append-log CRDT↔delta bridge over the recoverable
// NodeLocalRosterDbContext) + the (idempotent) install-level delta router; the roster doctype registers on that
// router via RosterSyncBootstrapHostedService (above), which also seeds the local genesis self-admission AS a
// synced record. On every inbound merge the projection reconstructs the WHOLE team roster from the synced
// records via MemberRoster.FromSyncedRecords — re-validating each record to genesis (signature + admitter
// authority + no-escalation; a revocation's revoker holds members:revoke) and DROPPING any forged / unsigned /
// wrong-signer / orphan / second-genesis record fail-closed — then adopts the validated roster into the live
// NodeTeamRoster. So the comms rosterBinding + the MemberSetTrustPolicy read the SYNCED membership: a member
// admitted on a peer now forge-proves here, and a synced revocation drops the member (eventual-convergence —
// the offline window). A peer CANNOT inject a fake member by sending a bogus roster delta (the trust anchor).
//
// SC4-C2: the projection's ONLY durable sink is the recoverable NodeLocalRosterDbContext (SQLCipher
// local-node.db); the trust is in the per-record signature (re-validated on rebuild), not the storage. A fresh
// single-user node still works (local genesis self-admission seeds the roster; it syncs ON TOP when peers
// exist). The admission MECHANISM (proximity/QR + invite) is the AdmissionCoordinator (already wired, gap #3
// surface separate); this is the SYNC of the records that mechanism produces.
builder.Services.AddNodeRoster();
builder.Services.AddSingleton<
    Harborline.Api.LocalNodeHost.Data.Identity.ISelectedSessionAuthorizationEpochReader,
    Harborline.Api.LocalNodeHost.Data.Identity.NodeSelectedSessionAuthorizationEpochReader>();
builder.Services.AddSingleton<
    Harborline.Api.LocalNodeHost.Data.Identity.ISelectedSessionPermissionResolver,
    Harborline.Api.LocalNodeHost.Data.Identity.SelectedSessionPermissionResolver>();

// ── KG-search "360-view" Slice 0 (ADR 0135 KG-search F3-lift amendment) ───────────────────────────────
//
// The deterministic, model-free, PEP-free, permission-CLIPPED local-first search over the EXISTING domain
// entities: search_nodes (a derived projection of Party/Lease/Invoice/JournalEntry/… — NOT a new source of
// truth), search_edges (their structured FK/reference relationships), and the real FTS5 trigram virtual
// table search_fts. The CRUX is the FAIL-CLOSED clip seam (G-1): AddNodeKnowledgeGraphSearch registers
// ClosureAuthorizedRecordSetProjection as the SOLE producer of every FTS5/graph query's record-narrowing
// WHERE (no "no-clip" alternative exists), so an un-clipped query is structurally impossible — fail-closed,
// never a tenant-wide fall-through to the bare ADR-0077 resolver (the SearchClipArchFence fences BOTH the
// FTS5 MATCH path AND the search_nodes content table).
//
// The clip resolves over the durable materialised closure in the same BEGIN IMMEDIATE transaction as search.
// Source writes advance catalog or tenant versions; the next read lazily rebuilds only the stale tenant.
builder.Services.AddNodeAuthorizationModel();
builder.Services.AddNodeKnowledgeGraphSearch();

// ── KG-search Slice 1b — the SECURE VECTOR layer (ADR 0135 KG-search F3-lift amendment) ────────────────
//
// Built + fully test-proven as SUBSTRATE (the same posture Slice 0 shipped in — no production route consumes
// it yet). It adds, under the G-1..G-6 + M1 gates:
//   • the vec0 / brute-force KNN over a per-subject-ENCRYPTED, binary-quantized embedding index
//     (search_vec_rows), with the EXACT clip on the vec path (metadata-column record_id IN (…) pre-filter —
//     VecRecordClip is the sole producer; VecClipArchFence makes an un-clipped vec read structurally
//     impossible);
//   • the durable NodeEfGrantStore (G-2) co-located in this SAME SQLCipher file, so the clip's grant read and
//     the scan run in ONE read transaction (single-snapshot revoke-TOCTOU closure — the Slice-0 deferral);
//   • G-3 OnlineOnly⇒never-index on the vec path; G-5 model+modelVersion+dimension pin (mismatch = hard
//     fault); G-6 per-subject-keyed index (crypto-shred a subject ⇒ their embeddings undecryptable + purged);
//   • the M1 no-fake-as-real gate (the indexer REJECTS any artifact whose model is not a registered floor;
//     the stub self-identifies as `stub-bge-m3` and a no-provider host fails closed —
//     provider.kg_floor_unavailable); and the RRF fusion of the dense (vec0) + lexical (FTS5) legs.
//
// WIRED LIVE in Slice 1d (this block). The secure vector layer is now registered into the composition root:
//
//   (a) the node's REAL root-seed-derived ITenantKeyProvider (RootSeedTenantKeyProvider over THIS install's
//       32-byte root seed — NOT the dev in-memory stub's shared development salt), so the per-subject embedding
//       sealing (G-6) uses install-secret key material. This MUST be registered BEFORE
//       AddHarborlineRecoveryCoordinator so its TryAdd keeps OUR provider, not the in-memory stub;
//   (b) the foundation-recovery crypto substrate (AddHarborlineRecoveryCoordinator) — ISubjectFieldEncryptor /
//       ISubjectFieldDecryptor (G-6) + the subject-erasure flow. The host already registered IOperationSigner
//       (NodePrincipalSigner) + IOperationVerifier + the kernel audit trail above, so the audited erasure
//       service resolves;
//   (c) the secure vector layer (AddNodeKnowledgeGraphVectorSearch) — replaces IGrantStore with the durable
//       file-co-located NodeEfGrantStore (G-2 same-txn), registers the indexer/read-service/KNN-engine under
//       the G-1..G-6 + M1 gates, AND registers the KgVecIndexSubjectErasurePropagator (F1 — the subject-erasure
//       flow now purges this index's per-subject rows + their cleartext vec0 residue on a crypto-shred);
//   (d) the REAL capability-driven IKgEmbeddingProvider + IKgReranker (the agent-client doctrine — the kg-embed
//       CLI drives the G-4 sandboxed BGE-M3 / bge-reranker-v2-m3 worker) WHEN LocalNode:KnowledgeGraph:CapabilityCliPath
//       is configured; otherwise NO provider is registered ⇒ the indexer fails closed at ingest
//       (provider.kg_floor_unavailable) rather than indexing an unverifiable artifact (M1).
//
// F2 (host-gated, NOT enabled here): the real vec0 native path stays OFF (HARBORLINE_KG_VEC0_REAL unset) until the
// native-gated parity + load-extension-re-disable test passes on Mac + Windows (po-mac/po-win follow-on). The
// composition selects the native-free brute-force KNN engine on this host, which carries the identical clip +
// per-subject-decrypt security semantics.
{
    var kgOptions = localNodeOptions.KnowledgeGraph;

    // (a) Stored, destroyable tenant/subject keys under ticket 043's resolved at-rest hierarchy.
    // The root-derived provider is retained only as a version-1 migration reader; current writes use
    // random stored version-2 keys and the erasure service destroys their subject slots.
    var legacyTenantKeys =
        new Harborline.Api.LocalNodeHost.Data.Search.Vector.RootSeedTenantKeyProvider(rootSeed);
    builder.Services.ConfigureStoredTenantKeys(
        Path.Combine(installFootprint.DataDirectory, "tenant-keys"),
        keyHierarchy.AtRestRootKey.Span,
        legacyTenantKeys);

    // (a2) the DURABLE GDPR subject-erasure stores (#1378 M-1 fix; ADR 0135 GDPR direction, ADR 0068 §1.3).
    //      These MUST be registered BEFORE AddHarborlineRecoveryCoordinator so its TryAddSingleton keeps OURS, not
    //      the restart-volatile InMemory* defaults. The registry prevents replacement-key creation after the
    //      stored provider deletes the subject slot; losing it could create a fresh key for an erased identity.
    //      Both stores ride the SAME SQLCipher
    //      local-node.db file (NodeLocalSearchDbContext) as the per-subject-encrypted KG index they gate, so a
    //      shred + its tombstone SURVIVE a restart. DI is lazy: the IDbContextFactory<NodeLocalSearchDbContext>
    //      these resolve is registered in AddSqlCipherLocalNodeDbContext*, so registering ahead of it is fine.
    builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Erasure.ISubjectErasureRegistry,
        Harborline.Api.LocalNodeHost.Data.Search.Vector.NodeEfSubjectErasureRegistry>();
    builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Erasure.ISubjectTombstoneStore,
        Harborline.Api.LocalNodeHost.Data.Search.Vector.NodeEfSubjectTombstoneStore>();

    // (b) the foundation-recovery crypto + subject-erasure substrate (G-6).
    builder.Services.AddHarborlineRecoveryCoordinator();

    // (b2) FAIL-CLOSED durable-erasure-store gate (#1378 M-1; the #1351 deep-review's structural gate). Throws at
    //      composition time unless BOTH erasure stores resolve to a NON-volatile implementation — i.e. proves the
    //      durable stores above won the TryAdd and the host is NOT silently shipping the volatile defaults behind
    //      a live erasure service. A node that wires the erasure path live MUST pass this gate; otherwise startup
    //      fails deterministically rather than leaving the Art-17 resurrection hole open.
    builder.Services.RequireDurableErasureStores();

    // (d) the real capability-driven embedding provider + reranker — ONLY when the CLI path is configured. The CLI
    //     drives the G-4 sandboxed worker (real BGE-M3 / bge-reranker on opt-in; a self-identifying stub
    //     otherwise, which the M1 gate refuses). The plaintext rides stdin, never argv.
    Harborline.Api.LocalNodeHost.Data.Search.Vector.IKgEmbeddingProvider? kgEmbeddingProvider = null;
    if (!string.IsNullOrWhiteSpace(kgOptions.CapabilityCliPath))
    {
        var cliClient = new Harborline.Api.LocalNodeHost.Data.Search.Vector.Cli.CapabilityKgCliClient(
            kgOptions.NodeBinary, kgOptions.CapabilityCliPath!, kgOptions.ArmRealWorker, kgOptions.WorkerPython);
        kgEmbeddingProvider = new Harborline.Api.LocalNodeHost.Data.Search.Vector.Cli.KgCliEmbeddingProvider(
            cliClient, kgOptions.TimeoutMs);
        builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.Data.Search.Vector.IKgEmbeddingProvider>(
            kgEmbeddingProvider);
        builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.Data.Search.Vector.IKgReranker>(
            new Harborline.Api.LocalNodeHost.Data.Search.Vector.Cli.KgCliReranker(cliClient, kgOptions.TimeoutMs));
        Console.WriteLine(
            "[local-node-host] KG-search Slice 1d: REAL capability-driven embedding + rerank provider WIRED "
            + $"(kg-embed CLI '{kgOptions.CapabilityCliPath}', real-worker={(kgOptions.ArmRealWorker ? "ARMED" : "stub")}). "
            + "The CLI drives the G-4 sandboxed BGE-M3 / bge-reranker-v2-m3 worker; a non-armed host produces "
            + "self-identifying stubs the M1 gate refuses (fail-closed at ingest).");
    }
    else
    {
        Console.WriteLine(
            "[local-node-host] KG-search Slice 1d: NO capability embedding provider configured "
            + "(LocalNode:KnowledgeGraph:CapabilityCliPath unset) — the vector index ingest path fails closed "
            + "(provider.kg_floor_unavailable); it never indexes an unverifiable artifact (M1).");
    }

    // (c) the secure vector layer (G-1..G-6 + M1 + the F1 erasure propagator).
    builder.Services.AddNodeKnowledgeGraphVectorSearch(kgEmbeddingProvider, allowStubModel: false);

    Console.WriteLine(
        "[local-node-host] KG-search Slice 1d: SECURE VECTOR layer WIRED LIVE (stored tenant/subject keys + "
        + "recovery crypto + NodeEfGrantStore G-2 + per-subject index G-6 + F1 erasure→vec0 purge wire). "
        + "Erasure reaches rostered replicas that receive the shred; it does not reach a replica that left "
        + "the roster before the shred. F2 real-vec0 native stays OFF pending the host-gated parity probe.");

    // (e) KG Slice 2-foundation — the safe interim PROPOSAL-ONLY generative GraphRAG (firewall-bound /
    //     human-CP-gated / §2.8.4-firewall-to-retrieved-text). Registers the grounding assembler (G-G2) + the
    //     firewall-bound GroundedProposalService, plus the REAL capability-driven generation provider WHEN the
    //     kg-generate CLI path is configured. The CLI drives the G-4 sandboxed Qwen2.5 worker that reads
    //     UNTRUSTED retrieved grounding and emits a TEXT PROPOSAL — the model has no hands (no tools / no egress /
    //     no DEK reach). When unset, NO generation provider is registered ⇒ the service fails closed
    //     (provider.kg_generate_floor_unavailable) rather than surfacing a fabricated proposal as genuine (M-G1).
    //     PROPOSAL-ONLY: the proposal is returned to the caller, NEVER acted on; the AUTONOMOUS form stays
    //     broker-PEP-gated by ratified design (NOT built), and the CP-park is the next sub-slice (2-actions). Only
    //     takes effect when the Slice-1 read service is present (an embedding provider was configured above).
    if (kgEmbeddingProvider is not null)
    {
        Harborline.Api.LocalNodeHost.Data.Search.Generation.IKgGenerationProvider? kgGenerationProvider = null;
        if (!string.IsNullOrWhiteSpace(kgOptions.GenerateCapabilityCliPath))
        {
            var generateCli = new Harborline.Api.LocalNodeHost.Data.Search.Generation.Cli.CapabilityKgGenerateCliClient(
                kgOptions.NodeBinary,
                kgOptions.GenerateCapabilityCliPath!,
                kgOptions.GenerateArmRealWorker,
                kgOptions.GenerateWorkerPython);
            kgGenerationProvider =
                new Harborline.Api.LocalNodeHost.Data.Search.Generation.Cli.KgCliGenerationProvider(
                    generateCli, kgOptions.TimeoutMs);
            builder.Services.AddSingleton<Harborline.Api.LocalNodeHost.Data.Search.Generation.IKgGenerationProvider>(
                kgGenerationProvider);
            Console.WriteLine(
                "[local-node-host] KG-search Slice 2-foundation: REAL capability-driven GENERATION provider WIRED "
                + $"(kg-generate CLI '{kgOptions.GenerateCapabilityCliPath}', "
                + $"real-worker={(kgOptions.GenerateArmRealWorker ? "ARMED" : "stub")}). The CLI drives the G-4 "
                + "sandboxed Qwen2.5 worker (no tools / no egress); a non-armed host produces self-identifying "
                + "stubs the M-G1 gate refuses (fail-closed). PROPOSAL-ONLY — the autonomous form stays PEP-gated.");
        }
        else
        {
            Console.WriteLine(
                "[local-node-host] KG-search Slice 2-foundation: NO capability generation provider configured "
                + "(LocalNode:KnowledgeGraph:GenerateCapabilityCliPath unset) — the grounded-proposal path fails closed "
                + "(provider.kg_generate_floor_unavailable); it never surfaces a fabricated proposal as genuine.");
        }

        builder.Services.AddNodeKnowledgeGraphGeneration(kgGenerationProvider);
    }
}

// ── Documents pillar D3 — the render/issue pipeline, WIRED LIVE (#111 §6 D3) ───────────────────────────
//
// The D1/D2 keystone proved DocumentIssuanceService end to end in a hand-built test host; this block
// composes the SAME pipeline into the node's real composition root so the D3 Harborline App UI (the /documents
// editor's live preview + the publish/issue guard) has a real HTTP surface to call. Registered AFTER
// AddHarborlineRecoveryCoordinator (above) so ISubjectFieldEncryptor resolves for the snapshot seal (§5.5).
//
//   (a) IPdfExportWriter — PDFsharp+MigraDoc (MIT), the ADR-0021 default adapter, the ONLY adapter this
//       desktop/JIT node registers (§3.4 — the mobile-carve AOT adapter is a later, off-critical-path swap).
//   (b) DocumentRenderWalker — the block-tree walker (stateless; one instance).
//   (c) The legal-hold subsystem (ADR 0142 / council F2), durable (a file-backed store under the node's
//       data directory, NOT the restart-volatile in-memory default) — a hold placed on ISSUE must survive a
//       restart or a crypto-shred could resurrect a retention-required subject's snapshot (the same
//       "durable gate w/ live service" invariant the GDPR erasure stores above already carry).
//   (d) IIssuedDocumentStore — in-memory today (mirrors the IDocumentTemplateRegistry registration above:
//       "in-memory today; a durable store is a Wave-2 axis" — the SAME documented interim, not a new one).
//   (e) DocumentIssuanceService over (a)-(d) + IBlobStore (already the node's EnvelopeBlobStore — no-side-door,
//       §C-3) — the one authoritative render+mint pipeline (§3.2).
//   (f) The D3 render/issue HTTP endpoint (HostedDocumentTemplateApiEndpoint) — a thin accessor-closure
//       wrapper (bug-2849 pattern) exposing POST .../render (non-minting preview) + POST .../issue (CP mint).
{
    builder.Services.AddPdfSharpDocumentRenderer();
    builder.Services.AddSingleton<Harborline.Api.Foundation.Documents.Rendering.DocumentRenderWalker>();

    var documentsLegalHoldFile = System.IO.Path.Combine(
        localNodeOptions.DataDirectory ?? System.IO.Path.Combine(AppContext.BaseDirectory, "data"),
        "documents",
        "legal-holds.jsonl");
    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(documentsLegalHoldFile)!);
    builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.LegalHold.ILegalHoldStore>(
        new Harborline.Api.Foundation.Recovery.LegalHold.FileSystemLegalHoldStore(documentsLegalHoldFile));
    builder.Services.AddHarborlineLegalHold();
    builder.Services.RequireDurableLegalHoldStore();

    builder.Services.AddSingleton<Harborline.Api.Foundation.Documents.Issuance.IIssuedDocumentStore,
        Harborline.Api.Foundation.Documents.Issuance.InMemoryIssuedDocumentStore>();

    builder.Services.AddSingleton<Harborline.Api.Foundation.Documents.Issuance.DocumentIssuanceService>();
}

// ── ADR 0055 — dynamic-FORMS engine, WIRED LIVE (the "built-but-unwired" amendment, 2026-06-25) ──────────
//
// The ADR 0055 forms engine (foundation-forms + foundation-forms-engine) is built, hardened (INV-S1..S4),
// and durable — but grep found ZERO IFormEngine / AddHarborlineFormEngine consumers in any apps/ host (ONR
// survey, earlier repository ticket #1460). This block makes it LIVE on the embedded node: AddNodeForms registers the four
// substrates FormEngine composes — the kernel schema registry, the asset entity store + audit log, the
// durable Entity-store-backed IFormDefinitionStore, and the macaroon form-capability issuer/verifier pair —
// plus the engine itself. IFieldEncryptor (INV-S3 PII-at-rest) is already registered by
// AddHarborlineRecoveryCoordinator above, so AddNodeForms is sequenced AFTER the recovery block.
//
// The HostedFormsApiEndpoint maps the render/submit routes (GET /api/local-node/forms/{formId},
// POST /api/local-node/forms/{formId}/submit) so the React SchemaForm renderer (a LATER ui-react item) can
// consume a typed FormView over the Harborline App→local-node-host HTTP channel. The route mints a verified
// CapabilityToken through the real macaroon issuer→verifier round-trip (the verifier is the ONLY component
// that can mint a token) — the route never fabricates a capability.
//
// CP-SAFETY (FIRST-PARTY ONLY): this serves FormDefinitions the node itself registered. A packet-carried /
// third-party form (which could write a CP-locked field) requires the fail-closed load-time CP-reachability
// validator shared with the ADR-0135 A1 packet-carried-interpreter discipline — that gate is a SEPARATE,
// LATER item (see the ADR 0055 amendment CP-safety follow-up); nothing on this path loads an
// externally-supplied definition. Registered before SharedHostedWebApp so routes are mapped before Kestrel.

// ── PERS-1 — STORAGE-ROLE durable, mandatory-envelope-at-rest blob store (ADR 0137 D4 / C-3; ADR 0127 seam) ──
//
// On the STORAGE role (LocalNode:Role=Storage) the node is a durable canonical store, so its IBlobStore MUST
// persist CIPHERTEXT ONLY: a FileSystemBlobStore (rooted at {DataDirectory}/blobs) wrapped by an
// EnvelopeBlobStore that AES-256-GCM-seals every payload under an install-secret per-tenant blob DEK
// (ITenantKeyProvider, purpose "blob-envelope-aes") BEFORE it reaches the backend. The raw FileSystemBlobStore is
// NEVER registered as IBlobStore directly — it lives only inside the envelope wrap (the C-3 no-side-door
// invariant; the EnvelopeBlobStoreArchFence proves it). Registered BEFORE AddNodeForms so its
// TryAddSingleton<IBlobStore, NodeInMemoryBlobStore> (the volatile EDGE default) is suppressed on this role.
//
// Fail-closed key posture: RequireRealBlobEnvelopeKeyProvider throws at composition unless the effective
// ITenantKeyProvider is the REAL RootSeedTenantKeyProvider (registered above, ~the KG block) and NOT the dev
// InMemoryTenantKeyProvider stub (a derivable stand-in key — bug-1312). The blob tenant is the genesis team's
// canonical projected tenant (the SAME string ActiveTeamTenantContext.ProjectTenantId / NodeTenant.Resolve
// produce), so a blob sealed today re-derives the same key after restart.
//
// Card 3750 (gap G2.1): the EDGE role is durable too. No shipped appsettings/env sets a role, so the
// Harborline App's bundled node ran the DEFAULT Edge role and fell through to AddNodeForms'
// TryAddSingleton<IBlobStore, NodeInMemoryBlobStore> — a standing data-loss defect (blobs died with the
// process, never sealed at rest). The edge wiring below is the SAME posture gate + FileSystemBlobStore-
// inside-EnvelopeBlobStore chain, WITHOUT the storage-role durability guard (a canonical multi-copy
// store's shed-seam gate; no shed caller exists today, and when one lands the edge role's canonical-copy
// semantics need deciding then). The in-memory store is now reachable only by explicit registration
// (tests).
{
    // Mirror the existing DataDirectory-resolution convention (Program.cs ~line 644).
    var blobDataDir = localNodeOptions.DataDirectory ?? System.IO.Path.Combine(AppContext.BaseDirectory, "data");
    var blobRoot = System.IO.Path.Combine(blobDataDir, "blobs");
    if (localNodeOptions.Role == NodeRole.Storage)
    {
        // PERS-2 F4: the storage-role blob wiring (posture gate + envelope-wrapped durable store) is a SINGLE
        // reusable method the arch-fence also exercises over the real graph — not a block the fence has to
        // re-implement.
        builder.Services.ConfigureStorageRoleBlobStore(blobDataDir, capturedGenesisTeamId);
        Console.WriteLine(
            "[local-node-host] PERS-1 STORAGE role: durable mandatory-envelope-at-rest blob store WIRED "
            + $"(FileSystemBlobStore '{blobRoot}' wrapped by EnvelopeBlobStore; ciphertext only at rest, ADR 0137 "
            + "D4/C-3). The raw backend is never the registered IBlobStore (no-side-door); the key-posture gate "
            + "confirmed the real RootSeedTenantKeyProvider (not the dev stub).");
    }
    else
    {
        builder.Services.ConfigureEdgeDurableBlobStore(blobDataDir, capturedGenesisTeamId);
        Console.WriteLine(
            "[local-node-host] EDGE role: durable mandatory-envelope-at-rest blob store WIRED "
            + $"(FileSystemBlobStore '{blobRoot}' wrapped by EnvelopeBlobStore; ciphertext only at rest; no "
            + "durability guard on a single-device node). The volatile in-memory default is suppressed; the "
            + "key-posture gate confirmed the real RootSeedTenantKeyProvider (not the dev stub).");
    }
}

// F2: relay the node's configured deployment region into the forms engine so a Reside-effect
// classified field's residency is enforced against this node's REAL jurisdiction (not the silent
// hardcoded "US" the LIVE composition otherwise used on every node).
// Ticket 016 acceptance 3: production/staging use the SQLCipher-backed local-node outbox. Register it
// BEFORE AddNodeAssetRegistry eventually calls AddFormSubmitProjections (whose TryAdd fallback is the
// Development-only in-memory implementation). An explicitly pre-registered in-memory outbox still wins
// this TryAdd and is therefore still refused by InMemoryFormSubmitOutboxGuardAssertion.
if (!builder.Environment.IsDevelopment())
{
    builder.Services.TryAddSingleton<Harborline.Api.Foundation.Forms.Submission.IFormSubmitOutbox,
        Harborline.Api.LocalNodeHost.Data.Forms.NodeEfFormSubmitOutbox>();
}
builder.Services.AddNodeForms(
    localNodeOptions.HostJurisdiction,
    static (services, entityMutations, hierarchyMutations) =>
    {
        services.AddSingleton<Harborline.Api.LocalNodeHost.Data.Entities.NodeRecordSchemas>();
        services.AddSingleton(sp => new Harborline.Api.LocalNodeHost.Data.Entities.NodeEntityWriter(
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Harborline.Api.LocalNodeHost.Data.LocalNodeDbContext>>(),
            entityMutations(sp),
            sp.GetRequiredService<Harborline.Api.Foundation.Assets.Entities.EntityBodyAdmission>(),
            sp.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Entities.NodeRecordSchemas>(),
            sp.GetRequiredService<Harborline.Api.Foundation.Authorization.AuthorizationGate>()));
        services.AddSingleton<Harborline.Api.Foundation.Assets.Entities.IEntityWriteCoordinator>(sp =>
            sp.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Entities.NodeEntityWriter>());
        services.AddSingleton<Harborline.Api.LocalNodeHost.Data.Entities.IHierarchyAuthorizedAuditWriter>(sp =>
            new Harborline.Api.LocalNodeHost.Data.Entities.HierarchyAuthorizedAuditWriter(
                sp.GetRequiredService<Harborline.Api.Foundation.Assets.Audit.IAuditLog>()));
        services.AddSingleton<Harborline.Api.Foundation.Assets.Hierarchy.IHierarchyCompositeCoordinator>(sp =>
            new Harborline.Api.LocalNodeHost.Data.Entities.NodeHierarchyCompositeCoordinator(
                entityMutations(sp),
                sp.GetRequiredService<Harborline.Api.Foundation.Assets.Entities.EntityBodyAdmission>(),
                hierarchyMutations(sp),
                sp.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Entities.IHierarchyAuthorizedAuditWriter>(),
                sp.GetRequiredService<Harborline.Api.Foundation.Authorization.AuthorizationGate>(),
                sp.GetRequiredService<TimeProvider>()));
        services.AddEntityStoreWorkflowDefinitionStore(entityMutations);
    });

// Card 3750 finding 2 — structural guard against silent re-regression: if either role branch above is
// deleted (or reordered after AddNodeForms so the volatile TryAdd default wins), the node must refuse to
// compose rather than silently revert to in-memory blobs. Same idiom as RequireDurableErasureStores (#1378
// M-1). Deliberately AFTER AddNodeForms so it also catches an ordering regression.
builder.Services.RequireDurableBlobStore();
// ── D2 — save-and-resume submission-DRAFT store (ADR 0135 amendment 2026-07-01) ────────────────────
//
// The save/resume companion to the submit path above. A submission-draft is keyed by the ratified
// (TenantId, case/subject id, PartyId) tuple, resolved through the fail-closed ADR-0102 IPartyContext.
//   • AddNodeDraftPartyContext wires that party seam for the single-operator node (a sum-interface
//     adapter over the active-team ICurrentUser/IAuthorizationContext + MultiTenancy.ITenantContext,
//     plus the single-operator resolver keyed off the roster genesis party id).
//   • AddNodeSubmissionDrafts registers the DURABLE NodeEfSubmissionDraftStore (over the SQLCipher
//     local-node.db, restart-surviving) as ISubmissionDraftStore — winning the package's in-memory
//     TryAdd default — plus the fail-closed draft + pre-auth-capture services. It composes the ADR-0139
//     erasure registry (crypto-shred: an erased subject's draft is deleted + dark) and the ADR-0142
//     legal-hold registry (a held draft survives its retention TTL). Registered AFTER AddNodeForms
//     (IFormDefinitionStore exists) + the recovery/governance blocks (erasure + legal-hold registries).
//   • NodeDraftsMigrator applies the form_drafts schema into the keyed store; HostedFormDraftsApiEndpoint
//     maps PUT/GET/DELETE .../drafts/{caseId} + GET .../drafts. Both registered before SharedHostedWebApp.
builder.Services.AddNodeDraftPartyContext(capturedGenesisPartyId);
builder.Services.AddNodeSubmissionDrafts();
builder.Services.AddHostedService<Harborline.Api.LocalNodeHost.Data.Drafts.NodeDraftsMigrator>();
// ── WF-KEY (W-7 / G5) — durable workflow-DEFINITION authoring surface ─────────────────────────────
//
// The process analog of the forms-definition persistence above: the durable EntityStore-backed
// IWorkflowDefinitionStore + the GET/PUT authoring routes the Harborline App workflow BUILDER rides on. It
// composes the SAME in-memory IEntityStore AddNodeForms registered (a different reserved schema, so no
// collision) and the IWorkflowAdmissionValidator AddNodeWorkflowEngine registered above — RegisterAsync
// runs that fail-closed admission gate BEFORE the store write, so an inadmissible definition (unclassified
// action, or a CP action reachable from an autonomous trigger with no interposed human-task) is rejected at
// persist. STORAGE only: an authored+admitted definition is stored + reloaded; the general A1 interpreter
// stays gated on the broker-PEP (ADR 0143). Registered AFTER AddNodeForms (IEntityStore exists) + after
// AddNodeWorkflowEngine (IWorkflowAdmissionValidator exists), BEFORE SharedHostedWebApp (maps before Kestrel).
// ── ADR 0101 Rev 3.1 Wave 2b — Asset Type System goes LIVE ─────────────────────────────────────────
//
// Wires the typed-entity registry (entities, containment/located-at edges, entity types, condition
// history) onto the DURABLE foundation audit substrate (U1) and — critically — decorates the forms
// engine composed by AddNodeForms above so a condition-rating inspection submission captures the
// entity's typed condition assessment ("one act, two artifacts") through the governed, audited,
// gate-protected projector. Registered AFTER AddNodeForms (it decorates the registered IFormEngine and
// rides the foundation IAuditLog AddHarborlineAssetsInMemory registered) and BEFORE SharedHostedWebApp
// (HostedAssetRegistryApiEndpoint maps its routes while the shared app is still pre-StartAsync). Live
// capture is only wired here because the F-ATOM (at-least-once outbox), F-SKIP (audited skips), and
// F-CLOCK (engine submit clock) gates are closed — the runner the decoration invokes is the durable
// OutboxFormSubmitProjectionRunner, so an interrupted projection is recoverable, never silently lost.
// F-RECON: AddNodeAssetRegistry ALSO schedules the reconcile-sweep daemon (startup drain + periodic
// sweep) that DRAINS that outbox on the running node, so an interrupted projection heals automatically
// rather than only via a client retry. The sweep interval is config-bound (LocalNode:AssetRegistry:
// ReconcileSweepIntervalSeconds; default 60s) — the startup drain runs immediately regardless.
var reconcileSweepInterval = TimeSpan.FromSeconds(
    localNodeOptions.AssetRegistry.ReconcileSweepIntervalSeconds is { } secs && secs > 0
        ? secs
        : Harborline.Api.LocalNodeHost.AssetRegistryOptions.DefaultReconcileSweepIntervalSeconds);
Harborline.Api.LocalNodeHost.Data.AssetRegistry.NodeAssetRegistryComposition.AddNodeAssetRegistry(
    builder.Services, reconcileSweepInterval);
// ADR 0101 Rev 3.2 Wave 5 — the durable SpatialFrameDescriptor store + signed epoch mint (0168
// OQ-1 ruling). AFTER AddNodeAssetRegistry: the durable audit swap must already be in place. The
// extension itself hard-fails the composition unless the store resolves to the package adapter,
// the port to NodeEfSpatialFrameDescriptorPort and the authority to the host home-claim
// implementation ([A10]). Mints REFUSE fail-closed until the genesis HomeEpochRecord
// (precondition 2) is written for a tenant — that is the designed posture, not a defect.
// CP-4 (precondition 3): the composition also registers SpatialFramePiiFieldSealer, which seals
// originDescription + georeference under the tenant DEK at the storage boundary on both the
// descriptor and quarantine rows (the shipped pii binding, SubjectScoped:false). It resolves the
// root-seed ITenantKeyProvider registered above — the validate step hard-fails if that is absent.
Harborline.Api.LocalNodeHost.Data.AssetRegistry.NodeSpatialFrameComposition.AddNodeSpatialFrameDescriptors(
    builder.Services);

// G4.1 — the READ-ONLY spatial-frame resolution surface over that store (ADR 0168 D2-A5:
// tenant-scoped resolution, never node-level existence). Reads go through the package store — the
// seam that unseals the two governed PII cells — never raw rows; no write route is mapped, and the
// mint attestation / quarantine content are not projected. Registered AFTER the composition above
// so ISpatialFrameDescriptorStore resolves to the validated package adapter.
// BUILD #127 slice 1 — startup half of the pack seed projection. Re-projects the active team's ACTIVE
// installed packs into the live registries at boot (IEntityTypeRegistry now exists; MultiTeamBootstrap ran
// earlier so an active team is resolvable). A strict no-op today (the v1 pack install store is in-memory —
// nothing survives a restart to re-project; the ON-ACTIVATE trigger in the install routes is what populates
// the dropdown within a boot), wired now so it is correct-by-construction the moment the durable store (F5)
// lands. Registered AFTER AddNodeAssetRegistry (the registry) + MultiTeamBootstrapHostedService (the tenant).
builder.Services.AddHostedService<Harborline.Api.LocalNodeHost.Data.PackProjection.PackSeedProjectionHostedService>();

// Ticket 208 slice 1 (L673, L685) — Access administration ships as ORDINARY content with every
// installation. On first run this exports, installs and activates the committed
// `harborline.access-administration` export document through the same IPackExporter/IPackInstaller path
// any third-party package takes (no bypass), so the grant form, holdings view, privileged-review workflow
// and access-by-person report are catalogue definitions a later package may replace. It carries
// definitions only and writes no grant row. Registered AFTER the pack install store + the seed projection
// service, so a restart reconciles first and this preload is then an idempotent no-op.
builder.Services.AddHostedService<
    Harborline.Api.LocalNodeHost.Data.PackProjection.AccessAdministrationPreloadHostedService>();

// ── ADR 0135 A1 (W-8): the general declarative interpreter — EXECUTE an authored+admitted+persisted
// definition through the broker-PEP. Registers the interpreter (the dispatcher's fallback for definitions with
// no typed handler), the server-side confirmation context (confirmer party + kind; engine proposer), the
// principal-kind resolver, and the ledger.post-journal-entry effect factory (through the PUBLIC delegate seam —
// the internal factory type is never named; the broker gates the CP post behind a human confirm). MUST run
// AFTER AddNodeWorkflowEngine (broker/catalog) + AddEntityStoreWorkflowDefinitionStore (execution face).
builder.Services.AddNodeDeclarativeWorkflowExecution();

// ── ADR 0135 A1 / 0143 R1-F — the generic CP-effect-confirmation + executed-run-report API (PR (c)) ───
//
// Maps the Harborline App "Effect Confirmations" surface (GET/GET/POST /api/local-node/workflow-confirmations — list
// interpreter-parked CP confirmations WITH their FE-1 basis; confirm passes ONLY the decision verb, the
// interpreter re-derives the effect from the pinned durable park + resolves the confirmer server-side) + the
// read-only executed-run report (GET /api/local-node/workflow-run-report — P6 declarative reporting over the
// substrate). The read models + the dispatcher are registered by AddNodeDeclarativeWorkflowExecution /
// AddNodeWorkflowEngine above; registered before SharedHostedWebApp so the paths are mapped before Kestrel.
// ── DEV-ONLY three-way-match demo seed — drives the W-8 executed run so the confirmations + report surfaces
// render REAL interpreter data (parked CP confirmation + a completed posted run) instead of a mock. Airtight
// dev gate (CalendarDevSeeder.ShouldSeed: IsDevelopment() only); resilient — never
// crashes the host. Registered AFTER AddNodeDeclarativeWorkflowExecution (the interpreter executes the runs).
builder.Services.AddHostedService<ThreeWayMatchDevSeeder>();

// ── DEV-ONLY demo form seed — the equipment-inspection form the Harborline App dynamic-form route renders ────
//
// The forms engine is now wired (above) but the node registers NO FormDefinition, so a GET would 404. On a
// DEVELOPMENT node only, FormsDevSeeder registers + publishes ONE first-party form — a Dubai
// asset-management equipment-inspection form (assetId / assetType / conditionRating / inspectedOn / status /
// followUp / notes + a PII inspectorName) with bilingual en/ar labels — for the active-team tenant
// (NodeTenant.Resolve, the SAME tenant the route's minted capability keys on). The Harborline App route then
// renders REAL engine data (not a mock) and a submit validates + persists a real instance. Gated by the
// SAME airtight CalendarDevSeeder.ShouldSeed gate (IsDevelopment() only) — it NEVER
// runs in Production, so a real tenant's form store is never polluted. Idempotent: a restart re-runs cleanly
// (an already-published demo form is left as-is). Registered AFTER AddNodeForms (the store exists) and the
// host-level bootstrap (an active team exists by StartAsync time).
builder.Services.AddHostedService<FormsDevSeeder>();

// ── DEV-ONLY residential living-standard catalog seed (ADR 0101 Rev 3.1 Wave 3a) ──────────────────
//
// Makes the ONR-curated residential living-standard SLICE live end-to-end on a development node: registers
// + publishes the catalog item FORM (10 items across electrical + plumbing), registers its condition-rating
// bindings for the active-team tenant, and seeds the immutable catalog into the shared seed store. A submit
// of the catalog form then projects a typed condition assessment PER RATED ITEM onto the inspected unit.
// Same airtight CalendarDevSeeder.ShouldSeed gate as FormsDevSeeder above (NEVER runs in Production);
// idempotent; different form id (does not touch the equipment-inspection demo). Registered AFTER
// AddNodeAssetRegistry (the binding + catalog stores exist) and the host-level bootstrap.
builder.Services.AddHostedService<LivingStandardCatalogDevSeeder>();

// ── DEV-ONLY prototype-showcase form ladder (CIC-requested demo artifacts, 2026-07-03) ─────────────
//
// FIVE additional, separate form definitions (showcase-*) spanning the complexity ladder L0 (flat) →
// L4 (deep-nested), for the form builder + runner to demo the platform's structural range. Additive
// sibling to FormsDevSeeder above — same airtight gate, different form ids, does not touch the
// equipment-inspection demo. See FormsShowcaseDevSeeder's remarks for the known render-engine gap
// (Section.Items nesting shows flat in a live GET view today) this seeder works around.
builder.Services.AddHostedService<FormsShowcaseDevSeeder>();

// ── DEV-ONLY KG calendar-event seed — index the seeded calendar events + grant the OS-user principal ──
//
// The Harborline App KG keyword-search demo (ONR survey onr-carrier-kg-search-calendar-demo-survey-2026-06-24).
// On a DEVELOPMENT node only, KgCalendarDevIndexer is the FIRST production driver of the otherwise-built-
// but-unwired KG-keyword substrate: it (1) projects the dev-seeded CalendarEvents into the search_nodes
// index as NodeType="calendar-event" rows, and (2) seeds the acting OS-user principal an explicit
// ForRecords access-grant naming EXACTLY those event ids — so the fail-closed KG clip returns those events
// for that principal, and ONLY that principal. The dev gate is the SAME airtight CalendarDevSeeder.ShouldSeed
// gate (IsDevelopment() only) — it NEVER runs in Production, so no index is polluted
// and no principal is auto-granted there. The grant's principal id is derived from the SAME
// roster-edge resolver the KG search route uses (the canonical tenant principal), so the grant and the route key on
// the IDENTICAL string (the pinned footgun). It resolves IGrantStore from DI, so it seeds into the LIVE
// store (the NodeEfGrantStore the vector composition above Replace'd in, not a dead InMemoryGrantStore).
// Registered AFTER CalendarDevSeeder (StartAsync runs in registration order → events exist first) and AFTER
// the KG composition (so NodeSearchIndexer + the live IGrantStore are registered). Idempotent: a restart
// re-runs cleanly (no duplicate index rows, no accreting grants). It seeds the index + the grant ONLY — it
// adds NO route and NEVER reads search_nodes directly (the read path stays the clipped NodeSearchReadService).
builder.Services.AddHostedService<KgCalendarDevIndexer>();

// ── Node-local KG keyword (FTS5) search API — the clipped search route over NodeSearchReadService ────
//
// GET /api/local-node/kg/search?q=<term>&limit=<n> — the FIRST production caller of the clipped KG read
// service. It resolves the tenant + the acting principal SERVER-SIDE (NodeTenant.Resolve + the same
// roster-edge resolver the dev grant seed uses), then goes THROUGH NodeSearchReadService.
// SearchAsync — the clipped path (the clip resolves the AuthorizedRecordScope FIRST and builds the query
// WHERE exclusively from it). It NEVER issues a raw search_nodes read (SearchClipArchFence forbids it; a
// bypass would be the no-mock-crypto anti-pattern). A principal without a grant gets ZERO hits (fail-closed).
// Registered before SharedHostedWebApp so the path is mapped before Kestrel starts; gated by the inc-4 F1
// listener-level caller-auth (loopback bind alone does not authenticate the calling process).
// ── Runtime admission surface (production-wiring gap #3) — a RUNNING node can DRIVE admission ──────────
//
// The two-user test drove the AdmissionCoordinator directly from the harness — there was NO runtime caller. This
// is the thin route over it: POST /api/local-node/admission/invites (generate-invite, off
// TeamTrustAnchor.FromRoster, caller must hold members:admit — the seeded genesis admitter does) + POST
// /api/local-node/admission/redeem (a joining node presents its party id + principal pubkey + team-scoped
// TRANSPORT subkey + the invite token → the local admin redeems it single-use and SIGNS the joining
// (party, principal-key) into the roster). The signed admission is then (1) PUBLISHED to the roster-sync doctype
// (above) so it converges to every peer, validated to genesis on the far side, AND (2) the admitted peer's
// TRANSPORT subkey is wired into NodeTeamRoster.AdmitPeer so MemberSetTrustPolicy trusts the new peer for the
// sync handshake. THE LINKAGE (gap #3 core): on a SYNCED REVOCATION, NodeTeamRoster.AdoptSyncedRoster PRUNES the
// revoked party's transport key — so a revoked member loses TRANSPORT-trust post-convergence (its handshake is
// rejected), not merely attribution. The transport subkey is HKDF(joiner-root-private, teamId) — material the
// admitting node never holds — so the JOINING node supplies its transport PUBLIC key in the redeem request
// (parallel to how the channel already carries the joiner's principal pubkey). GATED by the inc-4 F1
// listener-level caller-auth by DEFAULT (NOT in CallerAuthAllowlist — admission is admin-only; a tokenless caller
// → 401). No-escalation (MemberRoster.Admit) + invite single-use/TTL (IAdmissionTokenStore) are inherited. The
// Harborline App QR/invite UI is a flagged FED follow-on — this builds the route + a thin client seam, NOT the UI.
// Registered before SharedHostedWebApp so paths are mapped before Kestrel starts; AddNodeRoster (above) registers
// the RosterCrdtProjection + IOperationVerifier the endpoint resolves.
// ── ADR 0122 §D4 T2 — node-side payment-WRITE composition (CIC un-defer 2026-06-16) ──────────────────
//
// Records + applies invoice/bill payments fully offline. Lifts the node payment repos' read-only
// NotSupportedException write guards (P2 was read-only); recording a payment writes a Draft Payment to
// the recoverable local-node.db and APPLIES it to the target invoice/bill via
// DefaultPaymentApplicationService — which writes the PaymentApplication row that surfaces the payment
// (transitively, via the invoice's SubLedgerAccountId FK) in the ADR 0120 lease sub-ledger history.
// AddNodePaymentWrites registers the read/WRITE NodeEfPayment(Application)Repository (AddSingleton so it
// WINS over the read-only TryAdd in AddNodeLeaseSubLedgerReads — call it FIRST so the read-model composes
// over the writable repos), the DefaultPaymentApplicationService composed over the node AR/AP/payment
// repos + node-resident StaticNodeTenantContext + explicit NoopDomainEventPublisher, and the bug-2849
// write-service accessor.
//
// SC4-C2 (asserted by the SC4-T9(b) Layer-2 node payment-write DI-graph test in
// Sc4RecoverabilityGuardTests, mirroring the AR/AP write tests):
//   (a) the ONLY persistence sinks are the recoverable local-node.db — NodeEfPaymentRepository,
//       NodeEfPaymentApplicationRepository + (via the apply service updating balances) the node AR/AP
//       repos. No JE is posted on record (Bridge contract records Draft; GL-posting Clear/Bounce is the
//       deferred future lifecycle, not wired here), so no kernel CRDT-writer is reachable.
//   (b) IDomainEventPublisher is the NoopDomainEventPublisher; no IDomainEventStore is registered.
//   (c) NO kernel CRDT writer / per-team event log is wired into the payment-write composition.
//   (d) the payment + application repos + tenant context resolve to node EF reads/writes over
//       local-node.db; the record is idempotent on SourceReference (the dedupe state IS the recoverable
//       record + the ux_payments_tenant_source_ref unique index, never a seed-keyed KV).
//
// MUST run AFTER AddNodeBillWrites + AddNodeInvoiceWrites (apply service composes over their repos) and
// BEFORE AddNodeLeaseSubLedgerReads (so the writable payment repos win its read-only TryAdd).
builder.Services.AddNodePaymentWrites();

// ── ADR 0122 §D4 P2 → (b) — node-side lease sub-ledger READ composition (additive) ──────────────────
//
// Activates the offline lease payment-history read path on the embedded node so a fully-offline
// Harborline install (signal-bridge STOPPED) renders REAL lease payment-history from the recoverable
// local-node.db. Wires (read-only): the ADR 0120 sub-ledger account repo (in-memory v1) + the
// SubLedgerReadModel composed over the node EF AR/AP repos (registered by AddNodeInvoiceWrites /
// AddNodeBillWrites above) + the node payment repositories (now read/WRITE — registered by
// AddNodePaymentWrites above; its TryAdd here is a no-op) + the PM-pack lease→sub-ledger
// link/activation services. The read chain is
// lease → LeaseSubLedgerLink.GetByLeaseAsync → ISubLedgerReadModel.GetHistoryAsync.
//
// ADR 0122 §D4 T2 (CIC un-defer 2026-06-16): payment AUTHORING is now node-resident (AddNodePaymentWrites
// above); this composition's payment-repo TryAdds defer to the writable variant. The read-model resolves
// the payment-leg FK-authoritatively from the PaymentApplication rows the apply path writes (C-T2-1). The
// exact registration set lives in AddNodeLeaseSubLedgerReads (single source of truth, mirrors the
// AddNode*Writes compositions). MUST run AFTER AddNodeBillWrites + AddNodeInvoiceWrites + AddNodePaymentWrites.
//
// SC4-C2: every service here is READ-ONLY over the recoverable local-node.db (or an in-memory v1
// mapping store holding no irreplaceable financial value); none references a kernel CRDT-writer type,
// so the SC4-T9(b) Layer-1 IL scan + Layer-2 DI-graph assertions stay green. Registered before
// SharedHostedWebApp so the lease-history route is mapped before Kestrel starts.
builder.Services.AddNodeLeaseSubLedgerReads();
// ── ADR 0122 §D4 T2 — node-side payment-WRITE routes (pattern-009 — Bridge→node frontend rebind pair) ──
//
// POST/GET /api/local-node/invoices/{id}/payments + /api/local-node/bills/{id}/payments — the offline
// payment-recording surface. Recording a payment writes a Draft Payment + a PaymentApplication into the
// recoverable local-node.db (idempotent on SourceReference) and applies it to the target invoice/bill via
// DefaultPaymentApplicationService, so the payment surfaces in the ADR 0120 lease sub-ledger history.
// Mirrors the Bridge PaymentsEndpoints contract (frontend rebind = path swap). The chart + party are
// resolved server-side FROM the invoice/bill (never client-supplied); tenant is the active-team-derived
// tenant (ADR 0032 — NodeTenant.Resolve / ActiveTeamTenantContext, not a fixed "local");
// no auth / loopback-only. The composition lives in AddNodePaymentWrites above; this only maps the routes.
//
// SC4-C2: the write lands only in the recoverable local-node.db (payment + application + updated
// invoice/bill balance); no GL JE is posted on record; no kernel CRDT-writer is reachable.
// T1 local-first sweep: node-computed accounting-summary dashboard (GL aggregation +
// outstanding invoices). GET /api/local-node/accounting/{summary,outstanding} — replaces the
// Bridge ERPNext proxy so the Accounting dashboard renders offline. READ-ONLY projection over
// the now-node-resident GL + AR data in LocalNodeDbContext; references no kernel CRDT-writer
// type, so SC4-T9(b) is unaffected. Registered before SharedHostedWebApp so paths are mapped
// before Kestrel starts.
builder.Services.AddSingleton<NodeAccountingSummaryService>();
// ── T3 local-first sweep — node-side banking (reconciliation) ────────────────────────────────────────
//
// Flip the banking surface onto the embedded node so BankAccountsPage + BankAccountDetailPage list/
// create/archive accounts, import statements (CSV/OFX/QIF/CAMT.053), review + accept/un-match proposals,
// lock/unlock reconciliation, and run the mock bank feed — all fully offline (signal-bridge STOPPED). The
// match-engine + four parsers + accept/un-match services + IBankFeedProvider seam + MockBankFeedProvider
// already ship in blocks-banking; the four banking tables (bank_accounts / statement_lines / match_links /
// reconciliations, mapped by BankingEntityModule) already exist in the node InitialSchema migration. So
// this flip adds NO new schema on either LocalNodeDbContext or SignalBridgeDbContext — it is a repo +
// composition + endpoint + frontend-rebind job (ONR research 2026-06-16; UPF/Admiral T3 dispatch).
//
// AddNodeBankingWrites registers exactly the banking slice (single source of truth, mirrors the
// AddNode*Writes compositions): the four recoverable Node EF banking repos, the in-memory matching-rule
// repo (no matching_rules table on either provider — built-in heuristics only in v1), the node
// IFiscalPeriodRepository (the periods substrate wired NodeEfPeriodResolver, NOT the repo interface that
// AcceptMatchService needs), the parsers + ImportPipelineService + MatchingEngineService /
// AcceptMatchService / UnMatchService / TransferPairingDetector, and MockBankFeedProvider as the default
// IBankFeedProvider (Tier-2 mock-first; a real AIS adapter slots in behind it via
// UseVendorProviderIfConfigured). MUST run AFTER AddNodeFinancialPosting (MatchingEngineService reads the
// node IJournalStore == NodeEfJournalStore for match-proposal heuristics).
//
// SC4-C2 (asserted by the SC4-T9(b) Layer-2 banking DI-graph test in Sc4RecoverabilityGuardTests,
// mirroring the AR/AP write tests):
//   (a) the ONLY recoverability sinks are the four banking tables in the recoverable local-node.db; the
//       matching-rule repo is in-memory (no table; holds no persisted value). AcceptMatchService is
//       propose-never-auto-post — it posts NO JE, only mutates MatchLink + StatementLine state in the
//       banking repos, so no kernel CRDT-writer is reachable.
//   (b) no IDomainEventPublisher / IDomainEventStore is touched by the banking path.
//   (c) NO kernel CRDT writer (PostingEngine / ILedgerEventStream) and NO per-team FileBackedEventLog /
//       IEventLog is referenced by any banking service — the SC4-T9(b) Layer-1 IL scan stays green.
//   (d) the repos + period repo resolve to node EF reads/writes over local-node.db, never a seed-keyed KV.
//
// ADDITIVE — the Bridge banking path (/api/v1/bank-accounts, EfBankingPersistence over Postgres) is
// untouched; the frontend flip + Rust sc5 fail-close are a sequenced follow-up after the pattern-009
// security SPOT-CHECK. Registered before SharedHostedWebApp so paths are mapped before Kestrel starts.
builder.Services.AddNodeBankingWrites();
// T4 local-first sweep — payroll node-flip. Payroll had NO durable persistence anywhere (the block
// ships only in-memory repos; the Bridge wires AddInMemoryPayroll()). AddNodePayrollWrites registers
// exactly the payroll slice (single source of truth, mirrors the AddNode*Writes compositions): the
// three recoverable Node EF payroll repos over the node-exclusive NodeLocalPayrollDbContext (Pattern B
// — node-exclusive, kept out of the council C2 parity check because there is no Bridge EF sibling), the
// block PayRunPostingService (posts balanced JEs through the node IJournalPostingService), and the
// node-resident StaticNodeTenantContext (TryAdd — co-exists with the bill/invoice write compositions).
// MUST run AFTER AddNodeFinancialPosting + AddNodeBillWrites (the posting service + ITenantContext the
// pay-run post path reads are wired by those).
//
// SC4-C2 (asserted by the SC4-T9(b) Layer-2 payroll DI-graph test in Sc4RecoverabilityGuardTests):
//   (a)/(d) the only payroll persistence sinks are the recoverable local-node.db payroll tables (the
//       three Node EF repos); the pay-run JE posts through the recoverable NodeEfJournalStore.
//   (b) the pay-run post path posts a JournalEntry through the GL-of-record posting service; it does
//       NOT touch IDomainEventPublisher / IDomainEventStore directly.
//   (c) NO kernel CRDT writer (PostingEngine / ILedgerEventStream) and NO per-team FileBackedEventLog /
//       IEventLog is referenced by any payroll service — the SC4-T9(b) Layer-1 IL scan stays green.
//
// ADDITIVE — the Bridge payroll path (/api/v1/payroll, AddInMemoryPayroll) is untouched; the frontend
// flip + Rust sc5 fail-close are a sequenced follow-up after the pattern-009 security SPOT-CHECK.
// Registered before SharedHostedWebApp so paths are mapped before Kestrel starts.
builder.Services.AddNodePayrollWrites();
// ── T4 local-first sweep — node-side DOCUMENTS (blocks-docs) write + read (ADR 0127) ─────────────────
//
// Flip the tenant-wide documents surface onto the embedded node so DocumentsPage list/view/upload/
// attach work fully offline (signal-bridge STOPPED). Document bytes live INLINE in the SQLCipher-keyed
// local-node.db (StorageRef.ForInline → base64 inside the storage_ref_json column), so SC-1 (no
// plaintext at rest) + SC-4 (reseed survival) are satisfied BY CONSTRUCTION — no out-of-line blob root
// to separately envelope/bundle. The attachments + document_refs tables (mapped by the shared
// DocsEntityModule, registered above) already exist in the node InitialSchema snapshot, so this flip
// adds NO new migration on either LocalNodeDbContext or SignalBridgeDbContext (ADR 0127 §Migration
// posture).
//
// AddNodeDocsWrites registers exactly the docs slice (single source of truth, mirrors the AddNode*
// compositions): the node EF attachment + document-ref repos over the recoverable local-node.db, the
// CONCRETE IMimeTypeAndSizePolicy (the shared three-gate policy wrapped by NodeInlineCeilingPolicy which
// enforces the 25 MB inline ceiling via InlineBlobMaxBytes — NOT MaxAttachmentBytes), the
// AttachmentService built EXPLICITLY with that non-null policy (SEC-1 fail-closed), the DocumentRefService,
// and the bug-2849 accessor.
//
// SEC-1 (ADR 0127 BUILD-BLOCKING): the node wires a CONCRETE policy + a non-null AttachmentService, and
// NodeInlineCeilingPolicy throws at composition time on a non-positive ceiling — a null/unconfigured
// policy (the enforced-ceiling evaporation vector) can never ship. Above the 25 MB ceiling, the upload is
// REJECTED (UploadRejectedException → 413) with an actionable, PII-free message.
//
// SC4-C2 (asserted by the SC4-T9(b) Layer-2 docs DI-graph test in Sc4RecoverabilityGuardTests):
//   (a)/(d) the ONLY persistence sinks are the recoverable local-node.db attachments + document_refs
//       tables (inline bytes ride inside the SQLCipher storage_ref_json column).
//   (b) NO IDomainEventPublisher / IDomainEventStore is registered by the docs composition.
//   (c) NO kernel CRDT writer / per-team FileBackedEventLog / IEventLog is referenced — the repos +
//       services are pure compute + recoverable EF, so the SC4-T9(b) Layer-1 IL scan stays green.
//
// Documents are TENANT-WIDE (no entity dimension) — no X-Sunfish-Entity header. ADDITIVE — the Bridge
// documents path (/api/v1/documents, EfAttachmentRepository over Postgres) is untouched; the frontend
// flip + Rust sc5 fail-close are a sequenced follow-up after the pattern-009 security SPOT-CHECK.
// Registered before SharedHostedWebApp so paths are mapped before Kestrel starts.
builder.Services.AddNodeDocsWrites();
// ── T5 local-first sweep — node-side REPORTS (the read-side report cartridge family) ─────────────────
//
// Flip the reports surface onto the embedded node so all six report pages — Trial Balance, Balance
// Sheet, P&L, P&L by Property, AR Aging, AP Aging — compute + render fully offline from the now-node-
// resident GL (signal-bridge STOPPED). Reports are PURE READ projections: the cartridges compose over
// the read seams already wired above (IJournalStore == NodeEfJournalStore; IAccountResolver ==
// NodeEfAccountResolver via AddNodeFinancialPosting; IFiscalPeriodRepository == NodeEfFiscalPeriodRepository
// via AddNodeBankingWrites; IInvoiceRepository / IBillRepository == the node EF AR/AP repos via
// AddNodeInvoiceWrites / AddNodeBillWrites; IPartyReadModel == NodeEfPartyRepository via AddNodeContacts).
// This flip adds NO new schema (reads existing node tables) and NO write path — it is a substrate-
// registration + endpoint + frontend-rebind job (Engineer T5 assessment 2026-06-17). MUST run AFTER
// AddNodeFinancialPosting + AddNodeInvoiceWrites + AddNodeBillWrites + AddNodeBankingWrites + AddNodeContacts
// (all the cartridge read deps are wired by those).
//
// Two genuinely-new node read seams, both pure EF reads over the recoverable local-node.db:
//   * IChartRepository == NodeEfChartRepository — the chart envelope (Trial Balance + Balance Sheet
//     read RetainedEarningsAccountId; GET /api/local-node/charts reads name + base currency).
//   * IGeneralLedgerReadModel == InMemoryGeneralLedgerReadModel over the node IJournalStore — signed
//     per-account balances from the journal snapshot (posted + reversed, Draft excluded; single-device
//     set is small). Composes WITHOUT a new EF type (it takes only IJournalStore).
// IArAgingService / IApAgingService are registered directly over the node AR/AP repos + the ambient
// StaticNodeTenantContext (the same services AddBlocksFinancialAr/Ap would TryAdd; registered surgically
// here to avoid re-registering the rest of those clusters, which the node already wires).
//
// The reports cartridge substrate registers each cartridge's ICartridgeRegistrar at composition; the
// runner resolves cartridges from the ReportCartridgeRegistry at run-time, so the registry is drained
// once at startup via UseBlocksReports() inside HostedReportsApiEndpoint.StartAsync (the node has no
// WebApplication at composition time — the Bridge's post-build drain has no equivalent here). All SIX
// cartridges the pages use are registered (NOT the Bridge's 4 — a latent Bridge gap not replicated;
// RentRoll is not a T5 page consumer, so it is omitted).
//
// SC4-C2 / SC4-T9(b): reports are READ-ONLY. None of NodeEfChartRepository, InMemoryGeneralLedgerReadModel,
// the cartridges, the runner, ReportsRoutes, or HostedReportsApiEndpoint references a kernel CRDT-writer
// (PostingEngine / ILedgerEventStream / FileBackedEventLog / IEventLog) — so the SC4-T9(b) Layer-1 IL
// scan stays green and no recoverability sink is added (asserted by the read-only reports DI-graph test
// in Sc4RecoverabilityGuardTests). NO auth / loopback-only. Registered before SharedHostedWebApp so paths
// are mapped before Kestrel starts.
builder.Services.AddSingleton<Harborline.Api.Blocks.FinancialPeriods.Services.IChartRepository, NodeEfChartRepository>();
builder.Services.AddSingleton<IGeneralLedgerReadModel>(sp =>
    new InMemoryGeneralLedgerReadModel(sp.GetRequiredService<IJournalStore>()));
// ArAgingService / ApAgingService consume the NARROWED Harborline.Api.Foundation.MultiTenancy.ITenantContext
// (the financial-cluster consumer variant) — which is exactly what the active-team-derived
// ActiveTeamTenantContext implements and what the node AR/AP write compositions register (ADR 0032
// identity layer; the aging tenant follows the active org, not a fixed "local"). NOT the Authorization
// sum-interface facade.
builder.Services.AddSingleton<Harborline.Api.Blocks.FinancialAr.Services.IArAgingService>(sp =>
    new Harborline.Api.Blocks.FinancialAr.Services.ArAgingService(
        sp.GetRequiredService<Harborline.Api.Foundation.MultiTenancy.ITenantContext>(),
        sp.GetRequiredService<Harborline.Api.Blocks.FinancialAr.Services.IInvoiceRepository>()));
builder.Services.AddSingleton<Harborline.Api.Blocks.FinancialAp.Services.IApAgingService>(sp =>
    new Harborline.Api.Blocks.FinancialAp.Services.ApAgingService(
        sp.GetRequiredService<Harborline.Api.Foundation.MultiTenancy.ITenantContext>(),
        sp.GetRequiredService<Harborline.Api.Blocks.FinancialAp.Services.IBillRepository>()));
builder.Services.AddBlocksReportsSubstrate();
builder.Services.AddTrialBalanceCartridge();
builder.Services.AddArAgingSummaryCartridge();
builder.Services.AddApAgingSummaryCartridge();
builder.Services.AddBalanceSheetCartridge();
builder.Services.AddProfitAndLossCartridge();
builder.Services.AddProfitAndLossByPropertyCartridge();
// Testable final-composition seam: production passes null. Any supplied registration participates in
// the exact final graph validation below, after every normal module registration and before provider build.
finalServiceRegistration?.Invoke(builder.Services);
// Close every compiled inventory over the FINAL service graph before any provider is built. These
// validators do not add registrations. A raw, late, duplicate, or opaque component therefore refuses
// host construction instead of silently changing the executable runtime or persistence model.
var technicalRegistrationProfile = new LocalNodeHostedComponentProfile(
    webClientOptions.Enabled,
    webClientOptions.Enabled && !string.IsNullOrWhiteSpace(webClientOptions.LlmUpstreamBase),
    schedulingDogfoodEnabled);
builder.Services.AddLocalNodeTechnicalRegistrationEvidence(technicalRegistrationProfile);
var lanCertificate = SharedHostedWebApp.ConfigureHosting(
    builder.WebHost,
    localNodeOptions,
    callerSessionToken.IsEnforced,
    rootTimeProvider);
var finalServiceProviderFactory = new LocalNodeFinalGraphServiceProviderFactory(
    technicalRegistrationProfile,
    new ServiceProviderOptions
    {
        ValidateScopes = builder.Environment.IsDevelopment(),
        ValidateOnBuild = builder.Environment.IsDevelopment(),
    });
// Internal test seam: a probe builds the completed graph through the exact factory the host uses.
// Production supplies null. The callback runs only after every normal/final registration is present.
finalServiceProviderProbe?.Invoke(builder.Services, finalServiceProviderFactory);
builder.Host.UseServiceProviderFactory(finalServiceProviderFactory);

var app = builder.Build();
ResilientWindowsEventLogRegistration.Open(app.Services);
await using var listener = new SharedHostedWebApp(
    app,
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalNodeOptions>>(),
    app.Services.GetRequiredService<LocalNodeExecutableEndpointRegistry>(),
    app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>(),
    app.Services.GetRequiredService<TimeProvider>(),
    lanCertificate);
await using var endpointMapping = await LocalNodeEndpointMapping.MapAsync(
    app.Services,
    listener,
    technicalRegistrationProfile);
await listener.StartAsync(CancellationToken.None);
await Harborline.Api.LocalNodeHost.LocalNodeHostRuntime.RunAsync(app, listener);
    }
}

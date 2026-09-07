using System.Runtime.InteropServices;
using Harborline.Api.Foundation.LocalFirst.Installation;
using Harborline.Api.Kernel.Sync.Network;

namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// Host-wide configuration for the local-node process. Bound from the
/// <c>LocalNode</c> configuration section. See <c>appsettings.json</c> for the
/// default binding and the README for the service-manager integration
/// roadmap (Wave 4).
/// </summary>
public sealed class LocalNodeOptions
{
    /// <summary>
    /// PERS-1 — the node's deployment ROLE (ADR 0137 D11 cache/relay/canonical node-role model). Bound from
    /// <c>LocalNode:Role</c>. Defaults to <see cref="NodeRole.Edge"/> (the single-device Harborline App behaviour).
    /// BOTH roles wire a durable, mandatory-envelope-at-rest blob store (a <c>FileSystemBlobStore</c> wrapped by
    /// an <c>EnvelopeBlobStore</c>) as the node's <see cref="Harborline.Api.Foundation.Blobs.IBlobStore"/> (card 3750:
    /// the edge default was previously the volatile in-memory store — a data-loss defect). The roles differ in the
    /// durability guard: <see cref="NodeRole.Storage"/> (a canonical multi-copy store, ADR 0137 D4) additionally
    /// durability-gates the blob shed seam; the edge role does not (no second copy can ever be confirmed on a
    /// single device).
    /// </summary>
    public NodeRole Role { get; set; } = NodeRole.Edge;

    /// <summary>
    /// The node's deployment RESIDENCY region (ISO-style jurisdiction code, e.g. <c>"US"</c>,
    /// <c>"EU"</c>, <c>"AE"</c>). Bound from <c>LocalNode:HostJurisdiction</c>. Relayed into the
    /// dynamic-forms engine's <see cref="Harborline.Api.Foundation.Forms.Engine.FormEngineOptions.HostJurisdiction"/>
    /// (F-11 / F2) — the <c>TargetJurisdiction</c> the SPINE-2 Store PEP checks a <c>Reside</c>-effect
    /// classified field's residency eligibility against. Defaults to <c>"US"</c> to preserve the
    /// prior behaviour; a non-US deployment MUST set this so residency is enforced against the node's
    /// real region rather than a silent hardcoded default.
    /// </summary>
    public string HostJurisdiction { get; set; } = "US";

    /// <summary>
    /// Stable identifier for this local node, persisted across restarts once
    /// set. Defaults to a fresh GUID so the first boot is never anonymous;
    /// downstream waves (2.1 sync daemon, 3.3 Anchor) are expected to pin
    /// this from a provisioned identity store rather than accept the default.
    /// </summary>
    public string NodeId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Optional team identifier. Populated once the node is enrolled in a
    /// Harborline team; null on a fresh single-user install. Consumed by the
    /// Wave 6.3.E.2 single-team legacy bootstrap path (when
    /// <see cref="MultiTeamOptions.Enabled"/> is <c>false</c>).
    /// </summary>
    public string? TeamId { get; set; }

    /// <summary>
    /// Multi-team bootstrap configuration. When
    /// <see cref="MultiTeamOptions.Enabled"/> is <c>true</c>, the Wave 6.3.E.2
    /// <c>MultiTeamBootstrapHostedService</c> materializes the configured
    /// <see cref="MultiTeamOptions.TeamBootstraps"/> instead of the legacy
    /// single-team <see cref="TeamId"/> path.
    /// </summary>
    public MultiTeamOptions MultiTeam { get; set; } = new();

    /// <summary>
    /// Root directory for node-local state: encrypted store, event log,
    /// quarantine queue, key material, projections. Defaults to the
    /// platform-conventional per-user application data location (see
    /// <see cref="GetDefaultDataDirectory"/>).
    /// </summary>
    public string DataDirectory { get; set; } = GetDefaultDataDirectory();

    /// <summary>
    /// How long after the administrator-authority log first observed itself EMPTY the INSTALLER principal
    /// may still establish the first administrator (ADR 0066 clause 6). Bound from
    /// <c>LocalNode:InstallerWindow</c>.
    /// <see cref="TimeSpan.Zero"/> (or a negative value) disables the bound; the state gate and the one-shot
    /// installer seal still apply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a bound at all.</b> A node left indefinitely in "no administrator yet" is a standing
    /// administrator-creation opportunity: whoever next controls the environment the installer derives its
    /// authority from becomes the administrator. NIST SP 800-53 AC-2(2) is the same instinct applied to
    /// temporary and emergency accounts.
    /// </para>
    /// <para>
    /// <b>Anchored in the store, not on the filesystem.</b> The window used to be measured from
    /// <c>Directory.GetCreationTimeUtc(DataDirectory)</c>, which anyone able to write the data directory can
    /// move with <c>Directory.SetCreationTimeUtc</c>, which predates the feature on every directory that
    /// already existed, and whose IO-fault fallback re-opened a fresh window on every boot instead of once.
    /// The anchor is now an <c>InstallerWindowOpened</c> row the authority log appends the first time an
    /// installer attempt reads it empty — inside the same transaction as the state gate, and covered by the
    /// same hash chain.
    /// </para>
    /// <para>
    /// <b>The number is a placeholder, not a ruling.</b> Twenty-four hours is chosen to be conservative and
    /// obviously-long-enough for an attended install; ADR 0066 sets no figure, so this is the mechanism with a
    /// documented default rather than a policy decision. After it expires, establishing the first
    /// administrator uses the offline recovery command (clause 8), which is unaffected by this window.
    /// </para>
    /// </remarks>
    public TimeSpan InstallerWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Applies the data directory owned by the install identity unless configuration explicitly chose one.
    /// </summary>
    /// <param name="footprint">Filesystem footprint resolved from the durable install identity.</param>
    /// <param name="dataDirectoryIsConfigured">
    /// <c>true</c> when configuration supplied <c>LocalNode:DataDirectory</c>; explicit operator paths win.
    /// </param>
    public void ApplyInstallFootprint(InstallFootprint footprint, bool dataDirectoryIsConfigured)
    {
        ArgumentNullException.ThrowIfNull(footprint);
        if (!dataDirectoryIsConfigured)
        {
            DataDirectory = footprint.DataDirectory;
        }
    }

    /// <summary>
    /// TCP port the Wave 5.2.D health endpoint binds to. <c>0</c> (the
    /// default) means "auto-assign" — the host listens on whatever
    /// <c>ASPNETCORE_URLS</c> specifies, or lets Kestrel pick a free
    /// ephemeral port when no URL is configured. Bridge's
    /// <see cref="Harborline.Bridge.Orchestration.ITenantProcessSupervisor"/>
    /// sets this explicitly per child (Wave 5.2.C) so
    /// <see cref="Harborline.Bridge.Orchestration.TenantHealthMonitor"/> knows
    /// where to poll.
    /// </summary>
    public int HealthPort { get; set; } = 0;

    /// <summary>
    /// W4 app-hosted LAN listener configuration. LAN is deliberately opt-in; when enabled the
    /// transport binds only the configured address on HTTPS port 7443 in addition to the existing
    /// loopback endpoint. The listener never derives a LAN endpoint from <c>ASPNETCORE_URLS</c>.
    /// </summary>
    public LocalNodeLanOptions Lan { get; set; } = new();

    /// <summary>
    /// Optional hex-encoded 32-byte Ed25519 root seed injected by a parent
    /// supervisor. Wave 5.2 stop-work #1: Bridge's
    /// <see cref="Harborline.Bridge.Orchestration.ITenantSeedProvider"/>
    /// HKDF-expands the Bridge install-level root seed with info
    /// <c>"sunfish:bridge:tenant-seed:v1:{TenantId}"</c> and passes the
    /// result to each spawned <c>local-node-host</c> child via the
    /// <c>LocalNode__RootSeedHex</c> environment variable. When set, the
    /// host honors the injected seed and <b>skips its own keystore
    /// lookup</b>, giving each tenant on a shared Bridge host a
    /// cryptographically independent root identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Direct installs leave this null.</b> Anchor and standalone
    /// <c>local-node-host</c> runs continue to use the default keystore-
    /// backed <see cref="Harborline.Api.Kernel.Security.Keys.IRootSeedProvider"/>
    /// path. Only Bridge-spawned children set this value.
    /// </para>
    /// <para>
    /// <b>Validation.</b> The value must be a 64-character hex string
    /// (32 raw bytes). Any other length / encoding is treated as a
    /// configuration error and fails the host startup.
    /// </para>
    /// <para>
    /// <b>Trust model.</b> The env-var injection is trusted because parent
    /// and child share the same OS security domain. Deeper attestation
    /// (signed seed envelopes, mutual TLS) is a future wave.
    /// </para>
    /// </remarks>
    public string? RootSeedHex { get; set; }

    /// <summary>
    /// Wave 5.3.C — browser-facing WebSocket sync-daemon endpoint configuration.
    /// Controls whether <c>/ws</c> is mapped on the shared Kestrel listener and
    /// what per-message size cap is enforced on the inbound side.
    /// </summary>
    public BrowserWebSocketOptions BrowserWebSocket { get; set; } = new();

    /// <summary>
    /// ADR 0115 — peer-sync transport configuration. Gates whether each team
    /// binds a per-team LISTENING transport endpoint (inbound peer sync) or runs
    /// outbound-only.
    /// </summary>
    public SyncTransportOptions Sync { get; set; } = new();

    /// <summary>
    /// Optional hex-encoded 32-byte <b>Store DEK</b> for the relational financial
    /// store, injected by the Tauri shell (ADR 0115 SC-4 amendment, boundary
    /// Option A1). Passed via the <c>LocalNode__StoreDekHex</c> environment
    /// variable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it changes.</b> When set, the host keys <c>local-node.db</c> with
    /// this DEK <b>verbatim</b> instead of deriving the SQLCipher key from the
    /// root seed via <c>HKDF</c>. This is the SC-4 envelope-encryption design: the
    /// shell wraps the random Store DEK in two AEAD slots (Keychain-KEK +
    /// Argon2id-passphrase-KEK), resolves it from whichever slot is available, and
    /// injects the resolved DEK here. The SC-1 interceptor + startup guard are
    /// UNCHANGED — they still fail closed; only the key's <i>source</i> changes.
    /// </para>
    /// <para>
    /// <b>Backward compatible.</b> Leave null to keep the legacy behaviour (key =
    /// <c>HKDF(rootSeed, RelationalStoreKeyId)</c>) — the Bridge-spawned-tenant
    /// path and any non-SC-4 install are unaffected. The root seed STAYS injected
    /// regardless (it still drives the Ed25519/team identity); SC-4 adds the DEK
    /// as a second injected secret, it does not remove the seed.
    /// </para>
    /// <para>
    /// <b>Validation.</b> Must be a 64-character hex string (32 raw bytes); any
    /// other length/encoding fails host startup.
    /// </para>
    /// <para>
    /// <b>SECURITY (SC4-C3).</b> This value is logged <b>length-only</b>, NEVER the
    /// hex — identical to the root-seed handling. Logging the DEK would leak the
    /// only secret that decrypts the financial store.
    /// </para>
    /// </remarks>
    public string? StoreDekHex { get; set; }

    /// <summary>
    /// Optional per-boot cross-process CALLER-AUTH session token, injected by the
    /// Tauri shell that spawns this node (inc-4 Harborline App↔local-node-host connection).
    /// Passed via the <c>LocalNode__SessionToken</c> environment variable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it secures.</b> The shared Kestrel listener binds loopback only, but
    /// loopback-bind authenticates the HOST, not the calling PROCESS — ANY local
    /// process can reach a loopback port. inc-4 closes that gap: the Tauri shell
    /// generates a fresh CSPRNG token at boot, injects it here AND holds it in the
    /// shell, and the Harborline App presents it (<c>Authorization: Bearer &lt;token&gt;</c>)
    /// on the loopback node routes
    /// (<see cref="Harborline.Api.LocalNodeHost.Health.NodeCallerSessionToken"/>). inc-4 F1:
    /// the gate is LISTENER-LEVEL (the <c>SharedHostedWebApp</c> middleware) — it
    /// requires the bearer for EVERY route by default (gate-all-by-default), with a tiny
    /// explicit allowlist (<c>/health</c>, <c>/ws</c>), so a local stranger process that
    /// does not hold the token is rejected <b>fail-closed (401)</b> on any
    /// non-allowlisted route (including every financial-cluster write route).
    /// </para>
    /// <para>
    /// <b>Fail-closed posture (the load-bearing rule).</b> When this value is set, all
    /// non-allowlisted routes REQUIRE a matching bearer — missing/mismatched ⇒ 401. When it is
    /// <c>null</c> (a direct <c>dotnet run</c> / a Bridge-spawned tenant child that did
    /// not inject one), the guard runs in <b>dev/single-host-trusted</b> mode: it
    /// permits the call but the node logs the un-authenticated posture at startup. A
    /// SHIPPED Harborline App ALWAYS injects a token, so the production Harborline App path is always
    /// authenticated; the null path is the dev/test/Bridge-tenant fallback only.
    /// </para>
    /// <para>
    /// <b>Per-boot, never persisted.</b> A fresh token each launch — it is not written
    /// to disk on either side and dies with the process pair. Compared in constant time
    /// (see <see cref="Harborline.Api.LocalNodeHost.Health.NodeCallerSessionToken.Matches"/>)
    /// to avoid a timing side-channel.
    /// </para>
    /// <para>
    /// <b>SECURITY.</b> Logged length-only — NEVER the token value — identical to the
    /// root-seed / DEK handling. A token in a log is a credential leak.
    /// </para>
    /// </remarks>
    public string? SessionToken { get; set; }

    /// <summary>
    /// Two-sided WIRE ENROLLMENT (joiner) configuration. Bound from <c>LocalNode:Enrollment</c>. Present only when
    /// this node JOINS another team over the wire (the admitter's reachable address + optional token); unset on a
    /// node that only HOSTS its own team / admits others.
    /// </summary>
    public EnrollmentOptions? Enrollment { get; set; }

    /// <summary>
    /// DEV / troubleshooting diagnostics configuration. Bound from <c>LocalNode:Diagnostics</c>. Gates the verbose,
    /// structured comms/enrollment/trust diagnostic logging that makes the REAL failure reason plainly visible in the
    /// node console while the product is in PRE-RELEASE development. See <see cref="DiagnosticsOptions"/>.
    /// </summary>
    public DiagnosticsOptions Diagnostics { get; set; } = new();

    /// <summary>
    /// KG-search (local-first knowledge-graph) configuration. Bound from <c>LocalNode:KnowledgeGraph</c>. Wires
    /// the secure vector layer (ADR 0135 KG-search F3-lift amendment, Slice 1d) into the host: when
    /// <see cref="KnowledgeGraphOptions.CapabilityCliPath"/> is set, the node drives the capability <c>embeddings</c>/<c>rerank</c>
    /// runtime through the <c>kg-embed</c> CLI (the agent-client doctrine — CLI + SDK, not MCP) over the G-4
    /// sandboxed BGE-M3 / bge-reranker-v2-m3 worker. Unset ⇒ no real embedding provider ⇒ the index ingest path
    /// fails closed (<c>provider.kg_floor_unavailable</c>) rather than indexing an unverifiable artifact (M1).
    /// </summary>
    public KnowledgeGraphOptions KnowledgeGraph { get; set; } = new();

    /// <summary>
    /// PQC Phase 2 / BL-01 (ADR 0004 Amendment 2 GATE condition 5) — the hybrid-write CUTOVER opt-in. Bound from
    /// <c>LocalNode:HybridKemWrite</c>. Gates whether THIS install boxes the post-quantum hybrid suite #3 (standard
    /// X-Wing) for X-Wing-capable recipients. See <see cref="HybridKemWriteOptions"/>.
    /// </summary>
    public HybridKemWriteOptions HybridKemWrite { get; set; } = new();

    /// <summary>
    /// Asset Type System live-capture configuration (ADR 0101 Rev 3.1 Wave 2b). Bound from
    /// <c>LocalNode:AssetRegistry</c>. Tunes the post-submit projection reconcile-sweep daemon (F-RECON).
    /// See <see cref="AssetRegistryOptions"/>.
    /// </summary>
    public AssetRegistryOptions AssetRegistry { get; set; } = new();

    /// <summary>
    /// Installation identity-coordinator recovery configuration. Bound from
    /// <c>LocalNode:IdentityCoordinator</c>. The startup drain always runs immediately;
    /// this setting controls only the subsequent recovery cadence.
    /// </summary>
    public IdentityCoordinatorOptions IdentityCoordinator { get; set; } = new();

    /// <summary>
    /// Platform-conventional default for <see cref="DataDirectory"/>:
    /// <list type="bullet">
    ///   <item>Windows: <c>%LOCALAPPDATA%\Sunfish\LocalNode</c></item>
    ///   <item>macOS: <c>~/Library/Application Support/Sunfish/LocalNode</c></item>
    ///   <item>Linux: <c>$XDG_DATA_HOME/sunfish/local-node</c> (falls back to <c>~/.local/share</c>).</item>
    /// </list>
    /// Kept as a static helper so tests can inject a writable temp directory without
    /// relying on the host environment.
    /// </summary>
    public static string GetDefaultDataDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Sunfish", "LocalNode");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "Sunfish", "LocalNode");
        }

        // Linux / BSD / other Unix: honor XDG_DATA_HOME, fall back to ~/.local/share.
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return Path.Combine(xdg, "sunfish", "local-node");
        }
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userHome, ".local", "share", "sunfish", "local-node");
    }
}

/// <summary>Fixed W4 transport options for the optional local-node LAN listener.</summary>
public sealed class LocalNodeLanOptions
{
    /// <summary>LAN is disabled by default; this is the sole opt-in.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// A concrete, locally assigned non-loopback IP address. Wildcards and hostnames are invalid.
    /// </summary>
    public string? BindAddress { get; set; }

    /// <summary>The only W4 LAN port. This is fixed by the normative specification.</summary>
    public int Port { get; set; } = 7443;

    /// <summary>The exact DNS name or textual bind address clients use for the HTTPS request.</summary>
    public string? AdvertisedHost { get; set; }

    /// <summary>
    /// W4-issued node leaf certificate identity. The transport resolver accepts a PFX path or a
    /// platform certificate-store identity; it never generates a fallback or permits plaintext.
    /// </summary>
    public string? Certificate { get; set; }

    /// <summary>
    /// Optional password for a configured PFX file. It is a secret input and is never logged.
    /// </summary>
    public string? CertificatePassword { get; set; }
}

/// <summary>
/// PERS-1 — the node's deployment role (ADR 0137 D11 cache/relay/canonical node-role model). Phase 0 ships only the
/// two roles the MVP floor needs; the full D11a capability×availability taxonomy is a later phase.
/// </summary>
public enum NodeRole
{
    /// <summary>
    /// The DEFAULT — the current single-device behaviour, unchanged. The node uses the volatile in-memory edge blob
    /// default; it is not a durable canonical store.
    /// </summary>
    Edge = 0,

    /// <summary>
    /// A durable canonical STORAGE node (ADR 0137 D11b). The composition root wires the mandatory-envelope-at-rest
    /// blob store (filesystem backend wrapped by <c>EnvelopeBlobStore</c>) as the node's
    /// <see cref="Harborline.Api.Foundation.Blobs.IBlobStore"/> — ciphertext only at rest (D4 / C-3).
    /// </summary>
    Storage = 1,
}

/// <summary>
/// Two-sided WIRE ENROLLMENT (joiner) options. Bound from <c>LocalNode:Enrollment</c> (cerebrum [2026-06-21]).
/// Drives <c>NodeWireEnrollmentClient</c> when this node JOINS a peer team: the admitter's reachable base URL the
/// joiner POSTs its signed enroll request to, plus an optional Bearer token for a hardened admitter listener.
/// </summary>
public sealed class EnrollmentOptions
{
    /// <summary>
    /// The ADMITTER node's reachable base URL (e.g. <c>http://100.x.y.z:7777</c> over Tailscale, or the LAN
    /// address) — the joiner POSTs its signed enroll request to <c>{AdmitterUrl}/api/local-node/admission/redeem</c>.
    /// Operator-supplied (the Harborline App invite-entry UI sets it from the invite/peer config). A blank value fails the
    /// join closed at call time (never a wrong default).
    /// </summary>
    public string? AdmitterUrl { get; set; }

    /// <summary>
    /// Optional Bearer token for an admitter whose redeem listener requires inc-4 caller-auth (a hardened
    /// deployment). Null/blank in the single-office Tailscale posture where the admitter's per-team wire trust gate
    /// is the boundary (the SIGNED roster + out-of-band anchor are the real security, not this transport token).
    /// SECURITY: a token is a credential — never log its value.
    /// </summary>
    public string? AdmitterToken { get; set; }

    /// <summary>
    /// #1301 F-1 — the ADMITTER node's reachable SYNC endpoint (<c>tcp://&lt;host&gt;:7473</c> or bare
    /// <c>host:7473</c>) for the CROSS-MACHINE pre-trust enrollment channel. This is the F-1 fix for the loopback
    /// problem with <see cref="AdmitterUrl"/>: the HTTP redeem route is bound 127.0.0.1 + caller-auth-gated, so a
    /// REMOTE joiner can't reach it; the 7473 sync listener is the network endpoint the admitter binds for
    /// cross-machine sync anyway, and the pre-trust enrollment phase rides it (gated by the invite, not a session
    /// token). When set, the joiner uses <c>SocketEnrollmentTransport</c> over this endpoint; when blank it falls
    /// back to the loopback HTTP transport via <see cref="AdmitterUrl"/>. Operator-supplied (the Harborline App invite-
    /// entry / peer config sets it). A blank value with no <see cref="AdmitterUrl"/> fails the join closed at call
    /// time.
    /// </summary>
    public string? AdmitterSyncEndpoint { get; set; }
}

/// <summary>
/// DEV / troubleshooting diagnostics options. Bound from <c>LocalNode:Diagnostics</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Pre-release, the Harborline App flattened comms/enrollment reject reasons to a generic
/// catch-all and node-trust decisions (<c>PEER_UNTRUSTED</c>, roster adopt-vs-refuse, key-scope mismatches) were
/// invisible — so a join/sync failure surfaced as an opaque "rejected" with the REAL reason hidden, costing long
/// blind debugging slogs. This flag turns the real reason ON by default while we develop: a single toggle that
/// emits verbose, structured <c>[comms-diag]</c> lines at the points that previously hid the reason.
/// </para>
/// <para>
/// <b>Default ON for pre-release; OFF for production release.</b> <see cref="CommsDiagnosticLogging"/> defaults to
/// <c>true</c> so a developer/troubleshooter sees the real reason out of the box. It is DESIGNED to flip to
/// <c>false</c> for the production release (set <c>LocalNode:Diagnostics:CommsDiagnosticLogging=false</c>, or
/// remove/disable the dev sidecar's <c>ASPNETCORE_ENVIRONMENT=Development</c> posture). Default-ON is acceptable
/// because the diagnostics emit NO secret values — only lengths, id/hash PREFIXES, teamIds, party ids (already
/// public), boolean decisions, and scope names (the same <c>length=</c> / id-prefix discipline the root-seed +
/// session-token logging already follows). The flag being ON changes NO behaviour except emitting logs: it never
/// affects enrollment/admission/trust control flow, never weakens a gate, never discloses a key or token value.
/// </para>
/// </remarks>
public sealed class DiagnosticsOptions
{
    /// <summary>
    /// When <c>true</c> (the DEFAULT, for pre-release dev + troubleshooting), the host emits verbose, structured
    /// <c>[comms-diag]</c> diagnostic log lines at the comms / enrollment / peer-trust / roster decision points that
    /// otherwise hide the real reason (admission reject reasons, <c>PEER_UNTRUSTED</c> trust decisions, team adoption
    /// / supersession steps, key-scoping team-scope, roster adopt-vs-refuse). When <c>false</c> (the production
    /// release posture), those verbose lines are SUPPRESSED and only normal logging is emitted. NO secret value is
    /// ever logged in either state — the flag gates verbosity, not secrecy.
    /// </summary>
    public bool CommsDiagnosticLogging { get; set; } = true;
}

/// <summary>
/// KG-search options (ADR 0135 KG-search F3-lift amendment, Slice 1d). Bound from
/// <c>LocalNode:KnowledgeGraph</c>. Wires the .NET node to the capability <c>embeddings</c>/<c>rerank</c> runtime via
/// the <c>kg-embed</c> CLI (the agent-client doctrine — CLI(<c>--json</c>) + SDK, NOT MCP).
/// </summary>
public sealed class KnowledgeGraphOptions
{
    /// <summary>
    /// Absolute path to the compiled <c>kg-embed</c> CLI entry (<c>apps/capability-host/dist/runtime/kg-embed-cli.js</c>).
    /// When set, the node registers the real CLI-driven embedding/rerank provider; the CLI drives the G-4 sandboxed
    /// BGE-M3 / bge-reranker-v2-m3 worker. When unset/blank, NO real provider is registered ⇒ the index ingest path
    /// fails closed (<c>provider.kg_floor_unavailable</c>) rather than indexing an unverifiable artifact (M1).
    /// </summary>
    public string? CapabilityCliPath { get; set; }

    /// <summary>
    /// The Node binary used to run the CLI (defaults to <c>node</c> on PATH). Operator-overridable for a pinned
    /// fleet Node install.
    /// </summary>
    public string NodeBinary { get; set; } = "node";

    /// <summary>
    /// Whether to ARM the real BGE-M3 / bge-reranker worker (sets <c>CAPABILITY_HOST_KG_EMBED_REAL=1</c> for the CLI). When
    /// false (the default), the CLI returns deterministic STUB artifacts that self-identify (<c>stub-bge-m3</c>) and
    /// are therefore REFUSED by the indexer's M1 gate — so a host that has not provisioned the real models does not
    /// silently index fakes (it fails closed at ingest). Set true only on a host with the cached models + a
    /// torch/transformers interpreter.
    /// </summary>
    public bool ArmRealWorker { get; set; }

    /// <summary>
    /// Optional override for the worker's Python interpreter (sets <c>CAPABILITY_HOST_KG_EMBED_PYTHON</c> for the CLI). Null ⇒
    /// the CLI resolves its documented default. Only consulted when <see cref="ArmRealWorker"/> is true.
    /// </summary>
    public string? WorkerPython { get; set; }

    /// <summary>The per-invoke CLI deadline (milliseconds). Real CPU inference is slow; defaults to 120s.</summary>
    public int TimeoutMs { get; set; } = 120_000;

    // ── KG Slice 2-foundation — the proposal-only generative GraphRAG (the safe interim) ──────────────────

    /// <summary>
    /// Absolute path to the compiled <c>kg-generate</c> CLI entry
    /// (<c>apps/capability-host/dist/runtime/kg-generate-cli.js</c>). When set, the node registers the real CLI-driven
    /// GENERATION provider; the CLI drives the G-4 sandboxed Qwen2.5-7B-Instruct worker that reads UNTRUSTED
    /// retrieved grounding and emits a TEXT PROPOSAL (the §2.8.4 firewall extended to retrieved text — the model
    /// has no hands). When unset/blank, NO real generation provider is registered ⇒ the grounded-proposal path
    /// fails closed (<c>provider.kg_generate_floor_unavailable</c>) rather than emitting a fake proposal as a
    /// genuine answer (the no-fake-as-real family). PROPOSAL-ONLY — the autonomous form stays broker-PEP-gated.
    /// </summary>
    public string? GenerateCapabilityCliPath { get; set; }

    /// <summary>
    /// Whether to ARM the real Qwen2.5 generation worker (sets <c>CAPABILITY_HOST_KG_GENERATE_REAL=1</c> for the CLI). When
    /// false (the default), the CLI returns a deterministic, SELF-IDENTIFYING stub proposal (<c>stub-qwen2.5</c>)
    /// the consumer treats as floor-unavailable, never as a real grounded answer. Set true only on a host with the
    /// cached model + an LLM interpreter.
    /// </summary>
    public bool GenerateArmRealWorker { get; set; }

    /// <summary>
    /// Optional override for the generation worker's Python interpreter (sets <c>CAPABILITY_HOST_KG_GENERATE_PYTHON</c> for
    /// the CLI). Null ⇒ the CLI resolves its documented default. Only consulted when
    /// <see cref="GenerateArmRealWorker"/> is true.
    /// </summary>
    public string? GenerateWorkerPython { get; set; }
}

/// <summary>
/// PQC Phase 2 / BL-01 hybrid-write CUTOVER options (ADR 0004 Amendment 2 GATE condition 5). Bound from
/// <c>LocalNode:HybridKemWrite</c>. Drives <c>NodeHybridKemWritePolicyComposition.AddNodeHybridKemWritePolicy</c>,
/// which overrides kernel-security's fail-closed <c>DisabledHybridKemWritePolicy</c> default.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cutover this gates.</b> When ENABLED, the production tenant-DEK + role-key writers box the post-quantum
/// hybrid suite #3 (standard X-Wing — X25519 + ML-KEM-768) for a recipient that is X-Wing-CAPABLE (has a published,
/// roster-bound X-Wing public key). A NON-capable recipient still safely degrades to the legacy suite #1 (the
/// per-recipient capability gate in kernel-security's <c>TenantDekWrapper</c> — never stranded). Enabling this
/// CLOSES the harvest-now-decrypt-later (HNDL) hole for capable recipients.
/// </para>
/// <para>
/// <b>Default OFF — the safe pre-cutover state.</b> <see cref="Enabled"/> defaults to <c>false</c>, so a fresh /
/// un-opted-in install boxes suite #1 only (exactly the behaviour before this increment). The cutover is a
/// deliberate per-install act: set <c>LocalNode:HybridKemWrite:Enabled=true</c>.
/// </para>
/// <para>
/// <b>Staged rollout (local-first).</b> The fleet is per-install hosts, so this flag IS the rollout knob: opt a
/// CANARY cohort of installs in first (flag true for those installs only), confirm the offline round-trip + unwrap
/// health, then widen. There is no fleet-wide flip — each install opts in independently.
/// </para>
/// <para>
/// <b>Kill-switch.</b> Two reverts to suite #1 with nothing stranded (the read path keeps accepting already-emitted
/// suite #3): (1) set <see cref="Enabled"/> back to <c>false</c> + restart; or (2) the no-redeploy incident path —
/// set the env var <c>HARBORLINE_PQC_HYBRID_WRITE_DISABLED</c> truthy, which FORCES the policy off even when this flag
/// is <c>true</c> (<c>NodeHybridKemWritePolicyComposition.DisableEnvVarName</c>).
/// </para>
/// </remarks>
/// <summary>
/// Asset Type System configuration (ADR 0101 Rev 3.1 Wave 2b). Bound from <c>LocalNode:AssetRegistry</c>.
/// </summary>
public sealed class AssetRegistryOptions
{
    /// <summary>The default reconcile-sweep interval (seconds) when none is configured — a low-urgency
    /// recovery backstop; the startup drain runs immediately regardless.</summary>
    public const int DefaultReconcileSweepIntervalSeconds = 60;

    /// <summary>
    /// The periodic interval (seconds) at which the post-submit projection reconcile sweep runs on the node
    /// (F-RECON). Null / unset ⇒ <see cref="DefaultReconcileSweepIntervalSeconds"/>. A non-positive value is
    /// treated as the default. The STARTUP drain (the first sweep) always runs immediately, so this only
    /// governs how often a subsequently-interrupted projection is re-attempted.
    /// </summary>
    public int? ReconcileSweepIntervalSeconds { get; set; }
}

/// <summary>Configuration for the durable installation identity-coordinator recovery drain.</summary>
public sealed class IdentityCoordinatorOptions
{
    /// <summary>The default periodic recovery cadence (seconds).</summary>
    public const int DefaultRecoverySweepIntervalSeconds = 60;

    /// <summary>
    /// Seconds between recovery sweeps. Null / unset or non-positive values use the default.
    /// </summary>
    public int? RecoverySweepIntervalSeconds { get; set; }
}

public sealed class HybridKemWriteOptions
{
    /// <summary>
    /// The per-install opt-in for the hybrid-write cutover. <c>false</c> (the DEFAULT) ⇒ suite #1 only (the safe
    /// pre-cutover state). <c>true</c> ⇒ the writer boxes suite #3 for X-Wing-capable recipients (and still suite #1
    /// for incapable ones — the safe degrade). The effective state is ALSO gated by the env kill-switch
    /// (<c>HARBORLINE_PQC_HYBRID_WRITE_DISABLED</c> truthy forces it off regardless of this flag).
    /// </summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// ADR 0115 peer-sync transport configuration. Bound from
/// <c>LocalNode:Sync</c>.
/// </summary>
public sealed class SyncTransportOptions
{
    /// <summary>
    /// Operator-declared trust level for the network currently carrying node traffic. Bound from
    /// <c>LocalNode:Sync:NetworkTrust</c> and defaults fail-closed to <see cref="NetworkTrustLevel.Unknown"/>.
    /// The host does not infer trust from an SSID, interface category, or address range.
    /// </summary>
    public NetworkTrustLevel NetworkTrust { get; set; } = NetworkTrustLevel.Unknown;

    /// <summary>
    /// When <c>true</c>, each team binds a per-team LISTENING transport endpoint
    /// (<c>TeamPaths.TransportEndpoint</c> — a Unix-domain socket on POSIX / a
    /// named pipe on Windows) so remote peers can connect inbound. When
    /// <c>false</c>, the team uses an outbound-only transport (no socket bind).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Default semantics (null).</b> When left unset (<c>null</c>) the host
    /// derives the value from <see cref="MultiTeamOptions.Enabled"/>: a
    /// multi-team install listens for peers; a single-device single-team node
    /// (the Tauri sidecar boot contract sets <c>LocalNode:MultiTeam:Enabled=false</c>)
    /// runs outbound-only.
    /// </para>
    /// <para>
    /// <b>Why default off for single-device.</b> The embedded node has no inbound
    /// peers until multi-device sync (Stage 5, POST-v1). Binding a listening
    /// Unix-domain socket at the macOS per-user data directory
    /// (<c>~/Library/Application Support/...</c>) produces a <c>sun_path</c>
    /// over the macOS 104-char limit; the bind throws and takes the host down
    /// (bug-2847). The durable length-bounded UDS-path fix is a Stage-5
    /// prerequisite tracked in <c>kernel-runtime</c>.
    /// </para>
    /// </remarks>
    public bool? ListenForPeers { get; set; }

    /// <summary>
    /// Multi-device cross-machine LISTEN bind endpoint in <c>tcp://host:port</c>
    /// (or bare <c>host:port</c>) form. Consumed ONLY when
    /// <see cref="ListenForPeers"/> resolves true. When <c>null</c> (the default)
    /// the per-team transport binds the safe ephemeral loopback default
    /// (<c>tcp://127.0.0.1:0</c>) — the Bridge multi-tenant posture, where each
    /// tenant child speaks on its own never-colliding loopback port and no remote
    /// peer dials in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The production cross-machine shape</b> sets this to <c>0.0.0.0:7473</c>
    /// (the sync-daemon-protocol §2.1 default port) so a remote peer on the LAN /
    /// Tailscale mesh can reach the listener — the loopback default is unreachable
    /// off-host. The daemon is already trust-gated (<c>SharedRootTrustPolicy</c> on
    /// BOTH the initiator and the accept loop — only a same-root peer is accepted)
    /// and DoS-capped (per-handshake read-deadline + concurrency + per-IP caps
    /// before the bind), so binding <c>0.0.0.0</c> is the intended production
    /// posture, not an exposure.
    /// </para>
    /// <para>
    /// A <c>port</c> of <c>0</c> binds an OS-assigned ephemeral port (the resolved
    /// port is surfaced on the transport's <c>ListenEndpoint</c> for the mDNS
    /// advertisement). For cross-machine sync use a FIXED port (7473) so a static
    /// peer's dial target is stable across restarts.
    /// </para>
    /// </remarks>
    public string? BindAddress { get; set; }

    /// <summary>
    /// When <c>true</c>, the host advertises itself + auto-discovers + dials
    /// same-subnet peers over mDNS (paper §6.1 tier-1, zero-config LAN discovery).
    /// Default <c>false</c>: mDNS is link-local and cannot cross subnets, so it is
    /// off unless explicitly enabled. The fleet's devices are on different subnets
    /// and reach each other over Tailscale, where mDNS does not route — use
    /// <see cref="Peers"/> for that topology.
    /// </summary>
    public bool EnableMdns { get; set; }

    /// <summary>
    /// Config-driven STATIC peer dial list — each entry a <c>tcp://host:port</c>
    /// (or bare <c>host:port</c>) endpoint dialed at host startup via the gossip
    /// daemon's <c>AddPeer</c> with this node's team public key (the trust anchor;
    /// a same-root peer authenticates, a different-root one is rejected
    /// fail-closed). This is the cross-subnet / Tailscale-mesh path — the
    /// IMPORTANT one for the fleet, where mDNS cannot reach. Example:
    /// <c>["100.99.202.114:7473"]</c>.
    /// </summary>
    public List<string> Peers { get; set; } = new();

    /// <summary>
    /// Seconds between periodic anti-entropy gossip rounds. When <c>null</c> (the
    /// default) the host applies <see cref="DefaultRoundIntervalSeconds"/> — a
    /// snappier cadence than the library's paper-§6.1 30-second default, tuned for
    /// the single-user fleet's "edit-here-see-there" UX.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the catch-up cadence, not the primary latency knob.</b> With
    /// push-on-change wired (a local edit triggers an immediate authenticated round
    /// — <c>IGossipDaemon.TriggerPushAsync</c>), convergence is sub-second
    /// regardless of this value; the periodic round is the backstop that repairs a
    /// missed push (peer was offline, a dropped frame) and re-syncs after a
    /// reconnect. So the default need not be aggressive.
    /// </para>
    /// <para>
    /// <b>Why 5s, not 1s.</b> 1s is the test cadence; in production a 1s anti-entropy
    /// tick across every connected peer is needless network/CPU churn for a backstop.
    /// 5s keeps the missed-push repair window tight while leaving the paper's 30s
    /// available for operators who want the original anti-entropy cadence. Set this
    /// explicitly (e.g. <c>30</c>) to restore the paper cadence, or higher for a
    /// low-traffic mesh. Must be ≥ 1; the daemon clamps a sub-1 value to 1.
    /// </para>
    /// </remarks>
    public int? RoundIntervalSeconds { get; set; }

    /// <summary>
    /// The host's snappier default gossip round interval (seconds) applied when
    /// <see cref="RoundIntervalSeconds"/> is unset. Lower than the library's 30s
    /// paper default for a more responsive single-user "edit → appears on the
    /// other device" experience, while still serving only as the push-on-change
    /// backstop. See <see cref="RoundIntervalSeconds"/> for the rationale.
    /// </summary>
    public const int DefaultRoundIntervalSeconds = 5;
}

/// <summary>
/// Wave 5.3.C configuration for the <c>/ws</c> endpoint that carries the
/// WebSocket-framed sync-daemon transport. See
/// <c>_shared/product/wave-5.3-decomposition.md</c> §5.3.C for the full
/// design. Bound from <c>LocalNode:BrowserWebSocket</c>.
/// </summary>
public sealed class BrowserWebSocketOptions
{
    /// <summary>
    /// When <c>true</c> (the default) the hosted WebSocket endpoint maps
    /// <c>/ws</c> on the shared Kestrel listener and hands inbound
    /// connections to the registered <c>ISyncDaemonAcceptor</c>. When
    /// <c>false</c> the path is not mapped — useful for tenant children that
    /// intentionally expose only <c>/health</c> (e.g. Anchor direct-install).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum inbound WebSocket message size in bytes. Frames exceeding the
    /// cap are rejected with a <c>MessageTooBig</c> close. Default 4 MiB
    /// matches the sync-daemon-protocol §2.2 guidance for non-snapshot
    /// frames while leaving headroom below the 16 MiB hard-cap.
    /// </summary>
    public int MaxMessageBytes { get; set; } = 4 * 1024 * 1024;
}

/// <summary>
/// Wave 6.3.E.2 multi-team bootstrap configuration. Controls whether the host
/// materializes a single legacy team (via <see cref="LocalNodeOptions.TeamId"/>)
/// or an explicit list of teams at startup.
/// </summary>
public sealed class MultiTeamOptions
{
    /// <summary>
    /// When <c>true</c> (the default as of Wave 6.7), the
    /// <c>MultiTeamBootstrapHostedService</c> iterates
    /// <see cref="TeamBootstraps"/> and materializes each listed team. When
    /// <c>false</c>, a single legacy team is materialized from
    /// <see cref="LocalNodeOptions.TeamId"/>.
    /// </summary>
    /// <remarks>
    /// Wave 6.3 shipped this gated to <c>false</c> so v1 installs would not
    /// strand their top-level <c>sunfish.db</c> under the new
    /// <c>teams/{id}/</c> layout before the migration landed. Wave 6.7
    /// introduces <c>AnchorV1MigrationService</c> (accelerators/anchor) which
    /// detects the v1 layout on first launch and moves it into place, so
    /// multi-team is now the safe default. Downstream composition roots can
    /// still opt out by explicitly setting
    /// <c>LocalNode:MultiTeam:Enabled = false</c> in configuration.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Ordered list of teams to materialize at startup when
    /// <see cref="Enabled"/> is <c>true</c>. The first entry becomes the
    /// initial active team via <see cref="Harborline.Api.Kernel.Runtime.Teams.IActiveTeamAccessor"/>.
    /// </summary>
    public List<TeamBootstrap> TeamBootstraps { get; set; } = new();
}

/// <summary>
/// One configured team in <see cref="MultiTeamOptions.TeamBootstraps"/>. Carries
/// the team's GUID identifier plus an optional human-readable display name
/// forwarded to <c>ITeamContextFactory.GetOrCreateAsync</c>.
/// </summary>
public sealed class TeamBootstrap
{
    /// <summary>The team GUID (will be wrapped in a <see cref="Harborline.Api.Kernel.Runtime.Teams.TeamId"/>).</summary>
    public required Guid TeamId { get; set; }

    /// <summary>
    /// Optional display name surfaced in the team-switcher UI. When
    /// <c>null</c>, the bootstrap synthesizes a default
    /// (<c>"Team {guid:D}"</c>).
    /// </summary>
    public string? DisplayName { get; set; }
}

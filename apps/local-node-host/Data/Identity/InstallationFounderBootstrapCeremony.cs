using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.PasswordHashing;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The typed disposition of one local-installation-console bootstrap ceremony.</summary>
public enum InstallationFounderBootstrapCeremonyStatus
{
    /// <summary>Nothing was written: no complete local bootstrap authority was presented.</summary>
    SkippedNoAuthority,

    /// <summary>This ceremony minted the installation's one account, root binding, and root grant.</summary>
    Established,

    /// <summary>The installation was already bootstrapped; the same founder authority replayed.</summary>
    AlreadyEstablished,

    /// <summary>
    /// The installation was already bootstrapped under DIFFERENT founder authority evidence. The
    /// durable founder stays authoritative and nothing is written.
    /// </summary>
    RefusedChangedAuthority,

    /// <summary>
    /// The installation's founder evidence AUTHENTICATES but the root installation grant it attests
    /// to is not in the store. Nothing is written, and the host must not start through it: ADR 0160
    /// R3-H requires startup to prove the installation's binding and fail closed otherwise.
    /// </summary>
    RefusedInvalidFounderEvidence,
}

/// <summary>Non-secret outcome of one ceremony attempt. Never carries a credential or a handle.</summary>
/// <param name="Status">The typed disposition.</param>
/// <param name="Reason">A stable, classified-safe reason code suitable for an operator log.</param>
/// <param name="AccountId">The installation account id when one is known; otherwise null.</param>
public sealed record InstallationFounderBootstrapCeremonyOutcome(
    InstallationFounderBootstrapCeremonyStatus Status,
    string Reason,
    string? AccountId);

/// <summary>
/// The ONE production consumer of the installation founder-bootstrap authority — the ADR 0160 R3-E
/// "installation founder-account bootstrap" ceremony, run once per installation from the local
/// installation console.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this shape.</b> ADR 0160 R3-E says installation founder-account bootstrap "may run exactly
/// once when the installation has no account", and R3-F says "the one-time founder ceremony creates
/// the first root installation grant". The authority it drives already hard-codes the actor of that
/// ceremony as <c>local-bootstrap-authority</c> / <c>local-installation-console</c>, so the ceremony
/// is driven by LOCAL bootstrap authority — the founder credential the installation console
/// provisions into the node's own environment (<c>LocalNode__WebClient__FounderUsername</c> /
/// <c>__FounderPasswordHash</c>, minted by the node's own <c>hash-web-password</c> subcommand) — and
/// NOT by an anonymous listener route. There is deliberately no new HTTP surface: adding one would
/// put the installation-root mint behind a request the network can reach, which is a strictly larger
/// attack surface than the ADR asks for.
/// </para>
/// <para>
/// <b>Fail-closed.</b> Every precondition is a refusal, never a default. A disabled web profile, an
/// absent username or credential, a credential artifact outside the ADR 0097 verification policy, an
/// over-long username, or an unusable root-key fingerprint each return
/// <see cref="InstallationFounderBootstrapCeremonyStatus.SkippedNoAuthority"/> having written
/// nothing. An installation with no account then keeps its present behavior: every
/// <c>/api/session/account-challenge</c> attempt refuses.
/// </para>
/// <para>
/// <b>Idempotent, and not re-runnable to mint a second root.</b> Both ceremony coordinates are
/// DERIVED, not generated: the correlation id is the fixed ceremony identifier
/// <see cref="CorrelationId"/>, and the credential ceremony id is a domain-separated digest of the
/// installation's root fingerprint and the presented credential artifact. So a restart under
/// unchanged authority replays the SAME command and the authority answers
/// <c>IdempotentReplay</c>; a restart under a CHANGED environment credential replays the same
/// correlation with a different command fingerprint and the authority answers <c>ChangedReplay</c>,
/// which this ceremony surfaces as
/// <see cref="InstallationFounderBootstrapCeremonyStatus.RefusedChangedAuthority"/> — ADR 0160's
/// "changing the environment cannot overwrite username, password, status, Party, membership, or
/// permissions" invariant, made observable instead of silent. Generated coordinates would make both
/// cases indistinguishable from a bare "already initialized".
/// </para>
/// <para>
/// <b>Replay and concurrency are the authority's, not this ceremony's.</b> The singleton installation
/// key, the serializable transaction, and the uniqueness-conflict resolve path in
/// <see cref="InstallationFounderBootstrapService"/> are what make one hundred concurrent attempts
/// produce exactly one root. This ceremony adds no check-then-act of its own; it never queries "does
/// an account exist?" and then writes.
/// </para>
/// </remarks>
public sealed class InstallationFounderBootstrapCeremony
{
    /// <summary>
    /// The fixed correlation of the once-per-installation founder ceremony. It is deliberately a
    /// constant rather than a per-boot value: it is the coordinate the authority replays against.
    /// </summary>
    /// <remarks>
    /// <b>Load-bearing invariant — do not make this per-boot, and do not re-mint the founder grant
    /// under a new value.</b> It is now the coordinate the authority replays against on BOTH sides:
    /// the founder audit envelope is located by it, and so is the founder's root installation grant
    /// (<see cref="InstallationFounderBootstrapService"/>'s replay resolution). A per-boot value
    /// would miss on every restart of every installation and return <c>FounderGrantMissing</c>,
    /// which aborts host start — an unconditional brick, not an edge case.
    /// <para>
    /// The corollary binds a ceremony that does not exist yet: a future recovery ceremony must
    /// UPDATE the founder grant in place rather than re-mint it under a fresh correlation. Revoking
    /// by STATUS is safe — the resolve query deliberately does not filter <c>Status</c>.
    /// </para>
    /// </remarks>
    internal const string CorrelationId = "installation-founder-bootstrap/v1";

    /// <summary>Domain separator for the derived credential-ceremony id.</summary>
    private const string CredentialCeremonyLabel =
        "shipyard.installation-founder-bootstrap.credential-ceremony/v1";

    private readonly InstallationFounderBootstrapService _authority;
    private readonly IOptions<NodeWebClientOptions> _webClientOptions;
    private readonly string _rootPublicKeyFingerprint;
    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext>? _identityFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IDesktopOsSessionEvidence _sessionEvidence;
    private readonly string _dataDirectory;
    private string? _authenticatedInstallationId;

    /// <summary>Constructs the ceremony over the dormant authority and the local bootstrap evidence.</summary>
    /// <param name="authority">The once-only installation founder-bootstrap authority.</param>
    /// <param name="webClientOptions">The web profile carrying the console-provisioned founder credential.</param>
    /// <param name="rootPublicKeyFingerprint">
    /// The canonical fingerprint of the installation's ADR 0032 root Ed25519 public key, resolved at
    /// composition from the same root seed the node's stores are keyed from.
    /// </param>
    public InstallationFounderBootstrapCeremony(
        InstallationFounderBootstrapService authority,
        IOptions<NodeWebClientOptions> webClientOptions,
        string rootPublicKeyFingerprint,
        TimeProvider timeProvider,
        string dataDirectory)
        : this(
            authority,
            webClientOptions,
            rootPublicKeyFingerprint,
            identityFactory: null,
            timeProvider,
            new ProcessDesktopOsSessionEvidence(),
            dataDirectory)
    {
    }

    internal InstallationFounderBootstrapCeremony(
        InstallationFounderBootstrapService authority,
        IOptions<NodeWebClientOptions> webClientOptions,
        string rootPublicKeyFingerprint,
        IDbContextFactory<NodeLocalInstallationIdentityDbContext>? identityFactory,
        TimeProvider timeProvider,
        IDesktopOsSessionEvidence sessionEvidence,
        string dataDirectory)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _webClientOptions = webClientOptions ?? throw new ArgumentNullException(nameof(webClientOptions));
        _rootPublicKeyFingerprint = rootPublicKeyFingerprint
            ?? throw new ArgumentNullException(nameof(rootPublicKeyFingerprint));
        _identityFactory = identityFactory;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _sessionEvidence = sessionEvidence ?? throw new ArgumentNullException(nameof(sessionEvidence));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
    }

    /// <summary>
    /// Runs the ceremony at most once per installation. Returns a non-secret outcome. It throws when
    /// the authority cannot authenticate the installation's existing founder evidence against its own
    /// audit chain (<c>installation-identity.audit_evidence_invalid</c>); the narrower divergence of
    /// evidence that authenticates but whose root grant is absent is RETURNED as
    /// <see cref="InstallationFounderBootstrapCeremonyStatus.RefusedInvalidFounderEvidence"/> so the
    /// condition is classified and testable, and the host-start disposition is decided in one place —
    /// <see cref="InstallationFounderBootstrapCeremonyHostedService.StartAsync"/> — rather than by an
    /// exception escaping a query.
    /// </summary>
    /// <param name="cancellationToken">Cancels the ceremony.</param>
    public async Task<InstallationFounderBootstrapCeremonyOutcome> RunAsync(
        CancellationToken cancellationToken = default)
    {
        var options = _webClientOptions.Value;
        if (!options.Enabled)
        {
            return Skipped("installation-bootstrap.web_profile_disabled");
        }

        if (!KeyFingerprint.IsValid(_rootPublicKeyFingerprint))
        {
            return Skipped("installation-bootstrap.root_binding_unavailable");
        }

        var username = options.FounderUsername;
        var credentialHash = options.FounderPasswordHash;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(credentialHash))
        {
            return Skipped("installation-bootstrap.founder_credential_not_provisioned");
        }

        if (WebUsernameNormalizer.TryNormalize(username) is null)
        {
            return Skipped("installation-bootstrap.founder_username_out_of_range");
        }

        if (!Argon2idCredentialArtifact.IsCanonicalAndWithinVerificationPolicy(credentialHash))
        {
            return Skipped("installation-bootstrap.founder_credential_artifact_rejected");
        }

        var command = new InstallationFounderBootstrapCommand(
            username,
            credentialHash,
            DeriveCredentialCeremonyId(_rootPublicKeyFingerprint, credentialHash),
            _rootPublicKeyFingerprint,
            CorrelationId);

        var result = await _authority.InitializeAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.InstallationIdentityId is { } installationId &&
            result.Status is InstallationFounderBootstrapStatus.Created or
                InstallationFounderBootstrapStatus.IdempotentReplay)
        {
            _authenticatedInstallationId = installationId;
        }
        return result.Status switch
        {
            InstallationFounderBootstrapStatus.Created => new(
                InstallationFounderBootstrapCeremonyStatus.Established,
                "installation-bootstrap.established",
                result.AccountId),
            InstallationFounderBootstrapStatus.IdempotentReplay => new(
                InstallationFounderBootstrapCeremonyStatus.AlreadyEstablished,
                "installation-bootstrap.idempotent_replay",
                result.AccountId),
            InstallationFounderBootstrapStatus.AlreadyInitialized => new(
                InstallationFounderBootstrapCeremonyStatus.AlreadyEstablished,
                "installation-bootstrap.already_initialized",
                null),
            InstallationFounderBootstrapStatus.ChangedReplay => new(
                InstallationFounderBootstrapCeremonyStatus.RefusedChangedAuthority,
                "installation-bootstrap.changed_authority_refused",
                null),
            InstallationFounderBootstrapStatus.FounderGrantMissing => new(
                InstallationFounderBootstrapCeremonyStatus.RefusedInvalidFounderEvidence,
                "installation-bootstrap.founder_grant_missing",
                null),
            // Enumerated rather than defaulted: mapping an unrecognized authority status onto a
            // refusal would silently classify a new outcome as a known one.
            _ => throw new InvalidOperationException(
                "installation-identity.audit_evidence_invalid: the founder authority returned an " +
                $"unclassified status '{result.Status}'."),
        };
    }

    /// <summary>
    /// Issues only after the membership attach has deterministically derived the founder principal.
    /// The issuer independently reads the process's interactive OS identity and binds the resulting
    /// process-local claim to this exact target.
    /// </summary>
    internal Task<BootstrapClaim?> IssueClaimForTargetAsync(
        BootstrapGrantTarget target,
        CancellationToken cancellationToken)
    {
        if (_identityFactory is null ||
            _authenticatedInstallationId is null ||
            !string.Equals(_authenticatedInstallationId, target.InstallationId, StringComparison.Ordinal) ||
            !string.Equals(target.CeremonyCorrelationId, CorrelationId, StringComparison.Ordinal) ||
            target.Principal != FounderTenantMembershipAttachService.DerivePrincipal(
                target.TenantId, CorrelationId))
            return Task.FromResult<BootstrapClaim?>(null);

        var founderIdentity = _webClientOptions.Value.FounderUsername;
        if (string.IsNullOrWhiteSpace(founderIdentity))
            return Task.FromResult<BootstrapClaim?>(null);

        // Two issuers, tried in order, because a node may legitimately have no desktop.
        //
        // DesktopOsSession is the strongest local evidence and stays first: an interactive session
        // whose OS user IS the configured founder. It declines -- returns null, never throws -- when
        // Environment.UserInteractive is false, which is every Windows service and every launchd
        // daemon. Ticket 360: that decline is why the founder ceremony could not complete on ANY
        // headless host, which is the MVP's own deployment shape and both CI runners. It was read
        // for weeks as a macOS test defect; it is neither macOS nor a test defect.
        //
        // SelfHostedFileSystemOwner is the headless path and is NOT weaker by accident: it requires
        // the installation's own data directory to be owned by the process user, which is the same
        // claim a service account makes about the install it runs. BootstrapClaimRedemption.IsAccepted
        // already admits this issuer kind; only issuance was missing, so this wires a designed path
        // rather than widening the accepted set.
        var issuers = new IBootstrapClaimIssuer[]
        {
            new DesktopOsSessionBootstrapClaimIssuer(
                _timeProvider,
                _identityFactory,
                founderIdentity,
                _sessionEvidence),
            new SelfHostedFileSystemOwnerBootstrapClaimIssuer(
                _timeProvider,
                _identityFactory,
                _dataDirectory),
        };
        return IssueFirstAsync(issuers, target, cancellationToken);
    }

    private static async Task<BootstrapClaim?> IssueFirstAsync(
        IReadOnlyList<IBootstrapClaimIssuer> issuers,
        BootstrapGrantTarget target,
        CancellationToken cancellationToken)
    {
        foreach (var issuer in issuers)
        {
            var claim = await issuer.IssueAsync(target, TimeSpan.FromMinutes(10), cancellationToken)
                .ConfigureAwait(false);
            if (claim is not null) return claim;
        }
        return null;
    }

    /// <summary>
    /// Derives the deterministic credential-ceremony id. Domain-separated, and one-way over an
    /// artifact that is itself a salted one-way function of the password, so it is neither a
    /// credential nor an oracle for one; it changes if and only if the presented credential or the
    /// installation root binding changes.
    /// </summary>
    internal static string DeriveCredentialCeremonyId(
        string rootPublicKeyFingerprint,
        string credentialHash)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{CredentialCeremonyLabel}{rootPublicKeyFingerprint}{credentialHash}"));
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static InstallationFounderBootstrapCeremonyOutcome Skipped(string reason) =>
        new(InstallationFounderBootstrapCeremonyStatus.SkippedNoAuthority, reason, null);
}

/// <summary>
/// Runs the founder-bootstrap ceremony once at host start, after the encryption guard has proved the
/// SQLCipher key and applied the installation-identity schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>THREE conditions abort startup.</b> Two are the same deliberate fail-closed class: the
/// authority's <c>installation-identity.audit_evidence_invalid</c> throw (the installation's founder
/// evidence does not authenticate against its own audit chain) and
/// <see cref="InstallationFounderBootstrapCeremonyStatus.RefusedInvalidFounderEvidence"/> (it
/// authenticates but its root installation grant is absent). ADR 0160 R3-H requires startup to
/// prove the presented root is the active binding and to fail closed otherwise, so the host must not
/// serve identity through either. This mirrors the SC-1 store-encryption guard, which likewise
/// refuses to start rather than continue on unproven state.
/// </para>
/// <para>
/// The third is the defensive <c>_ =&gt;</c> arm of the ceremony's status switch, which throws on an
/// authority status it does not recognize. It is unreachable today — every member of
/// <see cref="InstallationFounderBootstrapStatus"/> is mapped — and it is deliberate: silently
/// classifying an unrecognized outcome as a known one is the worse failure. It does, however, borrow
/// the <c>audit_evidence_invalid</c> code for a condition that is not an audit-evidence failure, so
/// if it ever fires the reason code will misdirect diagnosis. Folded into the classification card
/// below rather than fixed here.
/// </para>
/// <para>
/// <b>Known gap, carded.</b> This runner does not yet DISTINGUISH the security conditions from an
/// availability failure. A <c>SqliteException</c> surviving the authority's busy-retry budget
/// (8 attempts, ~180 ms) propagates out of <see cref="StartAsync"/> and stops the host — and so does
/// a <c>DbUpdateException</c> that is neither a uniqueness conflict nor retryable contention, which
/// takes the identical path. Both are availability outcomes wearing a security justification.
/// Classifying them — propagate the fail-closed conditions, log-and-continue on the rest, since an
/// installation that cannot complete its bootstrap simply keeps refusing every challenge — is tracked
/// separately rather than widened into this card, together with the reason-code correction above.
/// </para>
/// </remarks>
internal sealed class InstallationFounderBootstrapCeremonyHostedService : IHostedService
{
    private readonly InstallationFounderBootstrapCeremony _ceremony;
    private readonly ILogger<InstallationFounderBootstrapCeremonyHostedService> _logger;

    public InstallationFounderBootstrapCeremonyHostedService(
        InstallationFounderBootstrapCeremony ceremony,
        ILogger<InstallationFounderBootstrapCeremonyHostedService> logger)
    {
        _ceremony = ceremony ?? throw new ArgumentNullException(nameof(ceremony));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var outcome = await _ceremony.RunAsync(cancellationToken).ConfigureAwait(false);
        switch (outcome.Status)
        {
            case InstallationFounderBootstrapCeremonyStatus.Established:
                _logger.LogInformation(
                    "Installation founder bootstrap {Reason}: the installation's one account, root " +
                    "binding, and root grant are established.",
                    outcome.Reason);
                break;
            case InstallationFounderBootstrapCeremonyStatus.AlreadyEstablished:
                _logger.LogInformation(
                    "Installation founder bootstrap {Reason}: the installation is already " +
                    "bootstrapped; nothing was written.",
                    outcome.Reason);
                break;
            case InstallationFounderBootstrapCeremonyStatus.RefusedInvalidFounderEvidence:
                _logger.LogCritical(
                    "Installation founder bootstrap {Reason}: this installation's authenticated " +
                    "founder evidence attests to a root installation grant that is not in the " +
                    "identity store. Nothing was written and the host will not start — ADR 0160 " +
                    "R3-H requires startup to prove the installation binding and fail closed. Use " +
                    "the governed recovery ceremony.",
                    outcome.Reason);
                throw new InvalidOperationException(
                    "installation-identity.audit_evidence_invalid: the root installation grant " +
                    "attested by the installation's authenticated founder evidence is absent.");
            case InstallationFounderBootstrapCeremonyStatus.RefusedChangedAuthority:
                _logger.LogWarning(
                    "Installation founder bootstrap {Reason}: the presented environment founder " +
                    "credential differs from this installation's durable founder. The durable " +
                    "founder remains authoritative and nothing was written — changing the " +
                    "environment cannot overwrite it. Use the governed recovery ceremony instead.",
                    outcome.Reason);
                break;
            default:
                _logger.LogWarning(
                    "Installation founder bootstrap {Reason}: no complete local bootstrap authority " +
                    "was presented, so no installation account exists and every account-challenge " +
                    "attempt refuses.",
                    outcome.Reason);
                break;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The single sanctioned composition path for the founder-bootstrap ceremony.</summary>
public static class InstallationFounderBootstrapCeremonyRegistration
{
    /// <summary>
    /// Registers the once-per-installation founder ceremony and its startup runner. Call this AFTER
    /// the SQLCipher registration so the encryption guard's hosted service — which applies the
    /// installation-identity schema — is ordered ahead of it.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="rootPublicKeyFingerprint">
    /// The canonical fingerprint of the installation's root Ed25519 public key.
    /// </param>
    public static IServiceCollection AddInstallationFounderBootstrapCeremony(
        this IServiceCollection services,
        string rootPublicKeyFingerprint,
        AuthorizationSeedProfile seedProfile,
        string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPublicKeyFingerprint);
        ArgumentNullException.ThrowIfNull(seedProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        services.AddSingleton(provider => new InstallationFounderBootstrapService(
            provider.GetRequiredService<
                Microsoft.EntityFrameworkCore.IDbContextFactory<NodeLocalInstallationIdentityDbContext>>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new BootstrapClaimRedemptionService(
            provider.GetRequiredService<
                Microsoft.EntityFrameworkCore.IDbContextFactory<NodeLocalInstallationIdentityDbContext>>(),
            provider.GetRequiredService<Harborline.Api.Blocks.AccessGrant.IGrantStore>(),
            provider.GetRequiredService<InitialGrantIssuanceService>(),
            seedProfile,
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton(provider => new InstallationFounderBootstrapCeremony(
            provider.GetRequiredService<InstallationFounderBootstrapService>(),
            provider.GetRequiredService<IOptions<NodeWebClientOptions>>(),
            rootPublicKeyFingerprint,
            provider.GetRequiredService<
                Microsoft.EntityFrameworkCore.IDbContextFactory<NodeLocalInstallationIdentityDbContext>>(),
            provider.GetRequiredService<TimeProvider>(),
            new ProcessDesktopOsSessionEvidence(),
            dataDirectory));
        services.AddHostedService<InstallationFounderBootstrapCeremonyHostedService>();
        return services;
    }
}

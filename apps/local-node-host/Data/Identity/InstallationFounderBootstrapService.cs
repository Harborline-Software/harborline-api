using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.PasswordHashing;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The typed outcome of the dormant installation-founder authority ceremony.</summary>
public enum InstallationFounderBootstrapStatus
{
    Created,
    IdempotentReplay,
    AlreadyInitialized,
    ChangedReplay,

    /// <summary>
    /// The installation's founder evidence AUTHENTICATES, but the root installation grant that
    /// evidence attests to is not in the store. A fail-closed divergence, not a replay: nothing is
    /// written and no founder authority can be resolved.
    /// </summary>
    FounderGrantMissing,
}

/// <summary>Secret-bearing bootstrap input. It is accepted only by the installation authority service.</summary>
public sealed record InstallationFounderBootstrapCommand(
    string Username,
    string CredentialHash,
    string CredentialCeremonyId,
    string RootPublicKeyFingerprint,
    string CorrelationId)
{
    /// <summary>
    /// Keeps the two secret-bearing members out of <see cref="object.ToString"/>. A positional record
    /// prints every property by default, and this type is now constructed on a live startup path — one
    /// structured-logging call with <c>{Command}</c> would otherwise put the founder's Argon2id
    /// artifact and username in the node log. The remaining members are non-secret by construction: a
    /// domain-separated one-way ceremony digest, a public-key fingerprint, and a fixed correlation.
    /// </summary>
    /// <remarks>
    /// Declared <c>private</c>, not <c>protected override</c>: for a sealed record whose base type is
    /// <see cref="object"/> the compiler synthesizes <c>PrintMembers</c> as private, and a user
    /// declaration must match that signature or the type does not compile — CS8879 ("Record member
    /// … must be private"), plus CS0115 for the absent base member.
    /// </remarks>
    private bool PrintMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append("Username = <redacted>, CredentialHash = <redacted>, CredentialCeremonyId = ");
        builder.Append(CredentialCeremonyId);
        builder.Append(", RootPublicKeyFingerprint = ");
        builder.Append(RootPublicKeyFingerprint);
        builder.Append(", CorrelationId = ");
        builder.Append(CorrelationId);
        return true;
    }
}

/// <summary>Non-secret result from one installation founder attempt.</summary>
public sealed record InstallationFounderBootstrapResult(
    InstallationFounderBootstrapStatus Status,
    string? InstallationIdentityId,
    string? AccountId,
    string? GrantId);

/// <summary>
/// Creates the first installation account, root binding, installation grant, and audit envelope in
/// one encrypted-store transaction.
/// </summary>
/// <remarks>
/// ADR 0160 R3-E's once-per-installation founder ceremony. Its ONE ratified production consumer is
/// <see cref="InstallationFounderBootstrapCeremony"/>, which runs it from local bootstrap authority
/// at host start; <c>InstallationIdentityDormancyArchTests</c> holds that consumer set to exactly
/// one file. No listener route reaches this authority, and none should: the actor recorded in its
/// audit envelope is the local installation console, not a web caller.
/// </remarks>
public sealed class InstallationFounderBootstrapService
{
    private const string FounderEventType = InstallationIdentityAuditEventTypes.FounderBootstrapped;
    private const int BusyRetryCount = 8;

    /// <summary>Issuer stamped on the root installation grant this ceremony mints. Written nowhere else.</summary>
    private const string RootGrantIssuerKind = "installation-bootstrap";

    private static readonly string RootPermissionsJson = JsonSerializer.Serialize(new[]
    {
        "installation:accounts:manage",
        "installation:audit:read",
        "installation:identity:recover",
        "installation:ownership:transfer",
        "installation:tenants:create",
    });

    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _contextFactory;
    private readonly TimeProvider _timeProvider;

    public InstallationFounderBootstrapService(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> contextFactory,
        TimeProvider timeProvider)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<InstallationFounderBootstrapResult> InitializeAsync(
        InstallationFounderBootstrapCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalizedUsername = NormalizeUsername(command.Username);
        ValidateCommand(command);
        var credentialCeremonyId = command.CredentialCeremonyId.ToLowerInvariant();
        var rootPublicKeyFingerprint = command.RootPublicKeyFingerprint.ToUpperInvariant();
        var commandFingerprint = InstallationAuditIntegrity.Hash(
            normalizedUsername,
            credentialCeremonyId,
            Argon2idCredentialArtifact.AlgorithmId,
            rootPublicKeyFingerprint);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TryInitializeAsync(
                    command,
                    normalizedUsername,
                    credentialCeremonyId,
                    rootPublicKeyFingerprint,
                    commandFingerprint,
                    _timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryableContention(exception) && attempt < BusyRetryCount)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<InstallationFounderBootstrapResult> TryInitializeAsync(
        InstallationFounderBootstrapCommand command,
        string normalizedUsername,
        string credentialCeremonyId,
        string rootPublicKeyFingerprint,
        string commandFingerprint,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        var existingIdentity = await context.InstallationIdentities.AsNoTracking()
            .SingleOrDefaultAsync(
                identity => identity.SingletonKey == InstallationIdentityRecord.SingletonKeyValue,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingIdentity is not null)
        {
            return await ResolveExistingAsync(
                context,
                existingIdentity,
                command,
                commandFingerprint,
                cancellationToken).ConfigureAwait(false);
        }

        var identityId = RandomHex(32);
        var accountId = RandomHex(32);
        var grantId = RandomHex(32);
        try
        {
            context.InstallationIdentities.Add(new InstallationIdentityRecord
            {
                SingletonKey = InstallationIdentityRecord.SingletonKeyValue,
                InstallationIdentityId = identityId,
                ActiveRootEpoch = 1,
                AuthorityVersion = InstallationIdentityRecord.Revision3AuthorityVersion,
                OwnerVersion = 1,
                CreatedAtUtc = occurredAtUtc,
                UpdatedAtUtc = occurredAtUtc,
            });
            context.Accounts.Add(new InstallationAccountRecord
            {
                AccountId = accountId,
                NormalizedUsername = normalizedUsername,
                CredentialHash = command.CredentialHash,
                CredentialAlgorithm = Argon2idCredentialArtifact.AlgorithmId,
                CredentialCeremonyId = credentialCeremonyId,
                CredentialVersion = 1,
                Status = InstallationAccountStatus.Active,
                SecurityVersion = 1,
                OwnerVersion = 1,
                CreatedAtUtc = occurredAtUtc,
                UpdatedAtUtc = occurredAtUtc,
            });
            context.RootKeyEpochs.Add(new InstallationRootKeyEpochRecord
            {
                InstallationIdentityId = identityId,
                EpochNumber = 1,
                RootPublicKeyFingerprint = rootPublicKeyFingerprint,
                Status = InstallationRootEpochStatus.Active,
                TransitionCorrelationId = command.CorrelationId,
                CreatedAtUtc = occurredAtUtc,
            });
            context.InstallationAccessGrants.Add(new InstallationAccessGrantRecord
            {
                GrantId = grantId,
                AccountId = accountId,
                PermissionsJson = RootPermissionsJson,
                Status = InstallationAccessGrantStatus.Active,
                IssuerKind = RootGrantIssuerKind,
                IssuerId = identityId,
                AuthorizationEpoch = 1,
                OwnerVersion = 1,
                AuditCorrelationId = command.CorrelationId,
                CreatedAtUtc = occurredAtUtc,
                UpdatedAtUtc = occurredAtUtc,
            });

            // The founder designation (earlier repository ticket #3373). Written HERE, in the same audited transaction
            // that mints the root grant, because the ceremony is the only production path to a first
            // account and it already knows that account — so this adds no lookup and no new failure
            // mode. The rejected alternative was to have the login rebind call founder-bind once a
            // selected session exists; that leaves a window in which the founder reads as unresolved,
            // and windows on identity facts become the state everyone tests against.
            //
            // Deliberately NOT written on the replay path. Replay stays a pure function of audit
            // evidence rather than mutable identity state, and an install bootstrapped before this
            // change keeps no designation — which is what keeps SelectedSessionMembership.Unresolved
            // reachable and correct rather than a value nothing can produce.
            var designationId = RandomHex(32);
            context.RootDesignations.Add(new InstallationIdentityRootDesignationRecord
            {
                SingletonKey = InstallationIdentityRootDesignationRecord.SingletonKeyValue,
                DesignationId = designationId,
                // Domain-separated from founder-bind's canonical binding digest: that one keys on a
                // verified tenant/principal/party triple, none of which exists yet at bootstrap. The
                // ceremony's source IS the identity and account it is creating in this transaction.
                SourceCompositeKeyDigest = InstallationAuditIntegrity.Hash(
                    "installation-founder-bootstrap-designation/v1",
                    identityId,
                    accountId),
                AccountId = accountId,
                ExpectedSourceVersion = 1,
                IdempotencyKeyDigest = InstallationAuditIntegrity.Hash(
                    "installation-founder-bootstrap-designation-idempotency/v1",
                    commandFingerprint),
                AuditCorrelationId = command.CorrelationId,
                OwnerVersion = 1,
                DesignatedAtUtc = occurredAtUtc,
                VerifiedAtUtc = occurredAtUtc,
            });

            var payloadDigest = InstallationAuditIntegrity.Hash(
                identityId,
                accountId,
                grantId,
                designationId,
                normalizedUsername,
                credentialCeremonyId,
                Argon2idCredentialArtifact.AlgorithmId,
                rootPublicKeyFingerprint,
                RootPermissionsJson,
                "1",
                "1");
            var envelope = new InstallationAuditEnvelopeRecord
            {
                InstallationIdentityId = identityId,
                Sequence = 1,
                CorrelationId = command.CorrelationId,
                CommandFingerprint = commandFingerprint,
                EventType = FounderEventType,
                ActorKind = "local-bootstrap-authority",
                ActorId = "local-installation-console",
                RootEpoch = 1,
                RootPublicKeyFingerprint = rootPublicKeyFingerprint,
                PreviousHash = InstallationAuditIntegrity.ZeroHash,
                EnvelopeHash = string.Empty,
                PayloadDigest = payloadDigest,
                OccurredAtUtc = occurredAtUtc,
            };
            envelope.EnvelopeHash = InstallationAuditIntegrity.ComputeEnvelopeHash(envelope);
            context.AuditEnvelopes.Add(envelope);
            context.AuditHeads.Add(new InstallationAuditHeadRecord
            {
                InstallationIdentityId = identityId,
                Sequence = 1,
                HeadHash = envelope.EnvelopeHash,
                OwnerVersion = 1,
                UpdatedAtUtc = occurredAtUtc,
            });

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new InstallationFounderBootstrapResult(
                InstallationFounderBootstrapStatus.Created,
                identityId,
                accountId,
                grantId);
        }
        catch (DbUpdateException exception) when (IsUniquenessConflict(exception))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();
            var winner = await context.InstallationIdentities.AsNoTracking()
                .SingleAsync(
                    identity => identity.SingletonKey == InstallationIdentityRecord.SingletonKeyValue,
                    cancellationToken)
                .ConfigureAwait(false);
            return await ResolveExistingAsync(
                context,
                winner,
                command,
                commandFingerprint,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Classifies a replay against an installation that already has an identity. Every row it reads is
    /// addressed by a key the authenticated evidence names — never by being the only row in its table.
    /// </summary>
    /// <remarks>
    /// This method runs on the host-start path (<see cref="InstallationFounderBootstrapCeremony"/>'s
    /// hosted runner) on EVERY boot of a bootstrapped installation, so an unhandled exception here
    /// stops the node from starting at all. <c>Accounts</c> is multi-row by design —
    /// <c>WebJoinerAccountMinter</c> adds a second installation account for every invited joiner — so
    /// resolving the founder by cardinality ("take the only row") armed a permanent, un-recoverable
    /// start failure the first time a joiner accepted. The founder's root grant is therefore resolved
    /// by this ceremony's own correlation, which the grant carries under a NOT NULL unique index and
    /// which no later governed ceremony over the account or the root rewrites.
    /// </remarks>
    private static async Task<InstallationFounderBootstrapResult> ResolveExistingAsync(
        NodeLocalInstallationIdentityDbContext context,
        InstallationIdentityRecord identity,
        InstallationFounderBootstrapCommand command,
        string commandFingerprint,
        CancellationToken cancellationToken)
    {
        var replay = await context.AuditEnvelopes.AsNoTracking()
            .SingleOrDefaultAsync(
                envelope => envelope.CorrelationId == command.CorrelationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (replay is null)
        {
            return new InstallationFounderBootstrapResult(
                InstallationFounderBootstrapStatus.AlreadyInitialized,
                null,
                null,
                null);
        }

        var head = await context.AuditHeads.AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.InstallationIdentityId == identity.InstallationIdentityId,
                cancellationToken)
            .ConfigureAwait(false);
        if (head is null ||
            replay.InstallationIdentityId != identity.InstallationIdentityId ||
            replay.Sequence != 1 ||
            !string.Equals(replay.EventType, FounderEventType, StringComparison.Ordinal) ||
            !await HasValidAuditChainAsync(context, replay, head, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "installation-identity.audit_evidence_invalid: founder replay evidence is not authenticated.");
        }

        var isSameCommand = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(replay.CommandFingerprint),
            Encoding.UTF8.GetBytes(commandFingerprint));
        if (!isSameCommand)
        {
            return new InstallationFounderBootstrapResult(
                InstallationFounderBootstrapStatus.ChangedReplay,
                null,
                null,
                null);
        }

        // Resolved by the ceremony's OWN durable coordinate: the correlation the audit envelope was
        // just located by, which the root grant carries.
        //
        // WHAT THIS REPLACES, PRECISELY — the two halves were not equally live, and the difference
        // matters to anyone weighing a revert. This was a predicate-less SingleAsync over Accounts
        // followed by one over InstallationAccessGrants. The ACCOUNT read was the live brick:
        // Accounts is multi-row by design — WebJoinerAccountMinter adds a second
        // InstallationAccountRecord when an invited joiner accepts at
        // POST /api/session/account-setup-accept — so the first acceptance would have thrown here, on
        // a host-start path, permanently. The GRANT read was latent rather than live: this service is
        // currently the ONLY production writer of InstallationAccessGrantRecord (the grant the
        // acceptance path issues is the separate TENANT substrate, through
        // AccountSetupAcceptanceService's grant issuer), so that table holds one row today. Do NOT
        // read "only one writer" as "the keyed lookup is unnecessary" — it is the same defect class,
        // and restoring either predicate-less read re-arms a node that never starts again.
        //
        // Correlation-keying rather than username-keying is deliberate: it keeps the replay decision a
        // function of the AUDIT EVIDENCE alone, which is what
        // Bootstrap_Replay_Does_Not_Depend_On_Later_Account_Or_Root_State requires — a governed
        // rename, credential rotation or root rotation must not re-classify an unchanged replay.
        //
        // Two schema facts make this lookup total, and both carry weight: AuditCorrelationId is NOT
        // NULL and carries the unique index ux_installation_access_grants_audit_correlation. The NOT
        // NULL half is not incidental — SQLite treats NULLs as DISTINCT in a unique index, so a
        // nullable column would have left the multiplicity hole open. The grant's FK to the account is
        // Restrict, but that leg is NOT load-bearing and nothing here rests on it: SQLite enforces
        // foreign keys only under PRAGMA foreign_keys, which this repo never sets.
        //
        // INVARIANT — see InstallationFounderBootstrapCeremony.CorrelationId. That correlation must
        // stay a compile-time constant and the founder grant must keep the value it was minted with; a
        // per-boot value would miss here on every restart of every installation.
        var rootGrant = await context.InstallationAccessGrants.AsNoTracking()
            .SingleOrDefaultAsync(
                grant => grant.AuditCorrelationId == command.CorrelationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (rootGrant is null)
        {
            return new InstallationFounderBootstrapResult(
                InstallationFounderBootstrapStatus.FounderGrantMissing,
                identity.InstallationIdentityId,
                null,
                null);
        }

        return new InstallationFounderBootstrapResult(
            InstallationFounderBootstrapStatus.IdempotentReplay,
            identity.InstallationIdentityId,
            rootGrant.AccountId,
            rootGrant.GrantId);
    }

    private static async Task<bool> HasValidAuditChainAsync(
        NodeLocalInstallationIdentityDbContext context,
        InstallationAuditEnvelopeRecord founderEnvelope,
        InstallationAuditHeadRecord head,
        CancellationToken cancellationToken)
    {
        if (head.Sequence < founderEnvelope.Sequence)
        {
            return false;
        }

        var chain = await context.AuditEnvelopes.AsNoTracking()
            .Where(envelope =>
                envelope.InstallationIdentityId == founderEnvelope.InstallationIdentityId &&
                envelope.Sequence >= founderEnvelope.Sequence &&
                envelope.Sequence <= head.Sequence)
            .OrderBy(envelope => envelope.Sequence)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (chain.LongLength != head.Sequence - founderEnvelope.Sequence + 1)
        {
            return false;
        }

        return InstallationAuditIntegrity.HasValidChain(
            chain,
            head,
            founderEnvelope.InstallationIdentityId);
    }

    private static string NormalizeUsername(string username)
        => WebUsernameNormalizer.NormalizeRequired(username);

    private static void ValidateCommand(InstallationFounderBootstrapCommand command)
    {
        if (!Argon2idCredentialArtifact.IsCanonicalAndWithinVerificationPolicy(command.CredentialHash))
        {
            throw new ArgumentException(
                "Credential artifact must be canonical Argon2id v1.3 at or above the policy floor.",
                nameof(command));
        }

        if (!Guid.TryParseExact(command.CredentialCeremonyId, "N", out _))
        {
            throw new ArgumentException(
                "Credential ceremony id must be a 32-character UUID without separators.",
                nameof(command));
        }

        if (!KeyFingerprint.IsValid(command.RootPublicKeyFingerprint))
        {
            throw new ArgumentException(
                "Root public-key fingerprint must use the canonical SHA-256 format.",
                nameof(command));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.CorrelationId);
        if (command.CorrelationId.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(command), "A bootstrap authority field exceeds its limit.");
        }
    }

    private static bool IsRetryableContention(Exception exception) =>
        exception is SqliteException { SqliteErrorCode: 5 or 6 } ||
        exception.InnerException is SqliteException { SqliteErrorCode: 5 or 6 };

    private static bool IsUniquenessConflict(Exception exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 };

    private static string RandomHex(int byteCount) => Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount));

}

using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Blocks.BuilderDefinitions;

namespace Harborline.Api.LocalNodeHost.Data.Configuration;

/// <summary>One Proposed change read back from the host, with the check it currently carries.</summary>
/// <param name="State">The platform's working state: baseline and autosaved edits.</param>
/// <param name="Check">The recorded check, or null when none was recorded.</param>
/// <param name="SavedVersionCount">How many immutable checkpoints this proposed change has taken.</param>
/// <param name="StartedBy">The authenticated principal that started it.</param>
public sealed record HostProposedChange(ProposedChangeState State, ProposedChangeCheck? Check,
    int SavedVersionCount, string StartedBy);

/// <summary>A Released package as the host holds it: the exact bytes and the api's signature over them.</summary>
/// <param name="Released">The platform's released package, rebuilt from the stored bytes.</param>
/// <param name="SignatureJson">The api's signed envelope over the artifact digest.</param>
/// <param name="ReleasedBy">The authenticated principal that released it.</param>
/// <param name="ReleasedAt">The admitted instant of the release.</param>
public sealed record HostReleasedPackage(ReleasedPackage Released, string SignatureJson, string ReleasedBy,
    DateTimeOffset ReleasedAt);

/// <summary>
/// The api half of propose, save and release (T-461, DES-0044 governance-ck-1 and ck-6): durable storage
/// for Proposed changes, their immutable Saved versions and the signed Released packages offered for
/// activation.
/// </summary>
/// <remarks>
/// <para>
/// The bar this slice draws. Editing a Proposed change never touches
/// <c>configuration_effective_generations</c>, and this type holds no reference to
/// <see cref="ConfigurationActivationTarget"/>'s write path: the only thing it reads from the activation
/// side is the current effective generation, as the baseline to record and later to compare against.
/// </para>
/// <para>
/// Signing is the api's, per ADR 0097 decision 6. The platform produces the provider-neutral document;
/// this signs the SHA-256 of those exact bytes with the node's operation signer, so a signature can only
/// vouch for one artifact. The digest a release returns is re-derived from the stored bytes on every
/// read rather than echoed from a stored column, so the digest shown to the author is the artifact.
/// </para>
/// </remarks>
public sealed class ConfigurationProposalStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<NodeLocalPacksDbContext> _factory;
    private readonly ConfigurationActivationTarget _activation;
    private readonly IOperationSigner _signer;

    /// <summary>Composes the store over the pack database, the effective-generation read and the node signer.</summary>
    public ConfigurationProposalStore(IDbContextFactory<NodeLocalPacksDbContext> factory,
        ConfigurationActivationTarget activation, IOperationSigner signer)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _activation = activation ?? throw new ArgumentNullException(nameof(activation));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
    }

    /// <summary>The generation currently governing the tenant; the baseline a proposed change starts from.</summary>
    public ConfigurationGeneration ReadEffective(TenantId tenant) => _activation.ReadEffective(tenant);

    /// <summary>Starts a Proposed change from the effective generation, recording it as the baseline.</summary>
    public HostProposedChange Start(TenantId tenant, string proposalId, string principal, DateTimeOffset now)
    {
        var state = ConfigurationProposal.Start(proposalId, ReadEffective(tenant));
        using var context = _factory.CreateDbContext();
        if (context.Proposals.Find(tenant.Value, proposalId) is not null)
            throw new ArgumentException("configuration-proposal-exists");
        context.Proposals.Add(new ConfigurationProposalRow
        {
            Tenant = tenant.Value, ProposalId = proposalId, BaselineDigest = state.BaselineDigest,
            EditsJson = Serialize(state.Edits), StartedBy = principal, StartedAt = now, AutosavedAt = now,
            SavedVersionCount = 0,
        });
        context.SaveChanges();
        return new(state, null, 0, principal);
    }

    /// <summary>Reads one Proposed change, or null when the tenant has none by that identity.</summary>
    public HostProposedChange? Read(TenantId tenant, string proposalId)
    {
        using var context = _factory.CreateDbContext();
        var row = context.Proposals.AsNoTracking()
            .FirstOrDefault(r => r.Tenant == tenant.Value && r.ProposalId == proposalId);
        return row is null ? null : Rebuild(row);
    }

    /// <summary>
    /// Autosaves one edit. The whole working set is rewritten from the platform's result, so an edit can
    /// never leave a half-applied set behind, and the recorded check is left exactly as it was: the check
    /// still names the digest it observed, and that digest no longer matches, which is the invalidation.
    /// </summary>
    public HostProposedChange Autosave(TenantId tenant, string proposalId, ProposedDefinitionEdit edit,
        DateTimeOffset now)
    {
        using var context = _factory.CreateDbContext();
        var row = Require(context, tenant, proposalId);
        var state = ConfigurationProposal.Autosave(Rebuild(row).State, edit);
        row.EditsJson = Serialize(state.Edits);
        row.AutosavedAt = now;
        context.SaveChanges();
        return Rebuild(row);
    }

    /// <summary>
    /// Freezes the current working edits into the next immutable Saved version. Rows are insert-only and
    /// keyed by ordinal, so a checkpoint already taken cannot be rewritten by a later save.
    /// </summary>
    public SavedVersion Save(TenantId tenant, string proposalId, string author, string rationale, DateTimeOffset now)
    {
        using var context = _factory.CreateDbContext();
        var row = Require(context, tenant, proposalId);
        var version = ConfigurationProposal.Save(Rebuild(row).State, row.SavedVersionCount + 1, author, rationale, now);
        context.SavedVersions.Add(new ConfigurationSavedVersionRow
        {
            Tenant = tenant.Value, ProposalId = proposalId, Ordinal = version.Ordinal, Digest = version.Digest,
            BaselineDigest = version.BaselineDigest, Author = version.Author, Rationale = version.Rationale,
            SavedAt = version.SavedAt, EditsJson = Serialize(version.Edits),
        });
        row.SavedVersionCount = version.Ordinal;
        context.SaveChanges();
        return version;
    }

    /// <summary>Reads one Saved version by ordinal, or null when the proposed change has no such checkpoint.</summary>
    public SavedVersion? ReadSavedVersion(TenantId tenant, string proposalId, int ordinal)
    {
        using var context = _factory.CreateDbContext();
        var row = context.SavedVersions.AsNoTracking()
            .FirstOrDefault(r => r.Tenant == tenant.Value && r.ProposalId == proposalId && r.Ordinal == ordinal);
        return row is null ? null : new SavedVersion(row.ProposalId, row.BaselineDigest, row.Ordinal, row.Author,
            row.Rationale, row.SavedAt, Deserialize(row.EditsJson), row.Digest);
    }

    /// <summary>
    /// Records a check against the working state as it is right now. T-463 owns what a check contains; this
    /// binds the receipt to the exact working digest so any later edit can be seen to have invalidated it.
    /// </summary>
    public HostProposedChange RecordCheck(TenantId tenant, string proposalId, string receiptId)
    {
        if (string.IsNullOrWhiteSpace(receiptId)) throw new ArgumentException("configuration-check-receipt-required");
        using var context = _factory.CreateDbContext();
        var row = Require(context, tenant, proposalId);
        row.CheckedDigest = ConfigurationProposal.WorkingDigest(Rebuild(row).State);
        row.CheckReceiptId = receiptId;
        context.SaveChanges();
        return Rebuild(row);
    }

    /// <summary>
    /// Releases one Saved version. The platform decides and exports; the api signs the exported bytes and
    /// stores them. A refusal writes nothing, so a refused release leaves no artifact anyone could activate.
    /// </summary>
    public async ValueTask<(ConfigurationReleaseResult Result, HostReleasedPackage? Stored)> ReleaseAsync(
        TenantId tenant, string proposalId, int ordinal, string packageKey, string revision,
        AuthorizationWriteContext authority, CancellationToken cancellationToken = default)
    {
        using var context = _factory.CreateDbContext();
        var row = Require(context, tenant, proposalId);
        var proposed = Rebuild(row);
        var version = ReadSavedVersion(tenant, proposalId, ordinal)
            ?? throw new ArgumentException("configuration-saved-version-missing");
        // A proposed change with no recorded check refuses in the producer, beside every other release
        // refusal, rather than here: the api does not get to author a release rule.
        var result = ConfigurationProposal.Release(proposed.State, version, proposed.Check,
            ReadEffective(tenant), packageKey, revision);
        if (result.Released is null) return (result, null);

        var released = result.Released;
        var principal = authority.Principal.Value;
        // The signature covers the artifact digest, the package identity and the saved version it carries,
        // so it vouches for one exact document rather than for a package name and revision.
        var signed = await _signer.SignAsync(new ReleasedPackageSubject(released.Digest, released.PackageKey,
            released.Revision, released.SavedVersionDigest, released.BaselineDigest, tenant.Value, principal),
            authority.At, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        var signatureJson = JsonSerializer.Serialize(signed, Json);

        var existing = await context.ReleasedPackages
            .FindAsync([tenant.Value, released.Digest], cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            context.ReleasedPackages.Add(new ConfigurationReleasedPackageRow
            {
                Tenant = tenant.Value, Digest = released.Digest, ProposalId = proposalId,
                SavedVersionDigest = released.SavedVersionDigest, BaselineDigest = released.BaselineDigest,
                PackageKey = released.PackageKey, Revision = released.Revision,
                Document = released.Document.ToArray(), SignatureJson = signatureJson,
                ReleasedBy = principal, CheckReceiptId = proposed.Check!.ReceiptId, ReleasedAt = authority.At,
            });
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return (result, new(released, signatureJson, principal, authority.At));
        }
        // Releasing the same saved version twice exports byte-identical bytes, so the first release stands
        // and its signature is returned rather than a second signature over the same artifact.
        return (result, new(Rebuild(existing), existing.SignatureJson, existing.ReleasedBy, existing.ReleasedAt));
    }

    /// <summary>
    /// The Released packages offered for activation, newest first. Every digest is re-derived from the
    /// stored bytes, so an offer cannot advertise an identity the artifact does not have.
    /// </summary>
    public IReadOnlyList<HostReleasedPackage> Offered(TenantId tenant, string? proposalId = null)
    {
        using var context = _factory.CreateDbContext();
        // SQLite cannot ORDER BY a DateTimeOffset, so the ordering is applied client side over the
        // tenant's own released rows rather than pushed into the query.
        return context.ReleasedPackages.AsNoTracking()
            .Where(r => r.Tenant == tenant.Value && (proposalId == null || r.ProposalId == proposalId))
            .AsEnumerable()
            .OrderByDescending(r => r.ReleasedAt).ThenBy(r => r.Digest, StringComparer.Ordinal)
            .Select(r => new HostReleasedPackage(Rebuild(r), r.SignatureJson, r.ReleasedBy, r.ReleasedAt))
            .ToArray();
    }

    /// <summary>Verifies one offered package's signature against the bytes it is offered with.</summary>
    public static bool VerifyOffer(HostReleasedPackage offer, IOperationVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(verifier);
        var signed = JsonSerializer.Deserialize<SignedOperation<ReleasedPackageSubject>>(offer.SignatureJson, Json);
        return signed is not null
            && signed.Payload.Digest == offer.Released.Digest
            && verifier.Verify(signed);
    }

    private static ConfigurationProposalRow Require(NodeLocalPacksDbContext context, TenantId tenant, string proposalId)
        => context.Proposals.FirstOrDefault(r => r.Tenant == tenant.Value && r.ProposalId == proposalId)
            ?? throw new ArgumentException("configuration-proposal-missing");

    private static HostProposedChange Rebuild(ConfigurationProposalRow row)
    {
        var state = new ProposedChangeState(row.ProposalId, row.Tenant, row.BaselineDigest, Deserialize(row.EditsJson));
        var check = row.CheckedDigest is null || row.CheckReceiptId is null
            ? null
            : new ProposedChangeCheck(row.ProposalId, row.CheckedDigest, row.CheckReceiptId);
        return new(state, check, row.SavedVersionCount, row.StartedBy);
    }

    // The digest is taken over the stored bytes, never read from the stored column: a row whose document
    // was tampered with therefore offers a different identity rather than the one it was released under.
    private static ReleasedPackage Rebuild(ConfigurationReleasedPackageRow row) =>
        new(row.ProposalId, row.BaselineDigest, row.SavedVersionDigest, row.PackageKey, row.Revision,
            row.Document, Convert.ToHexStringLower(SHA256.HashData(row.Document)));

    private static string Serialize(IReadOnlyList<ProposedDefinitionEdit> edits) =>
        JsonSerializer.Serialize(edits, Json);

    private static ProposedDefinitionEdit[] Deserialize(string json) =>
        JsonSerializer.Deserialize<ProposedDefinitionEdit[]>(json, Json) ?? [];
}

/// <summary>What the api's signature over a Released package covers.</summary>
/// <param name="Digest">The SHA-256 of the exported document bytes.</param>
/// <param name="PackageKey">The released package key.</param>
/// <param name="Revision">The released package revision.</param>
/// <param name="SavedVersionDigest">The exact Saved version the package carries.</param>
/// <param name="BaselineDigest">The baseline generation it was proposed against.</param>
/// <param name="TenantKey">The tenant the release belongs to.</param>
/// <param name="Principal">The server-derived principal that released it.</param>
public sealed record ReleasedPackageSubject(string Digest, string PackageKey, string Revision,
    string SavedVersionDigest, string BaselineDigest, string TenantKey, string Principal);

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.Foundation.Definitions;

/// <summary>
/// What one definition write touches, as a definition write's authorize stage compares it with the
/// <see cref="DefinitionWriteAdmission"/> it carries (ck-10 S3b).
/// </summary>
/// <param name="Tenant">The tenant of the written definition.</param>
/// <param name="RecordKind">The authorization record kind of the definition (<c>forms</c>, <c>scheduling</c>).</param>
/// <param name="AuthorOperation">The operation that confers authoring this kind of definition.</param>
/// <param name="DefinitionId">The definition id, unescaped.</param>
public sealed record DefinitionWriteTarget(
    TenantId Tenant,
    string RecordKind,
    string AuthorOperation,
    string DefinitionId)
{
    /// <summary>The pack source the caller's definition declares, when it declares one.</summary>
    public PackProjectionSource? DeclaredPackSource { get; init; }

    /// <summary>Whether a declared pack source must also name the admission's pack version.</summary>
    public bool ExactPackVersion { get; init; } = true;

    /// <summary>When set, a pack write's definition must carry the activation instant as its created instant.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>When set, a pack write's definition must carry the activation instant as its updated instant.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// The one authority a definition-lifecycle write carries (ck-10 S3b). <see cref="Decided"/> wraps a live
/// gate decision. <see cref="Replayed"/> carries a stored pack admission's provenance on a cold-start replay,
/// where no live decision exists; only the pack installer can mint one, through
/// <see cref="PackProjectionAuthority"/>'s replay path. Either way the write's authorize stage compares the
/// admission with the definition it writes: a non-null admission is never enough on its own.
/// </summary>
public abstract class DefinitionWriteAdmission
{
    private DefinitionWriteAdmission(PackProjectionAuthority? pack) => Pack = pack;

    /// <summary>The pack authority this admission was taken from, when the write is a pack projection.</summary>
    internal PackProjectionAuthority? Pack { get; }

    /// <summary>Wraps a live gate decision for the definition the write touches.</summary>
    public static DefinitionWriteAdmission Decide(AuthorizationDecision decision) => new Decided(decision, pack: null);

    internal static DefinitionWriteAdmission ForPack(AuthorizationDecision decision, PackProjectionAuthority pack) =>
        new Decided(decision, pack ?? throw new ArgumentNullException(nameof(pack)));

    /// <summary>Mints a replayed admission. Internal: reachable only from the pack installer's replay path.</summary>
    internal static Replayed Replay(
        TenantId tenant,
        ActorId principal,
        string operation,
        string packId,
        string packVersion,
        string targetKind,
        string targetId,
        DateTimeOffset instant,
        PackProjectionAuthority? pack) =>
        new(tenant, principal, operation, packId, packVersion, targetKind, targetId, instant, pack);

    /// <summary>Refuses this admission unless it confers the write on <paramref name="target"/>.</summary>
    public abstract void Authorize(DefinitionWriteTarget target);

    /// <summary>A live gate decision.</summary>
    public sealed class Decided : DefinitionWriteAdmission
    {
        internal Decided(AuthorizationDecision decision, PackProjectionAuthority? pack)
            : base(pack) => Decision = decision ?? throw new ArgumentNullException(nameof(decision));

        /// <summary>The decision the gate made for this act.</summary>
        public AuthorizationDecision Decision { get; }

        /// <inheritdoc />
        public override void Authorize(DefinitionWriteTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (Pack is null)
            {
                try
                {
                    Decision.RequireAllowedReaction(
                        AuthorizationOperation.Parse(target.AuthorOperation),
                        target.Tenant,
                        target.RecordKind,
                        Uri.EscapeDataString(target.DefinitionId));
                }
                catch (ArgumentException)
                {
                    // The facade refused a decision for another definition with the canonical denial.
                    throw new AuthorizationDeniedException(Decision);
                }
                return;
            }

            Pack.EnsureUsable();
            RequirePackTarget(target, Pack.Tenant, Pack.PackId, Pack.PackVersion, Pack.ActivationInstant);
            Decision.RequireAllowedReaction(
                AuthorizationOperation.Parse(Permission.PackagesOperate), target.Tenant, "pack", Pack.PackId);
        }
    }

    /// <summary>A stored pack admission replayed without a live decision.</summary>
    public sealed class Replayed : DefinitionWriteAdmission
    {
        internal Replayed(
            TenantId tenant,
            ActorId principal,
            string operation,
            string packId,
            string packVersion,
            string targetKind,
            string targetId,
            DateTimeOffset instant,
            PackProjectionAuthority? pack)
            : base(pack)
        {
            Tenant = tenant;
            Principal = principal;
            Operation = operation;
            PackId = packId;
            PackVersion = packVersion;
            TargetKind = targetKind;
            TargetId = targetId;
            Instant = instant;
        }

        public TenantId Tenant { get; }
        public ActorId Principal { get; }
        public string Operation { get; }
        public string PackId { get; }
        public string PackVersion { get; }
        public string TargetKind { get; }
        public string TargetId { get; }
        public DateTimeOffset Instant { get; }

        /// <inheritdoc />
        public override void Authorize(DefinitionWriteTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);
            Pack?.EnsureUsable();
            if (!string.Equals(Operation, Permission.PackagesOperate, StringComparison.Ordinal))
                throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.OperationMismatch);
            if (!string.Equals(TargetKind, "pack", StringComparison.Ordinal)
                || !string.Equals(TargetId, PackId, StringComparison.Ordinal))
                throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.TargetMismatch);
            if (string.IsNullOrWhiteSpace(Principal.Value))
                throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.PrincipalMismatch);
            RequirePackTarget(target, Tenant, PackId, PackVersion, Instant);
        }
    }

    private static void RequirePackTarget(
        DefinitionWriteTarget target, TenantId tenant, string packId, string packVersion, DateTimeOffset instant)
    {
        if (target.Tenant != tenant)
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.TenantMismatch);
        if (target.DeclaredPackSource is { } source
            && (!string.Equals(source.PackId, packId, StringComparison.Ordinal)
                || (target.ExactPackVersion && !string.Equals(source.PackVersion, packVersion, StringComparison.Ordinal))))
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.SourceMismatch);
        if (target.CreatedAt is { } created && created != instant)
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.WriteInstantMismatch);
        if (target.UpdatedAt is { } updated && updated != instant)
            throw new PackProjectionAuthorityException(PackProjectionAuthorityCodes.WriteInstantMismatch);
    }
}

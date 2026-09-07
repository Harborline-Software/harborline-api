using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// One membership-lensed tenant that remains usable after current authority validation.
/// The label is intentionally limited to the opaque tenant id until a tenant-owned label reader
/// is available; no roster, grant, Party, or business data crosses the installation boundary.
/// </summary>
internal sealed record InstallationTenantCandidate(
    TenantId TenantId,
    string DisplayLabel,
    TenantMembershipStatus MembershipStatus);

/// <summary>
/// Installation-level discovery for the tenants currently usable by one authenticated account.
/// Completed coordinator receipts bound discovery; tenant-owned authority decides usability.
/// </summary>
internal interface IInstallationTenantCandidateLocator
{
    Task<IReadOnlyList<InstallationTenantCandidate>> ListForAccountAsync(
        PrincipalUserId accountPrincipal,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InstallationTenantCandidate>> ListForAccountAsync(
        PrincipalUserId accountPrincipal,
        string? excludedCorrelationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The 0/1/N classification result. A null selection is the zero-membership case; non-empty cases
/// reuse <see cref="TenantSelection.ForSingle"/> and <see cref="TenantSelection.ForMultiple"/>.
/// </summary>
internal sealed record InstallationTenantMembershipClassification(
    IReadOnlyList<InstallationTenantCandidate> Candidates,
    TenantSelection? Selection)
{
    internal static InstallationTenantMembershipClassification From(
        IReadOnlyList<InstallationTenantCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var snapshot = candidates.ToArray();
        var selection = snapshot.Length == 0
            ? null
            : TenantSelection.Of(snapshot.Select(candidate => candidate.TenantId));
        return new InstallationTenantMembershipClassification(snapshot, selection);
    }
}

/// <summary>
/// Reads only completed installation receipts, then asks the existing membership coordinator to
/// revalidate account status, admission fences, membership status, Party, trust, grant, owner
/// version, and authorization epoch against each named tenant partition.
/// </summary>
internal sealed class InstallationTenantCandidateLocator(
    IDbContextFactory<NodeLocalInstallationIdentityDbContext> homeFactory,
    InstallationIdentityCoordinatorService membershipAuthority)
    : IInstallationTenantCandidateLocator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<NodeLocalInstallationIdentityDbContext> _homeFactory =
        homeFactory ?? throw new ArgumentNullException(nameof(homeFactory));
    private readonly InstallationIdentityCoordinatorService _membershipAuthority =
        membershipAuthority ?? throw new ArgumentNullException(nameof(membershipAuthority));

    public async Task<IReadOnlyList<InstallationTenantCandidate>> ListForAccountAsync(
        PrincipalUserId accountPrincipal,
        CancellationToken cancellationToken = default)
        => await ListForAccountAsync(
                accountPrincipal,
                excludedCorrelationId: null,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<InstallationTenantCandidate>> ListForAccountAsync(
        PrincipalUserId accountPrincipal,
        string? excludedCorrelationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountPrincipal.Value))
        {
            throw new ArgumentException("An account principal is required.", nameof(accountPrincipal));
        }

        string[] receiptPayloads;
        await using (var context = await _homeFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            receiptPayloads = await context.Coordinators
                .AsNoTracking()
                .Where(row =>
                    row.AccountId == accountPrincipal.Value &&
                    row.State == InstallationIdentityCoordinatorState.Completed)
                .OrderBy(row => row.CorrelationId)
                .Select(row => row.TenantIdsJson)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        // A corrupt completed receipt fails the entire account enumeration. Silently skipping it
        // would hide authority-evidence damage without the audit record required for recovery.
        var tenantIds = receiptPayloads
            .SelectMany(DeserializeTenantIds)
            .Select(CanonicalTenantId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static tenantId => tenantId, StringComparer.Ordinal)
            .ToArray();

        var candidates = new List<InstallationTenantCandidate>(tenantIds.Length);
        foreach (var tenantId in tenantIds)
        {
            TenantMembershipSnapshot? membership;
            try
            {
                membership = await _membershipAuthority.ResolveUsableMembershipAsync(
                        accountPrincipal.Value,
                        tenantId,
                        excludedCorrelationId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Canonical admission refusals are deliberately indistinguishable from a stale
                // candidate. The locator is discovery evidence, never authority or an oracle.
                continue;
            }

            if (membership is null || membership.Status != TenantMembershipStatus.Active)
            {
                continue;
            }

            var id = new TenantId(tenantId);
            candidates.Add(new InstallationTenantCandidate(
                id,
                id.Value,
                membership.Status));
        }

        return candidates;
    }

    private static IReadOnlyList<string> DeserializeTenantIds(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(payload, JsonOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "identity.tenant_candidate_receipt_invalid: completed receipt tenant ids are unreadable.",
                exception);
        }
    }

    private static string CanonicalTenantId(string tenantId)
    {
        if (!Guid.TryParse(tenantId, out var parsed))
        {
            throw new InvalidOperationException(
                "identity.tenant_candidate_receipt_invalid: completed receipt contains an invalid tenant id.");
        }

        return parsed.ToString("D");
    }
}

/// <summary>
/// Registers the candidate locator consumed by the live web tenant-selection authority. Additional
/// production consumers must be admitted through the identity architecture fence.
/// </summary>
internal static class InstallationTenantCandidateServiceCollectionExtensions
{
    internal static IServiceCollection AddInstallationTenantCandidateClassification(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IInstallationTenantCandidateLocator, InstallationTenantCandidateLocator>();
        return services;
    }
}

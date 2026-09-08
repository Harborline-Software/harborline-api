using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>
/// The two-act capability minted only after an invitation acceptance has created its installation account.
/// It can authorize the account's initial grant and its membership admission; it has no generic decision API.
/// </summary>
internal sealed class InvitationBootstrapAuthorization
{
    internal const string InitialGrantRecordKind = "invitation-initial-grant";
    internal const string MembershipRecordKind = "invitation-membership";
    internal const string Evidence = "invitation-possession-bootstrap-exception";
    internal const string ActAlreadyAuthorizedCode = "invitation.bootstrap.act_already_authorized";

    private readonly string _accountId;
    private readonly AuthorizationWriteContext _authority;
    private int _initialGrantIssuanceAuthorized;
    private int _membershipAdmissionAuthorized;

    internal InvitationBootstrapAuthorization(
        string accountId,
        TenantId tenant,
        DateTimeOffset acceptanceInstant,
        string invitationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(invitationId);

        _accountId = accountId;
        _authority = new AuthorizationWriteContext(
            new ActorId(CanonicalPrincipalValue(tenant, invitationId)),
            tenant,
            acceptanceInstant);
    }

    internal AuthorizationWriteContext Authority => _authority;

    internal AuthorizationDecision AuthorizeInitialGrantIssuance()
    {
        RequireFirstAuthorization(ref _initialGrantIssuanceAuthorized);
        return AuthorizationDecision.CreateBootstrap(
            _authority.Request(
                AuthorizationOperation.Parse(TeamRolePermissions.MembersManage),
                InitialGrantRecordKind,
                _accountId),
            Evidence);
    }

    internal AuthorizationDecision AuthorizeMembershipAdmission()
    {
        RequireFirstAuthorization(ref _membershipAdmissionAuthorized);
        return AuthorizationDecision.CreateBootstrap(
            _authority.Request(
                AuthorizationOperation.Parse(TeamRolePermissions.MembersManage),
                MembershipRecordKind,
                _accountId),
            Evidence);
    }

    private static void RequireFirstAuthorization(ref int authorizationFlag)
    {
        if (Interlocked.Exchange(ref authorizationFlag, 1) != 0)
            throw new InvalidOperationException(ActAlreadyAuthorizedCode);
    }

    private static string CanonicalPrincipalValue(TenantId tenant, string invitationId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var value in new[] { "web-tenant-principal/v1", tenant.Value, invitationId })
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

/// <summary>Specific receiving-writer checks for the two invitation ceremony decisions.</summary>
internal static class InvitationBootstrapDecisionValidation
{
    internal static void RequireInitialGrantIssuance(
        AuthorizationDecision decision,
        string mintedAccountId,
        AuthorizationWriteContext authority) =>
        Require(
            decision,
            mintedAccountId,
            authority,
            InvitationBootstrapAuthorization.InitialGrantRecordKind);

    internal static void RequireMembershipAdmission(
        AuthorizationDecision decision,
        string mintedAccountId,
        AuthorizationWriteContext authority) =>
        Require(
            decision,
            mintedAccountId,
            authority,
            InvitationBootstrapAuthorization.MembershipRecordKind);

    private static void Require(
        AuthorizationDecision decision,
        string mintedAccountId,
        AuthorizationWriteContext authority,
        string recordKind)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(mintedAccountId);
        decision.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.MembersManage),
            authority.Tenant,
            recordKind,
            mintedAccountId);
        if (decision.Request.Principal != authority.Principal ||
            decision.Request.At != authority.At ||
            !decision.Resolution.Any(step =>
                step.Stage == AuthorizationResolutionStage.Bootstrap &&
                step.Outputs.Contains(
                    $"bootstrap:{InvitationBootstrapAuthorization.Evidence}",
                    StringComparer.Ordinal)))
        {
            throw new ArgumentException(
                "The invitation bootstrap decision does not match the minted account principal or exception evidence.",
                nameof(decision));
        }
    }
}

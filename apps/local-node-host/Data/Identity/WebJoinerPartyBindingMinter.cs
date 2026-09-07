using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.People.Foundation.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data.People;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Mints (or resolves) the joiner's canonical Party → principal binding at accept time (D2).</summary>
internal interface IWebJoinerPartyBindingMinter
{
    /// <summary>Resolves the joiner's existing canonical Party binding or mints a fresh one.</summary>
    Task<CanonicalPartyReference> MintAsync(
        TenantId tenant,
        PrincipalUserId joinerPrincipal,
        PartyId actor,
        string displayName,
        DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Mints the web-plane joiner's canonical Party → principal binding at invitation-acceptance time
/// (MTW-2 #2614 decision D2, admiral-ruling-2026-07-22T2310Z). It creates a live People
/// <see cref="Party"/> and attaches the <c>principal-user</c> role edge whose opaque record id is the
/// joiner's canonical tenant principal, so <see cref="ICanonicalPrincipalPartyReader"/> — and the
/// grant-anchored membership admission — resolve the binding. The joiner enters the business
/// directory at acceptance (a consequence the ruling accepted; designating a Party in the invitation
/// was rejected because it would reopen #2613). The #3107 atlas bridge later verifies THIS exact
/// binding before signing the roster admission.
/// </summary>
/// <remarks>
/// <b>Idempotent on the principal binding.</b> If a live binding for the joiner principal already
/// resolves (a retry or the #3013 recovery path), the existing Party is returned and nothing new is
/// minted — the single-use invitation-consume gate upstream already serializes acceptance, so this
/// never races a second live binding for the same principal.
/// </remarks>
internal sealed class WebJoinerPartyBindingMinter : IWebJoinerPartyBindingMinter
{
    private readonly IPartyWriteService _partyWriteService;
    private readonly ICanonicalPrincipalPartyReader _partyReader;

    public WebJoinerPartyBindingMinter(
        IPartyWriteService partyWriteService,
        ICanonicalPrincipalPartyReader partyReader)
    {
        _partyWriteService = partyWriteService ?? throw new ArgumentNullException(nameof(partyWriteService));
        _partyReader = partyReader ?? throw new ArgumentNullException(nameof(partyReader));
    }

    /// <summary>
    /// Resolves the joiner's existing canonical Party binding or mints a fresh one. The
    /// <paramref name="actor"/> is the canonical principal of the account minted by the anonymous
    /// invitation-possession ceremony. Returns the canonical Party reference the acceptance saga records
    /// in the <c>AdmissionCompleted</c> contract.
    /// </summary>
    public async Task<CanonicalPartyReference> MintAsync(
        TenantId tenant,
        PrincipalUserId joinerPrincipal,
        PartyId actor,
        string displayName,
        DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default)
    {
        var existing = await _partyReader.ResolveAsync(tenant, joinerPrincipal, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.PartyId;
        }

        var party = await _partyWriteService
            .CreateAsync(tenant, PartyKind.Person, displayName, actor, admittedAt, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _partyWriteService
            .AttachRoleAsync(
                party.Id,
                NodeEfPartyRepository.PrincipalUserBindingRoleName,
                joinerPrincipal.Value,
                actor,
                admittedAt,
                cancellationToken)
            .ConfigureAwait(false);
        return new CanonicalPartyReference(party.Id.Value);
    }
}

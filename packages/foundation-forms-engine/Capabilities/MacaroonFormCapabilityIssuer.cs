using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Macaroons;

namespace Harborline.Api.Foundation.Forms.Engine.Capabilities;

/// <summary>
/// Reference <see cref="IFormCapabilityIssuer"/> over the foundation
/// <see cref="IMacaroonIssuer"/>. Writes the forms caveat vocabulary read by
/// <see cref="MacaroonFormCapabilityVerifier"/> — mirroring the public-listings
/// capability-promoter idiom.
/// </summary>
public sealed class MacaroonFormCapabilityIssuer : IFormCapabilityIssuer
{
    /// <summary>Default macaroon location (root-key namespace) for form capabilities.</summary>
    public const string DefaultLocation = "sunfish/forms";

    private readonly IMacaroonIssuer _issuer;
    private readonly string _location;

    /// <summary>Creates the issuer over the given macaroon issuer and root-key location.</summary>
    public MacaroonFormCapabilityIssuer(IMacaroonIssuer issuer, string location = DefaultLocation)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentException.ThrowIfNullOrEmpty(location);
        _issuer = issuer;
        _location = location;
    }

    /// <inheritdoc />
    public async Task<string> IssueAsync(
        TenantId tenant,
        ActorId subject,
        IReadOnlyList<string> roles,
        IReadOnlyList<FormCapabilityAction> actions,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(actions);
        if (tenant == default)
        {
            throw new ArgumentException("Tenant must be a concrete tenant, not the default.", nameof(tenant));
        }

        var caveats = new List<Caveat>
        {
            new($"{FormCapabilityCaveatNames.Tenant} = {tenant.Value}"),
            new($"{FormCapabilityCaveatNames.Subject} = {subject.Value}"),
            new($"{FormCapabilityCaveatNames.Expires} = {expiresAt.ToString("O", CultureInfo.InvariantCulture)}"),
        };

        foreach (var role in roles)
        {
            caveats.Add(new($"{FormCapabilityCaveatNames.Role} = {role}"));
        }

        foreach (var action in actions)
        {
            caveats.Add(new($"{FormCapabilityCaveatNames.Action} = {ActionToString(action)}"));
        }

        var identifier = Guid.NewGuid().ToString("N");
        var macaroon = await _issuer.MintAsync(_location, identifier, caveats, ct).ConfigureAwait(false);
        return MacaroonCodec.EncodeBase64Url(macaroon);
    }

    private static string ActionToString(FormCapabilityAction action) => action switch
    {
        FormCapabilityAction.Read => "read",
        FormCapabilityAction.Write => "write",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown form capability action."),
    };
}

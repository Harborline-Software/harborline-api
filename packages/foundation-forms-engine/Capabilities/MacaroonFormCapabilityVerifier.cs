using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Macaroons;

namespace Harborline.Api.Foundation.Forms.Engine.Capabilities;

/// <summary>
/// Reference <see cref="IFormCapabilityVerifier"/>. Pairs with
/// <see cref="MacaroonFormCapabilityIssuer"/>: the issuer writes
/// forms-specific caveats (<c>tenant = …</c>, <c>subject = …</c>,
/// <c>role = …</c>, <c>action = …</c>, <c>expires = …</c>) that fall outside
/// the foundation <c>FirstPartyCaveatParser</c> grammar, so this verifier
/// performs the signature-chain check inline (via
/// <see cref="MacaroonCodec.ComputeChain"/> + <see cref="IRootKeyStore"/>) and
/// parses every caveat block-locally — mirroring the established
/// public-listings prospect-capability verifier idiom.
/// </summary>
public sealed class MacaroonFormCapabilityVerifier : IFormCapabilityVerifier
{
    private readonly IRootKeyStore _keys;

    /// <summary>Creates the verifier over the given root-key store.</summary>
    public MacaroonFormCapabilityVerifier(IRootKeyStore keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
    }

    /// <inheritdoc />
    public async Task<CapabilityToken> VerifyAsync(string tokenBase64Url, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenBase64Url);

        Macaroon macaroon;
        try
        {
            macaroon = MacaroonCodec.DecodeBase64Url(tokenBase64Url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CapabilityDeniedException(Truncate(tokenBase64Url), $"decode failed: {ex.Message}");
        }

        var rootKey = await _keys.GetRootKeyAsync(macaroon.Location, ct).ConfigureAwait(false);
        if (rootKey is null)
        {
            throw new CapabilityDeniedException(macaroon.Identifier, $"no-root-key for location '{macaroon.Location}'");
        }

        var expected = MacaroonCodec.ComputeChain(rootKey, macaroon.Identifier, macaroon.Caveats);
        if (macaroon.Signature is null
            || macaroon.Signature.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(expected, macaroon.Signature))
        {
            throw new CapabilityDeniedException(macaroon.Identifier, "signature-mismatch");
        }

        var parsed = ParseCaveats(macaroon.Caveats, macaroon.Identifier);

        if (now > parsed.ExpiresAt)
        {
            throw new CapabilityDeniedException(macaroon.Identifier,
                $"expired: caveat={parsed.ExpiresAt:O}, now={now:O}");
        }

        return new CapabilityToken(parsed.Tenant, parsed.Subject, parsed.Roles, parsed.Actions, parsed.ExpiresAt);
    }

    private static string Truncate(string token)
        => token.Length <= 8 ? token : token[..8] + "...";

    private static ParsedCaveats ParseCaveats(IReadOnlyList<Caveat> caveats, string identifier)
    {
        TenantId? tenant = null;
        ActorId? subject = null;
        DateTimeOffset? expiresAt = null;
        var roles = new List<string>();
        var actions = new List<FormCapabilityAction>();

        foreach (var c in caveats)
        {
            var (key, value) = SplitKeyValue(c.Predicate);
            if (key is null)
            {
                throw new CapabilityDeniedException(identifier, $"malformed-caveat: '{c.Predicate}'");
            }

            switch (key)
            {
                case FormCapabilityCaveatNames.Tenant:
                    tenant = new TenantId(value);
                    break;

                case FormCapabilityCaveatNames.Subject:
                    subject = new ActorId(value);
                    break;

                case FormCapabilityCaveatNames.Role:
                    roles.Add(value);
                    break;

                case FormCapabilityCaveatNames.Action:
                    actions.Add(ParseAction(value, identifier));
                    break;

                case FormCapabilityCaveatNames.Expires:
                    if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedExpiry))
                    {
                        throw new CapabilityDeniedException(identifier, $"expires not iso8601: '{value}'");
                    }
                    expiresAt = parsedExpiry;
                    break;

                default:
                    throw new CapabilityDeniedException(identifier, $"unknown-caveat-key: '{key}'");
            }
        }

        if (tenant is null) throw new CapabilityDeniedException(identifier, "missing-caveat: tenant");
        if (subject is null) throw new CapabilityDeniedException(identifier, "missing-caveat: subject");
        if (expiresAt is null) throw new CapabilityDeniedException(identifier, "missing-caveat: expires");

        return new ParsedCaveats(tenant.Value, subject.Value, roles, actions, expiresAt.Value);
    }

    private static FormCapabilityAction ParseAction(string value, string identifier) => value switch
    {
        "read" => FormCapabilityAction.Read,
        "write" => FormCapabilityAction.Write,
        _ => throw new CapabilityDeniedException(identifier, $"unknown-action: '{value}'"),
    };

    private static (string? Key, string Value) SplitKeyValue(string predicate)
    {
        var eq = predicate.IndexOf('=', StringComparison.Ordinal);
        if (eq < 0)
        {
            return (null, string.Empty);
        }
        var key = predicate[..eq].Trim();
        var value = predicate[(eq + 1)..].Trim();
        return (key, value);
    }

    private sealed record ParsedCaveats(
        TenantId Tenant,
        ActorId Subject,
        IReadOnlyList<string> Roles,
        IReadOnlyList<FormCapabilityAction> Actions,
        DateTimeOffset ExpiresAt);
}

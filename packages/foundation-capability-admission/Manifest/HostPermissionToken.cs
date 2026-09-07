using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Foundation.CapabilityAdmission;

/// <summary>
/// Canonical token grammar for the <c>HostPermissions</c> inventory family (ADR 0169 D4).
/// A token is the permission identifier alone when the permission is unscoped, or
/// <c>&lt;identifier&gt;@&lt;scope-digest&gt;</c> when scoped. The scope digest is PINNED as the
/// first 16 lowercase hex characters (64 bits) of the SHA-256 of the canonical JSON encoding of
/// the permission's scope object — object keys sorted lexicographically (byte order), no
/// insignificant whitespace, UTF-8, no escaping beyond what JSON requires — the same
/// canonicalization release-manifest signing already applies (<see cref="CanonicalJson"/>).
/// Without the scope in the token, differently-scoped grants of the same permission would attest
/// byte-identically.
/// </summary>
public static class HostPermissionToken
{
    private const int ScopeDigestLength = 16;

    /// <summary>Formats the token for an unscoped permission: the identifier alone.</summary>
    public static string Format(string permissionIdentifier)
    {
        RequireIdentifier(permissionIdentifier);
        return permissionIdentifier;
    }

    /// <summary>
    /// Formats the token for a scoped permission: identifier, <c>@</c>, then the first 16
    /// lowercase hex characters of SHA-256 over the scope object's canonical JSON.
    /// </summary>
    public static string Format(string permissionIdentifier, JsonNode scope)
    {
        RequireIdentifier(permissionIdentifier);
        ArgumentNullException.ThrowIfNull(scope);
        return $"{permissionIdentifier}@{ScopeDigest(scope)}";
    }

    /// <summary>Computes the pinned 16-lowercase-hex scope digest for a scope object.</summary>
    public static string ScopeDigest(JsonNode scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var canonical = CanonicalJson.Serialize(scope);
        var digest = SHA256.HashData(canonical);
        return Convert.ToHexString(digest.AsSpan(0, ScopeDigestLength / 2)).ToLowerInvariant();
    }

    /// <summary>
    /// True when the token matches the pinned grammar: a non-empty identifier without <c>@</c>,
    /// optionally followed by one <c>@</c> and exactly 16 lowercase hex characters.
    /// </summary>
    public static bool IsWellFormed(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var at = token.IndexOf('@', StringComparison.Ordinal);
        if (at < 0)
        {
            return true;
        }
        if (at == 0)
        {
            return false;
        }

        var suffix = token.AsSpan(at + 1);
        if (suffix.Length != ScopeDigestLength || suffix.Contains('@'))
        {
            return false;
        }
        foreach (var c in suffix)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }
        return true;
    }

    private static void RequireIdentifier(string permissionIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionIdentifier);
        if (permissionIdentifier.Contains('@', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A host permission identifier must not contain '@'; the '@' separator is " +
                "reserved for the scope digest.",
                nameof(permissionIdentifier));
        }
    }
}

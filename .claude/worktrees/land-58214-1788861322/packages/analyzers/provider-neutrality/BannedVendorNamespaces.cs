using System;
using System.Collections.Immutable;
using System.IO;

namespace Harborline.Api.Analyzers.ProviderNeutrality;

/// <summary>
/// Loads the declared vendor SDK namespace prefixes that are forbidden outside
/// provider packages (per ADR 0013 provider-neutrality).
/// </summary>
internal static class BannedVendorNamespaces
{
    private const string DeclarationResourceName =
        "Harborline.Api.Analyzers.ProviderNeutrality.BannedVendorNamespaces.txt";

    /// <summary>
    /// Prefixes are matched case-sensitively and require an exact, namespace,
    /// or generic-type boundary, so <c>Stripe</c> matches <c>Stripe.PaymentIntents</c>
    /// and a declared generic type matches its closed forms, but not a longer identifier.
    /// </summary>
    public static readonly ImmutableArray<string> Prefixes = LoadPrefixes();

    /// <summary>
    /// Returns the matched prefix if <paramref name="namespaceName"/> equals or
    /// starts with one of the registry's prefixes followed by a '.', otherwise null.
    /// </summary>
    public static string? Match(string namespaceName)
    {
        if (string.IsNullOrEmpty(namespaceName))
        {
            return null;
        }

        foreach (var prefix in Prefixes)
        {
            if (IsPrefixMatch(namespaceName, prefix))
            {
                return prefix;
            }
        }

        return null;
    }

    private static bool IsPrefixMatch(string candidate, string prefix)
    {
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (candidate.Length == prefix.Length)
        {
            return true;
        }

        return candidate[prefix.Length] is '.' or '<';
    }

    private static ImmutableArray<string> LoadPrefixes()
    {
        using var stream = typeof(BannedVendorNamespaces).Assembly
            .GetManifestResourceStream(DeclarationResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Provider-neutrality declaration '{DeclarationResourceName}' is missing.");
        }

        using var reader = new StreamReader(stream);
        var prefixes = ImmutableArray.CreateBuilder<string>();
        while (reader.ReadLine() is { } line)
        {
            var value = line.Trim();
            if (value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            prefixes.Add(value);
        }

        return prefixes.ToImmutable();
    }
}

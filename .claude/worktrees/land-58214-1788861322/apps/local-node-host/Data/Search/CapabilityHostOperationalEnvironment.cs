using System;
using System.Collections.Generic;
using System.Linq;

namespace Harborline.Api.LocalNodeHost.Data.Search;

/// <summary>Ticket 245 clean-break guard for capability-host subprocess configuration.</summary>
internal static class CapabilityHostOperationalEnvironment
{
    private const string LegacyPrefix = "HULL_";
    private const string ReplacementPrefix = "CAPABILITY_HOST_";

    /// <summary>Refuse stale operator configuration before starting a capability-host child.</summary>
    public static void RefuseLegacyVariables(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var legacyName = environment.Keys
            .Where(name => name.StartsWith(LegacyPrefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (legacyName is null)
        {
            return;
        }

        var replacement = ReplacementPrefix + legacyName[LegacyPrefix.Length..];
        throw new InvalidOperationException(
            $"Legacy capability-host environment variable {legacyName} is not supported; use {replacement}.");
    }
}

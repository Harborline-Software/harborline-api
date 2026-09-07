using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;

namespace Harborline.Api.Blocks.Workflow.Durable;

// ─────────────────────────────────────────────────────────────────────────────
//  WF-KEY — the CAPABILITY→AUTHORITY registry (ADR 0128 / ADR 0134 P1a, F2), as the
//  .NET workflow-admission gate sees it. This is the closure for red-team Chains 1+5 /
//  security-engineering F2: the admission validator must DERIVE an action's CP/AP class
//  from a canonical authority source instead of trusting the author-declared label.
//
//  SINGLE SOURCE OF TRUTH — no A4 drift. This reads the SAME `command-authority.json`
//  the harborline-sdk TS `authorityOf` and the `.mjs` invoke bridge read (embedded from
//  `packages/harborline-sdk/src/command-authority.json` at build; see the .csproj
//  <EmbeddedResource> link). All three faces resolve one file, so a CP/AP disagreement
//  across runtimes is impossible by construction.
//
//  FAIL-CLOSED — an UNKNOWN / unregistered capability resolves to CP (mirrors the TS
//  `authorityOf` unknown⇒CP default), so a capability that is not in the registry can
//  never be laundered to AP through an authored label. A malformed authority value in
//  the JSON throws at load (fail-closed-loudly, mirrors the TS F-3a assertion) — a
//  malformed registry is a build/config error, not a silent allow.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Resolves a workflow action's <see cref="ActionClassification"/> from the canonical
/// capability→authority registry (ADR 0128). Unknown ⇒ CP (fail-closed).
/// </summary>
public interface ICapabilityAuthorityRegistry
{
    /// <summary>
    /// The DERIVED authority class of a capability. Returns <see cref="ActionClassification.CP"/> for
    /// any capability not present in the registry (fail-closed — an unknown capability is never AP).
    /// Never returns <see cref="ActionClassification.Unspecified"/>.
    /// </summary>
    ActionClassification AuthorityOf(string capabilityRef);
}

/// <summary>
/// The default <see cref="ICapabilityAuthorityRegistry"/> — reads the canonical
/// <c>command-authority.json</c> embedded from harborline-sdk (the single cross-runtime source).
/// Immutable + thread-safe after construction (a frozen dictionary).
/// </summary>
public sealed class CapabilityAuthorityRegistry : ICapabilityAuthorityRegistry
{
    /// <summary>The embedded resource logical name (set by the .csproj <c>&lt;LogicalName&gt;</c>).</summary>
    internal const string ResourceName = "command-authority.json";

    private readonly FrozenDictionary<string, ActionClassification> _byCapability;

    private CapabilityAuthorityRegistry(FrozenDictionary<string, ActionClassification> byCapability)
        => _byCapability = byCapability;

    /// <summary>
    /// The canonical registry loaded from the embedded <c>command-authority.json</c>. Constructed once,
    /// lazily, and shared — the parameterless <see cref="WorkflowAdmissionValidator"/> uses this.
    /// </summary>
    public static CapabilityAuthorityRegistry Canonical { get; } = FromEmbeddedResource();

    /// <inheritdoc />
    public ActionClassification AuthorityOf(string capabilityRef)
    {
        ArgumentNullException.ThrowIfNull(capabilityRef);
        // Fail-closed: an unregistered capability is CP (mirrors TS `authorityOf` unknown⇒CP).
        return _byCapability.TryGetValue(capabilityRef, out var c) ? c : ActionClassification.CP;
    }

    /// <summary>
    /// Builds a registry from an explicit capability→class map (test seam — lets a unit test pin a
    /// small registry without the embedded file). Fail-closed default is preserved by
    /// <see cref="AuthorityOf"/>.
    /// </summary>
    public static CapabilityAuthorityRegistry FromMap(IReadOnlyDictionary<string, ActionClassification> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return new CapabilityAuthorityRegistry(map.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>Loads the canonical registry from the embedded <c>command-authority.json</c>.</summary>
    private static CapabilityAuthorityRegistry FromEmbeddedResource()
    {
        var asm = typeof(CapabilityAuthorityRegistry).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found in {asm.GetName().Name}. " +
                "The canonical command-authority.json must be embedded (see the .csproj <EmbeddedResource> link).");
        using var doc = JsonDocument.Parse(stream);
        return new CapabilityAuthorityRegistry(Parse(doc).ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>
    /// Parses the canonical <c>{ "commands": { "&lt;cap&gt;": { "authority": "AP"|"CP", … } } }</c> shape.
    /// A missing/invalid <c>authority</c> value throws (fail-closed-loudly — mirrors the TS F-3a assertion).
    /// </summary>
    internal static Dictionary<string, ActionClassification> Parse(JsonDocument doc)
    {
        var map = new Dictionary<string, ActionClassification>(StringComparer.Ordinal);
        if (!doc.RootElement.TryGetProperty("commands", out var commands)
            || commands.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "command-authority.json: expected a top-level 'commands' object (fail-closed-loudly).");
        }

        foreach (var command in commands.EnumerateObject())
        {
            if (!command.Value.TryGetProperty("authority", out var authorityEl)
                || authorityEl.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    $"command-authority.json: command '{command.Name}' has no string 'authority' (fail-closed-loudly).");
            }

            map[command.Name] = authorityEl.GetString() switch
            {
                "CP" => ActionClassification.CP,
                "AP" => ActionClassification.AP,
                var other => throw new InvalidOperationException(
                    $"command-authority.json: command '{command.Name}' has invalid authority '{other}' — " +
                    "must be exactly \"AP\" or \"CP\" (fail-closed-loudly)."),
            };
        }

        return map;
    }
}

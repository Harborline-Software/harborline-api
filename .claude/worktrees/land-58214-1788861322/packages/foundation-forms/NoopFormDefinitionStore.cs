using System.Runtime.CompilerServices;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms;

/// <summary>
/// Read-only empty placeholder implementation of
/// <see cref="IFormDefinitionStore"/> (ADR 0055 §"Schema Registry" line 417
/// relocation artifact, FN-4). Lookups return <see langword="null"/> /
/// <see cref="FormDefinitionNotFoundException"/>; enumeration yields no
/// rows; shared lifecycle mutations report the same not-found result as an empty real store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Hosts that have not yet wired a real
/// <see cref="IFormDefinitionStore"/> (in-memory or Postgres) still need
/// surfaces that <i>read</i> the registry — most notably the Ship's Office
/// browser (ADR 0083) which lists <c>DynamicTemplate</c> rows — to compose
/// without throwing. This Noop satisfies that read-side composition while
/// fail-loud rejecting any attempted mutation; the composition root replaces
/// it with a real store the moment forms authoring is wired.
/// </para>
/// <para>
/// <b>Mutator posture.</b> Domain-specific authoring operations remain unsupported. Shared lifecycle
/// operations address a revision, so this empty adapter reports the ordinary opaque not-found failure;
/// no member of the common lifecycle has adapter-specific unsupported behavior.
/// </para>
/// <para>
/// <b>DI registration.</b> Registered via
/// <see cref="DependencyInjection.FormsServiceCollectionExtensions.TryAddNoopFormDefinitionStore"/>
/// as a <c>TryAddSingleton</c> default — host composition overrides with
/// <see cref="DependencyInjection.FormsServiceCollectionExtensions.AddInMemoryFormDefinitionStore"/>
/// (or a Postgres-backed registration when that adapter ships) at the
/// composition root.
/// </para>
/// </remarks>
public sealed class NoopFormDefinitionStore : IFormDefinitionStore
{
    /// <inheritdoc />
    /// <exception cref="FormDefinitionNotFoundException">Always — the Noop
    /// store contains no revisions, so every <c>Get</c> is a miss.</exception>
    public ValueTask<FormDefinition> GetAsync(DefinitionCoordinates coordinates, CancellationToken ct = default)
        => throw NotFound(coordinates);

    /// <inheritdoc />
    /// <remarks>Always returns <see langword="null"/> — the Noop store has
    /// no Published revision for any definition.</remarks>
    public ValueTask<FormDefinition?> GetCurrentPublishedAsync(DefinitionAddress address, CancellationToken ct = default)
        => new((FormDefinition?)null);

    /// <inheritdoc />
    /// <remarks>Always empty — the Noop store has no definitions.</remarks>
#pragma warning disable CS1998 // intentional: contract is async-enumerable; an empty sequence with no await is the canonical Noop shape
    public async IAsyncEnumerable<FormDefinition> ListByTenantAsync(TenantId tenant, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        yield break;
    }
#pragma warning restore CS1998

    /// <inheritdoc />
#pragma warning disable CS1998
    public async IAsyncEnumerable<FormDefinition> ListPublishedAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        yield break;
    }
#pragma warning restore CS1998

    private const string ReadOnlyMessage =
        "NoopFormDefinitionStore is a read-only placeholder; register a real IFormDefinitionStore " +
        "(AddInMemoryFormDefinitionStore for in-process / single-tenant scenarios, or a Postgres-backed " +
        "store via the foundation-assets-postgres extension) at the composition root to enable authoring.";

    private static FormDefinitionNotFoundException NotFound(DefinitionCoordinates coordinates)
        => new(
            new FormDefinitionId(coordinates.Address.Identity.Value),
            new SemanticVersion(coordinates.Version.Major, coordinates.Version.Minor, coordinates.Version.Patch),
            coordinates.Address.Tenant);
}

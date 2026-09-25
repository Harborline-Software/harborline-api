using Harborline.Contracts.Fields;
using Harborline.Foundation.FieldRuntime;

using PlatformTenantId = Harborline.Foundation.Assets.Common.TenantId;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// T-664: the host's only route to an editor for a value-domain field. The released field runtime
/// (<see cref="ValueDomainRuntime"/>) decides from the domain's source and readable cardinality
/// (DES-0030 decision 3); the host carries that decision and never maps an authored hint itself.
/// </summary>
internal static class FieldEditorChoice
{
    private static readonly PlatformTenantId LiteralTenant = new("literal-domain");
    private static readonly LiteralDomain Literals = new();

    /// <summary>Resolves a literal value set through the field runtime.</summary>
    public static ValueTask<ResolvedValueDomain> ResolveAsync(
        IReadOnlyList<string> values, TimeProvider clock, string jsonPointer, CancellationToken cancellationToken)
        => new ValueDomainRuntime(Literals, Literals, clock).ResolveAsync(
            new ValueDomainDefinition(LiteralValues: values),
            new FieldDomainScope(LiteralTenant, "literal-domain"),
            jsonPointer,
            cancellationToken);

    /// <summary>
    /// Puts the runtime's editor and permitted values on every value-domain field of the render wire,
    /// the way the platform's own Forms mapper does, so an authored hint cannot overrule it.
    /// </summary>
    public static async ValueTask<FormViewDto> ApplyAsync(FormViewDto view, TimeProvider clock, CancellationToken cancellationToken)
    {
        var chosen = new Dictionary<string, ResolvedValueDomain>(StringComparer.Ordinal);
        foreach (var field in view.Sections.SelectMany(section => section.Fields.Concat(Nested(section.Items))))
        {
            if (field.Options is { Count: > 0 } options && !chosen.ContainsKey(field.Name))
                chosen[field.Name] = await ResolveAsync(options, clock, $"/{field.Name}", cancellationToken).ConfigureAwait(false);
        }

        FormViewFieldDto Field(FormViewFieldDto field) => chosen.TryGetValue(field.Name, out var domain)
            ? field with { ControlHint = domain.Editor.ToString(), PermittedValues = domain.Values }
            : field;
        FormViewItemDto Item(FormViewItemDto item) => item with
        {
            Field = item.Field is null ? null : Field(item.Field),
            Items = item.Items?.Select(Item).ToList(),
        };
        return view with
        {
            Sections = view.Sections.Select(section => section with
            {
                Fields = section.Fields.Select(Field).ToList(),
                Items = section.Items?.Select(Item).ToList(),
            }).ToList(),
        };
    }

    private static IEnumerable<FormViewFieldDto> Nested(IEnumerable<FormViewItemDto>? items) =>
        items?.SelectMany(item => (item.Field is null ? [] : new[] { item.Field }).Concat(Nested(item.Items))) ?? [];

    /// <summary>An in-memory literal set: complete, tenant-agnostic, and readable in full.</summary>
    private sealed class LiteralDomain : IFieldDomainSource, IFieldDomainSnapshot, IFieldDomainReadAuthority
    {
        public PlatformTenantId Tenant => LiteralTenant;
        public string Revision => "literal-domain";
        public bool IsComplete => true;

        public ValueTask<IFieldDomainSnapshot> OpenSnapshotAsync(
            PlatformTenantId tenant,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IFieldDomainSnapshot>(this);
        }

        public IReadOnlyList<FieldDomainMember>? GetTaxonomyScheme(TaxonomySchemeReference scheme) => null;
        public IReadOnlyList<FieldDomainMember>? GetRecords(string recordTypeId) => null;

        public ValueTask<bool> CanReadAsync(
            FieldDomainScope scope,
            ValueDomainDefinition domain,
            FieldDomainMember member,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        }
    }
}

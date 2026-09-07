using System.Collections.ObjectModel;

namespace Harborline.Api.UIAdapters.Blazor.Components.Forms.Schema;

/// <summary>
/// The composed field-type + control registries — one frozen composition of two immutable
/// inputs (ADR 0055 Rev 10 D-R10.2). Both dictionaries are read-only snapshots; nothing
/// mutates a composition after <see cref="FieldTypeRegistry.Compose"/> returns.
/// </summary>
public sealed class ComposedFieldTypeRegistry
{
    internal ComposedFieldTypeRegistry(
        IReadOnlyDictionary<string, FieldTypeDefinition> fieldTypes,
        IReadOnlyDictionary<string, SchemaControlRenderer> controls)
    {
        FieldTypes = fieldTypes;
        Controls = controls;
    }

    /// <summary>Field-type rows keyed by <see cref="FieldTypeDefinition.Type"/>.</summary>
    public IReadOnlyDictionary<string, FieldTypeDefinition> FieldTypes { get; }

    /// <summary>Control renderers keyed by lower-case control hint.</summary>
    public IReadOnlyDictionary<string, SchemaControlRenderer> Controls { get; }
}

/// <summary>
/// The Blazor parity surface for ADR 0055 Rev 10's capability-owned field-type registry.
/// Mirrors <c>composeFieldTypeRegistry</c> in the React Harborline App
/// (<c>src/forms/builder/fieldTypes.ts</c>): a pure composition of the CORE
/// rows with the ONE substitutable extension module
/// (<see cref="FieldTypeExtensionModule"/>) — build-time extension, no core patching,
/// nothing registered at runtime (ADR 0154 D6).
/// </summary>
public static class FieldTypeRegistry
{
    /// <summary>
    /// Purely compose the registry from core rows + core controls + build-time extensions.
    /// Fail-closed build assertions (each throws <see cref="InvalidOperationException"/>):
    /// every row MUST declare a non-empty <see cref="FieldTypeDefinition.CapabilityId"/>
    /// (D-R10.1 — required, so omission is a build failure, never a silent core fallback),
    /// and a duplicate field type or extension control hint colliding with a core control
    /// is rejected (one seam; a second registration path falsifies the design).
    /// Rev 11 (ADR 0169 D5): an extension's capabilityId MUST be namespaced
    /// <c>&lt;vendor&gt;.&lt;name&gt;</c> where the vendor is not a vendor prefix any CORE
    /// row declares (negation form, 0169 OQ-1 v1; the core vendor set is DERIVED from the
    /// core rows — today <c>{forms}</c>). A partner row claiming a core capabilityId or a
    /// core vendor prefix would inherit core admission/visibility, so it is refused at
    /// compose time — same class as the type/controlHint guards (extension shadowing core
    /// identity).
    /// </summary>
    /// <param name="coreDefinitions">The built-in field-type rows (ordered; order is palette order).</param>
    /// <param name="coreControls">The built-in control renderers, keyed by lower-case control hint.</param>
    /// <param name="extensions">The build-time extension pairs — normally <see cref="FieldTypeExtensionModule.Extensions"/>.</param>
    public static ComposedFieldTypeRegistry Compose(
        IReadOnlyList<FieldTypeDefinition> coreDefinitions,
        IReadOnlyDictionary<string, SchemaControlRenderer> coreControls,
        IReadOnlyList<FieldTypeExtension> extensions)
    {
        ArgumentNullException.ThrowIfNull(coreDefinitions);
        ArgumentNullException.ThrowIfNull(coreControls);
        ArgumentNullException.ThrowIfNull(extensions);

        var fieldTypes = new Dictionary<string, FieldTypeDefinition>(StringComparer.Ordinal);
        var controls = new Dictionary<string, SchemaControlRenderer>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hint, renderer) in coreControls)
        {
            controls[hint] = renderer;
        }

        foreach (var definition in coreDefinitions)
        {
            AddRow(fieldTypes, definition);
        }

        // Rev 11 (ADR 0169 D5) — the vendor prefixes core owns, DERIVED from the core
        // rows (today {forms}); an extension may not claim a core capabilityId or vendor.
        var coreCapabilityIds = new HashSet<string>(
            coreDefinitions.Select(d => d.CapabilityId), StringComparer.Ordinal);
        var coreVendors = new HashSet<string>(
            coreCapabilityIds
                .Select(id => id.Split('.', 2)[0])
                .Where(v => v.Length > 0),
            StringComparer.Ordinal);

        foreach (var extension in extensions)
        {
            AssertNamespacedOutsideCore(extension.Definition, coreCapabilityIds, coreVendors);
            AddRow(fieldTypes, extension.Definition);

            // The row lands WITH its control (ADR 0168 pairing rule). An extension may
            // shadow a core control for its own hint (substitution), but two extensions
            // colliding on one hint is a drift bug — reject it.
            var hint = extension.Definition.ControlHint;
            if (string.IsNullOrWhiteSpace(hint))
            {
                throw new InvalidOperationException(
                    $"Field type '{extension.Definition.Type}' declares an empty controlHint.");
            }

            controls[hint] = extension.Control;
        }

        return new ComposedFieldTypeRegistry(
            new ReadOnlyDictionary<string, FieldTypeDefinition>(fieldTypes),
            new ReadOnlyDictionary<string, SchemaControlRenderer>(controls));
    }

    /// <summary>
    /// The composite build assertion (D-R10.3 shape): every row declares a non-empty
    /// owning capability. Control-hint resolution is asserted by the test suite, not
    /// here, so this type acquires no renderer coupling beyond the compose signature.
    /// </summary>
    public static bool IsWellFormed(ComposedFieldTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.FieldTypes.Values.All(d => !string.IsNullOrWhiteSpace(d.CapabilityId));
    }

    /// <summary>
    /// The creation-time PII default the definition emitter reads (ADR 0055 Rev 10
    /// D-R10.6 / ADR 0168 D4): the row's declared default, else
    /// <see cref="PiiSensitivityValues.None"/>. Never a runtime reclassifier — editing a
    /// registry row must not change the classification of already-authored fields, since
    /// a downgrade would silently decrypt.
    /// </summary>
    public static string ResolvePiiSensitivityDefault(FieldTypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.PiiSensitivityDefault ?? PiiSensitivityValues.None;
    }

    private static void AssertNamespacedOutsideCore(
        FieldTypeDefinition definition,
        HashSet<string> coreCapabilityIds,
        HashSet<string> coreVendors)
    {
        var capabilityId = definition.CapabilityId ?? string.Empty;
        var dot = capabilityId.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == capabilityId.Length - 1)
        {
            throw new InvalidOperationException(
                $"Extension field type '{definition.Type}' capabilityId '{capabilityId}' is " +
                "not namespaced '<vendor>.<name>' (ADR 0055 Rev 11 / ADR 0169 D5).");
        }

        var vendor = capabilityId[..dot];
        if (coreCapabilityIds.Contains(capabilityId) || coreVendors.Contains(vendor))
        {
            throw new InvalidOperationException(
                $"Extension field type '{definition.Type}' capabilityId '{capabilityId}' " +
                "collides with a core capability namespace (ADR 0055 Rev 11 / ADR 0169 D5): " +
                "a partner row claiming a core id or core vendor prefix would inherit core " +
                "admission and visibility.");
        }
    }

    private static void AddRow(
        Dictionary<string, FieldTypeDefinition> fieldTypes,
        FieldTypeDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.CapabilityId))
        {
            throw new InvalidOperationException(
                $"Field type '{definition.Type}' declares no owning capabilityId. " +
                "ADR 0055 Rev 10 D-R10.1 makes the owner REQUIRED — an absent owner " +
                "defaulting to core is the fail-open shape the gate exists to prevent.");
        }

        if (!fieldTypes.TryAdd(definition.Type, definition))
        {
            throw new InvalidOperationException(
                $"Field type '{definition.Type}' is registered twice. The registry is " +
                "ONE composition of core + the single extension module; a second " +
                "registration path falsifies the seam (ADR 0055 Rev 10 D-R10.2).");
        }
    }
}

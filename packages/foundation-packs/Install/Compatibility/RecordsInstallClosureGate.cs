using System.Text;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Blocks.BuilderDefinitions;

namespace Harborline.Api.Foundation.Packs.Install.Compatibility;

/// <summary>
/// Connects a verified pack to the Platform Records install gate (DES-0015 records-ck-41,
/// <see cref="RecordTypeDefinitionStore.AdmitInstallAsync"/>). Every Record Type the pack ships is admitted at the
/// Install stage against a <see cref="RecordsInstallClosure"/> read only from the pack's verified seed layer, never
/// from the node's live catalogue, and under the installing host's tenant.
/// </summary>
/// <remarks>
/// <para>
/// The closure's <see cref="RecordsInstallClosure.PackDefinitions"/> are the Record Types the verified pack itself
/// ships, each identified by its body's <c>record_type_id</c>. A release keys an item by its definition key (for
/// example <c>records/invoice</c>), which is not the Records identity.
/// </para>
/// <para>
/// The closure's <see cref="RecordsInstallClosure.Exposures"/> are always empty. A <see cref="PinnedExposure"/> needs
/// the producer's definition version and the <c>sha256:</c> digest of its stored body, and no signed transport
/// carries either: a release item is pinned at its package revision, and its body is re-canonicalized on the way.
/// Every cross-package pin therefore refuses <c>records.reference.closure_missing</c> until the producer signs those
/// facts. Neither may be taken from the pin, the live catalogue or a re-canonicalized body (records-ck-40).
/// </para>
/// <para>
/// Records travels as <see cref="PackContentKind.AssetTypeDefinition"/>, the same kind as legacy asset types, and no
/// signed member tells the two apart. An item of that kind whose body is a JSON object with a <c>record_type_id</c>
/// member claims the Records grammar, which requires that member, and is gated. So is a body of that kind that carries a
/// field reference (<c>fields[*].reference</c>) without a <c>record_type_id</c> (G-DISC, owner decision P10, fail
/// closed): it is admitted under the empty id, so the Platform gate refuses it instead of it installing ungated. A
/// legacy asset type has neither member and passes through unchanged.
/// </para>
/// <para>
/// A Class has no transport content kind, so no Class is ever a pack definition and an unpinned Class target refuses
/// (ledger holds[0], T-724 ruling 51).
/// </para>
/// <para>
/// Each admitted entry carries the seed item's own version only because <see cref="RecordTypeDefinitionPackageEntry"/>
/// requires one. On a Released install that is the package revision. The Platform gate reads it only to build the
/// structural-admission document; it never reaches <see cref="ReadClosure"/>, which is the sole source of what a pin is
/// compared against. The owner ruled on 2026-10-12 (P10, decision packet ruling 4) that "never take a definition
/// version from a package revision" is scoped to the closure's Exposures, and accepted this inert own-definition entry
/// version; T-INERT pins that it reaches neither the closure nor the verdict. No Platform overload without a version is
/// required.
/// </para>
/// <para>
/// No host composes this gate yet (G-HOST): <see cref="PackInstaller.RecordsAdmission"/> is null unless a host sets it.
/// </para>
/// </remarks>
public static class RecordsInstallClosureGate
{
    /// <summary>
    /// The Platform Records install gate as a host binds it, for example a host's
    /// <see cref="RecordTypeDefinitionStore.AdmitInstallAsync"/>.
    /// </summary>
    /// <param name="tenant">The installing host's tenant.</param>
    /// <param name="entry">One Record Type the pack ships.</param>
    /// <param name="closure">What the pack and its pinned closure declare.</param>
    /// <param name="cancellationToken">Cancels the admission.</param>
    public delegate ValueTask<DefinitionRefusalReport> AdmitInstall(
        string tenant, RecordTypeDefinitionPackageEntry entry, RecordsInstallClosure closure,
        CancellationToken cancellationToken);

    /// <summary>Reads the closure a pack's verified seed layer declares. See the type remarks for what it leaves empty.</summary>
    /// <param name="items">The verified seed layer, in pack content order.</param>
    public static RecordsInstallClosure ReadClosure(IReadOnlyList<PackSeedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var shipped = items
            .Where(item => item.Kind == PackContentKind.AssetTypeDefinition)
            .Select(RecordTypeId)
            .OfType<string>()
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(id => new InstallDefinitionTarget(DefinitionKind.Records, id))
            .ToArray();
        return new RecordsInstallClosure(shipped, []);
    }

    /// <summary>
    /// The seed items the gate admits, in pack content order: every <see cref="PackContentKind.AssetTypeDefinition"/>
    /// item whose body claims the Records grammar by carrying a <c>record_type_id</c> member or a field reference
    /// (G-DISC).
    /// </summary>
    /// <param name="items">The verified seed layer, in pack content order.</param>
    public static IReadOnlyList<PackSeedItem> GatedItems(IReadOnlyList<PackSeedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Where(item => GatedId(item) is not null).ToArray();
    }

    /// <summary>
    /// Admits every Record Type in <paramref name="items"/> through <paramref name="admit"/> and returns their refusals
    /// together, in pack content order, each keyed by its content key with the gate's own pointer. A code the API
    /// publishes in <see cref="PackRecordsInstallCodes"/> is kept; any other Platform code refuses under
    /// <see cref="PackRecordsInstallCodes.Unpublished"/>, with the Platform code in the message. Writes nothing, so a
    /// caller that refuses on any of them leaves no effect.
    /// </summary>
    /// <param name="tenant">The installing host's tenant; a body's own envelope tenant is not consulted.</param>
    /// <param name="items">The verified seed layer, in pack content order.</param>
    /// <param name="admit">The bound Platform gate.</param>
    /// <param name="cancellationToken">Cancels the admission.</param>
    public static async ValueTask<PackAdmissionResult> AdmitAsync(TenantId tenant, IReadOnlyList<PackSeedItem> items,
        AdmitInstall admit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(admit);
        var closure = ReadClosure(items);
        var refusals = new List<PackAdmissionRefusal>();
        foreach (var item in items)
        {
            if (GatedId(item) is not { } id)
                continue;
            var entry = new RecordTypeDefinitionPackageEntry(id, item.Version,
                PlatformPackageContent.PresentJson(Encoding.UTF8.GetBytes(item.CanonicalJson)));
            var report = await admit(tenant.Value, entry, closure, cancellationToken).ConfigureAwait(false);
            var subject = id.Length == 0 ? "The Records-shaped body '" + item.Key + "'" : "Record Type '" + id + "'";
            refusals.AddRange(report.Refusals.Select(refusal => new PackAdmissionRefusal(item.Key,
                PackRecordsInstallCodes.PublishedCode(refusal.Code),
                $"{subject} was refused at the {report.Stage} stage of the Records install gate ({refusal.Code}).")
            {
                Pointer = refusal.Pointer,
            }));
        }
        return refusals.Count == 0 ? PackAdmissionResult.Admissible : new PackAdmissionResult(refusals);
    }

    // The id an item is gated under, or null when it is not gated. G-DISC (owner decision P10, fail closed): an item of the
    // Records transport kind is gated when its body carries record_type_id, or, without one, a field reference
    // (fields[*].reference); the latter is gated under "" so the Platform gate refuses it rather than it passing ungated.
    private static string? GatedId(PackSeedItem item)
    {
        if (item.Kind != PackContentKind.AssetTypeDefinition)
            return null;
        return RecordTypeId(item) ?? (HasFieldReference(item) ? "" : null);
    }

    // Whether the body is an object whose fields array holds an object with a reference member, whatever its value.
    private static bool HasFieldReference(PackSeedItem item)
    {
        try
        {
            using var body = JsonDocument.Parse(item.CanonicalJson);
            return body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array
                && fields.EnumerateArray().Any(field => field.ValueKind == JsonValueKind.Object && field.TryGetProperty("reference", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The body's record_type_id: the member's string value, "" when it is present but not a string (the gate then
    // refuses the document), or null when the body is not an object carrying that member.
    private static string? RecordTypeId(PackSeedItem item)
    {
        try
        {
            using var body = JsonDocument.Parse(item.CanonicalJson);
            if (body.RootElement.ValueKind != JsonValueKind.Object
                || !body.RootElement.TryGetProperty("record_type_id", out var id))
                return null;
            return id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The Records install-gate refusal codes the API publishes on a pack install (ticket 394: every pack refusal code is
/// declared by a <c>*Codes</c> catalogue). Each value is the Platform gate's own code at the pinned contract
/// (DES-0015 records-ck-41; <see cref="RecordTypeDefinitionStore.AdmitInstallAsync"/>): the Install-phase structural
/// codes and the edge codes. A Platform code not listed here is never passed through unpublished: it refuses under
/// <see cref="Unpublished"/>, fail-closed, so a new Platform code is a deliberate publication, not a silent one.
/// </summary>
public static class PackRecordsInstallCodes
{
    /// <summary>The Records body does not parse as a Record Type, or a required member is missing.</summary>
    public const string DocumentInvalid = "records.document_invalid";

    /// <summary>The Records body carries no envelope.</summary>
    public const string EnvelopeRequired = "records.envelope_required";

    /// <summary>The envelope declares no definition contract version.</summary>
    public const string ContractMissing = "definition.contract.missing";

    /// <summary>The envelope's definition contract version is outside the host's window.</summary>
    public const string ContractOutOfWindow = "definition.contract.out_of_window";

    /// <summary>The envelope's section is not a valid section slug.</summary>
    public const string SectionInvalid = "records.identity.section_invalid";

    /// <summary>The Record Type has no name.</summary>
    public const string NameRequired = "records.identity.name_required";

    /// <summary>The body's <c>record_type_id</c> is not the id it is admitted under.</summary>
    public const string RecordTypeIdImmutable = "records.identity.record_type_id_immutable";

    /// <summary>The <c>record_type_id</c> is not scoped by the envelope's section.</summary>
    public const string SectionMismatch = "records.identity.section_mismatch";

    /// <summary>The envelope names no owning package.</summary>
    public const string PackageRequired = "records.package.required";

    /// <summary>An envelope requirement is not spelled <c>pack-key@interfaceVersion</c>.</summary>
    public const string RequirementInvalid = "records.package.requirement_invalid";

    /// <summary>The envelope's exposure declares a non-positive interface version.</summary>
    public const string ExposureInvalid = "records.package.exposure_invalid";

    /// <summary>The Record Type names no Class home.</summary>
    public const string ClassRequired = "records.class.required";

    /// <summary>The Record Type declares no record class.</summary>
    public const string RecordClassRequired = "records.record_class.required";

    /// <summary>An unpinned reference's target is neither shipped by the pack nor the type itself.</summary>
    public const string TargetUnresolved = "records.reference.target_unresolved";

    /// <summary>A pin names the installing definition's own package.</summary>
    public const string PinUnexpected = "records.reference.pin_unexpected";

    /// <summary>A pin names a different definition than the reference targets.</summary>
    public const string PinStale = "records.reference.pin_stale";

    /// <summary>A pinned package is not in the definition's <c>requires</c>.</summary>
    public const string DependencyUndeclared = "records.reference.dependency_undeclared";

    /// <summary>The pin's interface version is not the one the definition requires.</summary>
    public const string InterfaceIncompatible = "records.reference.interface_incompatible";

    /// <summary>The pinned dependency closure declares no such exposure.</summary>
    public const string ClosureMissing = "records.reference.closure_missing";

    /// <summary>The pinned dependency closure declares the exposure at another version, digest or interface.</summary>
    public const string ClosureChanged = "records.reference.closure_changed";

    /// <summary>
    /// Fail-closed fallback: the Records gate refused with a code the API does not publish (for example a validator or
    /// schema-compile code). The refusal stands; its message names the Platform code.
    /// </summary>
    public const string Unpublished = "pack.install.refused.records_unpublished_refusal";

    /// <summary>
    /// Preview and Check: a Records gate is composed and the pack ships Record Types, but the synchronous dry run does
    /// not run the asynchronous gate, so it cannot promise an install. Install evaluates the gate itself.
    /// </summary>
    public const string GateNotEvaluated = "pack.install.refused.records_gate_not_evaluated";

    /// <summary>Returns <paramref name="platformCode"/> when the API publishes it, otherwise <see cref="Unpublished"/>.</summary>
    /// <param name="platformCode">A code the Platform Records install gate reported.</param>
    public static string PublishedCode(string? platformCode)
    {
        return platformCode switch
        {
            DocumentInvalid => PackRecordsInstallCodes.DocumentInvalid,
            EnvelopeRequired => PackRecordsInstallCodes.EnvelopeRequired,
            ContractMissing => PackRecordsInstallCodes.ContractMissing,
            ContractOutOfWindow => PackRecordsInstallCodes.ContractOutOfWindow,
            SectionInvalid => PackRecordsInstallCodes.SectionInvalid,
            NameRequired => PackRecordsInstallCodes.NameRequired,
            RecordTypeIdImmutable => PackRecordsInstallCodes.RecordTypeIdImmutable,
            SectionMismatch => PackRecordsInstallCodes.SectionMismatch,
            PackageRequired => PackRecordsInstallCodes.PackageRequired,
            RequirementInvalid => PackRecordsInstallCodes.RequirementInvalid,
            ExposureInvalid => PackRecordsInstallCodes.ExposureInvalid,
            ClassRequired => PackRecordsInstallCodes.ClassRequired,
            RecordClassRequired => PackRecordsInstallCodes.RecordClassRequired,
            TargetUnresolved => PackRecordsInstallCodes.TargetUnresolved,
            PinUnexpected => PackRecordsInstallCodes.PinUnexpected,
            PinStale => PackRecordsInstallCodes.PinStale,
            DependencyUndeclared => PackRecordsInstallCodes.DependencyUndeclared,
            InterfaceIncompatible => PackRecordsInstallCodes.InterfaceIncompatible,
            ClosureMissing => PackRecordsInstallCodes.ClosureMissing,
            ClosureChanged => PackRecordsInstallCodes.ClosureChanged,
            _ => PackRecordsInstallCodes.Unpublished,
        };
    }
}

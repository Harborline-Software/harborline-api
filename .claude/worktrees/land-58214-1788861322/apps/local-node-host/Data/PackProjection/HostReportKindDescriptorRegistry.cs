using System.Linq;
using System.Text;
using System.Text.Json;

using Harborline.Api.Blocks.Reports;
using Harborline.Api.Foundation.ReportDefinitions;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>
/// Adapts the host's registered report cartridges to report-definition descriptor admission.
/// This slice admits a registered report kind and an object-shaped parameter payload only; typed
/// per-cartridge parameter binding and validation by deserializing <see cref="ReportDefinition.Parameters"/>
/// into each cartridge's <c>TParams</c> is the recorded deepening follow-up.
/// </summary>
public sealed class HostReportKindDescriptorRegistry : IReportDefinitionDescriptorRegistry
{
    private const string KindUnknown = "report_definition.kind_unknown";
    private const string ParametersNotObject = "report_definition.parameters_not_object";

    private readonly ReportCartridgeRegistry _cartridges;

    /// <summary>Initializes descriptor admission over the host's singleton cartridge registry.</summary>
    /// <param name="cartridges">The host's registered report cartridges.</param>
    public HostReportKindDescriptorRegistry(ReportCartridgeRegistry cartridges)
    {
        _cartridges = cartridges ?? throw new ArgumentNullException(nameof(cartridges));
    }

    /// <inheritdoc />
    public ValueTask AdmitAsync(
        ReportDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        var kindIsRegistered = _cartridges.RegisteredKinds.Any(
            kind => StringComparer.Ordinal.Equals(ToKebabCase(kind.ToString()), definition.ReportKind));
        if (!kindIsRegistered)
        {
            throw new ReportDefinitionGovernanceException(KindUnknown);
        }

        if (definition.Parameters.ValueKind != JsonValueKind.Object)
        {
            throw new ReportDefinitionGovernanceException(ParametersNotObject);
        }

        return ValueTask.CompletedTask;
    }

    private static string ToKebabCase(string value)
    {
        var result = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (index > 0
                && char.IsUpper(current)
                && (char.IsLower(value[index - 1])
                    || (index + 1 < value.Length && char.IsLower(value[index + 1]))))
            {
                result.Append('-');
            }

            result.Append(char.ToLowerInvariant(current));
        }

        return result.ToString();
    }
}

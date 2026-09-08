using System.Linq;
using System.Text;
using System.Text.Json;

using Harborline.Api.Blocks.Banking.Feed;
using Harborline.Api.Blocks.Banking.Import;
using Harborline.Api.Foundation.DataExchangeDefinitions;
using Harborline.Api.Foundation.Import.Extraction;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>
/// Adapts the host's registered exchange surfaces to data-exchange-definition descriptor admission.
/// This slice admits a registered exchange kind and an object-shaped settings payload only; typed
/// per-kind settings binding and validation (for example, deserializing a
/// <c>banking.statement-import/csv</c> definition's <see cref="DataExchangeDefinition.Settings"/>
/// into <c>CsvColumnMapping</c> and running its <c>Validate()</c>) is the recorded deepening follow-up.
/// The portability-export routes (<c>/api/local-node/data-exports</c>, ADR 0014) are NEVER admitted as
/// an exchange kind; definitions carry no credentials, cursors, or connection state.
/// </summary>
public sealed class HostDataExchangeKindDescriptorRegistry : IDataExchangeDefinitionDescriptorRegistry
{
    private const string KindUnknown = "data_exchange_definition.kind_unknown";
    private const string SettingsNotObject = "data_exchange_definition.settings_not_object";

    private readonly IReadOnlySet<string> _registeredKinds;

    /// <summary>Initializes descriptor admission over the host's registered exchange surfaces.</summary>
    /// <param name="parsers">The host's registered statement-file parsers.</param>
    /// <param name="feedProvider">The host's registered bank-feed provider.</param>
    public HostDataExchangeKindDescriptorRegistry(
        IEnumerable<IStatementFileParser> parsers,
        IBankFeedProvider feedProvider)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        ArgumentNullException.ThrowIfNull(feedProvider);

        _registeredKinds = parsers
            .Select(parser => "banking.statement-import/" + ToKebabCase(parser.Format))
            .Append("banking.feed/" + ToKebabCase(feedProvider.GetType().Name))
            .Append("import.erpnext/" + ToKebabCase(nameof(SourceAccessMode.MariaDbDump)))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public ValueTask AdmitAsync(
        DataExchangeDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_registeredKinds.Contains(definition.ExchangeKind))
        {
            throw new DataExchangeDefinitionGovernanceException(KindUnknown);
        }

        if (definition.Settings.ValueKind != JsonValueKind.Object)
        {
            throw new DataExchangeDefinitionGovernanceException(SettingsNotObject);
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

using System.Text.Json;

using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.ReportDefinitions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ReportDefinitions;

public sealed class ReportDefinitionEnvelopeTests
{
    [Fact]
    public void Definition_coordinates_and_provenance_are_projected_through_the_envelope()
    {
        using var parameters = JsonDocument.Parse("{\"period\":\"2026-08\"}");
        using var provenance = JsonDocument.Parse("{\"pack\":\"finance-core\"}");
        var definition = new ReportDefinition
        {
            Tenant = "tenant-27",
            Key = "monthly-pl",
            Version = "3.2.1",
            Provenance = provenance.RootElement.Clone(),
            SchemaVersion = 1,
            ReportKind = "profit-and-loss",
            Title = "Monthly P&L",
            Parameters = parameters.RootElement.Clone(),
        };

        Assert.Equal("monthly-pl", definition.Envelope.Identity);
        Assert.Equal("3.2.1", definition.Envelope.Version);
        Assert.Equal("tenant-27", definition.Envelope.Tenant);
        Assert.Equal("finance-core", definition.Envelope.Provenance.GetProperty("pack").GetString());
    }

    [Fact]
    public void Authored_retention_policy_is_refused_by_the_shared_policy_authority()
    {
        using var provenance = JsonDocument.Parse("{}");

        var exception = Assert.Throws<DefinitionPolicyAuthorityBypassException>(() =>
            new DefinitionEnvelope<string, string, string, JsonElement>(
                "monthly-pl",
                "1.0.0",
                "tenant-27",
                CascadeLayer.Tenant,
                provenance.RootElement.Clone(),
                DefinitionRetentionClass.Unspecified,
                Array.Empty<DefinitionRequirement>()));

        Assert.Equal("definition.retention.registry_bypass", exception.ErrorCode);
    }

    [Fact]
    public void Authored_legal_hold_policy_is_refused_by_the_shared_policy_authority()
    {
        using var provenance = JsonDocument.Parse("{}");

        var exception = Assert.Throws<DefinitionPolicyAuthorityBypassException>(() =>
            new DefinitionEnvelope<string, string, string, JsonElement>(
                "monthly-pl",
                "1.0.0",
                "tenant-27",
                CascadeLayer.Tenant,
                provenance.RootElement.Clone(),
                DefinitionLegalHold.Held,
                Array.Empty<DefinitionRequirement>()));

        Assert.Equal("definition.legal_hold.registry_bypass", exception.ErrorCode);
    }
}

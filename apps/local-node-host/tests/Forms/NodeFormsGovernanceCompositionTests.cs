using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Governance.Enforcement;
using Harborline.Api.Foundation.Governance.Resolution;
using Harborline.Api.LocalNodeHost.Data.Forms;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

/// <summary>
/// F-11 composition proof: <see cref="NodeFormsComposition.AddNodeForms"/> makes the SPINE-2
/// governance layer LIVE on the embedded node (classification enforces on field VALUES) and the
/// composition-root fail-closed gate passes — the engine resolves with the enforcer + resolver
/// wired, so it can never silently fall back to the legacy PII-only path.
/// </summary>
public sealed class NodeFormsGovernanceCompositionTests
{
    [Fact]
    public void AddNodeForms_WithoutKernelAuditModule_FailsAtComposition()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddNodeForms());

        Assert.Contains("IAuthorizedAuditTrail", error.Message, StringComparison.Ordinal);
        Assert.Contains("AddHarborlineKernelAudit", error.Message, StringComparison.Ordinal);
        Assert.Contains("AddEnrollmentCompensatingControlAudit", error.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider BuildNodeForms(string? hostJurisdiction = null)
    {
        var services = new ServiceCollection();

        // The field encryptor the recovery coordinator would normally supply (mirrors the
        // production ordering: recovery/crypto substrate BEFORE AddNodeForms). The per-subject
        // crypto + erasure services are supplied by AddNodeForms' fail-closed defaults.
        services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();

        services.AddTestAuthorizationGate().AddTestNodeForms(hostJurisdiction);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddNodeForms_WiresGovernanceSeam_Live()
    {
        using var sp = BuildNodeForms();

        Assert.NotNull(sp.GetService<IFieldPolicyEnforcer>());
        Assert.NotNull(sp.GetService<IAspectResolver>());
    }

    [Fact]
    public void AddNodeForms_ResolvesEngine_GatePasses()
    {
        using var sp = BuildNodeForms();

        // RequireGovernanceEnforcement is set in AddNodeForms; the engine only constructs
        // because the seam resolved — the fail-closed composition-root gate passed.
        var engine = sp.GetService<IFormEngine>();
        Assert.NotNull(engine);
        Assert.IsType<FormEngine>(engine);
    }

    [Theory]
    [InlineData("EU")]
    [InlineData("AE")]
    public void AddNodeForms_RelaysConfiguredHostJurisdiction(string jurisdiction)
    {
        // F2 — the LIVE node must govern a Reside-effect classified field's residency against its
        // REAL deployment region, not a silent hardcoded "US". FormEngineOptions.HostJurisdiction is
        // exactly the TargetJurisdiction the SPINE-2 Store PEP checks residency eligibility against,
        // so relaying the configured jurisdiction into it proves a non-US host is honored.
        using var sp = BuildNodeForms(jurisdiction);

        var options = sp.GetRequiredService<FormEngineOptions>();
        Assert.Equal(jurisdiction, options.HostJurisdiction);
    }

    [Fact]
    public void AddNodeForms_DefaultHostJurisdiction_StaysUs_BackCompat()
    {
        // A no-arg / blank jurisdiction keeps the FormEngineOptions default so pre-existing callers
        // and every non-residency-bearing form stay unchanged.
        using var sp = BuildNodeForms();

        Assert.Equal("US", sp.GetRequiredService<FormEngineOptions>().HostJurisdiction);
    }
}

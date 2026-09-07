using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Foundation.RuleEngine.Compilation;
using Harborline.Api.Foundation.RuleEngine.Graph;

namespace Harborline.Api.Foundation.RuleEngine.DependencyInjection;

/// <summary>Creates a per-definition <see cref="IFormRuleGraph"/> (the graph is stateful per instance).</summary>
public interface IFormRuleGraphFactory
{
    /// <summary>Creates a form rule graph over an already-compiled definition.</summary>
    IFormRuleGraph Create(CompiledGraph compiled);
}

internal sealed class FormRuleGraphFactory : IFormRuleGraphFactory
{
    private readonly RuleEngineLimits _limits;
    private readonly TimeProvider _clock;

    public FormRuleGraphFactory(RuleEngineLimits limits, TimeProvider clock)
    {
        _limits = limits;
        _clock = clock;
    }

    public IFormRuleGraph Create(CompiledGraph compiled) => new FormRuleGraph(compiled, _limits, _clock);
}

/// <summary>DI wiring for the SPINE-1 rule engine (SPINE-1 design §5.2).</summary>
public static class RuleEngineServiceCollectionExtensions
{
    /// <summary>
    /// Registers the public rule-engine seams: <see cref="IGuardEvaluator"/> (workflow
    /// guards) and <see cref="IFormRuleGraphFactory"/> (form graphs). Uses
    /// <see cref="RuleEngineLimits.Default"/> + <see cref="TimeProvider.System"/> unless
    /// overridden upstream.
    /// </summary>
    public static IServiceCollection AddHarborlineRuleEngine(this IServiceCollection services, RuleEngineLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(limits ?? RuleEngineLimits.Default);
        services.TryAddSingleton<IGuardEvaluator>(sp => new GuardEvaluator(
            sp.GetRequiredService<RuleEngineLimits>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IFormRuleGraphFactory>(sp => new FormRuleGraphFactory(
            sp.GetRequiredService<RuleEngineLimits>(), sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}

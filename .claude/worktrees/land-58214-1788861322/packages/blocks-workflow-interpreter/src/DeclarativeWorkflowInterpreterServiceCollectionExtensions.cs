using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.Workflow.Durable;

namespace Harborline.Api.Blocks.Workflow.Interpreter;

/// <summary>
/// DI registration for the ADR 0135 A1 declarative interpreter. Registers
/// <see cref="IDeclarativeWorkflowInterpreter"/> so the <see cref="WorkflowTriggerDispatcher"/> resolves it as
/// the fallback for definitions with no typed handler.
/// </summary>
public static class DeclarativeWorkflowInterpreterServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="DeclarativeWorkflowInterpreter"/> as <see cref="IDeclarativeWorkflowInterpreter"/>.
    /// Requires (register FIRST): the durable engine (<c>AddDurableWorkflowEngine</c> — the broker +
    /// <see cref="IWorkflowEffectCatalog"/>), the definition store (<c>AddEntityStoreWorkflowDefinitionStore</c>
    /// — the <see cref="IWorkflowDefinitionExecutionStore"/> face), and a host
    /// <see cref="IWorkflowConfirmationContext"/> (the server-derived confirmer + engine proposer).
    /// </summary>
    public static IServiceCollection AddDeclarativeWorkflowInterpreter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IDeclarativeWorkflowInterpreter>(sp => new DeclarativeWorkflowInterpreter(
            sp.GetRequiredService<IWorkflowDefinitionExecutionStore>(),
            sp.GetRequiredService<IWorkflowEffectBroker>(),
            sp.GetRequiredService<IWorkflowEffectCatalog>(),
            sp.GetRequiredService<IWorkflowConfirmationContext>()));
        return services;
    }
}

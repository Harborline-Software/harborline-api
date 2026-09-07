using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.LocalNodeHost.Data;

/// <summary>
/// A participant in the local node's shared durable-mutation boundary.
/// </summary>
/// <remarks>
/// <para>
/// The local node persists doctypes through the main <see cref="LocalNodeDbContext"/> and a catalog of
/// node-exclusive EF contexts. Registering an implementation once makes it participate in
/// <c>SaveChanges</c> for every production context in that graph; repositories and route handlers do
/// not need per-store edits.
/// </para>
/// <para>
/// Implementations stage additional rows on <paramref name="context"/> but MUST NOT call
/// <c>SaveChanges</c>, open another context, or commit an independent transaction. The caller's original
/// save remains the one commit boundary, so the primary mutation and every enlisted row commit or roll
/// back together. Implementations are resolved by a singleton interceptor and therefore must be
/// thread-safe; per-mutation state belongs on the supplied context or in an already-established ambient
/// scope.
/// </para>
/// </remarks>
public interface ILocalNodeSaveChangesEnlister
{
    /// <summary>Stages rows for a synchronous save attempt.</summary>
    void Enlist(DbContext context);

    /// <summary>Stages rows for an asynchronous save attempt.</summary>
    ValueTask EnlistAsync(DbContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// The single cross-store EF interception point for local-node durable mutations.
/// </summary>
internal sealed class LocalNodeSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly IReadOnlyList<ILocalNodeSaveChangesEnlister> _enlisters;

    public LocalNodeSaveChangesInterceptor(IEnumerable<ILocalNodeSaveChangesEnlister> enlisters)
    {
        ArgumentNullException.ThrowIfNull(enlisters);
        _enlisters = enlisters.ToArray();
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        var context = eventData.Context;
        if (context is null || !context.ChangeTracker.HasChanges())
        {
            return result;
        }

        foreach (var enlister in _enlisters)
        {
            enlister.Enlist(context);
        }

        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context is null || !context.ChangeTracker.HasChanges())
        {
            return result;
        }

        foreach (var enlister in _enlisters)
        {
            await enlister.EnlistAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}

/// <summary>Registers and attaches the shared local-node save interceptor.</summary>
internal static class LocalNodeSaveChangesEnlistmentRegistration
{
    internal static IServiceCollection AddLocalNodeSaveChangesEnlistment(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<LocalNodeSaveChangesInterceptor>();
        return services;
    }

    internal static DbContextOptionsBuilder AddLocalNodeSaveChangesEnlistment(
        this DbContextOptionsBuilder options,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        return options.AddInterceptors(
            services.GetRequiredService<LocalNodeSaveChangesInterceptor>());
    }
}

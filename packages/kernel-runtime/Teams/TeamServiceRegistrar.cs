using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.Kernel.Runtime.Teams;

/// <summary>
/// Delegate that populates a fresh <see cref="IServiceCollection"/> with the
/// services that should be resolved <em>per team</em>. Invoked once per team
/// at the moment that team's <see cref="TeamContext"/> is first materialized.
/// </summary>
/// <remarks>
/// <para>
/// This is the integration seam between Wave 6.1 (this file — scaffolded shape)
/// and Wave 6.3 (real per-team service wiring: team-scoped <c>IGossipDaemon</c>,
/// <c>ILeaseCoordinator</c>, <c>IEventLog</c>, <c>IEncryptedStore</c>,
/// <c>IQuarantineQueue</c>, <c>IBucketRegistry</c>, plus a per-team <c>IPluginRegistry</c>
/// bound to that team's plugin set).
/// </para>
/// <para>
/// Wave 6.1 ships a no-op default (see
/// <c>TeamContextFactory.DefaultRegistrar</c>) so the factory and the
/// <see cref="IActiveTeamAccessor"/> can be exercised end-to-end without Wave 6.3
/// being in place. Consumers in Wave 6.3 will replace the default with a real
/// registrar via <c>AddHarborlineMultiTeam(registrar)</c>.
/// </para>
/// <para>
/// <b>Container-bridge (ONR survey 2026-06-19, CIC option a + a2).</b> The third
/// parameter — the <b>outer</b> install-level <see cref="IServiceProvider"/> — is
/// the seam that lets the per-team child container bridge to install-level
/// singletons (the synced-doctype delta router that owns the contacts
/// projection). The per-team daemon is composed in the child (ADR 0032 per-team
/// daemon intact), but its <c>IDeltaProducer</c>/<c>IDeltaSink</c> resolve a thin
/// bridge to the outer provider's router — so two machines actually converge
/// contacts. The outer provider is a process-lifetime singleton that outlives
/// every <see cref="TeamContext"/>, so resolving from it here is lifetime-safe.
/// Registrars that do not need the outer provider simply ignore the parameter.
/// </para>
/// </remarks>
/// <param name="services">The fresh <see cref="IServiceCollection"/> to populate.
/// Services registered here will be available through
/// <see cref="TeamContext.Services"/> for that team only.</param>
/// <param name="teamId">The team whose scope is being constructed. Registrars that
/// need per-team paths (SQLCipher DB filename, event-log directory, keystore
/// prefix) branch on this.</param>
/// <param name="outerProvider">The install-level (outer) <see cref="IServiceProvider"/>
/// that owns process-lifetime singletons — the synced-doctype delta router, the
/// <c>LocalNodeDbContext</c> factory, etc. The registrar bridges a per-team
/// service to an outer singleton by registering a child factory that resolves it
/// from this provider. Lifetime-safe: the outer provider outlives every team
/// context. May be an empty provider in tests that don't exercise the bridge.</param>
public delegate void TeamServiceRegistrar(
    IServiceCollection services,
    TeamId teamId,
    IServiceProvider outerProvider);

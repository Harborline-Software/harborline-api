using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.Api.LocalNodeHost.Capabilities;

namespace Harborline.Api.LocalNodeHost.Health;

internal sealed class LocalNodeEndpointMapping : IAsyncDisposable
{
    private readonly IReadOnlyList<IHostedService> _mappers;

    private LocalNodeEndpointMapping(IReadOnlyList<IHostedService> mappers)
    {
        _mappers = mappers;
    }

    internal static async Task<LocalNodeEndpointMapping> MapAsync(
        IServiceProvider services,
        SharedHostedWebApp listener,
        LocalNodeHostedComponentProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(profile);

        var mappers = new List<IHostedService>();
        Add<WebSession.HostedEffectivePermissionsApiEndpoint>(mappers, services, listener);
        if (profile.WebClientEnabled)
        {
            Add<WebSession.HostedWebSessionApiEndpoint>(mappers, services, listener);
            if (profile.LlmProxyEnabled)
            {
                Add<WebSession.HostedLlmProxyApiEndpoint>(mappers, services, listener);
            }
        }
        Add<HostedHealthEndpoint>(mappers, services, listener);
        Add<HostedWebSocketEndpoint>(mappers, services, listener);
        Add<HostedLocalNodeApiEndpoint>(mappers, services, listener);
        Add<HostedSyncStatusApiEndpoint>(mappers, services, listener);
        Add<HostedDataExportApiEndpoint>(mappers, services, listener);
        Add<HostedLifecycleApiEndpoint>(mappers, services, listener);
        Add<HostedTeamsApiEndpoint>(mappers, services, listener);
        Add<HostedMaintenanceApiEndpoint>(mappers, services, listener);
        Add<HostedCurrentPrincipalSignatureApiEndpoint>(mappers, services, listener);
        Add<HostedEntityApiEndpoint>(mappers, services, listener);
        Add<HostedChartOfAccountsApiEndpoint>(mappers, services, listener);
        Add<HostedErpnextImportPreviewApiEndpoint>(mappers, services, listener);
        Add<HostedPackComposerApiEndpoint>(mappers, services, listener);
        Add<HostedPackInstallApiEndpoint>(mappers, services, listener);
        Add<Feed.HostedChannelFeedApiEndpoint>(mappers, services, listener);
        Add<HostedPackComposeApiEndpoint>(mappers, services, listener);
        Add<HostedAccountingPeriodApiEndpoint>(mappers, services, listener);
        Add<HostedPropertyApiEndpoint>(mappers, services, listener);
        Add<HostedLeaseApiEndpoint>(mappers, services, listener);
        Add<HostedPaymentApiEndpoint>(mappers, services, listener);
        Add<HostedJournalEntryApiEndpoint>(mappers, services, listener);
        Add<HostedAuditEventApiEndpoint>(mappers, services, listener);
        Add<HostedConsentRecordApiEndpoint>(mappers, services, listener);
        Add<HostedBillApiEndpoint>(mappers, services, listener);
        Add<HostedInvoiceApiEndpoint>(mappers, services, listener);
        Add<HostedRecurringInvoiceApiEndpoint>(mappers, services, listener);
        Add<HostedApprovalTaskApiEndpoint>(mappers, services, listener);
        Add<HostedContactApiEndpoint>(mappers, services, listener);
        if (profile.SchedulingDogfoodEnabled)
        {
            Add<HostedSchedulingApiEndpoint>(mappers, services, listener);
        }
        Add<HostedCalendarApiEndpoint>(mappers, services, listener);
        Add<HostedCalendarCollectionApiEndpoint>(mappers, services, listener);
        Add<HostedOrgBrandingApiEndpoint>(mappers, services, listener);
        Add<HostedCommsApiEndpoint>(mappers, services, listener);
        Add<HostedDocumentTemplateApiEndpoint>(mappers, services, listener);
        Add<HostedFormsApiEndpoint>(mappers, services, listener);
        Add<HostedFormDraftsApiEndpoint>(mappers, services, listener);
        Add<HostedWorkflowDefinitionApiEndpoint>(mappers, services, listener);
        Add<HostedAssetRegistryApiEndpoint>(mappers, services, listener);
        Add<HostedSpatialFrameApiEndpoint>(mappers, services, listener);
        Add<HostedDeclarativeWorkflowApiEndpoint>(mappers, services, listener);
        Add<HostedKgSearchApiEndpoint>(mappers, services, listener);
        Add<HostedAdmissionApiEndpoint>(mappers, services, listener);
        Add<HostedCompromisedDeviceResponseApiEndpoint>(mappers, services, listener);
        Add<HostedLeaseSubLedgerApiEndpoint>(mappers, services, listener);
        Add<HostedPaymentWriteApiEndpoint>(mappers, services, listener);
        Add<HostedAccountingSummaryApiEndpoint>(mappers, services, listener);
        Add<HostedBankingApiEndpoint>(mappers, services, listener);
        Add<HostedPayrollApiEndpoint>(mappers, services, listener);
        Add<HostedDocumentApiEndpoint>(mappers, services, listener);
        Add<HostedReportsApiEndpoint>(mappers, services, listener);
        Add<HostedReportDefinitionApiEndpoint>(mappers, services, listener);
        Add<HostedViewDefinitionApiEndpoint>(mappers, services, listener);
        Add<HostedDataExchangeDefinitionApiEndpoint>(mappers, services, listener);
        Add<HostedAuthorizationAdminApiEndpoint>(mappers, services, listener);

        foreach (var mapper in mappers)
        {
            await mapper.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        return new LocalNodeEndpointMapping(mappers);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var mapper in _mappers.Reverse())
        {
            await mapper.StopAsync(CancellationToken.None).ConfigureAwait(false);
            if (mapper is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (mapper is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static void Add<TMapper>(
        ICollection<IHostedService> mappers,
        IServiceProvider services,
        SharedHostedWebApp listener)
        where TMapper : class, IHostedService
    {
        mappers.Add(ActivatorUtilities.CreateInstance<TMapper>(services, listener));
    }
}

using Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Registers the compromised-device response route on the shared node listener.</summary>
public sealed class HostedCompromisedDeviceResponseApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly ICompromisedDeviceResponseService _responses;
    private readonly NodeCallerSessionToken _callerAuth;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the hosted compromised-device response route registrar.</summary>
    public HostedCompromisedDeviceResponseApiEndpoint(
        SharedHostedWebApp sharedApp,
        ICompromisedDeviceResponseService responses,
        NodeCallerSessionToken callerAuth,
        TimeProvider timeProvider)
    {
        _sharedApp = sharedApp ?? throw new ArgumentNullException(nameof(sharedApp));
        _responses = responses ?? throw new ArgumentNullException(nameof(responses));
        _callerAuth = callerAuth ?? throw new ArgumentNullException(nameof(callerAuth));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app =>
            CompromisedDeviceResponseRoutes.Map(
                app.MapSelectedSessionProductGroup(),
                _responses,
                _callerAuth,
                _timeProvider));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using Microsoft.Extensions.Hosting;

namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// Refuses startup when the volatile reference outbox is selected outside Development.
/// </summary>
public sealed class InMemoryFormSubmitOutboxGuardAssertion : IHostedService
{
    private readonly IFormSubmitOutbox _outbox;
    private readonly IHostEnvironment _environment;

    /// <summary>Creates the startup assertion over the effective outbox and host environment.</summary>
    public InMemoryFormSubmitOutboxGuardAssertion(IFormSubmitOutbox outbox, IHostEnvironment environment)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_outbox is InMemoryFormSubmitOutbox && !_environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "The in-memory form-submit outbox is permitted only in Development because pending "
                + "submission projections would be lost on restart. Register a durable IFormSubmitOutbox "
                + "before calling AddFormSubmitProjections.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

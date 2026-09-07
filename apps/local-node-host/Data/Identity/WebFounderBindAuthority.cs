using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>The exact disposition of one founder-bind attempt, as the web surface may observe it.</summary>
public enum WebFounderBindStatus
{
    /// <summary>This request wrote the one-time installation-root designation.</summary>
    Bound,

    /// <summary>The same idempotency key and the same evidence already completed; the winner's facts are returned.</summary>
    Replayed,

    /// <summary>The same idempotency key was replayed with DIFFERENT founder evidence. Refused.</summary>
    Conflict,

    /// <summary>A different key cannot replace the existing singleton designation.</summary>
    AlreadyDesignated,

    /// <summary>The designated installation account is missing or not active.</summary>
    AccountNotActive,

    /// <summary>The request carried values the domain command refuses (blank/oversized/non-positive).</summary>
    InvalidRequest,
}

/// <summary>
/// The evidence one founder-bind attempt binds. Every field except <see cref="IdempotencyKey"/> is
/// derived from the live selected-session request principal — the browser cannot supply them.
/// </summary>
public sealed record WebFounderBindRequest(
    string AccountId,
    TenantId TenantId,
    PrincipalUserId PrincipalUserId,
    CanonicalPartyReference PartyId,
    long ExpectedSourceVersion,
    string IdempotencyKey,
    string AuditCorrelationId);

/// <summary>
/// The wire-safe result of a founder-bind attempt. Deliberately carries NO designation id, no
/// idempotency-key digest, and no source-composite digest — those are authority-store secrets and
/// never cross the HTTP boundary.
/// </summary>
public sealed record WebFounderBindOutcome(
    WebFounderBindStatus Status,
    string? AccountId,
    long OwnerVersion,
    DateTimeOffset? DesignatedAtUtc);

/// <summary>Exposes the one-time installation-root designation to exactly one web-session route.</summary>
public interface IWebFounderBindAuthority
{
    /// <summary>Binds the installation founder, or reports why the attempt was refused.</summary>
    Task<WebFounderBindOutcome> BindAsync(
        WebFounderBindRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The single production consumer of <see cref="InstallationFounderBindingService"/>. It exists so
/// that exactly ONE route may reach the founder designation, and so the domain service's thrown
/// argument/state failures become closed, enumerable statuses instead of unhandled 500s. It adds no
/// policy of its own: idempotency, singleton enforcement, and contention handling stay in the
/// domain service.
/// </summary>
internal sealed class WebFounderBindAuthority : IWebFounderBindAuthority
{
    private readonly InstallationFounderBindingService _binding;

    public WebFounderBindAuthority(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> contextFactory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _binding = new InstallationFounderBindingService(contextFactory, timeProvider);
    }

    /// <inheritdoc />
    public async Task<WebFounderBindOutcome> BindAsync(
        WebFounderBindRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        InstallationFounderBindingCommand command;
        try
        {
            command = new InstallationFounderBindingCommand(
                request.AccountId,
                new CanonicalPartyBinding(
                    request.TenantId,
                    request.PrincipalUserId,
                    request.PartyId),
                request.ExpectedSourceVersion,
                request.IdempotencyKey,
                request.AuditCorrelationId);
        }
        catch (ArgumentException)
        {
            // A malformed binding coordinate is a refused request, never a server fault.
            return Refused(WebFounderBindStatus.InvalidRequest);
        }

        InstallationFounderBindingResult result;
        try
        {
            result = await _binding.BindAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // ValidateCommand refuses blank/oversized/non-positive fields (ArgumentOutOfRangeException
            // derives from ArgumentException, so both arrive here).
            return Refused(WebFounderBindStatus.InvalidRequest);
        }
        catch (InvalidOperationException exception)
            when (exception.Message.Contains(
                "installation-identity.founder_account_not_active",
                StringComparison.Ordinal))
        {
            return Refused(WebFounderBindStatus.AccountNotActive);
        }

        return result.Status switch
        {
            InstallationFounderBindingStatus.Created =>
                Completed(WebFounderBindStatus.Bound, result.Receipt),
            InstallationFounderBindingStatus.IdempotentReplay =>
                Completed(WebFounderBindStatus.Replayed, result.Receipt),
            InstallationFounderBindingStatus.ChangedReplay =>
                Refused(WebFounderBindStatus.Conflict),
            InstallationFounderBindingStatus.AlreadyDesignated =>
                Refused(WebFounderBindStatus.AlreadyDesignated),
            _ => Refused(WebFounderBindStatus.Conflict),
        };
    }

    private static WebFounderBindOutcome Completed(
        WebFounderBindStatus status,
        InstallationFounderBindingReceipt? receipt) =>
        receipt is null
            ? Refused(WebFounderBindStatus.Conflict)
            : new WebFounderBindOutcome(
                status,
                receipt.AccountId,
                receipt.OwnerVersion,
                receipt.DesignatedAtUtc);

    private static WebFounderBindOutcome Refused(WebFounderBindStatus status) =>
        new(status, null, 0, null);
}

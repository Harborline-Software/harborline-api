namespace Harborline.Api.Foundation.Authorization;

public sealed class AuthorizationDeniedException : InvalidOperationException
{
    public AuthorizationDeniedException(AuthorizationDecision decision)
        : base("The authorization gate denied the requested act.")
    {
        ArgumentNullException.ThrowIfNull(decision);
        Decision = decision;
    }

    public AuthorizationDecision Decision { get; }
}

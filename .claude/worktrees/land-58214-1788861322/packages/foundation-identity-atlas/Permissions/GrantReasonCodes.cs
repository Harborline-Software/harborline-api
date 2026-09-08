namespace Harborline.Api.Foundation.IdentityAtlas.Permissions;

/// <summary>The closed vocabulary accepted for grant and revocation reasons.</summary>
public static class GrantReasonCodes
{
    public const string Bootstrap = "bootstrap";
    public const string Invitation = "invitation";
    public const string Manual = "manual";
    public const string Workflow = "workflow";
    public const string Ticket = "ticket";
    public const string RevocationReview = "revocation-review";
    public const string RevocationOffboarding = "revocation-offboarding";
    public const string RevocationCompromise = "revocation-compromise";
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Bootstrap, Invitation, Manual, Workflow, Ticket,
        RevocationReview, RevocationOffboarding, RevocationCompromise,
    };
}

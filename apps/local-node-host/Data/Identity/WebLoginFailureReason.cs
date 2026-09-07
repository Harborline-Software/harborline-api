namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>Why a web login authority refused an otherwise admitted attempt.</summary>
public enum WebLoginFailureReason
{
    /// <summary>The supplied username/password pair did not match a usable credential.</summary>
    CredentialMismatch,

    /// <summary>The supplied input was malformed.</summary>
    MalformedInput,

    /// <summary>An authority gate refused the operation independently of the credential.</summary>
    AuthorityRefused,

    /// <summary>The web profile or provisioned credential cannot currently accept logins.</summary>
    ProvisioningRefused,
}

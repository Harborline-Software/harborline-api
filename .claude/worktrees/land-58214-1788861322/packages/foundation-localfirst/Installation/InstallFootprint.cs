namespace Harborline.Api.Foundation.LocalFirst.Installation;

/// <summary>The filesystem paths exclusively owned by one install identity.</summary>
/// <param name="Identity">Durable install identity from which the ownership decision is made.</param>
/// <param name="DataDirectory">Root directory for node-local state.</param>
/// <param name="DatabasePath">Default encrypted <c>sunfish.db</c> path.</param>
/// <param name="KeystoreDirectory">Directory containing this install's platform-keystore files.</param>
/// <param name="UsesLegacyPaths">Whether this identity owns the pre-namespacing default paths.</param>
public sealed record InstallFootprint(
    InstallIdentity Identity,
    string DataDirectory,
    string DatabasePath,
    string KeystoreDirectory,
    bool UsesLegacyPaths);

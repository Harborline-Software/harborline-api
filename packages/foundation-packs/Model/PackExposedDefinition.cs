namespace Harborline.Api.Foundation.Packs.Model;

/// <summary>
/// One exposed definition's signed catalogue facts (Goal 3 P3, owner decision P10 Option A; DES-0015 records-ck-40,
/// records-ck-41): the catalogue's immutable version of the exact published revision a release bound the definition to,
/// and that revision's <c>sha256:</c> store digest. These are the facts a consumer's cross-package pin carries and the
/// Records install gate compares. They are copied from the producer's signed release, never recomputed: never a package
/// revision, a content address or a member of the body, and never a digest of the re-canonicalized body a pack carries.
/// </summary>
/// <param name="DefinitionKey">The exposed definition key, one of the manifest's <see cref="PackManifest.Exposes"/>.</param>
/// <param name="Version">The bound revision's catalogue version.</param>
/// <param name="Digest">The bound revision's store digest: <c>sha256:</c> and 64 lowercase hex digits.</param>
public sealed record PackExposedDefinition(string DefinitionKey, string Version, string Digest);

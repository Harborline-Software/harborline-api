/**
 * TypeScript projection of `Harborline.Api.Blocks.Docs.Models.StorageRef`.
 *
 * The C# `StorageRefKindJsonConverter` writes the discriminator values below. The
 * nullable `inlineBytes` field is the base64 string emitted by System.Text.Json for
 * `ReadOnlyMemory<byte>?`; the other nullable fields remain opaque strings. Property
 * names follow the camelCase wire convention used by this package's contracts.
 *
 * Per ADR 0168 D4 and `_shared/engineering/crdt-friendly-schema-conventions.md` §9,
 * the reference is the sync payload; the referenced body is fetched separately.
 */

/** JSON discriminator for the C# `StorageRefKind` enum. */
export type StorageRefKind = 'inline' | 'foundationBlob' | 'externalUrl'

/** The three-tier storage reference record used by blocks-docs. */
export interface StorageRef {
  kind: StorageRefKind
  inlineBytes: string | null
  foundationCid: string | null
  externalUrl: string | null
}

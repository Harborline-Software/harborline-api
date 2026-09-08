/**
 * TypeScript projection of the shipped C# `RegistryEntityId`.
 *
 * `RegistryEntityId` is an opaque string-backed record whose JSON converter writes a
 * bare string. Per ADR 0168 D2/D3 and ADR 0101 Rev 3.1/A4, this generic typed-entity
 * reference is the anchor used by cross-domain records; it is not an `AssetId`.
 */
export type EntityRef = string

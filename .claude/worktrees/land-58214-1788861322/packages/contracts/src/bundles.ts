/**
 * Business-case bundle manifest contracts.
 *
 * Mirrors Harborline.Api.Foundation.Catalog.Bundles.BusinessCaseBundleManifest (ADR 0007 + A1).
 * Read-only consumption shape for non-.NET consumers (Harborline Toolbox, flight-deck, etc.).
 *
 * Authoritative source:
 *   packages/foundation-catalog/Bundles/BusinessCaseBundleManifest.cs
 *
 * Canonical JSON fixtures (drift-detection corpus):
 *   packages/foundation-catalog/Manifests/Bundles/*.bundle.json
 *
 * Drift discipline: this file MUST mirror the C# record field-for-field. The
 * fixture-roundtrip test in __tests__/bundles.test.ts enforces drift detection
 * at CI time: any shape change to the C# record (or the on-disk JSON fixtures)
 * that is not reflected here will cause the test to fail.
 *
 * JSON serialisation: ADR 0007 §Decision states manifests use System.Text.Json
 * default camelCase property names. All interface fields here are camelCase.
 *
 * Nullable C# fields (string?) → optional + null-union in TypeScript (?: string | null).
 * Collection fields default to empty arrays; dictionary fields to empty objects.
 */

/**
 * Business-case bundle manifest. A bundle is configuration, not code: it names
 * the reusable modules to activate, the feature defaults to apply, and the
 * provider integrations it requires. See ADR 0007.
 */
export interface BusinessCaseBundleManifest {
  /** Stable bundle identifier, reverse-DNS style (e.g. sunfish.bundles.property-management). */
  key: string;
  /** Human-readable bundle name. */
  name: string;
  /** Semver. See ADR 0007 for upgrade-safety semantics. */
  version: string;
  /** Optional longer description. */
  description?: string | null;
  /** Bundle category. */
  category: BundleCategory;
  /** Bundle lifecycle status. */
  status: BundleStatus;
  /** Engineering readiness note; free-form by design. */
  maturity: string;
  /** Module keys that must be installed for the bundle to activate. */
  requiredModules: string[];
  /** Module keys that may be activated per edition. */
  optionalModules: string[];
  /** Default feature values applied at tenant provisioning. */
  featureDefaults: Record<string, string>;
  /** Edition key -> module keys activated for that edition. */
  editionMappings: Record<string, string[]>;
  /** Deployment modes this bundle supports. */
  deploymentModesSupported: DeploymentMode[];
  /** Provider-category requirements. */
  providerRequirements: ProviderRequirement[];
  /** Named provider-configuration profiles. */
  integrationProfiles: string[];
  /** Pre-built workspaces/dashboards seeded for new tenants. */
  seedWorkspaces: string[];
  /** Personas (drives default roles, navigation, and seed data). */
  personas: string[];
  /** Free-form data-ownership / export / residency policy. */
  dataOwnership?: string | null;
  /** Free-form compliance framing and notes. */
  complianceNotes?: string | null;
  /**
   * Per ADR 0007-A1 — install-time minimum-spec gating per ADR 0063.
   * Absent (undefined) or null when the bundle does not opt in to install-time gating.
   * When non-null, the bundle's install-UX surface consumes this as an opaque display.
   *
   * Note: field is omitted from JSON when null (JsonIgnoreCondition.WhenWritingNull).
   * Pre-A1 manifests omit this field entirely — consumers MUST treat absence as null.
   */
  requirements?: MinimumSpec | null;
}

/** Business-case bundle category. */
export type BundleCategory = 'Operations' | 'Diligence' | 'Finance' | 'Platform';

/** Bundle lifecycle status. */
export type BundleStatus = 'Draft' | 'Preview' | 'GA' | 'Deprecated';

/** Deployment mode supported by a bundle. */
export type DeploymentMode = 'Lite' | 'SelfHosted' | 'HostedSaaS';

/**
 * Provider category for a provider-requirement entry. Maps to
 * Harborline.Api.Foundation.Catalog.Bundles.ProviderCategory C# enum.
 */
export type ProviderCategory =
  | 'Billing'
  | 'Payments'
  | 'BankingFeed'
  | 'FeatureFlags'
  | 'ChannelManager'
  | 'Messaging'
  | 'Storage'
  | 'IdentityProvider'
  | 'Other';

/** A provider-category requirement declared by a bundle. */
export interface ProviderRequirement {
  /** The provider category required or recommended. */
  category: ProviderCategory;
  /** Whether the provider is strictly required (true) or optional (false). */
  required: boolean;
  /** Human-readable explanation of why the provider is needed. */
  purpose?: string | null;
}

/**
 * Per ADR 0007-A1; foundation-catalog-local stub today. Will be replaced by
 * Harborline.Api.Foundation.MissionSpace.MinimumSpec when ADR 0063 Phase 1 substrate
 * ships. The field signature is unchanged across that future rename.
 *
 * Non-.NET consumers (Harborline Toolbox, flight-deck) SHOULD treat this field as
 * opaque-display only — do NOT evaluate against host MissionEnvelope.
 * ADR 0063 Phase 2 wiring is the evaluation step; Q6 surfaces it informatively.
 */
export interface MinimumSpec {
  /** Spec enforcement policy; absent when not specified. */
  policy?: 'Required' | 'Recommended' | 'Informational';
  /** Additional fields tolerated via index signature for forward-compat with ADR 0063 Phase 1. */
  [key: string]: unknown;
}

//! Rust projection of the language-neutral Harborline App and Capability protocol.

pub mod generated;
pub use generated::*;

#[cfg(test)]
mod tests {
    use super::*;
    use serde::{Deserialize, Serialize, de::DeserializeOwned};
    use serde_json::Value;
    use std::{collections::HashSet, fs, path::PathBuf};

    #[derive(Debug, Deserialize)]
    struct FixtureCase {
        model: String,
        file: String,
        outcome: String,
        constraint: Option<String>,
        path: Option<String>,
    }

    #[derive(Debug, Deserialize)]
    struct FixtureManifest {
        fixtures: Vec<FixtureCase>,
        #[serde(rename = "negativeFixtures")]
        negative_fixtures: Vec<FixtureCase>,
    }

    fn fixture_directory() -> PathBuf {
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../protocol/fixtures")
    }

    fn parse_as<T: DeserializeOwned + Serialize>(source: &str) -> Result<Value, String> {
        let model = serde_json::from_str::<T>(source).map_err(|error| error.to_string())?;
        serde_json::to_value(model).map_err(|error| error.to_string())
    }

    fn parse_model(model: &str, source: &str) -> Result<Value, String> {
        match model {
            "EmptyRequest" => parse_as::<EmptyRequest>(source),
            "EmptyResponse" => parse_as::<EmptyResponse>(source),
            "Principal" => parse_as::<Principal>(source),
            "ProtocolError" => parse_as::<ProtocolError>(source),
            "Usage" => parse_as::<Usage>(source),
            "Artifact" => parse_as::<Artifact>(source),
            "CapabilityInvokeRequest" => parse_as::<CapabilityInvokeRequest>(source),
            "CapabilityResult" => parse_as::<CapabilityResult>(source),
            "RuntimeHealth" => parse_as::<RuntimeHealth>(source),
            "HealthReport" => parse_as::<HealthReport>(source),
            "ProviderDescriptor" => parse_as::<ProviderDescriptor>(source),
            "RuntimeCapability" => parse_as::<RuntimeCapability>(source),
            "AnnounceRequest" => parse_as::<AnnounceRequest>(source),
            "AnnounceResult" => parse_as::<AnnounceResult>(source),
            "NegotiateRequest" => parse_as::<NegotiateRequest>(source),
            "NegotiateResult" => parse_as::<NegotiateResult>(source),
            "AddressRequest" => parse_as::<AddressRequest>(source),
            "AddressResult" => parse_as::<AddressResult>(source),
            "SecureRequest" => parse_as::<SecureRequest>(source),
            "SecureResult" => parse_as::<SecureResult>(source),
            "CapabilityHostInvokeRequest" => parse_as::<CapabilityHostInvokeRequest>(source),
            "ObserveRequest" => parse_as::<ObserveRequest>(source),
            "ResolveRequest" => parse_as::<ResolveRequest>(source),
            "ResolutionResult" => parse_as::<ResolutionResult>(source),
            "ComposeRequest" => parse_as::<ComposeRequest>(source),
            "ComposeResult" => parse_as::<ComposeResult>(source),
            "CpDemoRequest" => parse_as::<CpDemoRequest>(source),
            "CpDemoMeta" => parse_as::<CpDemoMeta>(source),
            "CpDemoResult" => parse_as::<CpDemoResult>(source),
            "NodeStatus" => parse_as::<NodeStatus>(source),
            "BackupStatus" => parse_as::<BackupStatus>(source),
            "DataLocationStatus" => parse_as::<DataLocationStatus>(source),
            "DeviceCapabilityProfile" => parse_as::<DeviceCapabilityProfile>(source),
            "PeerSyncConfig" => parse_as::<PeerSyncConfig>(source),
            "RendererLogEntry" => parse_as::<RendererLogEntry>(source),
            "SyncPeerStatus" => parse_as::<SyncPeerStatus>(source),
            "SyncCadence" => parse_as::<SyncCadence>(source),
            "EnrollmentStatus" => parse_as::<EnrollmentStatus>(source),
            "LastPeerExchange" => parse_as::<LastPeerExchange>(source),
            "SyncRecency" => parse_as::<SyncRecency>(source),
            "HarborlineSyncStatus" => parse_as::<HarborlineSyncStatus>(source),
            _ => Err(format!("unknown protocol fixture model {model}")),
        }
    }

    fn is_known_model(model: &str) -> bool {
        matches!(
            model,
            "EmptyRequest"
                | "EmptyResponse"
                | "Principal"
                | "ProtocolError"
                | "Usage"
                | "Artifact"
                | "CapabilityResult"
                | "RuntimeHealth"
                | "HealthReport"
                | "ProviderDescriptor"
                | "RuntimeCapability"
                | "AnnounceRequest"
                | "AnnounceResult"
                | "NegotiateRequest"
                | "NegotiateResult"
                | "AddressRequest"
                | "AddressResult"
                | "SecureRequest"
                | "SecureResult"
                | "CapabilityInvokeRequest"
                | "CapabilityHostInvokeRequest"
                | "ObserveRequest"
                | "ResolveRequest"
                | "ResolutionResult"
                | "ComposeRequest"
                | "ComposeResult"
                | "CpDemoRequest"
                | "CpDemoMeta"
                | "CpDemoResult"
                | "NodeStatus"
                | "BackupStatus"
                | "DataLocationStatus"
                | "DeviceCapabilityProfile"
                | "PeerSyncConfig"
                | "RendererLogEntry"
                | "SyncPeerStatus"
                | "SyncCadence"
                | "EnrollmentStatus"
                | "LastPeerExchange"
                | "SyncRecency"
                | "HarborlineSyncStatus"
        )
    }

    fn assert_declared_constraint(case: &FixtureCase, error: &str) {
        let path = case
            .path
            .as_deref()
            .unwrap_or_else(|| panic!("{} must name its offending property", case.file));
        if !error.contains(path) {
            assert!(
                (case.model == "Principal" && path == "kind" && error.contains("unknown variant"))
                    || (case.model == "CapabilityResult"
                        && path == "progress"
                        && case.constraint.as_deref() == Some("minimum")
                        && error.contains("outside schema range"))
                    || (case.model == "DeviceCapabilityProfile"
                        && path == "detectedAtMs"
                        && case.constraint.as_deref() == Some("maximum")
                        && error.contains("outside schema range"))
                    || (case.model == "SyncCadence"
                        && path == "roundIntervalSeconds"
                        && case.constraint.as_deref() == Some("maximum")
                        && error.contains("outside schema range")),
                "{}: rejection did not identify {path}: {error}",
                case.file
            );
        }

        match case.constraint.as_deref() {
            Some("required") => assert!(
                error.contains("missing field"),
                "{}: expected a required-field rejection: {error}",
                case.file
            ),
            Some("enum") => assert!(
                error.contains("unknown variant") || error.contains("outside enum"),
                "{}: expected an enum rejection: {error}",
                case.file
            ),
            Some("exact-case") => assert!(
                error.contains("unknown field"),
                "{}: expected an exact-wire-case rejection: {error}",
                case.file
            ),
            Some("minimum") | Some("maximum") => assert!(
                error.contains("outside schema range"),
                "{}: expected a bounded-integer rejection: {error}",
                case.file
            ),
            Some("unknown-property") => assert!(
                error.contains("unknown field"),
                "{}: expected an unknown-property rejection: {error}",
                case.file
            ),
            Some(other) => panic!("{}: unsupported declared constraint {other}", case.file),
            None => panic!("{} must name its violated constraint", case.file),
        }
    }

    #[test]
    fn refuses_to_serialize_an_integer_above_the_cross_language_maximum() {
        let cadence = SyncCadence {
            next_round_at: Some("2026-08-02T12:00:00Z".to_owned()),
            round_interval_seconds: 9_007_199_254_740_992,
        };

        let error = serde_json::to_string(&cadence).expect_err("out-of-range value must not serialize");
        assert!(error.to_string().contains("value outside schema range"));
    }

    #[test]
    fn renamed_capability_request_is_a_known_fixture_model() {
        assert!(is_known_model("CapabilityInvokeRequest"));
    }

    #[test]
    fn manifest_cases_match_declared_outcomes() {
        let directory = fixture_directory();
        let manifest_source =
            fs::read_to_string(directory.join("manifest.json")).expect("fixture manifest");
        let manifest = serde_json::from_str::<FixtureManifest>(&manifest_source)
            .expect("fixture manifest JSON");
        let positive_models: HashSet<String> = manifest
            .fixtures
            .iter()
            .map(|case| case.model.clone())
            .collect();
        let cases: Vec<FixtureCase> = manifest
            .fixtures
            .into_iter()
            .chain(manifest.negative_fixtures)
            .collect();

        for case in &cases {
            assert!(is_known_model(&case.model), "unknown protocol fixture model {}", case.model);
        }

        let schema_source = fs::read_to_string(
            PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../protocol/schemas/carrier-protocol.schema.json"),
        )
        .expect("canonical protocol schema");
        let schema = serde_json::from_str::<Value>(&schema_source).expect("canonical schema JSON");
        let definitions = schema
            .get("$defs")
            .and_then(Value::as_object)
            .expect("canonical schema definitions");
        for definition in definitions.keys() {
            assert!(
                positive_models.contains(definition.as_str()),
                "canonical schema definition {definition} has no positive manifest case"
            );
        }

        for case in cases {
            let source = fs::read_to_string(directory.join(&case.file)).expect("fixture JSON");
            let expected: Value = serde_json::from_str(&source).expect("fixture JSON");
            let parsed = parse_model(&case.model, &source);

            match case.outcome.as_str() {
                "accept" => {
                    let actual = parsed.unwrap_or_else(|error| {
                        panic!(
                            "{}: Rust projection rejected an accepted fixture: {error}",
                            case.file
                        )
                    });
                    assert_eq!(
                        expected, actual,
                        "Rust projection drifted for {}",
                        case.file
                    );
                }
                "reject" => {
                    assert!(
                        case.constraint
                            .as_deref()
                            .is_some_and(|value| !value.is_empty()),
                        "{} must name its violated constraint",
                        case.file
                    );
                    let error = match parsed {
                        Ok(_) => panic!(
                            "{}: Rust projection accepted a fixture violating {}",
                            case.file,
                            case.constraint.as_deref().unwrap_or("unknown constraint")
                        ),
                        Err(error) => error,
                    };
                    assert_declared_constraint(&case, &error);
                }
                outcome => panic!("{}: unknown fixture outcome {outcome}", case.file),
            }
        }
    }
}

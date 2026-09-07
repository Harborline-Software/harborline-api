// GENERATED from packages/contracts/protocol. DO NOT EDIT.
// Protocol shipyard.carrier 2.0.0.
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use serde_json::Value;
use std::collections::BTreeMap;

pub const HARBORLINE_PROTOCOL_ID: &str = "shipyard.carrier";
pub const HARBORLINE_PROTOCOL_VERSION: &str = "2.0.0";

pub mod operation_ids {
    pub const CAPABILITY_ANNOUNCE: &str = "capability.announce";
    pub const CAPABILITY_NEGOTIATE: &str = "capability.negotiate";
    pub const CAPABILITY_ADDRESS: &str = "capability.address";
    pub const CAPABILITY_SECURE: &str = "capability.secure";
    pub const CAPABILITY_INVOKE: &str = "capability.invoke";
    pub const CAPABILITY_OBSERVE: &str = "capability.observe";
    pub const CAPABILITY_RESOLVE: &str = "capability.resolve";
    pub const CAPABILITY_COMPOSE: &str = "capability.compose";
    pub const CARRIER_HOST_CAPABILITY_INVOKE: &str = "carrier.host.capabilityInvoke";
    pub const CARRIER_HOST_CAPABILITY_HEALTH: &str = "carrier.host.capabilityHealth";
    pub const CARRIER_HOST_CAPABILITY_CP_DEMO_EXECUTE: &str = "carrier.host.capabilityCpDemoExecute";
    pub const CARRIER_HOST_CURRENT_PRINCIPAL: &str = "carrier.host.currentPrincipal";
    pub const CARRIER_HOST_NODE_STATUS: &str = "carrier.host.nodeStatus";
    pub const CARRIER_HOST_DATA_LOCATION_STATUS: &str = "carrier.host.dataLocationStatus";
    pub const CARRIER_HOST_DEVICE_CAPABILITY_PROFILE: &str = "carrier.host.deviceCapabilityProfile";
    pub const CARRIER_HOST_GET_PEER_SYNC_CONFIG: &str = "carrier.host.getPeerSyncConfig";
    pub const CARRIER_HOST_SET_PEER_SYNC_CONFIG: &str = "carrier.host.setPeerSyncConfig";
    pub const CARRIER_HOST_APPEND_RENDERER_LOG: &str = "carrier.host.appendRendererLog";
    pub const CARRIER_APPLICATION_GET_SYNC_STATUS: &str = "carrier.application.getSyncStatus";
}

pub mod host_commands {
    pub const CAPABILITY_INVOKE: &str = "capability_invoke";
    pub const CAPABILITY_HEALTH: &str = "capability_health";
    pub const CAPABILITY_CP_DEMO_EXECUTE: &str = "capability_cp_demo_execute";
    pub const CURRENT_PRINCIPAL: &str = "current_principal";
    pub const NODE_STATUS: &str = "node_status";
    pub const DATA_LOCATION_STATUS: &str = "get_data_location_status";
    pub const DEVICE_CAPABILITY_PROFILE: &str = "get_device_capability_profile";
    pub const GET_PEER_SYNC_CONFIG: &str = "get_peer_sync_config";
    pub const SET_PEER_SYNC_CONFIG: &str = "set_peer_sync_config";
    pub const APPEND_RENDERER_LOG: &str = "append_renderer_log";
}

pub mod application_routes {
    pub const GET_SYNC_STATUS: &str = "/api/local-node/sync-status";
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct EmptyRequest {
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct EmptyResponse {
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum PrincipalKind {
    #[serde(rename = "local-os-user")]
    LocalOsUser,
    #[serde(rename = "service")]
    Service,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Principal {
    #[serde(rename = "id")]
    pub id: String,
    #[serde(rename = "displayName")]
    pub display_name: String,
    #[serde(rename = "kind")]
    pub kind: PrincipalKind,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ProtocolErrorFaultDomain {
    #[serde(rename = "input")]
    Input,
    #[serde(rename = "provider")]
    Provider,
    #[serde(rename = "membrane")]
    Membrane,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ProtocolError {
    #[serde(rename = "faultDomain")]
    pub fault_domain: ProtocolErrorFaultDomain,
    #[serde(rename = "retryable")]
    pub retryable: bool,
    #[serde(rename = "code")]
    pub code: String,
    #[serde(rename = "message")]
    pub message: String,
    #[serde(rename = "retryAfter", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_protocol_error_retry_after", serialize_with = "serialize_protocol_error_retry_after")]
    pub retry_after: Option<i64>,
}

fn deserialize_protocol_error_retry_after<'de, D>(deserializer: D) -> Result<Option<i64>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<i64>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    if let Some(value) = value {
        if value < 0 || value > 9007199254740991 { return Err(serde::de::Error::custom("value outside schema range")); }
    }
    Ok(value)
}

fn serialize_protocol_error_retry_after<S>(value: &Option<i64>, serializer: S) -> Result<S::Ok, S::Error>
where
    S: Serializer,
{
    if let Some(value) = value {
        if *value < 0 || *value > 9007199254740991 { return Err(serde::ser::Error::custom("value outside schema range")); }
    }
    value.serialize(serializer)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum UsageTier {
    #[serde(rename = "local")]
    Local,
    #[serde(rename = "remote")]
    Remote,
    #[serde(rename = "cloud")]
    Cloud,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Usage {
    #[serde(rename = "unit")]
    pub unit: String,
    #[serde(rename = "quantity")]
    pub quantity: f64,
    #[serde(rename = "costMicros", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_usage_cost_micros", serialize_with = "serialize_usage_cost_micros")]
    pub cost_micros: Option<i64>,
    #[serde(rename = "tier")]
    pub tier: UsageTier,
}

fn deserialize_usage_cost_micros<'de, D>(deserializer: D) -> Result<Option<i64>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<i64>::deserialize(deserializer)?;
    if let Some(value) = value {
        if value < 0 || value > 9007199254740991 { return Err(serde::de::Error::custom("value outside schema range")); }
    }
    Ok(value)
}

fn serialize_usage_cost_micros<S>(value: &Option<i64>, serializer: S) -> Result<S::Ok, S::Error>
where
    S: Serializer,
{
    if let Some(value) = value {
        if *value < 0 || *value > 9007199254740991 { return Err(serde::ser::Error::custom("value outside schema range")); }
    }
    value.serialize(serializer)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Artifact {
    #[serde(rename = "kind")]
    pub kind: String,
    #[serde(rename = "uri", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_artifact_uri")]
    pub uri: Option<String>,
    #[serde(rename = "mime", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_artifact_mime")]
    pub mime: Option<String>,
    #[serde(rename = "text", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_artifact_text")]
    pub text: Option<String>,
    #[serde(flatten)]
    pub additional_properties: BTreeMap<String, Value>,
}

fn deserialize_artifact_uri<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

fn deserialize_artifact_mime<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

fn deserialize_artifact_text<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum CapabilityResultStatus {
    #[serde(rename = "accepted")]
    Accepted,
    #[serde(rename = "running")]
    Running,
    #[serde(rename = "succeeded")]
    Succeeded,
    #[serde(rename = "partial")]
    Partial,
    #[serde(rename = "failed")]
    Failed,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CapabilityResult {
    #[serde(rename = "jobId")]
    pub job_id: String,
    #[serde(rename = "status")]
    pub status: CapabilityResultStatus,
    #[serde(rename = "progress", deserialize_with = "deserialize_capability_result_progress", serialize_with = "serialize_capability_result_progress")]
    pub progress: f64,
    #[serde(rename = "artifacts")]
    pub artifacts: Vec<Artifact>,
    #[serde(rename = "usage")]
    pub usage: Usage,
    #[serde(rename = "error", deserialize_with = "deserialize_capability_result_error")]
    pub error: Option<ProtocolError>,
}

fn deserialize_capability_result_progress<'de, D>(deserializer: D) -> Result<f64, D::Error>
where
    D: Deserializer<'de>,
{
    let value = f64::deserialize(deserializer)?;
    if value < 0.0 || value > 1.0 { return Err(serde::de::Error::custom("value outside schema range")); }
    Ok(value)
}

fn serialize_capability_result_progress<S>(value: &f64, serializer: S) -> Result<S::Ok, S::Error>
where
    S: Serializer,
{
    if *value < 0.0 || *value > 1.0 { return Err(serde::ser::Error::custom("value outside schema range")); }
    value.serialize(serializer)
}

fn deserialize_capability_result_error<'de, D>(deserializer: D) -> Result<Option<ProtocolError>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<ProtocolError>::deserialize(deserializer)?;
    Ok(value)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum RuntimeHealthState {
    #[serde(rename = "up")]
    Up,
    #[serde(rename = "degraded")]
    Degraded,
    #[serde(rename = "down")]
    Down,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RuntimeHealth {
    #[serde(rename = "runtimeId")]
    pub runtime_id: String,
    #[serde(rename = "capability")]
    pub capability: String,
    #[serde(rename = "state")]
    pub state: RuntimeHealthState,
    #[serde(rename = "detail", deserialize_with = "deserialize_runtime_health_detail")]
    pub detail: Option<String>,
    #[serde(rename = "extensions", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_runtime_health_extensions")]
    pub extensions: Option<BTreeMap<String, Value>>,
}

fn deserialize_runtime_health_detail<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    Ok(value)
}

fn deserialize_runtime_health_extensions<'de, D>(deserializer: D) -> Result<Option<BTreeMap<String, Value>>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<BTreeMap<String, Value>>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct HealthReport {
    #[serde(rename = "runtimes")]
    pub runtimes: Vec<RuntimeHealth>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ProviderDescriptor {
    #[serde(rename = "id")]
    pub id: String,
    #[serde(flatten)]
    pub additional_properties: BTreeMap<String, Value>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RuntimeCapability {
    #[serde(rename = "capabilityId")]
    pub capability_id: String,
    #[serde(rename = "schemaVersion")]
    pub schema_version: String,
    #[serde(rename = "providers")]
    pub providers: Vec<ProviderDescriptor>,
    #[serde(rename = "extensions", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_runtime_capability_extensions")]
    pub extensions: Option<BTreeMap<String, Value>>,
}

fn deserialize_runtime_capability_extensions<'de, D>(deserializer: D) -> Result<Option<BTreeMap<String, Value>>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<BTreeMap<String, Value>>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct AnnounceRequest {
    #[serde(rename = "runtimeId")]
    pub runtime_id: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct AnnounceResult {
    #[serde(rename = "runtimeId")]
    pub runtime_id: String,
    #[serde(rename = "name")]
    pub name: String,
    #[serde(rename = "contractVersion")]
    pub contract_version: String,
    #[serde(rename = "capabilities")]
    pub capabilities: Vec<RuntimeCapability>,
    #[serde(rename = "extensions", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_announce_result_extensions")]
    pub extensions: Option<BTreeMap<String, Value>>,
}

fn deserialize_announce_result_extensions<'de, D>(deserializer: D) -> Result<Option<BTreeMap<String, Value>>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<BTreeMap<String, Value>>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NegotiateRequest {
    #[serde(rename = "runtimeId")]
    pub runtime_id: String,
    #[serde(rename = "contractVersion")]
    pub contract_version: String,
    #[serde(rename = "capabilitySchemaVersions")]
    pub capability_schema_versions: BTreeMap<String, String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NegotiateResult {
    #[serde(rename = "compatible")]
    pub compatible: bool,
    #[serde(rename = "agreedContractVersion")]
    pub agreed_contract_version: String,
    #[serde(rename = "acceptedCapabilities")]
    pub accepted_capabilities: Vec<String>,
    #[serde(rename = "reason", deserialize_with = "deserialize_negotiate_result_reason")]
    pub reason: Option<String>,
}

fn deserialize_negotiate_result_reason<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    Ok(value)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct AddressRequest {
    #[serde(rename = "runtimeId")]
    pub runtime_id: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum AddressResultMode {
    #[serde(rename = "in-process")]
    InProcess,
    #[serde(rename = "local-subprocess")]
    LocalSubprocess,
    #[serde(rename = "remote-mesh")]
    RemoteMesh,
    #[serde(rename = "relay")]
    Relay,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct AddressResult {
    #[serde(rename = "runtimeId")]
    pub runtime_id: String,
    #[serde(rename = "mode")]
    pub mode: AddressResultMode,
    #[serde(rename = "baseUrl", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_address_result_base_url")]
    pub base_url: Option<String>,
    #[serde(rename = "extensions", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_address_result_extensions")]
    pub extensions: Option<BTreeMap<String, Value>>,
}

fn deserialize_address_result_base_url<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

fn deserialize_address_result_extensions<'de, D>(deserializer: D) -> Result<Option<BTreeMap<String, Value>>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<BTreeMap<String, Value>>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SecureRequest {
    #[serde(rename = "operationId")]
    pub operation_id: String,
    #[serde(rename = "capabilityId")]
    pub capability_id: String,
    #[serde(rename = "correlationId")]
    pub correlation_id: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum SecureResultAuthority {
    #[serde(rename = "AP")]
    AP,
    #[serde(rename = "CP")]
    CP,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SecureResult {
    #[serde(rename = "allowed")]
    pub allowed: bool,
    #[serde(rename = "authority")]
    pub authority: SecureResultAuthority,
    #[serde(rename = "reason", deserialize_with = "deserialize_secure_result_reason")]
    pub reason: Option<String>,
}

fn deserialize_secure_result_reason<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    Ok(value)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CapabilityInvokeRequest {
    #[serde(rename = "capability")]
    pub capability: String,
    #[serde(rename = "core")]
    pub core: Value,
    #[serde(rename = "correlationId")]
    pub correlation_id: String,
    #[serde(rename = "idempotencyKey")]
    pub idempotency_key: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CapabilityHostInvokeRequest {
    #[serde(rename = "capability")]
    pub capability: String,
    #[serde(rename = "core")]
    pub core: Value,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ObserveRequestKind {
    #[serde(rename = "startup")]
    Startup,
    #[serde(rename = "liveness")]
    Liveness,
    #[serde(rename = "readiness")]
    Readiness,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ObserveRequest {
    #[serde(rename = "runtimeId", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_observe_request_runtime_id")]
    pub runtime_id: Option<String>,
    #[serde(rename = "kind")]
    pub kind: ObserveRequestKind,
}

fn deserialize_observe_request_runtime_id<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ResolveRequest {
    #[serde(rename = "capabilityId")]
    pub capability_id: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ResolutionResultTier {
    #[serde(rename = "local")]
    Local,
    #[serde(rename = "remote")]
    Remote,
    #[serde(rename = "cloud")]
    Cloud,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ResolutionResultResolutionState {
    #[serde(rename = "available")]
    Available,
    #[serde(rename = "locked-entitlement")]
    LockedEntitlement,
    #[serde(rename = "upsell")]
    Upsell,
    #[serde(rename = "unavailable-hardware")]
    UnavailableHardware,
    #[serde(rename = "degraded")]
    Degraded,
    #[serde(rename = "not-in-edition")]
    NotInEdition,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ResolutionResultSelectionReason {
    #[serde(rename = "only-candidate")]
    OnlyCandidate,
    #[serde(rename = "preferred-by-policy")]
    PreferredByPolicy,
    #[serde(rename = "hardware-fit")]
    HardwareFit,
    #[serde(rename = "fallback-floor")]
    FallbackFloor,
    #[serde(rename = "entitlement-gated")]
    EntitlementGated,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ResolutionResultSpeedHint {
    #[serde(rename = "fast")]
    Fast,
    #[serde(rename = "moderate")]
    Moderate,
    #[serde(rename = "slow")]
    Slow,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ResolutionResult {
    #[serde(rename = "chosenProviderId", deserialize_with = "deserialize_resolution_result_chosen_provider_id")]
    pub chosen_provider_id: Option<String>,
    #[serde(rename = "tier")]
    pub tier: ResolutionResultTier,
    #[serde(rename = "resolutionState")]
    pub resolution_state: ResolutionResultResolutionState,
    #[serde(rename = "selectionReason")]
    pub selection_reason: ResolutionResultSelectionReason,
    #[serde(rename = "speedHint")]
    pub speed_hint: ResolutionResultSpeedHint,
    #[serde(rename = "reason")]
    pub reason: String,
    #[serde(rename = "isFallback")]
    pub is_fallback: bool,
    #[serde(rename = "extensions", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_resolution_result_extensions")]
    pub extensions: Option<BTreeMap<String, Value>>,
}

fn deserialize_resolution_result_chosen_provider_id<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    Ok(value)
}

fn deserialize_resolution_result_extensions<'de, D>(deserializer: D) -> Result<Option<BTreeMap<String, Value>>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<BTreeMap<String, Value>>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ComposeRequest {
    #[serde(rename = "editionId")]
    pub edition_id: String,
    #[serde(rename = "capabilityIds")]
    pub capability_ids: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ComposeResult {
    #[serde(rename = "editionId")]
    pub edition_id: String,
    #[serde(rename = "acceptedCapabilityIds")]
    pub accepted_capability_ids: Vec<String>,
    #[serde(rename = "rejectedCapabilityIds")]
    pub rejected_capability_ids: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CpDemoRequest {
    #[serde(rename = "note", deserialize_with = "deserialize_cp_demo_request_note")]
    pub note: Option<String>,
}

fn deserialize_cp_demo_request_note<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    Ok(value)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum CpDemoMetaCommand {
    #[serde(rename = "demo-cp-op")]
    DemoCpOp,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CpDemoMeta {
    #[serde(rename = "command")]
    pub command: CpDemoMetaCommand,
    #[serde(rename = "confirmed")]
    pub confirmed: bool,
    #[serde(rename = "note")]
    pub note: String,
    #[serde(rename = "executedAt")]
    pub executed_at: String,
    #[serde(rename = "proposedBy")]
    pub proposed_by: Principal,
    #[serde(rename = "confirmedBy")]
    pub confirmed_by: Principal,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum CpDemoResultStatus {
    #[serde(rename = "accepted")]
    Accepted,
    #[serde(rename = "running")]
    Running,
    #[serde(rename = "succeeded")]
    Succeeded,
    #[serde(rename = "partial")]
    Partial,
    #[serde(rename = "failed")]
    Failed,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct CpDemoResult {
    #[serde(rename = "jobId")]
    pub job_id: String,
    #[serde(rename = "status")]
    pub status: CpDemoResultStatus,
    #[serde(rename = "progress", deserialize_with = "deserialize_cp_demo_result_progress", serialize_with = "serialize_cp_demo_result_progress")]
    pub progress: f64,
    #[serde(rename = "artifacts")]
    pub artifacts: Vec<Artifact>,
    #[serde(rename = "usage")]
    pub usage: Usage,
    #[serde(rename = "error", deserialize_with = "deserialize_cp_demo_result_error")]
    pub error: Option<ProtocolError>,
    #[serde(rename = "meta", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_cp_demo_result_meta")]
    pub meta: Option<CpDemoMeta>,
}

fn deserialize_cp_demo_result_progress<'de, D>(deserializer: D) -> Result<f64, D::Error>
where
    D: Deserializer<'de>,
{
    let value = f64::deserialize(deserializer)?;
    if value < 0.0 || value > 1.0 { return Err(serde::de::Error::custom("value outside schema range")); }
    Ok(value)
}

fn serialize_cp_demo_result_progress<S>(value: &f64, serializer: S) -> Result<S::Ok, S::Error>
where
    S: Serializer,
{
    if *value < 0.0 || *value > 1.0 { return Err(serde::ser::Error::custom("value outside schema range")); }
    value.serialize(serializer)
}

fn deserialize_cp_demo_result_error<'de, D>(deserializer: D) -> Result<Option<ProtocolError>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<ProtocolError>::deserialize(deserializer)?;
    Ok(value)
}

fn deserialize_cp_demo_result_meta<'de, D>(deserializer: D) -> Result<Option<CpDemoMeta>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<CpDemoMeta>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum NodeStatusState {
    #[serde(rename = "starting")]
    Starting,
    #[serde(rename = "running")]
    Running,
    #[serde(rename = "failed")]
    Failed,
    #[serde(rename = "stopped")]
    Stopped,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct NodeStatus {
    #[serde(rename = "state")]
    pub state: NodeStatusState,
    #[serde(rename = "baseUrl", deserialize_with = "deserialize_node_status_base_url")]
    pub base_url: Option<String>,
    #[serde(rename = "sessionToken", deserialize_with = "deserialize_node_status_session_token")]
    pub session_token: Option<String>,
    #[serde(rename = "detail", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_node_status_detail")]
    pub detail: Option<String>,
    #[serde(rename = "extensions", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_node_status_extensions")]
    pub extensions: Option<BTreeMap<String, Value>>,
}

fn deserialize_node_status_base_url<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    Ok(value)
}

fn deserialize_node_status_session_token<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    Ok(value)
}

fn deserialize_node_status_detail<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

fn deserialize_node_status_extensions<'de, D>(deserializer: D) -> Result<Option<BTreeMap<String, Value>>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<BTreeMap<String, Value>>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum BackupStatusState {
    #[serde(rename = "notConfigured")]
    NotConfigured,
    #[serde(rename = "configured")]
    Configured,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct BackupStatus {
    #[serde(rename = "state")]
    pub state: BackupStatusState,
    #[serde(rename = "destination", deserialize_with = "deserialize_backup_status_destination")]
    pub destination: Option<String>,
    #[serde(rename = "lastSuccessfulBackupAtMs", deserialize_with = "deserialize_backup_status_last_successful_backup_at_ms", serialize_with = "serialize_backup_status_last_successful_backup_at_ms")]
    pub last_successful_backup_at_ms: Option<i64>,
}

fn deserialize_backup_status_destination<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    Ok(value)
}

fn deserialize_backup_status_last_successful_backup_at_ms<'de, D>(deserializer: D) -> Result<Option<i64>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<i64>::deserialize(deserializer)?;
    if let Some(value) = value {
        if value < 0 || value > 9007199254740991 { return Err(serde::de::Error::custom("value outside schema range")); }
    }
    Ok(value)
}

fn serialize_backup_status_last_successful_backup_at_ms<S>(value: &Option<i64>, serializer: S) -> Result<S::Ok, S::Error>
where
    S: Serializer,
{
    if let Some(value) = value {
        if *value < 0 || *value > 9007199254740991 { return Err(serde::ser::Error::custom("value outside schema range")); }
    }
    value.serialize(serializer)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum DataLocationStatusDataDirectorySource {
    #[serde(rename = "applicationData")]
    ApplicationData,
    #[serde(rename = "developmentOverride")]
    DevelopmentOverride,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct DataLocationStatus {
    #[serde(rename = "dataDirectory")]
    pub data_directory: String,
    #[serde(rename = "dataDirectorySource")]
    pub data_directory_source: DataLocationStatusDataDirectorySource,
    #[serde(rename = "backup")]
    pub backup: BackupStatus,
    #[serde(rename = "extensions", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_data_location_status_extensions")]
    pub extensions: Option<BTreeMap<String, Value>>,
}

fn deserialize_data_location_status_extensions<'de, D>(deserializer: D) -> Result<Option<BTreeMap<String, Value>>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<BTreeMap<String, Value>>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i64)]
pub enum DeviceCapabilityProfileSchemaVersion {
    Value1 = 1,
}

impl Serialize for DeviceCapabilityProfileSchemaVersion {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error> where S: serde::Serializer {
        serializer.serialize_i64(*self as i64)
    }
}

impl<'de> Deserialize<'de> for DeviceCapabilityProfileSchemaVersion {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error> where D: Deserializer<'de> {
        match i64::deserialize(deserializer)? {
            1 => Ok(Self::Value1),
            _ => Err(serde::de::Error::custom("value is outside enum")),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum DeviceCapabilityProfileOsFamily {
    #[serde(rename = "macos")]
    Macos,
    #[serde(rename = "windows")]
    Windows,
    #[serde(rename = "linux")]
    Linux,
    #[serde(rename = "ios")]
    Ios,
    #[serde(rename = "android")]
    Android,
    #[serde(rename = "unknown")]
    Unknown,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum DeviceCapabilityProfileFastMemoryKind {
    #[serde(rename = "unified")]
    Unified,
    #[serde(rename = "dedicatedVram")]
    DedicatedVram,
    #[serde(rename = "system")]
    System,
    #[serde(rename = "unknown")]
    Unknown,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum DeviceCapabilityProfileBandwidthClass {
    #[serde(rename = "low")]
    Low,
    #[serde(rename = "moderate")]
    Moderate,
    #[serde(rename = "high")]
    High,
    #[serde(rename = "unknown")]
    Unknown,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum DeviceCapabilityProfileBandwidthEvidence {
    #[serde(rename = "inferred")]
    Inferred,
    #[serde(rename = "unknown")]
    Unknown,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum DeviceCapabilityProfileMaximumLocalAiTier {
    #[serde(rename = "T-E")]
    TE,
    #[serde(rename = "T-S")]
    TS,
    #[serde(rename = "T-A")]
    TA,
    #[serde(rename = "T-R")]
    TR,
    #[serde(rename = "T-C")]
    TC,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum DeviceCapabilityProfileDetectionStatus {
    #[serde(rename = "complete")]
    Complete,
    #[serde(rename = "partial")]
    Partial,
    #[serde(rename = "unknown")]
    Unknown,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum DeviceCapabilityProfileLimitations {
    #[serde(rename = "bandwidthUnavailable")]
    BandwidthUnavailable,
    #[serde(rename = "dedicatedVramUnavailable")]
    DedicatedVramUnavailable,
    #[serde(rename = "fastMemoryUnavailable")]
    FastMemoryUnavailable,
    #[serde(rename = "hostUnavailable")]
    HostUnavailable,
    #[serde(rename = "invalidHostResponse")]
    InvalidHostResponse,
    #[serde(rename = "mobileWorkingSetEstimated")]
    MobileWorkingSetEstimated,
    #[serde(rename = "probeFailed")]
    ProbeFailed,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct DeviceCapabilityProfile {
    #[serde(rename = "schemaVersion")]
    pub schema_version: DeviceCapabilityProfileSchemaVersion,
    #[serde(rename = "detectedAtMs", deserialize_with = "deserialize_device_capability_profile_detected_at_ms", serialize_with = "serialize_device_capability_profile_detected_at_ms")]
    pub detected_at_ms: i64,
    #[serde(rename = "osFamily")]
    pub os_family: DeviceCapabilityProfileOsFamily,
    #[serde(rename = "architecture")]
    pub architecture: String,
    #[serde(rename = "systemMemoryBytes", deserialize_with = "deserialize_device_capability_profile_system_memory_bytes", serialize_with = "serialize_device_capability_profile_system_memory_bytes")]
    pub system_memory_bytes: Option<i64>,
    #[serde(rename = "fastMemoryBytes", deserialize_with = "deserialize_device_capability_profile_fast_memory_bytes", serialize_with = "serialize_device_capability_profile_fast_memory_bytes")]
    pub fast_memory_bytes: Option<i64>,
    #[serde(rename = "fastMemoryKind")]
    pub fast_memory_kind: DeviceCapabilityProfileFastMemoryKind,
    #[serde(rename = "bandwidthClass")]
    pub bandwidth_class: DeviceCapabilityProfileBandwidthClass,
    #[serde(rename = "bandwidthEvidence")]
    pub bandwidth_evidence: DeviceCapabilityProfileBandwidthEvidence,
    #[serde(rename = "maximumLocalAiTier")]
    pub maximum_local_ai_tier: DeviceCapabilityProfileMaximumLocalAiTier,
    #[serde(rename = "detectionStatus")]
    pub detection_status: DeviceCapabilityProfileDetectionStatus,
    #[serde(rename = "limitations")]
    pub limitations: Vec<DeviceCapabilityProfileLimitations>,
    #[serde(rename = "extensions", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_device_capability_profile_extensions")]
    pub extensions: Option<BTreeMap<String, Value>>,
}

fn deserialize_device_capability_profile_detected_at_ms<'de, D>(deserializer: D) -> Result<i64, D::Error>
where
    D: Deserializer<'de>,
{
    let value = i64::deserialize(deserializer)?;
    if value < 0 || value > 9007199254740991 { return Err(serde::de::Error::custom("value outside schema range")); }
    Ok(value)
}

fn serialize_device_capability_profile_detected_at_ms<S>(value: &i64, serializer: S) -> Result<S::Ok, S::Error>
where
    S: Serializer,
{
    if *value < 0 || *value > 9007199254740991 { return Err(serde::ser::Error::custom("value outside schema range")); }
    value.serialize(serializer)
}

fn deserialize_device_capability_profile_system_memory_bytes<'de, D>(deserializer: D) -> Result<Option<i64>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<i64>::deserialize(deserializer)?;
    if let Some(value) = value {
        if value < 0 || value > 9007199254740991 { return Err(serde::de::Error::custom("value outside schema range")); }
    }
    Ok(value)
}

fn serialize_device_capability_profile_system_memory_bytes<S>(value: &Option<i64>, serializer: S) -> Result<S::Ok, S::Error>
where
    S: Serializer,
{
    if let Some(value) = value {
        if *value < 0 || *value > 9007199254740991 { return Err(serde::ser::Error::custom("value outside schema range")); }
    }
    value.serialize(serializer)
}

fn deserialize_device_capability_profile_fast_memory_bytes<'de, D>(deserializer: D) -> Result<Option<i64>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<i64>::deserialize(deserializer)?;
    if let Some(value) = value {
        if value < 0 || value > 9007199254740991 { return Err(serde::de::Error::custom("value outside schema range")); }
    }
    Ok(value)
}

fn serialize_device_capability_profile_fast_memory_bytes<S>(value: &Option<i64>, serializer: S) -> Result<S::Ok, S::Error>
where
    S: Serializer,
{
    if let Some(value) = value {
        if *value < 0 || *value > 9007199254740991 { return Err(serde::ser::Error::custom("value outside schema range")); }
    }
    value.serialize(serializer)
}

fn deserialize_device_capability_profile_extensions<'de, D>(deserializer: D) -> Result<Option<BTreeMap<String, Value>>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<BTreeMap<String, Value>>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct PeerSyncConfig {
    #[serde(rename = "enableMdns", deserialize_with = "deserialize_peer_sync_config_enable_mdns")]
    pub enable_mdns: Option<bool>,
    #[serde(rename = "peers")]
    pub peers: Vec<String>,
    #[serde(rename = "bindAddress", deserialize_with = "deserialize_peer_sync_config_bind_address")]
    pub bind_address: Option<String>,
}

fn deserialize_peer_sync_config_enable_mdns<'de, D>(deserializer: D) -> Result<Option<bool>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<bool>::deserialize(deserializer)?;
    Ok(value)
}

fn deserialize_peer_sync_config_bind_address<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    Ok(value)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct RendererLogEntry {
    #[serde(rename = "level")]
    pub level: String,
    #[serde(rename = "message")]
    pub message: String,
    #[serde(rename = "stack", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_renderer_log_entry_stack")]
    pub stack: Option<String>,
}

fn deserialize_renderer_log_entry_stack<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum SyncPeerStatusState {
    #[serde(rename = "has")]
    Has,
    #[serde(rename = "will")]
    Will,
    #[serde(rename = "should")]
    Should,
    #[serde(rename = "couldnt")]
    Couldnt,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SyncPeerStatus {
    #[serde(rename = "deviceId")]
    pub device_id: String,
    #[serde(rename = "label")]
    pub label: String,
    #[serde(rename = "state")]
    pub state: SyncPeerStatusState,
    #[serde(rename = "lastReachedAt", deserialize_with = "deserialize_sync_peer_status_last_reached_at")]
    pub last_reached_at: Option<String>,
    #[serde(rename = "offlineDurationMs", deserialize_with = "deserialize_sync_peer_status_offline_duration_ms", serialize_with = "serialize_sync_peer_status_offline_duration_ms")]
    pub offline_duration_ms: Option<i64>,
    #[serde(rename = "isSecurityEvent")]
    pub is_security_event: bool,
    #[serde(rename = "errorCode", deserialize_with = "deserialize_sync_peer_status_error_code")]
    pub error_code: Option<String>,
}

fn deserialize_sync_peer_status_last_reached_at<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    Ok(value)
}

fn deserialize_sync_peer_status_offline_duration_ms<'de, D>(deserializer: D) -> Result<Option<i64>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<i64>::deserialize(deserializer)?;
    if let Some(value) = value {
        if value < 0 || value > 9007199254740991 { return Err(serde::de::Error::custom("value outside schema range")); }
    }
    Ok(value)
}

fn serialize_sync_peer_status_offline_duration_ms<S>(value: &Option<i64>, serializer: S) -> Result<S::Ok, S::Error>
where
    S: Serializer,
{
    if let Some(value) = value {
        if *value < 0 || *value > 9007199254740991 { return Err(serde::ser::Error::custom("value outside schema range")); }
    }
    value.serialize(serializer)
}

fn deserialize_sync_peer_status_error_code<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    Ok(value)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SyncCadence {
    #[serde(rename = "nextRoundAt", deserialize_with = "deserialize_sync_cadence_next_round_at")]
    pub next_round_at: Option<String>,
    #[serde(rename = "roundIntervalSeconds", deserialize_with = "deserialize_sync_cadence_round_interval_seconds", serialize_with = "serialize_sync_cadence_round_interval_seconds")]
    pub round_interval_seconds: i64,
}

fn deserialize_sync_cadence_next_round_at<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    Ok(value)
}

fn deserialize_sync_cadence_round_interval_seconds<'de, D>(deserializer: D) -> Result<i64, D::Error>
where
    D: Deserializer<'de>,
{
    let value = i64::deserialize(deserializer)?;
    if value < 0 || value > 9007199254740991 { return Err(serde::de::Error::custom("value outside schema range")); }
    Ok(value)
}

fn serialize_sync_cadence_round_interval_seconds<S>(value: &i64, serializer: S) -> Result<S::Ok, S::Error>
where
    S: Serializer,
{
    if *value < 0 || *value > 9007199254740991 { return Err(serde::ser::Error::custom("value outside schema range")); }
    value.serialize(serializer)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct EnrollmentStatus {
    #[serde(rename = "complete")]
    pub complete: bool,
    #[serde(rename = "joinedTeamId", deserialize_with = "deserialize_enrollment_status_joined_team_id")]
    pub joined_team_id: Option<String>,
    #[serde(rename = "reason", deserialize_with = "deserialize_enrollment_status_reason")]
    pub reason: Option<String>,
}

fn deserialize_enrollment_status_joined_team_id<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    Ok(value)
}

fn deserialize_enrollment_status_reason<'de, D>(deserializer: D) -> Result<Option<String>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<String>::deserialize(deserializer)?;
    Ok(value)
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct LastPeerExchange {
    #[serde(rename = "peerDeviceId")]
    pub peer_device_id: String,
    #[serde(rename = "peerLabel")]
    pub peer_label: String,
    #[serde(rename = "exchangedAt")]
    pub exchanged_at: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SyncRecency {
    #[serde(rename = "basis")]
    pub basis: String,
    #[serde(rename = "currentness")]
    pub currentness: String,
    #[serde(rename = "lastExchange", deserialize_with = "deserialize_sync_recency_last_exchange")]
    pub last_exchange: Option<LastPeerExchange>,
}

fn deserialize_sync_recency_last_exchange<'de, D>(deserializer: D) -> Result<Option<LastPeerExchange>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<LastPeerExchange>::deserialize(deserializer)?;
    Ok(value)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum HarborlineSyncStatusAggregate {
    #[serde(rename = "has")]
    Has,
    #[serde(rename = "will")]
    Will,
    #[serde(rename = "should")]
    Should,
    #[serde(rename = "couldnt")]
    Couldnt,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct HarborlineSyncStatus {
    #[serde(rename = "aggregate")]
    pub aggregate: HarborlineSyncStatusAggregate,
    #[serde(rename = "asOf")]
    pub as_of: String,
    #[serde(rename = "peers")]
    pub peers: Vec<SyncPeerStatus>,
    #[serde(rename = "cadence")]
    pub cadence: SyncCadence,
    #[serde(rename = "recency")]
    pub recency: SyncRecency,
    #[serde(rename = "enrollment", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_harborline_sync_status_enrollment")]
    pub enrollment: Option<EnrollmentStatus>,
    #[serde(rename = "extensions", default, skip_serializing_if = "Option::is_none", deserialize_with = "deserialize_harborline_sync_status_extensions")]
    pub extensions: Option<BTreeMap<String, Value>>,
}

fn deserialize_harborline_sync_status_enrollment<'de, D>(deserializer: D) -> Result<Option<EnrollmentStatus>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<EnrollmentStatus>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

fn deserialize_harborline_sync_status_extensions<'de, D>(deserializer: D) -> Result<Option<BTreeMap<String, Value>>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<BTreeMap<String, Value>>::deserialize(deserializer)?;
    if value.is_none() { return Err(serde::de::Error::custom("value must not be null")); }
    Ok(value)
}

#[allow(async_fn_in_trait)]
pub trait CapabilityPort {
    type Error;
    async fn announce(&self, request: AnnounceRequest) -> Result<AnnounceResult, Self::Error>;
    async fn negotiate(&self, request: NegotiateRequest) -> Result<NegotiateResult, Self::Error>;
    async fn address(&self, request: AddressRequest) -> Result<AddressResult, Self::Error>;
    async fn secure(&self, request: SecureRequest) -> Result<SecureResult, Self::Error>;
    async fn invoke(&self, request: CapabilityInvokeRequest) -> Result<CapabilityResult, Self::Error>;
    async fn observe(&self, request: ObserveRequest) -> Result<HealthReport, Self::Error>;
    async fn resolve(&self, request: ResolveRequest) -> Result<ResolutionResult, Self::Error>;
    async fn compose(&self, request: ComposeRequest) -> Result<ComposeResult, Self::Error>;
}

#[allow(async_fn_in_trait)]
pub trait HarborlineHostPort {
    type Error;
    async fn capability_invoke(&self, request: CapabilityHostInvokeRequest) -> Result<CapabilityResult, Self::Error>;
    async fn capability_health(&self, request: EmptyRequest) -> Result<HealthReport, Self::Error>;
    async fn capability_cp_demo_execute(&self, request: CpDemoRequest) -> Result<CpDemoResult, Self::Error>;
    async fn current_principal(&self, request: EmptyRequest) -> Result<Principal, Self::Error>;
    async fn node_status(&self, request: EmptyRequest) -> Result<NodeStatus, Self::Error>;
    async fn data_location_status(&self, request: EmptyRequest) -> Result<DataLocationStatus, Self::Error>;
    async fn device_capability_profile(&self, request: EmptyRequest) -> Result<DeviceCapabilityProfile, Self::Error>;
    async fn get_peer_sync_config(&self, request: EmptyRequest) -> Result<PeerSyncConfig, Self::Error>;
    async fn set_peer_sync_config(&self, request: PeerSyncConfig) -> Result<EmptyResponse, Self::Error>;
    async fn append_renderer_log(&self, request: RendererLogEntry) -> Result<EmptyResponse, Self::Error>;
}

#[allow(async_fn_in_trait)]
pub trait HarborlineApplicationPort {
    type Error;
    async fn get_sync_status(&self, request: EmptyRequest) -> Result<HarborlineSyncStatus, Self::Error>;
}

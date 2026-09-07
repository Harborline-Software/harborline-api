// GENERATED from packages/contracts/protocol. DO NOT EDIT.
// Protocol shipyard.carrier 2.0.0.
#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Protocol;

public static class HarborlineProtocol
{
    public const string Id = "shipyard.carrier";
    public const string Version = "2.0.0";
    public const string CapabilityAnnounce = "capability.announce";
    public const string CapabilityNegotiate = "capability.negotiate";
    public const string CapabilityAddress = "capability.address";
    public const string CapabilitySecure = "capability.secure";
    public const string CapabilityInvoke = "capability.invoke";
    public const string CapabilityObserve = "capability.observe";
    public const string CapabilityResolve = "capability.resolve";
    public const string CapabilityCompose = "capability.compose";
    public const string CarrierHostCapabilityInvoke = "carrier.host.capabilityInvoke";
    public const string CarrierHostCapabilityHealth = "carrier.host.capabilityHealth";
    public const string CarrierHostCapabilityCpDemoExecute = "carrier.host.capabilityCpDemoExecute";
    public const string CarrierHostCurrentPrincipal = "carrier.host.currentPrincipal";
    public const string CarrierHostNodeStatus = "carrier.host.nodeStatus";
    public const string CarrierHostDataLocationStatus = "carrier.host.dataLocationStatus";
    public const string CarrierHostDeviceCapabilityProfile = "carrier.host.deviceCapabilityProfile";
    public const string CarrierHostGetPeerSyncConfig = "carrier.host.getPeerSyncConfig";
    public const string CarrierHostSetPeerSyncConfig = "carrier.host.setPeerSyncConfig";
    public const string CarrierHostAppendRendererLog = "carrier.host.appendRendererLog";
    public const string CarrierApplicationGetSyncStatus = "carrier.application.getSyncStatus";
}

public static class HarborlineHostCommands
{
    public const string CapabilityInvoke = "capability_invoke";
    public const string CapabilityHealth = "capability_health";
    public const string CapabilityCpDemoExecute = "capability_cp_demo_execute";
    public const string CurrentPrincipal = "current_principal";
    public const string NodeStatus = "node_status";
    public const string DataLocationStatus = "get_data_location_status";
    public const string DeviceCapabilityProfile = "get_device_capability_profile";
    public const string GetPeerSyncConfig = "get_peer_sync_config";
    public const string SetPeerSyncConfig = "set_peer_sync_config";
    public const string AppendRendererLog = "append_renderer_log";
}

public static class HarborlineApplicationRoutes
{
    public const string GetSyncStatus = "/api/local-node/sync-status";
}

[JsonConverter(typeof(EmptyRequestJsonConverter))]
public sealed record EmptyRequest
{
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EmptyRequestWire
{
}

internal sealed class EmptyRequestJsonConverter : JsonConverter<EmptyRequest>
{
    public override EmptyRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateEmptyRequest(value);
        _ = JsonSerializer.Deserialize<EmptyRequestWire>(value.GetRawText(), options) ?? throw new JsonException("EmptyRequest: expected object");
        return new EmptyRequest
        {
        };
    }
    public override void Write(Utf8JsonWriter writer, EmptyRequest value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new EmptyRequestWire
        {
        }, options);
    }

    private static void ValidateEmptyRequest(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("EmptyRequest: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                default:
                    throw new JsonException($"EmptyRequest: unexpected property {property.Name}");
            }
        }
    }
}

[JsonConverter(typeof(EmptyResponseJsonConverter))]
public sealed record EmptyResponse
{
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EmptyResponseWire
{
}

internal sealed class EmptyResponseJsonConverter : JsonConverter<EmptyResponse>
{
    public override EmptyResponse Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateEmptyResponse(value);
        _ = JsonSerializer.Deserialize<EmptyResponseWire>(value.GetRawText(), options) ?? throw new JsonException("EmptyResponse: expected object");
        return new EmptyResponse
        {
        };
    }
    public override void Write(Utf8JsonWriter writer, EmptyResponse value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new EmptyResponseWire
        {
        }, options);
    }

    private static void ValidateEmptyResponse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("EmptyResponse: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                default:
                    throw new JsonException($"EmptyResponse: unexpected property {property.Name}");
            }
        }
    }
}

[JsonConverter(typeof(PrincipalKindJsonConverter))]
public enum PrincipalKind
{
    LocalOsUser = 0,
    Service = 1,
}

public sealed class PrincipalKindJsonConverter : JsonConverter<PrincipalKind>
{
    public override PrincipalKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "local-os-user" => PrincipalKind.LocalOsUser,
        "service" => PrincipalKind.Service,
        _ => throw new JsonException("invalid PrincipalKind"),
    };
    public override void Write(Utf8JsonWriter writer, PrincipalKind value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        PrincipalKind.LocalOsUser => "local-os-user",
        PrincipalKind.Service => "service",
        _ => throw new JsonException("invalid PrincipalKind"),
    });
}

[JsonConverter(typeof(PrincipalJsonConverter))]
public sealed record Principal
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }
    [JsonPropertyName("kind")]
    public required PrincipalKind Kind { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PrincipalWire
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }
    [JsonPropertyName("kind")]
    public required PrincipalKind Kind { get; init; }
}

internal sealed class PrincipalJsonConverter : JsonConverter<Principal>
{
    public override Principal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidatePrincipal(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<PrincipalWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("Principal: expected object");
        return new Principal
        {
            Id = wire.Id,
            DisplayName = wire.DisplayName,
            Kind = wire.Kind,
        };
    }
    public override void Write(Utf8JsonWriter writer, Principal value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new PrincipalWire
        {
            Id = value.Id,
            DisplayName = value.DisplayName,
            Kind = value.Kind,
        }, options);
    }

    private static void ValidatePrincipal(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Principal: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "id":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("Principal.id: value must not be null");
                    break;
                case "displayName":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("Principal.displayName: value must not be null");
                    break;
                case "kind":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("Principal.kind: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase)) throw new JsonException("Principal.id: property name must match exact wire casing");
                    if (string.Equals(property.Name, "displayName", StringComparison.OrdinalIgnoreCase)) throw new JsonException("Principal.displayName: property name must match exact wire casing");
                    if (string.Equals(property.Name, "kind", StringComparison.OrdinalIgnoreCase)) throw new JsonException("Principal.kind: property name must match exact wire casing");
                    throw new JsonException($"Principal: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("id", out _)) throw new JsonException("Principal: missing required property id");
        if (!value.TryGetProperty("displayName", out _)) throw new JsonException("Principal: missing required property displayName");
        if (!value.TryGetProperty("kind", out _)) throw new JsonException("Principal: missing required property kind");
    }
}

[JsonConverter(typeof(ProtocolErrorFaultDomainJsonConverter))]
public enum ProtocolErrorFaultDomain
{
    Input = 0,
    Provider = 1,
    Membrane = 2,
}

public sealed class ProtocolErrorFaultDomainJsonConverter : JsonConverter<ProtocolErrorFaultDomain>
{
    public override ProtocolErrorFaultDomain Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "input" => ProtocolErrorFaultDomain.Input,
        "provider" => ProtocolErrorFaultDomain.Provider,
        "membrane" => ProtocolErrorFaultDomain.Membrane,
        _ => throw new JsonException("invalid ProtocolErrorFaultDomain"),
    };
    public override void Write(Utf8JsonWriter writer, ProtocolErrorFaultDomain value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        ProtocolErrorFaultDomain.Input => "input",
        ProtocolErrorFaultDomain.Provider => "provider",
        ProtocolErrorFaultDomain.Membrane => "membrane",
        _ => throw new JsonException("invalid ProtocolErrorFaultDomain"),
    });
}

[JsonConverter(typeof(ProtocolErrorJsonConverter))]
public sealed record ProtocolError
{
    [JsonPropertyName("faultDomain")]
    public required ProtocolErrorFaultDomain FaultDomain { get; init; }
    [JsonPropertyName("retryable")]
    public required bool Retryable { get; init; }
    [JsonPropertyName("code")]
    public required string Code { get; init; }
    [JsonPropertyName("message")]
    public required string Message { get; init; }
    private long? _retryAfter;
    [JsonPropertyName("retryAfter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RetryAfter
    {
        get => _retryAfter;
        init
        {
            if (value < 0L || value > 9007199254740991L) throw new ArgumentOutOfRangeException(nameof(RetryAfter));
            _retryAfter = value;
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProtocolErrorWire
{
    [JsonPropertyName("faultDomain")]
    public required ProtocolErrorFaultDomain FaultDomain { get; init; }
    [JsonPropertyName("retryable")]
    public required bool Retryable { get; init; }
    [JsonPropertyName("code")]
    public required string Code { get; init; }
    [JsonPropertyName("message")]
    public required string Message { get; init; }
    [JsonPropertyName("retryAfter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RetryAfter { get; init; }
}

internal sealed class ProtocolErrorJsonConverter : JsonConverter<ProtocolError>
{
    public override ProtocolError Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateProtocolError(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<ProtocolErrorWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("ProtocolError: expected object");
        return new ProtocolError
        {
            FaultDomain = wire.FaultDomain,
            Retryable = wire.Retryable,
            Code = wire.Code,
            Message = wire.Message,
            RetryAfter = wire.RetryAfter,
        };
    }
    public override void Write(Utf8JsonWriter writer, ProtocolError value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new ProtocolErrorWire
        {
            FaultDomain = value.FaultDomain,
            Retryable = value.Retryable,
            Code = value.Code,
            Message = value.Message,
            RetryAfter = value.RetryAfter,
        }, options);
    }

    private static void ValidateProtocolError(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("ProtocolError: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "faultDomain":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ProtocolError.faultDomain: value must not be null");
                    break;
                case "retryable":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ProtocolError.retryable: value must not be null");
                    break;
                case "code":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ProtocolError.code: value must not be null");
                    break;
                case "message":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ProtocolError.message: value must not be null");
                    break;
                case "retryAfter":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ProtocolError.retryAfter: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "faultDomain", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ProtocolError.faultDomain: property name must match exact wire casing");
                    if (string.Equals(property.Name, "retryable", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ProtocolError.retryable: property name must match exact wire casing");
                    if (string.Equals(property.Name, "code", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ProtocolError.code: property name must match exact wire casing");
                    if (string.Equals(property.Name, "message", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ProtocolError.message: property name must match exact wire casing");
                    if (string.Equals(property.Name, "retryAfter", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ProtocolError.retryAfter: property name must match exact wire casing");
                    throw new JsonException($"ProtocolError: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("faultDomain", out _)) throw new JsonException("ProtocolError: missing required property faultDomain");
        if (!value.TryGetProperty("retryable", out _)) throw new JsonException("ProtocolError: missing required property retryable");
        if (!value.TryGetProperty("code", out _)) throw new JsonException("ProtocolError: missing required property code");
        if (!value.TryGetProperty("message", out _)) throw new JsonException("ProtocolError: missing required property message");
    }
}

[JsonConverter(typeof(UsageTierJsonConverter))]
public enum UsageTier
{
    Local = 0,
    Remote = 1,
    Cloud = 2,
}

public sealed class UsageTierJsonConverter : JsonConverter<UsageTier>
{
    public override UsageTier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "local" => UsageTier.Local,
        "remote" => UsageTier.Remote,
        "cloud" => UsageTier.Cloud,
        _ => throw new JsonException("invalid UsageTier"),
    };
    public override void Write(Utf8JsonWriter writer, UsageTier value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        UsageTier.Local => "local",
        UsageTier.Remote => "remote",
        UsageTier.Cloud => "cloud",
        _ => throw new JsonException("invalid UsageTier"),
    });
}

[JsonConverter(typeof(UsageJsonConverter))]
public sealed record Usage
{
    [JsonPropertyName("unit")]
    public required string Unit { get; init; }
    [JsonPropertyName("quantity")]
    public required double Quantity { get; init; }
    private long? _costMicros;
    [JsonPropertyName("costMicros")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CostMicros
    {
        get => _costMicros;
        init
        {
            if (value < 0L || value > 9007199254740991L) throw new ArgumentOutOfRangeException(nameof(CostMicros));
            _costMicros = value;
        }
    }
    [JsonPropertyName("tier")]
    public required UsageTier Tier { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UsageWire
{
    [JsonPropertyName("unit")]
    public required string Unit { get; init; }
    [JsonPropertyName("quantity")]
    public required double Quantity { get; init; }
    [JsonPropertyName("costMicros")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CostMicros { get; init; }
    [JsonPropertyName("tier")]
    public required UsageTier Tier { get; init; }
}

internal sealed class UsageJsonConverter : JsonConverter<Usage>
{
    public override Usage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateUsage(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<UsageWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("Usage: expected object");
        return new Usage
        {
            Unit = wire.Unit,
            Quantity = wire.Quantity,
            CostMicros = wire.CostMicros,
            Tier = wire.Tier,
        };
    }
    public override void Write(Utf8JsonWriter writer, Usage value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new UsageWire
        {
            Unit = value.Unit,
            Quantity = value.Quantity,
            CostMicros = value.CostMicros,
            Tier = value.Tier,
        }, options);
    }

    private static void ValidateUsage(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Usage: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "unit":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("Usage.unit: value must not be null");
                    break;
                case "quantity":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("Usage.quantity: value must not be null");
                    break;
                case "costMicros":
                    break;
                case "tier":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("Usage.tier: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "unit", StringComparison.OrdinalIgnoreCase)) throw new JsonException("Usage.unit: property name must match exact wire casing");
                    if (string.Equals(property.Name, "quantity", StringComparison.OrdinalIgnoreCase)) throw new JsonException("Usage.quantity: property name must match exact wire casing");
                    if (string.Equals(property.Name, "costMicros", StringComparison.OrdinalIgnoreCase)) throw new JsonException("Usage.costMicros: property name must match exact wire casing");
                    if (string.Equals(property.Name, "tier", StringComparison.OrdinalIgnoreCase)) throw new JsonException("Usage.tier: property name must match exact wire casing");
                    throw new JsonException($"Usage: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("unit", out _)) throw new JsonException("Usage: missing required property unit");
        if (!value.TryGetProperty("quantity", out _)) throw new JsonException("Usage: missing required property quantity");
        if (!value.TryGetProperty("tier", out _)) throw new JsonException("Usage: missing required property tier");
    }
}

[JsonConverter(typeof(ArtifactJsonConverter))]
public sealed record Artifact
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonPropertyName("uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uri { get; init; }
    [JsonPropertyName("mime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mime { get; init; }
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

internal sealed record ArtifactWire
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonPropertyName("uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uri { get; init; }
    [JsonPropertyName("mime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mime { get; init; }
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

internal sealed class ArtifactJsonConverter : JsonConverter<Artifact>
{
    public override Artifact Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateArtifact(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<ArtifactWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("Artifact: expected object");
        return new Artifact
        {
            Kind = wire.Kind,
            Uri = wire.Uri,
            Mime = wire.Mime,
            Text = wire.Text,
            AdditionalProperties = wire.AdditionalProperties,
        };
    }
    public override void Write(Utf8JsonWriter writer, Artifact value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new ArtifactWire
        {
            Kind = value.Kind,
            Uri = value.Uri,
            Mime = value.Mime,
            Text = value.Text,
            AdditionalProperties = value.AdditionalProperties,
        }, options);
    }

    private static void ValidateArtifact(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Artifact: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "kind":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("Artifact.kind: value must not be null");
                    break;
                case "uri":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("Artifact.uri: value must not be null");
                    break;
                case "mime":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("Artifact.mime: value must not be null");
                    break;
                case "text":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("Artifact.text: value must not be null");
                    break;
                default:
                    break;
            }
        }
        if (!value.TryGetProperty("kind", out _)) throw new JsonException("Artifact: missing required property kind");
    }
}

[JsonConverter(typeof(CapabilityResultStatusJsonConverter))]
public enum CapabilityResultStatus
{
    Accepted = 0,
    Running = 1,
    Succeeded = 2,
    Partial = 3,
    Failed = 4,
}

public sealed class CapabilityResultStatusJsonConverter : JsonConverter<CapabilityResultStatus>
{
    public override CapabilityResultStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "accepted" => CapabilityResultStatus.Accepted,
        "running" => CapabilityResultStatus.Running,
        "succeeded" => CapabilityResultStatus.Succeeded,
        "partial" => CapabilityResultStatus.Partial,
        "failed" => CapabilityResultStatus.Failed,
        _ => throw new JsonException("invalid CapabilityResultStatus"),
    };
    public override void Write(Utf8JsonWriter writer, CapabilityResultStatus value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        CapabilityResultStatus.Accepted => "accepted",
        CapabilityResultStatus.Running => "running",
        CapabilityResultStatus.Succeeded => "succeeded",
        CapabilityResultStatus.Partial => "partial",
        CapabilityResultStatus.Failed => "failed",
        _ => throw new JsonException("invalid CapabilityResultStatus"),
    });
}

[JsonConverter(typeof(CapabilityResultJsonConverter))]
public sealed record CapabilityResult
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }
    [JsonPropertyName("status")]
    public required CapabilityResultStatus Status { get; init; }
    private double _progress;
    [JsonPropertyName("progress")]
    public required double Progress
    {
        get => _progress;
        init
        {
            if (value < 0d || value > 1d) throw new ArgumentOutOfRangeException(nameof(Progress));
            _progress = value;
        }
    }
    [JsonPropertyName("artifacts")]
    public required IReadOnlyList<Artifact> Artifacts { get; init; }
    [JsonPropertyName("usage")]
    public required Usage Usage { get; init; }
    [JsonPropertyName("error")]
    public required ProtocolError? Error { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CapabilityResultWire
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }
    [JsonPropertyName("status")]
    public required CapabilityResultStatus Status { get; init; }
    [JsonPropertyName("progress")]
    public required double Progress { get; init; }
    [JsonPropertyName("artifacts")]
    public required IReadOnlyList<Artifact> Artifacts { get; init; }
    [JsonPropertyName("usage")]
    public required Usage Usage { get; init; }
    [JsonPropertyName("error")]
    public required ProtocolError? Error { get; init; }
}

internal sealed class CapabilityResultJsonConverter : JsonConverter<CapabilityResult>
{
    public override CapabilityResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateCapabilityResult(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<CapabilityResultWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("CapabilityResult: expected object");
        return new CapabilityResult
        {
            JobId = wire.JobId,
            Status = wire.Status,
            Progress = wire.Progress,
            Artifacts = wire.Artifacts,
            Usage = wire.Usage,
            Error = wire.Error,
        };
    }
    public override void Write(Utf8JsonWriter writer, CapabilityResult value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new CapabilityResultWire
        {
            JobId = value.JobId,
            Status = value.Status,
            Progress = value.Progress,
            Artifacts = value.Artifacts,
            Usage = value.Usage,
            Error = value.Error,
        }, options);
    }

    private static void ValidateCapabilityResult(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("CapabilityResult: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "jobId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CapabilityResult.jobId: value must not be null");
                    break;
                case "status":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CapabilityResult.status: value must not be null");
                    break;
                case "progress":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CapabilityResult.progress: value must not be null");
                    break;
                case "artifacts":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CapabilityResult.artifacts: value must not be null");
                    break;
                case "usage":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CapabilityResult.usage: value must not be null");
                    break;
                case "error":
                    break;
                default:
                    if (string.Equals(property.Name, "jobId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CapabilityResult.jobId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "status", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CapabilityResult.status: property name must match exact wire casing");
                    if (string.Equals(property.Name, "progress", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CapabilityResult.progress: property name must match exact wire casing");
                    if (string.Equals(property.Name, "artifacts", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CapabilityResult.artifacts: property name must match exact wire casing");
                    if (string.Equals(property.Name, "usage", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CapabilityResult.usage: property name must match exact wire casing");
                    if (string.Equals(property.Name, "error", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CapabilityResult.error: property name must match exact wire casing");
                    throw new JsonException($"CapabilityResult: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("jobId", out _)) throw new JsonException("CapabilityResult: missing required property jobId");
        if (!value.TryGetProperty("status", out _)) throw new JsonException("CapabilityResult: missing required property status");
        if (!value.TryGetProperty("progress", out _)) throw new JsonException("CapabilityResult: missing required property progress");
        if (!value.TryGetProperty("artifacts", out _)) throw new JsonException("CapabilityResult: missing required property artifacts");
        if (!value.TryGetProperty("usage", out _)) throw new JsonException("CapabilityResult: missing required property usage");
        if (!value.TryGetProperty("error", out _)) throw new JsonException("CapabilityResult: missing required property error");
    }
}

[JsonConverter(typeof(RuntimeHealthStateJsonConverter))]
public enum RuntimeHealthState
{
    Up = 0,
    Degraded = 1,
    Down = 2,
}

public sealed class RuntimeHealthStateJsonConverter : JsonConverter<RuntimeHealthState>
{
    public override RuntimeHealthState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "up" => RuntimeHealthState.Up,
        "degraded" => RuntimeHealthState.Degraded,
        "down" => RuntimeHealthState.Down,
        _ => throw new JsonException("invalid RuntimeHealthState"),
    };
    public override void Write(Utf8JsonWriter writer, RuntimeHealthState value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        RuntimeHealthState.Up => "up",
        RuntimeHealthState.Degraded => "degraded",
        RuntimeHealthState.Down => "down",
        _ => throw new JsonException("invalid RuntimeHealthState"),
    });
}

[JsonConverter(typeof(RuntimeHealthJsonConverter))]
public sealed record RuntimeHealth
{
    [JsonPropertyName("runtimeId")]
    public required string RuntimeId { get; init; }
    [JsonPropertyName("capability")]
    public required string Capability { get; init; }
    [JsonPropertyName("state")]
    public required RuntimeHealthState State { get; init; }
    [JsonPropertyName("detail")]
    public required string? Detail { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RuntimeHealthWire
{
    [JsonPropertyName("runtimeId")]
    public required string RuntimeId { get; init; }
    [JsonPropertyName("capability")]
    public required string Capability { get; init; }
    [JsonPropertyName("state")]
    public required RuntimeHealthState State { get; init; }
    [JsonPropertyName("detail")]
    public required string? Detail { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

internal sealed class RuntimeHealthJsonConverter : JsonConverter<RuntimeHealth>
{
    public override RuntimeHealth Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateRuntimeHealth(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<RuntimeHealthWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("RuntimeHealth: expected object");
        return new RuntimeHealth
        {
            RuntimeId = wire.RuntimeId,
            Capability = wire.Capability,
            State = wire.State,
            Detail = wire.Detail,
            Extensions = wire.Extensions,
        };
    }
    public override void Write(Utf8JsonWriter writer, RuntimeHealth value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new RuntimeHealthWire
        {
            RuntimeId = value.RuntimeId,
            Capability = value.Capability,
            State = value.State,
            Detail = value.Detail,
            Extensions = value.Extensions,
        }, options);
    }

    private static void ValidateRuntimeHealth(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("RuntimeHealth: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "runtimeId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("RuntimeHealth.runtimeId: value must not be null");
                    break;
                case "capability":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("RuntimeHealth.capability: value must not be null");
                    break;
                case "state":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("RuntimeHealth.state: value must not be null");
                    break;
                case "detail":
                    break;
                case "extensions":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("RuntimeHealth.extensions: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "runtimeId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("RuntimeHealth.runtimeId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "capability", StringComparison.OrdinalIgnoreCase)) throw new JsonException("RuntimeHealth.capability: property name must match exact wire casing");
                    if (string.Equals(property.Name, "state", StringComparison.OrdinalIgnoreCase)) throw new JsonException("RuntimeHealth.state: property name must match exact wire casing");
                    if (string.Equals(property.Name, "detail", StringComparison.OrdinalIgnoreCase)) throw new JsonException("RuntimeHealth.detail: property name must match exact wire casing");
                    if (string.Equals(property.Name, "extensions", StringComparison.OrdinalIgnoreCase)) throw new JsonException("RuntimeHealth.extensions: property name must match exact wire casing");
                    throw new JsonException($"RuntimeHealth: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("runtimeId", out _)) throw new JsonException("RuntimeHealth: missing required property runtimeId");
        if (!value.TryGetProperty("capability", out _)) throw new JsonException("RuntimeHealth: missing required property capability");
        if (!value.TryGetProperty("state", out _)) throw new JsonException("RuntimeHealth: missing required property state");
        if (!value.TryGetProperty("detail", out _)) throw new JsonException("RuntimeHealth: missing required property detail");
    }
}

[JsonConverter(typeof(HealthReportJsonConverter))]
public sealed record HealthReport
{
    [JsonPropertyName("runtimes")]
    public required IReadOnlyList<RuntimeHealth> Runtimes { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HealthReportWire
{
    [JsonPropertyName("runtimes")]
    public required IReadOnlyList<RuntimeHealth> Runtimes { get; init; }
}

internal sealed class HealthReportJsonConverter : JsonConverter<HealthReport>
{
    public override HealthReport Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateHealthReport(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<HealthReportWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("HealthReport: expected object");
        return new HealthReport
        {
            Runtimes = wire.Runtimes,
        };
    }
    public override void Write(Utf8JsonWriter writer, HealthReport value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new HealthReportWire
        {
            Runtimes = value.Runtimes,
        }, options);
    }

    private static void ValidateHealthReport(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("HealthReport: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "runtimes":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("HealthReport.runtimes: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "runtimes", StringComparison.OrdinalIgnoreCase)) throw new JsonException("HealthReport.runtimes: property name must match exact wire casing");
                    throw new JsonException($"HealthReport: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("runtimes", out _)) throw new JsonException("HealthReport: missing required property runtimes");
    }
}

[JsonConverter(typeof(ProviderDescriptorJsonConverter))]
public sealed record ProviderDescriptor
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

internal sealed record ProviderDescriptorWire
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

internal sealed class ProviderDescriptorJsonConverter : JsonConverter<ProviderDescriptor>
{
    public override ProviderDescriptor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateProviderDescriptor(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<ProviderDescriptorWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("ProviderDescriptor: expected object");
        return new ProviderDescriptor
        {
            Id = wire.Id,
            AdditionalProperties = wire.AdditionalProperties,
        };
    }
    public override void Write(Utf8JsonWriter writer, ProviderDescriptor value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new ProviderDescriptorWire
        {
            Id = value.Id,
            AdditionalProperties = value.AdditionalProperties,
        }, options);
    }

    private static void ValidateProviderDescriptor(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("ProviderDescriptor: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "id":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ProviderDescriptor.id: value must not be null");
                    break;
                default:
                    break;
            }
        }
        if (!value.TryGetProperty("id", out _)) throw new JsonException("ProviderDescriptor: missing required property id");
    }
}

[JsonConverter(typeof(RuntimeCapabilityJsonConverter))]
public sealed record RuntimeCapability
{
    [JsonPropertyName("capabilityId")]
    public required string CapabilityId { get; init; }
    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }
    [JsonPropertyName("providers")]
    public required IReadOnlyList<ProviderDescriptor> Providers { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RuntimeCapabilityWire
{
    [JsonPropertyName("capabilityId")]
    public required string CapabilityId { get; init; }
    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }
    [JsonPropertyName("providers")]
    public required IReadOnlyList<ProviderDescriptor> Providers { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

internal sealed class RuntimeCapabilityJsonConverter : JsonConverter<RuntimeCapability>
{
    public override RuntimeCapability Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateRuntimeCapability(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<RuntimeCapabilityWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("RuntimeCapability: expected object");
        return new RuntimeCapability
        {
            CapabilityId = wire.CapabilityId,
            SchemaVersion = wire.SchemaVersion,
            Providers = wire.Providers,
            Extensions = wire.Extensions,
        };
    }
    public override void Write(Utf8JsonWriter writer, RuntimeCapability value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new RuntimeCapabilityWire
        {
            CapabilityId = value.CapabilityId,
            SchemaVersion = value.SchemaVersion,
            Providers = value.Providers,
            Extensions = value.Extensions,
        }, options);
    }

    private static void ValidateRuntimeCapability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("RuntimeCapability: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "capabilityId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("RuntimeCapability.capabilityId: value must not be null");
                    break;
                case "schemaVersion":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("RuntimeCapability.schemaVersion: value must not be null");
                    break;
                case "providers":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("RuntimeCapability.providers: value must not be null");
                    break;
                case "extensions":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("RuntimeCapability.extensions: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "capabilityId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("RuntimeCapability.capabilityId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "schemaVersion", StringComparison.OrdinalIgnoreCase)) throw new JsonException("RuntimeCapability.schemaVersion: property name must match exact wire casing");
                    if (string.Equals(property.Name, "providers", StringComparison.OrdinalIgnoreCase)) throw new JsonException("RuntimeCapability.providers: property name must match exact wire casing");
                    if (string.Equals(property.Name, "extensions", StringComparison.OrdinalIgnoreCase)) throw new JsonException("RuntimeCapability.extensions: property name must match exact wire casing");
                    throw new JsonException($"RuntimeCapability: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("capabilityId", out _)) throw new JsonException("RuntimeCapability: missing required property capabilityId");
        if (!value.TryGetProperty("schemaVersion", out _)) throw new JsonException("RuntimeCapability: missing required property schemaVersion");
        if (!value.TryGetProperty("providers", out _)) throw new JsonException("RuntimeCapability: missing required property providers");
    }
}

[JsonConverter(typeof(AnnounceRequestJsonConverter))]
public sealed record AnnounceRequest
{
    [JsonPropertyName("runtimeId")]
    public required string RuntimeId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AnnounceRequestWire
{
    [JsonPropertyName("runtimeId")]
    public required string RuntimeId { get; init; }
}

internal sealed class AnnounceRequestJsonConverter : JsonConverter<AnnounceRequest>
{
    public override AnnounceRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateAnnounceRequest(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<AnnounceRequestWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("AnnounceRequest: expected object");
        return new AnnounceRequest
        {
            RuntimeId = wire.RuntimeId,
        };
    }
    public override void Write(Utf8JsonWriter writer, AnnounceRequest value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new AnnounceRequestWire
        {
            RuntimeId = value.RuntimeId,
        }, options);
    }

    private static void ValidateAnnounceRequest(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("AnnounceRequest: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "runtimeId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("AnnounceRequest.runtimeId: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "runtimeId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("AnnounceRequest.runtimeId: property name must match exact wire casing");
                    throw new JsonException($"AnnounceRequest: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("runtimeId", out _)) throw new JsonException("AnnounceRequest: missing required property runtimeId");
    }
}

[JsonConverter(typeof(AnnounceResultJsonConverter))]
public sealed record AnnounceResult
{
    [JsonPropertyName("runtimeId")]
    public required string RuntimeId { get; init; }
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    [JsonPropertyName("contractVersion")]
    public required string ContractVersion { get; init; }
    [JsonPropertyName("capabilities")]
    public required IReadOnlyList<RuntimeCapability> Capabilities { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AnnounceResultWire
{
    [JsonPropertyName("runtimeId")]
    public required string RuntimeId { get; init; }
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    [JsonPropertyName("contractVersion")]
    public required string ContractVersion { get; init; }
    [JsonPropertyName("capabilities")]
    public required IReadOnlyList<RuntimeCapability> Capabilities { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

internal sealed class AnnounceResultJsonConverter : JsonConverter<AnnounceResult>
{
    public override AnnounceResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateAnnounceResult(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<AnnounceResultWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("AnnounceResult: expected object");
        return new AnnounceResult
        {
            RuntimeId = wire.RuntimeId,
            Name = wire.Name,
            ContractVersion = wire.ContractVersion,
            Capabilities = wire.Capabilities,
            Extensions = wire.Extensions,
        };
    }
    public override void Write(Utf8JsonWriter writer, AnnounceResult value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new AnnounceResultWire
        {
            RuntimeId = value.RuntimeId,
            Name = value.Name,
            ContractVersion = value.ContractVersion,
            Capabilities = value.Capabilities,
            Extensions = value.Extensions,
        }, options);
    }

    private static void ValidateAnnounceResult(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("AnnounceResult: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "runtimeId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("AnnounceResult.runtimeId: value must not be null");
                    break;
                case "name":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("AnnounceResult.name: value must not be null");
                    break;
                case "contractVersion":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("AnnounceResult.contractVersion: value must not be null");
                    break;
                case "capabilities":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("AnnounceResult.capabilities: value must not be null");
                    break;
                case "extensions":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("AnnounceResult.extensions: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "runtimeId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("AnnounceResult.runtimeId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "name", StringComparison.OrdinalIgnoreCase)) throw new JsonException("AnnounceResult.name: property name must match exact wire casing");
                    if (string.Equals(property.Name, "contractVersion", StringComparison.OrdinalIgnoreCase)) throw new JsonException("AnnounceResult.contractVersion: property name must match exact wire casing");
                    if (string.Equals(property.Name, "capabilities", StringComparison.OrdinalIgnoreCase)) throw new JsonException("AnnounceResult.capabilities: property name must match exact wire casing");
                    if (string.Equals(property.Name, "extensions", StringComparison.OrdinalIgnoreCase)) throw new JsonException("AnnounceResult.extensions: property name must match exact wire casing");
                    throw new JsonException($"AnnounceResult: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("runtimeId", out _)) throw new JsonException("AnnounceResult: missing required property runtimeId");
        if (!value.TryGetProperty("name", out _)) throw new JsonException("AnnounceResult: missing required property name");
        if (!value.TryGetProperty("contractVersion", out _)) throw new JsonException("AnnounceResult: missing required property contractVersion");
        if (!value.TryGetProperty("capabilities", out _)) throw new JsonException("AnnounceResult: missing required property capabilities");
    }
}

[JsonConverter(typeof(NegotiateRequestJsonConverter))]
public sealed record NegotiateRequest
{
    [JsonPropertyName("runtimeId")]
    public required string RuntimeId { get; init; }
    [JsonPropertyName("contractVersion")]
    public required string ContractVersion { get; init; }
    [JsonPropertyName("capabilitySchemaVersions")]
    public required IReadOnlyDictionary<string, string> CapabilitySchemaVersions { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record NegotiateRequestWire
{
    [JsonPropertyName("runtimeId")]
    public required string RuntimeId { get; init; }
    [JsonPropertyName("contractVersion")]
    public required string ContractVersion { get; init; }
    [JsonPropertyName("capabilitySchemaVersions")]
    public required IReadOnlyDictionary<string, string> CapabilitySchemaVersions { get; init; }
}

internal sealed class NegotiateRequestJsonConverter : JsonConverter<NegotiateRequest>
{
    public override NegotiateRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateNegotiateRequest(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<NegotiateRequestWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("NegotiateRequest: expected object");
        return new NegotiateRequest
        {
            RuntimeId = wire.RuntimeId,
            ContractVersion = wire.ContractVersion,
            CapabilitySchemaVersions = wire.CapabilitySchemaVersions,
        };
    }
    public override void Write(Utf8JsonWriter writer, NegotiateRequest value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new NegotiateRequestWire
        {
            RuntimeId = value.RuntimeId,
            ContractVersion = value.ContractVersion,
            CapabilitySchemaVersions = value.CapabilitySchemaVersions,
        }, options);
    }

    private static void ValidateNegotiateRequest(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("NegotiateRequest: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "runtimeId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("NegotiateRequest.runtimeId: value must not be null");
                    break;
                case "contractVersion":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("NegotiateRequest.contractVersion: value must not be null");
                    break;
                case "capabilitySchemaVersions":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("NegotiateRequest.capabilitySchemaVersions: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "runtimeId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("NegotiateRequest.runtimeId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "contractVersion", StringComparison.OrdinalIgnoreCase)) throw new JsonException("NegotiateRequest.contractVersion: property name must match exact wire casing");
                    if (string.Equals(property.Name, "capabilitySchemaVersions", StringComparison.OrdinalIgnoreCase)) throw new JsonException("NegotiateRequest.capabilitySchemaVersions: property name must match exact wire casing");
                    throw new JsonException($"NegotiateRequest: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("runtimeId", out _)) throw new JsonException("NegotiateRequest: missing required property runtimeId");
        if (!value.TryGetProperty("contractVersion", out _)) throw new JsonException("NegotiateRequest: missing required property contractVersion");
        if (!value.TryGetProperty("capabilitySchemaVersions", out _)) throw new JsonException("NegotiateRequest: missing required property capabilitySchemaVersions");
    }
}

[JsonConverter(typeof(NegotiateResultJsonConverter))]
public sealed record NegotiateResult
{
    [JsonPropertyName("compatible")]
    public required bool Compatible { get; init; }
    [JsonPropertyName("agreedContractVersion")]
    public required string AgreedContractVersion { get; init; }
    [JsonPropertyName("acceptedCapabilities")]
    public required IReadOnlyList<string> AcceptedCapabilities { get; init; }
    [JsonPropertyName("reason")]
    public required string? Reason { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record NegotiateResultWire
{
    [JsonPropertyName("compatible")]
    public required bool Compatible { get; init; }
    [JsonPropertyName("agreedContractVersion")]
    public required string AgreedContractVersion { get; init; }
    [JsonPropertyName("acceptedCapabilities")]
    public required IReadOnlyList<string> AcceptedCapabilities { get; init; }
    [JsonPropertyName("reason")]
    public required string? Reason { get; init; }
}

internal sealed class NegotiateResultJsonConverter : JsonConverter<NegotiateResult>
{
    public override NegotiateResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateNegotiateResult(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<NegotiateResultWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("NegotiateResult: expected object");
        return new NegotiateResult
        {
            Compatible = wire.Compatible,
            AgreedContractVersion = wire.AgreedContractVersion,
            AcceptedCapabilities = wire.AcceptedCapabilities,
            Reason = wire.Reason,
        };
    }
    public override void Write(Utf8JsonWriter writer, NegotiateResult value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new NegotiateResultWire
        {
            Compatible = value.Compatible,
            AgreedContractVersion = value.AgreedContractVersion,
            AcceptedCapabilities = value.AcceptedCapabilities,
            Reason = value.Reason,
        }, options);
    }

    private static void ValidateNegotiateResult(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("NegotiateResult: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "compatible":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("NegotiateResult.compatible: value must not be null");
                    break;
                case "agreedContractVersion":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("NegotiateResult.agreedContractVersion: value must not be null");
                    break;
                case "acceptedCapabilities":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("NegotiateResult.acceptedCapabilities: value must not be null");
                    break;
                case "reason":
                    break;
                default:
                    if (string.Equals(property.Name, "compatible", StringComparison.OrdinalIgnoreCase)) throw new JsonException("NegotiateResult.compatible: property name must match exact wire casing");
                    if (string.Equals(property.Name, "agreedContractVersion", StringComparison.OrdinalIgnoreCase)) throw new JsonException("NegotiateResult.agreedContractVersion: property name must match exact wire casing");
                    if (string.Equals(property.Name, "acceptedCapabilities", StringComparison.OrdinalIgnoreCase)) throw new JsonException("NegotiateResult.acceptedCapabilities: property name must match exact wire casing");
                    if (string.Equals(property.Name, "reason", StringComparison.OrdinalIgnoreCase)) throw new JsonException("NegotiateResult.reason: property name must match exact wire casing");
                    throw new JsonException($"NegotiateResult: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("compatible", out _)) throw new JsonException("NegotiateResult: missing required property compatible");
        if (!value.TryGetProperty("agreedContractVersion", out _)) throw new JsonException("NegotiateResult: missing required property agreedContractVersion");
        if (!value.TryGetProperty("acceptedCapabilities", out _)) throw new JsonException("NegotiateResult: missing required property acceptedCapabilities");
        if (!value.TryGetProperty("reason", out _)) throw new JsonException("NegotiateResult: missing required property reason");
    }
}

[JsonConverter(typeof(AddressRequestJsonConverter))]
public sealed record AddressRequest
{
    [JsonPropertyName("runtimeId")]
    public required string RuntimeId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AddressRequestWire
{
    [JsonPropertyName("runtimeId")]
    public required string RuntimeId { get; init; }
}

internal sealed class AddressRequestJsonConverter : JsonConverter<AddressRequest>
{
    public override AddressRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateAddressRequest(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<AddressRequestWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("AddressRequest: expected object");
        return new AddressRequest
        {
            RuntimeId = wire.RuntimeId,
        };
    }
    public override void Write(Utf8JsonWriter writer, AddressRequest value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new AddressRequestWire
        {
            RuntimeId = value.RuntimeId,
        }, options);
    }

    private static void ValidateAddressRequest(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("AddressRequest: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "runtimeId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("AddressRequest.runtimeId: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "runtimeId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("AddressRequest.runtimeId: property name must match exact wire casing");
                    throw new JsonException($"AddressRequest: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("runtimeId", out _)) throw new JsonException("AddressRequest: missing required property runtimeId");
    }
}

[JsonConverter(typeof(AddressResultModeJsonConverter))]
public enum AddressResultMode
{
    InProcess = 0,
    LocalSubprocess = 1,
    RemoteMesh = 2,
    Relay = 3,
}

public sealed class AddressResultModeJsonConverter : JsonConverter<AddressResultMode>
{
    public override AddressResultMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "in-process" => AddressResultMode.InProcess,
        "local-subprocess" => AddressResultMode.LocalSubprocess,
        "remote-mesh" => AddressResultMode.RemoteMesh,
        "relay" => AddressResultMode.Relay,
        _ => throw new JsonException("invalid AddressResultMode"),
    };
    public override void Write(Utf8JsonWriter writer, AddressResultMode value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        AddressResultMode.InProcess => "in-process",
        AddressResultMode.LocalSubprocess => "local-subprocess",
        AddressResultMode.RemoteMesh => "remote-mesh",
        AddressResultMode.Relay => "relay",
        _ => throw new JsonException("invalid AddressResultMode"),
    });
}

[JsonConverter(typeof(AddressResultJsonConverter))]
public sealed record AddressResult
{
    [JsonPropertyName("runtimeId")]
    public required string RuntimeId { get; init; }
    [JsonPropertyName("mode")]
    public required AddressResultMode Mode { get; init; }
    [JsonPropertyName("baseUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseUrl { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AddressResultWire
{
    [JsonPropertyName("runtimeId")]
    public required string RuntimeId { get; init; }
    [JsonPropertyName("mode")]
    public required AddressResultMode Mode { get; init; }
    [JsonPropertyName("baseUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseUrl { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

internal sealed class AddressResultJsonConverter : JsonConverter<AddressResult>
{
    public override AddressResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateAddressResult(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<AddressResultWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("AddressResult: expected object");
        return new AddressResult
        {
            RuntimeId = wire.RuntimeId,
            Mode = wire.Mode,
            BaseUrl = wire.BaseUrl,
            Extensions = wire.Extensions,
        };
    }
    public override void Write(Utf8JsonWriter writer, AddressResult value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new AddressResultWire
        {
            RuntimeId = value.RuntimeId,
            Mode = value.Mode,
            BaseUrl = value.BaseUrl,
            Extensions = value.Extensions,
        }, options);
    }

    private static void ValidateAddressResult(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("AddressResult: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "runtimeId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("AddressResult.runtimeId: value must not be null");
                    break;
                case "mode":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("AddressResult.mode: value must not be null");
                    break;
                case "baseUrl":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("AddressResult.baseUrl: value must not be null");
                    break;
                case "extensions":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("AddressResult.extensions: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "runtimeId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("AddressResult.runtimeId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "mode", StringComparison.OrdinalIgnoreCase)) throw new JsonException("AddressResult.mode: property name must match exact wire casing");
                    if (string.Equals(property.Name, "baseUrl", StringComparison.OrdinalIgnoreCase)) throw new JsonException("AddressResult.baseUrl: property name must match exact wire casing");
                    if (string.Equals(property.Name, "extensions", StringComparison.OrdinalIgnoreCase)) throw new JsonException("AddressResult.extensions: property name must match exact wire casing");
                    throw new JsonException($"AddressResult: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("runtimeId", out _)) throw new JsonException("AddressResult: missing required property runtimeId");
        if (!value.TryGetProperty("mode", out _)) throw new JsonException("AddressResult: missing required property mode");
    }
}

[JsonConverter(typeof(SecureRequestJsonConverter))]
public sealed record SecureRequest
{
    [JsonPropertyName("operationId")]
    public required string OperationId { get; init; }
    [JsonPropertyName("capabilityId")]
    public required string CapabilityId { get; init; }
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SecureRequestWire
{
    [JsonPropertyName("operationId")]
    public required string OperationId { get; init; }
    [JsonPropertyName("capabilityId")]
    public required string CapabilityId { get; init; }
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }
}

internal sealed class SecureRequestJsonConverter : JsonConverter<SecureRequest>
{
    public override SecureRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateSecureRequest(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<SecureRequestWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("SecureRequest: expected object");
        return new SecureRequest
        {
            OperationId = wire.OperationId,
            CapabilityId = wire.CapabilityId,
            CorrelationId = wire.CorrelationId,
        };
    }
    public override void Write(Utf8JsonWriter writer, SecureRequest value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new SecureRequestWire
        {
            OperationId = value.OperationId,
            CapabilityId = value.CapabilityId,
            CorrelationId = value.CorrelationId,
        }, options);
    }

    private static void ValidateSecureRequest(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("SecureRequest: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "operationId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("SecureRequest.operationId: value must not be null");
                    break;
                case "capabilityId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("SecureRequest.capabilityId: value must not be null");
                    break;
                case "correlationId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("SecureRequest.correlationId: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "operationId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SecureRequest.operationId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "capabilityId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SecureRequest.capabilityId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "correlationId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SecureRequest.correlationId: property name must match exact wire casing");
                    throw new JsonException($"SecureRequest: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("operationId", out _)) throw new JsonException("SecureRequest: missing required property operationId");
        if (!value.TryGetProperty("capabilityId", out _)) throw new JsonException("SecureRequest: missing required property capabilityId");
        if (!value.TryGetProperty("correlationId", out _)) throw new JsonException("SecureRequest: missing required property correlationId");
    }
}

[JsonConverter(typeof(SecureResultAuthorityJsonConverter))]
public enum SecureResultAuthority
{
    AP = 0,
    CP = 1,
}

public sealed class SecureResultAuthorityJsonConverter : JsonConverter<SecureResultAuthority>
{
    public override SecureResultAuthority Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "AP" => SecureResultAuthority.AP,
        "CP" => SecureResultAuthority.CP,
        _ => throw new JsonException("invalid SecureResultAuthority"),
    };
    public override void Write(Utf8JsonWriter writer, SecureResultAuthority value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        SecureResultAuthority.AP => "AP",
        SecureResultAuthority.CP => "CP",
        _ => throw new JsonException("invalid SecureResultAuthority"),
    });
}

[JsonConverter(typeof(SecureResultJsonConverter))]
public sealed record SecureResult
{
    [JsonPropertyName("allowed")]
    public required bool Allowed { get; init; }
    [JsonPropertyName("authority")]
    public required SecureResultAuthority Authority { get; init; }
    [JsonPropertyName("reason")]
    public required string? Reason { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SecureResultWire
{
    [JsonPropertyName("allowed")]
    public required bool Allowed { get; init; }
    [JsonPropertyName("authority")]
    public required SecureResultAuthority Authority { get; init; }
    [JsonPropertyName("reason")]
    public required string? Reason { get; init; }
}

internal sealed class SecureResultJsonConverter : JsonConverter<SecureResult>
{
    public override SecureResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateSecureResult(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<SecureResultWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("SecureResult: expected object");
        return new SecureResult
        {
            Allowed = wire.Allowed,
            Authority = wire.Authority,
            Reason = wire.Reason,
        };
    }
    public override void Write(Utf8JsonWriter writer, SecureResult value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new SecureResultWire
        {
            Allowed = value.Allowed,
            Authority = value.Authority,
            Reason = value.Reason,
        }, options);
    }

    private static void ValidateSecureResult(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("SecureResult: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "allowed":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("SecureResult.allowed: value must not be null");
                    break;
                case "authority":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("SecureResult.authority: value must not be null");
                    break;
                case "reason":
                    break;
                default:
                    if (string.Equals(property.Name, "allowed", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SecureResult.allowed: property name must match exact wire casing");
                    if (string.Equals(property.Name, "authority", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SecureResult.authority: property name must match exact wire casing");
                    if (string.Equals(property.Name, "reason", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SecureResult.reason: property name must match exact wire casing");
                    throw new JsonException($"SecureResult: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("allowed", out _)) throw new JsonException("SecureResult: missing required property allowed");
        if (!value.TryGetProperty("authority", out _)) throw new JsonException("SecureResult: missing required property authority");
        if (!value.TryGetProperty("reason", out _)) throw new JsonException("SecureResult: missing required property reason");
    }
}

[JsonConverter(typeof(CapabilityInvokeRequestJsonConverter))]
public sealed record CapabilityInvokeRequest
{
    [JsonPropertyName("capability")]
    public required string Capability { get; init; }
    [JsonPropertyName("core")]
    public required JsonElement Core { get; init; }
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }
    [JsonPropertyName("idempotencyKey")]
    public required string IdempotencyKey { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CapabilityInvokeRequestWire
{
    [JsonPropertyName("capability")]
    public required string Capability { get; init; }
    [JsonPropertyName("core")]
    public required JsonElement Core { get; init; }
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }
    [JsonPropertyName("idempotencyKey")]
    public required string IdempotencyKey { get; init; }
}

internal sealed class CapabilityInvokeRequestJsonConverter : JsonConverter<CapabilityInvokeRequest>
{
    public override CapabilityInvokeRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateCapabilityInvokeRequest(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<CapabilityInvokeRequestWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("CapabilityInvokeRequest: expected object");
        return new CapabilityInvokeRequest
        {
            Capability = wire.Capability,
            Core = wire.Core,
            CorrelationId = wire.CorrelationId,
            IdempotencyKey = wire.IdempotencyKey,
        };
    }
    public override void Write(Utf8JsonWriter writer, CapabilityInvokeRequest value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new CapabilityInvokeRequestWire
        {
            Capability = value.Capability,
            Core = value.Core,
            CorrelationId = value.CorrelationId,
            IdempotencyKey = value.IdempotencyKey,
        }, options);
    }

    private static void ValidateCapabilityInvokeRequest(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("CapabilityInvokeRequest: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "capability":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CapabilityInvokeRequest.capability: value must not be null");
                    break;
                case "core":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CapabilityInvokeRequest.core: value must not be null");
                    break;
                case "correlationId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CapabilityInvokeRequest.correlationId: value must not be null");
                    break;
                case "idempotencyKey":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CapabilityInvokeRequest.idempotencyKey: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "capability", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CapabilityInvokeRequest.capability: property name must match exact wire casing");
                    if (string.Equals(property.Name, "core", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CapabilityInvokeRequest.core: property name must match exact wire casing");
                    if (string.Equals(property.Name, "correlationId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CapabilityInvokeRequest.correlationId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "idempotencyKey", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CapabilityInvokeRequest.idempotencyKey: property name must match exact wire casing");
                    throw new JsonException($"CapabilityInvokeRequest: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("capability", out _)) throw new JsonException("CapabilityInvokeRequest: missing required property capability");
        if (!value.TryGetProperty("core", out _)) throw new JsonException("CapabilityInvokeRequest: missing required property core");
        if (!value.TryGetProperty("correlationId", out _)) throw new JsonException("CapabilityInvokeRequest: missing required property correlationId");
        if (!value.TryGetProperty("idempotencyKey", out _)) throw new JsonException("CapabilityInvokeRequest: missing required property idempotencyKey");
    }
}

[JsonConverter(typeof(CapabilityHostInvokeRequestJsonConverter))]
public sealed record CapabilityHostInvokeRequest
{
    [JsonPropertyName("capability")]
    public required string Capability { get; init; }
    [JsonPropertyName("core")]
    public required JsonElement Core { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CapabilityHostInvokeRequestWire
{
    [JsonPropertyName("capability")]
    public required string Capability { get; init; }
    [JsonPropertyName("core")]
    public required JsonElement Core { get; init; }
}

internal sealed class CapabilityHostInvokeRequestJsonConverter : JsonConverter<CapabilityHostInvokeRequest>
{
    public override CapabilityHostInvokeRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateCapabilityHostInvokeRequest(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<CapabilityHostInvokeRequestWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("CapabilityHostInvokeRequest: expected object");
        return new CapabilityHostInvokeRequest
        {
            Capability = wire.Capability,
            Core = wire.Core,
        };
    }
    public override void Write(Utf8JsonWriter writer, CapabilityHostInvokeRequest value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new CapabilityHostInvokeRequestWire
        {
            Capability = value.Capability,
            Core = value.Core,
        }, options);
    }

    private static void ValidateCapabilityHostInvokeRequest(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("CapabilityHostInvokeRequest: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "capability":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CapabilityHostInvokeRequest.capability: value must not be null");
                    break;
                case "core":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CapabilityHostInvokeRequest.core: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "capability", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CapabilityHostInvokeRequest.capability: property name must match exact wire casing");
                    if (string.Equals(property.Name, "core", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CapabilityHostInvokeRequest.core: property name must match exact wire casing");
                    throw new JsonException($"CapabilityHostInvokeRequest: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("capability", out _)) throw new JsonException("CapabilityHostInvokeRequest: missing required property capability");
        if (!value.TryGetProperty("core", out _)) throw new JsonException("CapabilityHostInvokeRequest: missing required property core");
    }
}

[JsonConverter(typeof(ObserveRequestKindJsonConverter))]
public enum ObserveRequestKind
{
    Startup = 0,
    Liveness = 1,
    Readiness = 2,
}

public sealed class ObserveRequestKindJsonConverter : JsonConverter<ObserveRequestKind>
{
    public override ObserveRequestKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "startup" => ObserveRequestKind.Startup,
        "liveness" => ObserveRequestKind.Liveness,
        "readiness" => ObserveRequestKind.Readiness,
        _ => throw new JsonException("invalid ObserveRequestKind"),
    };
    public override void Write(Utf8JsonWriter writer, ObserveRequestKind value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        ObserveRequestKind.Startup => "startup",
        ObserveRequestKind.Liveness => "liveness",
        ObserveRequestKind.Readiness => "readiness",
        _ => throw new JsonException("invalid ObserveRequestKind"),
    });
}

[JsonConverter(typeof(ObserveRequestJsonConverter))]
public sealed record ObserveRequest
{
    [JsonPropertyName("runtimeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeId { get; init; }
    [JsonPropertyName("kind")]
    public required ObserveRequestKind Kind { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ObserveRequestWire
{
    [JsonPropertyName("runtimeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeId { get; init; }
    [JsonPropertyName("kind")]
    public required ObserveRequestKind Kind { get; init; }
}

internal sealed class ObserveRequestJsonConverter : JsonConverter<ObserveRequest>
{
    public override ObserveRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateObserveRequest(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<ObserveRequestWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("ObserveRequest: expected object");
        return new ObserveRequest
        {
            RuntimeId = wire.RuntimeId,
            Kind = wire.Kind,
        };
    }
    public override void Write(Utf8JsonWriter writer, ObserveRequest value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new ObserveRequestWire
        {
            RuntimeId = value.RuntimeId,
            Kind = value.Kind,
        }, options);
    }

    private static void ValidateObserveRequest(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("ObserveRequest: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "runtimeId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ObserveRequest.runtimeId: value must not be null");
                    break;
                case "kind":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ObserveRequest.kind: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "runtimeId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ObserveRequest.runtimeId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "kind", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ObserveRequest.kind: property name must match exact wire casing");
                    throw new JsonException($"ObserveRequest: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("kind", out _)) throw new JsonException("ObserveRequest: missing required property kind");
    }
}

[JsonConverter(typeof(ResolveRequestJsonConverter))]
public sealed record ResolveRequest
{
    [JsonPropertyName("capabilityId")]
    public required string CapabilityId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ResolveRequestWire
{
    [JsonPropertyName("capabilityId")]
    public required string CapabilityId { get; init; }
}

internal sealed class ResolveRequestJsonConverter : JsonConverter<ResolveRequest>
{
    public override ResolveRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateResolveRequest(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<ResolveRequestWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("ResolveRequest: expected object");
        return new ResolveRequest
        {
            CapabilityId = wire.CapabilityId,
        };
    }
    public override void Write(Utf8JsonWriter writer, ResolveRequest value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new ResolveRequestWire
        {
            CapabilityId = value.CapabilityId,
        }, options);
    }

    private static void ValidateResolveRequest(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("ResolveRequest: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "capabilityId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ResolveRequest.capabilityId: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "capabilityId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ResolveRequest.capabilityId: property name must match exact wire casing");
                    throw new JsonException($"ResolveRequest: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("capabilityId", out _)) throw new JsonException("ResolveRequest: missing required property capabilityId");
    }
}

[JsonConverter(typeof(ResolutionResultTierJsonConverter))]
public enum ResolutionResultTier
{
    Local = 0,
    Remote = 1,
    Cloud = 2,
}

public sealed class ResolutionResultTierJsonConverter : JsonConverter<ResolutionResultTier>
{
    public override ResolutionResultTier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "local" => ResolutionResultTier.Local,
        "remote" => ResolutionResultTier.Remote,
        "cloud" => ResolutionResultTier.Cloud,
        _ => throw new JsonException("invalid ResolutionResultTier"),
    };
    public override void Write(Utf8JsonWriter writer, ResolutionResultTier value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        ResolutionResultTier.Local => "local",
        ResolutionResultTier.Remote => "remote",
        ResolutionResultTier.Cloud => "cloud",
        _ => throw new JsonException("invalid ResolutionResultTier"),
    });
}

[JsonConverter(typeof(ResolutionResultResolutionStateJsonConverter))]
public enum ResolutionResultResolutionState
{
    Available = 0,
    LockedEntitlement = 1,
    Upsell = 2,
    UnavailableHardware = 3,
    Degraded = 4,
    NotInEdition = 5,
}

public sealed class ResolutionResultResolutionStateJsonConverter : JsonConverter<ResolutionResultResolutionState>
{
    public override ResolutionResultResolutionState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "available" => ResolutionResultResolutionState.Available,
        "locked-entitlement" => ResolutionResultResolutionState.LockedEntitlement,
        "upsell" => ResolutionResultResolutionState.Upsell,
        "unavailable-hardware" => ResolutionResultResolutionState.UnavailableHardware,
        "degraded" => ResolutionResultResolutionState.Degraded,
        "not-in-edition" => ResolutionResultResolutionState.NotInEdition,
        _ => throw new JsonException("invalid ResolutionResultResolutionState"),
    };
    public override void Write(Utf8JsonWriter writer, ResolutionResultResolutionState value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        ResolutionResultResolutionState.Available => "available",
        ResolutionResultResolutionState.LockedEntitlement => "locked-entitlement",
        ResolutionResultResolutionState.Upsell => "upsell",
        ResolutionResultResolutionState.UnavailableHardware => "unavailable-hardware",
        ResolutionResultResolutionState.Degraded => "degraded",
        ResolutionResultResolutionState.NotInEdition => "not-in-edition",
        _ => throw new JsonException("invalid ResolutionResultResolutionState"),
    });
}

[JsonConverter(typeof(ResolutionResultSelectionReasonJsonConverter))]
public enum ResolutionResultSelectionReason
{
    OnlyCandidate = 0,
    PreferredByPolicy = 1,
    HardwareFit = 2,
    FallbackFloor = 3,
    EntitlementGated = 4,
}

public sealed class ResolutionResultSelectionReasonJsonConverter : JsonConverter<ResolutionResultSelectionReason>
{
    public override ResolutionResultSelectionReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "only-candidate" => ResolutionResultSelectionReason.OnlyCandidate,
        "preferred-by-policy" => ResolutionResultSelectionReason.PreferredByPolicy,
        "hardware-fit" => ResolutionResultSelectionReason.HardwareFit,
        "fallback-floor" => ResolutionResultSelectionReason.FallbackFloor,
        "entitlement-gated" => ResolutionResultSelectionReason.EntitlementGated,
        _ => throw new JsonException("invalid ResolutionResultSelectionReason"),
    };
    public override void Write(Utf8JsonWriter writer, ResolutionResultSelectionReason value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        ResolutionResultSelectionReason.OnlyCandidate => "only-candidate",
        ResolutionResultSelectionReason.PreferredByPolicy => "preferred-by-policy",
        ResolutionResultSelectionReason.HardwareFit => "hardware-fit",
        ResolutionResultSelectionReason.FallbackFloor => "fallback-floor",
        ResolutionResultSelectionReason.EntitlementGated => "entitlement-gated",
        _ => throw new JsonException("invalid ResolutionResultSelectionReason"),
    });
}

[JsonConverter(typeof(ResolutionResultSpeedHintJsonConverter))]
public enum ResolutionResultSpeedHint
{
    Fast = 0,
    Moderate = 1,
    Slow = 2,
}

public sealed class ResolutionResultSpeedHintJsonConverter : JsonConverter<ResolutionResultSpeedHint>
{
    public override ResolutionResultSpeedHint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "fast" => ResolutionResultSpeedHint.Fast,
        "moderate" => ResolutionResultSpeedHint.Moderate,
        "slow" => ResolutionResultSpeedHint.Slow,
        _ => throw new JsonException("invalid ResolutionResultSpeedHint"),
    };
    public override void Write(Utf8JsonWriter writer, ResolutionResultSpeedHint value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        ResolutionResultSpeedHint.Fast => "fast",
        ResolutionResultSpeedHint.Moderate => "moderate",
        ResolutionResultSpeedHint.Slow => "slow",
        _ => throw new JsonException("invalid ResolutionResultSpeedHint"),
    });
}

[JsonConverter(typeof(ResolutionResultJsonConverter))]
public sealed record ResolutionResult
{
    [JsonPropertyName("chosenProviderId")]
    public required string? ChosenProviderId { get; init; }
    [JsonPropertyName("tier")]
    public required ResolutionResultTier Tier { get; init; }
    [JsonPropertyName("resolutionState")]
    public required ResolutionResultResolutionState ResolutionState { get; init; }
    [JsonPropertyName("selectionReason")]
    public required ResolutionResultSelectionReason SelectionReason { get; init; }
    [JsonPropertyName("speedHint")]
    public required ResolutionResultSpeedHint SpeedHint { get; init; }
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
    [JsonPropertyName("isFallback")]
    public required bool IsFallback { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ResolutionResultWire
{
    [JsonPropertyName("chosenProviderId")]
    public required string? ChosenProviderId { get; init; }
    [JsonPropertyName("tier")]
    public required ResolutionResultTier Tier { get; init; }
    [JsonPropertyName("resolutionState")]
    public required ResolutionResultResolutionState ResolutionState { get; init; }
    [JsonPropertyName("selectionReason")]
    public required ResolutionResultSelectionReason SelectionReason { get; init; }
    [JsonPropertyName("speedHint")]
    public required ResolutionResultSpeedHint SpeedHint { get; init; }
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
    [JsonPropertyName("isFallback")]
    public required bool IsFallback { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

internal sealed class ResolutionResultJsonConverter : JsonConverter<ResolutionResult>
{
    public override ResolutionResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateResolutionResult(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<ResolutionResultWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("ResolutionResult: expected object");
        return new ResolutionResult
        {
            ChosenProviderId = wire.ChosenProviderId,
            Tier = wire.Tier,
            ResolutionState = wire.ResolutionState,
            SelectionReason = wire.SelectionReason,
            SpeedHint = wire.SpeedHint,
            Reason = wire.Reason,
            IsFallback = wire.IsFallback,
            Extensions = wire.Extensions,
        };
    }
    public override void Write(Utf8JsonWriter writer, ResolutionResult value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new ResolutionResultWire
        {
            ChosenProviderId = value.ChosenProviderId,
            Tier = value.Tier,
            ResolutionState = value.ResolutionState,
            SelectionReason = value.SelectionReason,
            SpeedHint = value.SpeedHint,
            Reason = value.Reason,
            IsFallback = value.IsFallback,
            Extensions = value.Extensions,
        }, options);
    }

    private static void ValidateResolutionResult(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("ResolutionResult: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "chosenProviderId":
                    break;
                case "tier":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ResolutionResult.tier: value must not be null");
                    break;
                case "resolutionState":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ResolutionResult.resolutionState: value must not be null");
                    break;
                case "selectionReason":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ResolutionResult.selectionReason: value must not be null");
                    break;
                case "speedHint":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ResolutionResult.speedHint: value must not be null");
                    break;
                case "reason":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ResolutionResult.reason: value must not be null");
                    break;
                case "isFallback":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ResolutionResult.isFallback: value must not be null");
                    break;
                case "extensions":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ResolutionResult.extensions: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "chosenProviderId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ResolutionResult.chosenProviderId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "tier", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ResolutionResult.tier: property name must match exact wire casing");
                    if (string.Equals(property.Name, "resolutionState", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ResolutionResult.resolutionState: property name must match exact wire casing");
                    if (string.Equals(property.Name, "selectionReason", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ResolutionResult.selectionReason: property name must match exact wire casing");
                    if (string.Equals(property.Name, "speedHint", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ResolutionResult.speedHint: property name must match exact wire casing");
                    if (string.Equals(property.Name, "reason", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ResolutionResult.reason: property name must match exact wire casing");
                    if (string.Equals(property.Name, "isFallback", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ResolutionResult.isFallback: property name must match exact wire casing");
                    if (string.Equals(property.Name, "extensions", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ResolutionResult.extensions: property name must match exact wire casing");
                    throw new JsonException($"ResolutionResult: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("chosenProviderId", out _)) throw new JsonException("ResolutionResult: missing required property chosenProviderId");
        if (!value.TryGetProperty("tier", out _)) throw new JsonException("ResolutionResult: missing required property tier");
        if (!value.TryGetProperty("resolutionState", out _)) throw new JsonException("ResolutionResult: missing required property resolutionState");
        if (!value.TryGetProperty("selectionReason", out _)) throw new JsonException("ResolutionResult: missing required property selectionReason");
        if (!value.TryGetProperty("speedHint", out _)) throw new JsonException("ResolutionResult: missing required property speedHint");
        if (!value.TryGetProperty("reason", out _)) throw new JsonException("ResolutionResult: missing required property reason");
        if (!value.TryGetProperty("isFallback", out _)) throw new JsonException("ResolutionResult: missing required property isFallback");
    }
}

[JsonConverter(typeof(ComposeRequestJsonConverter))]
public sealed record ComposeRequest
{
    [JsonPropertyName("editionId")]
    public required string EditionId { get; init; }
    [JsonPropertyName("capabilityIds")]
    public required IReadOnlyList<string> CapabilityIds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ComposeRequestWire
{
    [JsonPropertyName("editionId")]
    public required string EditionId { get; init; }
    [JsonPropertyName("capabilityIds")]
    public required IReadOnlyList<string> CapabilityIds { get; init; }
}

internal sealed class ComposeRequestJsonConverter : JsonConverter<ComposeRequest>
{
    public override ComposeRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateComposeRequest(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<ComposeRequestWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("ComposeRequest: expected object");
        return new ComposeRequest
        {
            EditionId = wire.EditionId,
            CapabilityIds = wire.CapabilityIds,
        };
    }
    public override void Write(Utf8JsonWriter writer, ComposeRequest value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new ComposeRequestWire
        {
            EditionId = value.EditionId,
            CapabilityIds = value.CapabilityIds,
        }, options);
    }

    private static void ValidateComposeRequest(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("ComposeRequest: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "editionId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ComposeRequest.editionId: value must not be null");
                    break;
                case "capabilityIds":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ComposeRequest.capabilityIds: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "editionId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ComposeRequest.editionId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "capabilityIds", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ComposeRequest.capabilityIds: property name must match exact wire casing");
                    throw new JsonException($"ComposeRequest: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("editionId", out _)) throw new JsonException("ComposeRequest: missing required property editionId");
        if (!value.TryGetProperty("capabilityIds", out _)) throw new JsonException("ComposeRequest: missing required property capabilityIds");
    }
}

[JsonConverter(typeof(ComposeResultJsonConverter))]
public sealed record ComposeResult
{
    [JsonPropertyName("editionId")]
    public required string EditionId { get; init; }
    [JsonPropertyName("acceptedCapabilityIds")]
    public required IReadOnlyList<string> AcceptedCapabilityIds { get; init; }
    [JsonPropertyName("rejectedCapabilityIds")]
    public required IReadOnlyList<string> RejectedCapabilityIds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ComposeResultWire
{
    [JsonPropertyName("editionId")]
    public required string EditionId { get; init; }
    [JsonPropertyName("acceptedCapabilityIds")]
    public required IReadOnlyList<string> AcceptedCapabilityIds { get; init; }
    [JsonPropertyName("rejectedCapabilityIds")]
    public required IReadOnlyList<string> RejectedCapabilityIds { get; init; }
}

internal sealed class ComposeResultJsonConverter : JsonConverter<ComposeResult>
{
    public override ComposeResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateComposeResult(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<ComposeResultWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("ComposeResult: expected object");
        return new ComposeResult
        {
            EditionId = wire.EditionId,
            AcceptedCapabilityIds = wire.AcceptedCapabilityIds,
            RejectedCapabilityIds = wire.RejectedCapabilityIds,
        };
    }
    public override void Write(Utf8JsonWriter writer, ComposeResult value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new ComposeResultWire
        {
            EditionId = value.EditionId,
            AcceptedCapabilityIds = value.AcceptedCapabilityIds,
            RejectedCapabilityIds = value.RejectedCapabilityIds,
        }, options);
    }

    private static void ValidateComposeResult(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("ComposeResult: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "editionId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ComposeResult.editionId: value must not be null");
                    break;
                case "acceptedCapabilityIds":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ComposeResult.acceptedCapabilityIds: value must not be null");
                    break;
                case "rejectedCapabilityIds":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("ComposeResult.rejectedCapabilityIds: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "editionId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ComposeResult.editionId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "acceptedCapabilityIds", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ComposeResult.acceptedCapabilityIds: property name must match exact wire casing");
                    if (string.Equals(property.Name, "rejectedCapabilityIds", StringComparison.OrdinalIgnoreCase)) throw new JsonException("ComposeResult.rejectedCapabilityIds: property name must match exact wire casing");
                    throw new JsonException($"ComposeResult: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("editionId", out _)) throw new JsonException("ComposeResult: missing required property editionId");
        if (!value.TryGetProperty("acceptedCapabilityIds", out _)) throw new JsonException("ComposeResult: missing required property acceptedCapabilityIds");
        if (!value.TryGetProperty("rejectedCapabilityIds", out _)) throw new JsonException("ComposeResult: missing required property rejectedCapabilityIds");
    }
}

[JsonConverter(typeof(CpDemoRequestJsonConverter))]
public sealed record CpDemoRequest
{
    [JsonPropertyName("note")]
    public required string? Note { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CpDemoRequestWire
{
    [JsonPropertyName("note")]
    public required string? Note { get; init; }
}

internal sealed class CpDemoRequestJsonConverter : JsonConverter<CpDemoRequest>
{
    public override CpDemoRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateCpDemoRequest(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<CpDemoRequestWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("CpDemoRequest: expected object");
        return new CpDemoRequest
        {
            Note = wire.Note,
        };
    }
    public override void Write(Utf8JsonWriter writer, CpDemoRequest value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new CpDemoRequestWire
        {
            Note = value.Note,
        }, options);
    }

    private static void ValidateCpDemoRequest(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("CpDemoRequest: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "note":
                    break;
                default:
                    if (string.Equals(property.Name, "note", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CpDemoRequest.note: property name must match exact wire casing");
                    throw new JsonException($"CpDemoRequest: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("note", out _)) throw new JsonException("CpDemoRequest: missing required property note");
    }
}

[JsonConverter(typeof(CpDemoMetaCommandJsonConverter))]
public enum CpDemoMetaCommand
{
    DemoCpOp = 0,
}

public sealed class CpDemoMetaCommandJsonConverter : JsonConverter<CpDemoMetaCommand>
{
    public override CpDemoMetaCommand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "demo-cp-op" => CpDemoMetaCommand.DemoCpOp,
        _ => throw new JsonException("invalid CpDemoMetaCommand"),
    };
    public override void Write(Utf8JsonWriter writer, CpDemoMetaCommand value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        CpDemoMetaCommand.DemoCpOp => "demo-cp-op",
        _ => throw new JsonException("invalid CpDemoMetaCommand"),
    });
}

[JsonConverter(typeof(CpDemoMetaJsonConverter))]
public sealed record CpDemoMeta
{
    [JsonPropertyName("command")]
    public required CpDemoMetaCommand Command { get; init; }
    [JsonPropertyName("confirmed")]
    public required bool Confirmed { get; init; }
    [JsonPropertyName("note")]
    public required string Note { get; init; }
    [JsonPropertyName("executedAt")]
    public required string ExecutedAt { get; init; }
    [JsonPropertyName("proposedBy")]
    public required Principal ProposedBy { get; init; }
    [JsonPropertyName("confirmedBy")]
    public required Principal ConfirmedBy { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CpDemoMetaWire
{
    [JsonPropertyName("command")]
    public required CpDemoMetaCommand Command { get; init; }
    [JsonPropertyName("confirmed")]
    public required bool Confirmed { get; init; }
    [JsonPropertyName("note")]
    public required string Note { get; init; }
    [JsonPropertyName("executedAt")]
    public required string ExecutedAt { get; init; }
    [JsonPropertyName("proposedBy")]
    public required Principal ProposedBy { get; init; }
    [JsonPropertyName("confirmedBy")]
    public required Principal ConfirmedBy { get; init; }
}

internal sealed class CpDemoMetaJsonConverter : JsonConverter<CpDemoMeta>
{
    public override CpDemoMeta Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateCpDemoMeta(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<CpDemoMetaWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("CpDemoMeta: expected object");
        return new CpDemoMeta
        {
            Command = wire.Command,
            Confirmed = wire.Confirmed,
            Note = wire.Note,
            ExecutedAt = wire.ExecutedAt,
            ProposedBy = wire.ProposedBy,
            ConfirmedBy = wire.ConfirmedBy,
        };
    }
    public override void Write(Utf8JsonWriter writer, CpDemoMeta value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new CpDemoMetaWire
        {
            Command = value.Command,
            Confirmed = value.Confirmed,
            Note = value.Note,
            ExecutedAt = value.ExecutedAt,
            ProposedBy = value.ProposedBy,
            ConfirmedBy = value.ConfirmedBy,
        }, options);
    }

    private static void ValidateCpDemoMeta(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("CpDemoMeta: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "command":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CpDemoMeta.command: value must not be null");
                    break;
                case "confirmed":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CpDemoMeta.confirmed: value must not be null");
                    break;
                case "note":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CpDemoMeta.note: value must not be null");
                    break;
                case "executedAt":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CpDemoMeta.executedAt: value must not be null");
                    break;
                case "proposedBy":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CpDemoMeta.proposedBy: value must not be null");
                    break;
                case "confirmedBy":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CpDemoMeta.confirmedBy: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "command", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CpDemoMeta.command: property name must match exact wire casing");
                    if (string.Equals(property.Name, "confirmed", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CpDemoMeta.confirmed: property name must match exact wire casing");
                    if (string.Equals(property.Name, "note", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CpDemoMeta.note: property name must match exact wire casing");
                    if (string.Equals(property.Name, "executedAt", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CpDemoMeta.executedAt: property name must match exact wire casing");
                    if (string.Equals(property.Name, "proposedBy", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CpDemoMeta.proposedBy: property name must match exact wire casing");
                    if (string.Equals(property.Name, "confirmedBy", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CpDemoMeta.confirmedBy: property name must match exact wire casing");
                    throw new JsonException($"CpDemoMeta: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("command", out _)) throw new JsonException("CpDemoMeta: missing required property command");
        if (!value.TryGetProperty("confirmed", out _)) throw new JsonException("CpDemoMeta: missing required property confirmed");
        if (!value.TryGetProperty("note", out _)) throw new JsonException("CpDemoMeta: missing required property note");
        if (!value.TryGetProperty("executedAt", out _)) throw new JsonException("CpDemoMeta: missing required property executedAt");
        if (!value.TryGetProperty("proposedBy", out _)) throw new JsonException("CpDemoMeta: missing required property proposedBy");
        if (!value.TryGetProperty("confirmedBy", out _)) throw new JsonException("CpDemoMeta: missing required property confirmedBy");
    }
}

[JsonConverter(typeof(CpDemoResultStatusJsonConverter))]
public enum CpDemoResultStatus
{
    Accepted = 0,
    Running = 1,
    Succeeded = 2,
    Partial = 3,
    Failed = 4,
}

public sealed class CpDemoResultStatusJsonConverter : JsonConverter<CpDemoResultStatus>
{
    public override CpDemoResultStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "accepted" => CpDemoResultStatus.Accepted,
        "running" => CpDemoResultStatus.Running,
        "succeeded" => CpDemoResultStatus.Succeeded,
        "partial" => CpDemoResultStatus.Partial,
        "failed" => CpDemoResultStatus.Failed,
        _ => throw new JsonException("invalid CpDemoResultStatus"),
    };
    public override void Write(Utf8JsonWriter writer, CpDemoResultStatus value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        CpDemoResultStatus.Accepted => "accepted",
        CpDemoResultStatus.Running => "running",
        CpDemoResultStatus.Succeeded => "succeeded",
        CpDemoResultStatus.Partial => "partial",
        CpDemoResultStatus.Failed => "failed",
        _ => throw new JsonException("invalid CpDemoResultStatus"),
    });
}

[JsonConverter(typeof(CpDemoResultJsonConverter))]
public sealed record CpDemoResult
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }
    [JsonPropertyName("status")]
    public required CpDemoResultStatus Status { get; init; }
    private double _progress;
    [JsonPropertyName("progress")]
    public required double Progress
    {
        get => _progress;
        init
        {
            if (value < 0d || value > 1d) throw new ArgumentOutOfRangeException(nameof(Progress));
            _progress = value;
        }
    }
    [JsonPropertyName("artifacts")]
    public required IReadOnlyList<Artifact> Artifacts { get; init; }
    [JsonPropertyName("usage")]
    public required Usage Usage { get; init; }
    [JsonPropertyName("error")]
    public required ProtocolError? Error { get; init; }
    [JsonPropertyName("meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CpDemoMeta? Meta { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CpDemoResultWire
{
    [JsonPropertyName("jobId")]
    public required string JobId { get; init; }
    [JsonPropertyName("status")]
    public required CpDemoResultStatus Status { get; init; }
    [JsonPropertyName("progress")]
    public required double Progress { get; init; }
    [JsonPropertyName("artifacts")]
    public required IReadOnlyList<Artifact> Artifacts { get; init; }
    [JsonPropertyName("usage")]
    public required Usage Usage { get; init; }
    [JsonPropertyName("error")]
    public required ProtocolError? Error { get; init; }
    [JsonPropertyName("meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CpDemoMeta? Meta { get; init; }
}

internal sealed class CpDemoResultJsonConverter : JsonConverter<CpDemoResult>
{
    public override CpDemoResult Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateCpDemoResult(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<CpDemoResultWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("CpDemoResult: expected object");
        return new CpDemoResult
        {
            JobId = wire.JobId,
            Status = wire.Status,
            Progress = wire.Progress,
            Artifacts = wire.Artifacts,
            Usage = wire.Usage,
            Error = wire.Error,
            Meta = wire.Meta,
        };
    }
    public override void Write(Utf8JsonWriter writer, CpDemoResult value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new CpDemoResultWire
        {
            JobId = value.JobId,
            Status = value.Status,
            Progress = value.Progress,
            Artifacts = value.Artifacts,
            Usage = value.Usage,
            Error = value.Error,
            Meta = value.Meta,
        }, options);
    }

    private static void ValidateCpDemoResult(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("CpDemoResult: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "jobId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CpDemoResult.jobId: value must not be null");
                    break;
                case "status":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CpDemoResult.status: value must not be null");
                    break;
                case "progress":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CpDemoResult.progress: value must not be null");
                    break;
                case "artifacts":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CpDemoResult.artifacts: value must not be null");
                    break;
                case "usage":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CpDemoResult.usage: value must not be null");
                    break;
                case "error":
                    break;
                case "meta":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("CpDemoResult.meta: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "jobId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CpDemoResult.jobId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "status", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CpDemoResult.status: property name must match exact wire casing");
                    if (string.Equals(property.Name, "progress", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CpDemoResult.progress: property name must match exact wire casing");
                    if (string.Equals(property.Name, "artifacts", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CpDemoResult.artifacts: property name must match exact wire casing");
                    if (string.Equals(property.Name, "usage", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CpDemoResult.usage: property name must match exact wire casing");
                    if (string.Equals(property.Name, "error", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CpDemoResult.error: property name must match exact wire casing");
                    if (string.Equals(property.Name, "meta", StringComparison.OrdinalIgnoreCase)) throw new JsonException("CpDemoResult.meta: property name must match exact wire casing");
                    throw new JsonException($"CpDemoResult: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("jobId", out _)) throw new JsonException("CpDemoResult: missing required property jobId");
        if (!value.TryGetProperty("status", out _)) throw new JsonException("CpDemoResult: missing required property status");
        if (!value.TryGetProperty("progress", out _)) throw new JsonException("CpDemoResult: missing required property progress");
        if (!value.TryGetProperty("artifacts", out _)) throw new JsonException("CpDemoResult: missing required property artifacts");
        if (!value.TryGetProperty("usage", out _)) throw new JsonException("CpDemoResult: missing required property usage");
        if (!value.TryGetProperty("error", out _)) throw new JsonException("CpDemoResult: missing required property error");
    }
}

[JsonConverter(typeof(NodeStatusStateJsonConverter))]
public enum NodeStatusState
{
    Starting = 0,
    Running = 1,
    Failed = 2,
    Stopped = 3,
}

public sealed class NodeStatusStateJsonConverter : JsonConverter<NodeStatusState>
{
    public override NodeStatusState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "starting" => NodeStatusState.Starting,
        "running" => NodeStatusState.Running,
        "failed" => NodeStatusState.Failed,
        "stopped" => NodeStatusState.Stopped,
        _ => throw new JsonException("invalid NodeStatusState"),
    };
    public override void Write(Utf8JsonWriter writer, NodeStatusState value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        NodeStatusState.Starting => "starting",
        NodeStatusState.Running => "running",
        NodeStatusState.Failed => "failed",
        NodeStatusState.Stopped => "stopped",
        _ => throw new JsonException("invalid NodeStatusState"),
    });
}

[JsonConverter(typeof(NodeStatusJsonConverter))]
public sealed record NodeStatus
{
    [JsonPropertyName("state")]
    public required NodeStatusState State { get; init; }
    [JsonPropertyName("baseUrl")]
    public required string? BaseUrl { get; init; }
    [JsonPropertyName("sessionToken")]
    public required string? SessionToken { get; init; }
    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record NodeStatusWire
{
    [JsonPropertyName("state")]
    public required NodeStatusState State { get; init; }
    [JsonPropertyName("baseUrl")]
    public required string? BaseUrl { get; init; }
    [JsonPropertyName("sessionToken")]
    public required string? SessionToken { get; init; }
    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

internal sealed class NodeStatusJsonConverter : JsonConverter<NodeStatus>
{
    public override NodeStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateNodeStatus(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<NodeStatusWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("NodeStatus: expected object");
        return new NodeStatus
        {
            State = wire.State,
            BaseUrl = wire.BaseUrl,
            SessionToken = wire.SessionToken,
            Detail = wire.Detail,
            Extensions = wire.Extensions,
        };
    }
    public override void Write(Utf8JsonWriter writer, NodeStatus value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new NodeStatusWire
        {
            State = value.State,
            BaseUrl = value.BaseUrl,
            SessionToken = value.SessionToken,
            Detail = value.Detail,
            Extensions = value.Extensions,
        }, options);
    }

    private static void ValidateNodeStatus(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("NodeStatus: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "state":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("NodeStatus.state: value must not be null");
                    break;
                case "baseUrl":
                    break;
                case "sessionToken":
                    break;
                case "detail":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("NodeStatus.detail: value must not be null");
                    break;
                case "extensions":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("NodeStatus.extensions: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "state", StringComparison.OrdinalIgnoreCase)) throw new JsonException("NodeStatus.state: property name must match exact wire casing");
                    if (string.Equals(property.Name, "baseUrl", StringComparison.OrdinalIgnoreCase)) throw new JsonException("NodeStatus.baseUrl: property name must match exact wire casing");
                    if (string.Equals(property.Name, "sessionToken", StringComparison.OrdinalIgnoreCase)) throw new JsonException("NodeStatus.sessionToken: property name must match exact wire casing");
                    if (string.Equals(property.Name, "detail", StringComparison.OrdinalIgnoreCase)) throw new JsonException("NodeStatus.detail: property name must match exact wire casing");
                    if (string.Equals(property.Name, "extensions", StringComparison.OrdinalIgnoreCase)) throw new JsonException("NodeStatus.extensions: property name must match exact wire casing");
                    throw new JsonException($"NodeStatus: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("state", out _)) throw new JsonException("NodeStatus: missing required property state");
        if (!value.TryGetProperty("baseUrl", out _)) throw new JsonException("NodeStatus: missing required property baseUrl");
        if (!value.TryGetProperty("sessionToken", out _)) throw new JsonException("NodeStatus: missing required property sessionToken");
    }
}

[JsonConverter(typeof(BackupStatusStateJsonConverter))]
public enum BackupStatusState
{
    NotConfigured = 0,
    Configured = 1,
}

public sealed class BackupStatusStateJsonConverter : JsonConverter<BackupStatusState>
{
    public override BackupStatusState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "notConfigured" => BackupStatusState.NotConfigured,
        "configured" => BackupStatusState.Configured,
        _ => throw new JsonException("invalid BackupStatusState"),
    };
    public override void Write(Utf8JsonWriter writer, BackupStatusState value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        BackupStatusState.NotConfigured => "notConfigured",
        BackupStatusState.Configured => "configured",
        _ => throw new JsonException("invalid BackupStatusState"),
    });
}

[JsonConverter(typeof(BackupStatusJsonConverter))]
public sealed record BackupStatus
{
    [JsonPropertyName("state")]
    public required BackupStatusState State { get; init; }
    [JsonPropertyName("destination")]
    public required string? Destination { get; init; }
    private long? _lastSuccessfulBackupAtMs;
    [JsonPropertyName("lastSuccessfulBackupAtMs")]
    public required long? LastSuccessfulBackupAtMs
    {
        get => _lastSuccessfulBackupAtMs;
        init
        {
            if (value < 0L || value > 9007199254740991L) throw new ArgumentOutOfRangeException(nameof(LastSuccessfulBackupAtMs));
            _lastSuccessfulBackupAtMs = value;
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record BackupStatusWire
{
    [JsonPropertyName("state")]
    public required BackupStatusState State { get; init; }
    [JsonPropertyName("destination")]
    public required string? Destination { get; init; }
    [JsonPropertyName("lastSuccessfulBackupAtMs")]
    public required long? LastSuccessfulBackupAtMs { get; init; }
}

internal sealed class BackupStatusJsonConverter : JsonConverter<BackupStatus>
{
    public override BackupStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateBackupStatus(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<BackupStatusWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("BackupStatus: expected object");
        return new BackupStatus
        {
            State = wire.State,
            Destination = wire.Destination,
            LastSuccessfulBackupAtMs = wire.LastSuccessfulBackupAtMs,
        };
    }
    public override void Write(Utf8JsonWriter writer, BackupStatus value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new BackupStatusWire
        {
            State = value.State,
            Destination = value.Destination,
            LastSuccessfulBackupAtMs = value.LastSuccessfulBackupAtMs,
        }, options);
    }

    private static void ValidateBackupStatus(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("BackupStatus: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "state":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("BackupStatus.state: value must not be null");
                    break;
                case "destination":
                    break;
                case "lastSuccessfulBackupAtMs":
                    break;
                default:
                    if (string.Equals(property.Name, "state", StringComparison.OrdinalIgnoreCase)) throw new JsonException("BackupStatus.state: property name must match exact wire casing");
                    if (string.Equals(property.Name, "destination", StringComparison.OrdinalIgnoreCase)) throw new JsonException("BackupStatus.destination: property name must match exact wire casing");
                    if (string.Equals(property.Name, "lastSuccessfulBackupAtMs", StringComparison.OrdinalIgnoreCase)) throw new JsonException("BackupStatus.lastSuccessfulBackupAtMs: property name must match exact wire casing");
                    throw new JsonException($"BackupStatus: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("state", out _)) throw new JsonException("BackupStatus: missing required property state");
        if (!value.TryGetProperty("destination", out _)) throw new JsonException("BackupStatus: missing required property destination");
        if (!value.TryGetProperty("lastSuccessfulBackupAtMs", out _)) throw new JsonException("BackupStatus: missing required property lastSuccessfulBackupAtMs");
    }
}

[JsonConverter(typeof(DataLocationStatusDataDirectorySourceJsonConverter))]
public enum DataLocationStatusDataDirectorySource
{
    ApplicationData = 0,
    DevelopmentOverride = 1,
}

public sealed class DataLocationStatusDataDirectorySourceJsonConverter : JsonConverter<DataLocationStatusDataDirectorySource>
{
    public override DataLocationStatusDataDirectorySource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "applicationData" => DataLocationStatusDataDirectorySource.ApplicationData,
        "developmentOverride" => DataLocationStatusDataDirectorySource.DevelopmentOverride,
        _ => throw new JsonException("invalid DataLocationStatusDataDirectorySource"),
    };
    public override void Write(Utf8JsonWriter writer, DataLocationStatusDataDirectorySource value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        DataLocationStatusDataDirectorySource.ApplicationData => "applicationData",
        DataLocationStatusDataDirectorySource.DevelopmentOverride => "developmentOverride",
        _ => throw new JsonException("invalid DataLocationStatusDataDirectorySource"),
    });
}

[JsonConverter(typeof(DataLocationStatusJsonConverter))]
public sealed record DataLocationStatus
{
    [JsonPropertyName("dataDirectory")]
    public required string DataDirectory { get; init; }
    [JsonPropertyName("dataDirectorySource")]
    public required DataLocationStatusDataDirectorySource DataDirectorySource { get; init; }
    [JsonPropertyName("backup")]
    public required BackupStatus Backup { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DataLocationStatusWire
{
    [JsonPropertyName("dataDirectory")]
    public required string DataDirectory { get; init; }
    [JsonPropertyName("dataDirectorySource")]
    public required DataLocationStatusDataDirectorySource DataDirectorySource { get; init; }
    [JsonPropertyName("backup")]
    public required BackupStatus Backup { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

internal sealed class DataLocationStatusJsonConverter : JsonConverter<DataLocationStatus>
{
    public override DataLocationStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateDataLocationStatus(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<DataLocationStatusWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("DataLocationStatus: expected object");
        return new DataLocationStatus
        {
            DataDirectory = wire.DataDirectory,
            DataDirectorySource = wire.DataDirectorySource,
            Backup = wire.Backup,
            Extensions = wire.Extensions,
        };
    }
    public override void Write(Utf8JsonWriter writer, DataLocationStatus value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new DataLocationStatusWire
        {
            DataDirectory = value.DataDirectory,
            DataDirectorySource = value.DataDirectorySource,
            Backup = value.Backup,
            Extensions = value.Extensions,
        }, options);
    }

    private static void ValidateDataLocationStatus(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("DataLocationStatus: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "dataDirectory":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("DataLocationStatus.dataDirectory: value must not be null");
                    break;
                case "dataDirectorySource":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("DataLocationStatus.dataDirectorySource: value must not be null");
                    break;
                case "backup":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("DataLocationStatus.backup: value must not be null");
                    break;
                case "extensions":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("DataLocationStatus.extensions: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "dataDirectory", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DataLocationStatus.dataDirectory: property name must match exact wire casing");
                    if (string.Equals(property.Name, "dataDirectorySource", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DataLocationStatus.dataDirectorySource: property name must match exact wire casing");
                    if (string.Equals(property.Name, "backup", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DataLocationStatus.backup: property name must match exact wire casing");
                    if (string.Equals(property.Name, "extensions", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DataLocationStatus.extensions: property name must match exact wire casing");
                    throw new JsonException($"DataLocationStatus: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("dataDirectory", out _)) throw new JsonException("DataLocationStatus: missing required property dataDirectory");
        if (!value.TryGetProperty("dataDirectorySource", out _)) throw new JsonException("DataLocationStatus: missing required property dataDirectorySource");
        if (!value.TryGetProperty("backup", out _)) throw new JsonException("DataLocationStatus: missing required property backup");
    }
}

[JsonConverter(typeof(DeviceCapabilityProfileSchemaVersionJsonConverter))]
public enum DeviceCapabilityProfileSchemaVersion : long
{
    Value1 = 1,
}

public sealed class DeviceCapabilityProfileSchemaVersionJsonConverter : JsonConverter<DeviceCapabilityProfileSchemaVersion>
{
    public override DeviceCapabilityProfileSchemaVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetInt64() switch
    {
        1L => DeviceCapabilityProfileSchemaVersion.Value1,
        _ => throw new JsonException("invalid DeviceCapabilityProfileSchemaVersion"),
    };
    public override void Write(Utf8JsonWriter writer, DeviceCapabilityProfileSchemaVersion value, JsonSerializerOptions options) => writer.WriteNumberValue(value switch
    {
        DeviceCapabilityProfileSchemaVersion.Value1 => 1L,
        _ => throw new JsonException("invalid DeviceCapabilityProfileSchemaVersion"),
    });
}

[JsonConverter(typeof(DeviceCapabilityProfileOsFamilyJsonConverter))]
public enum DeviceCapabilityProfileOsFamily
{
    Macos = 0,
    Windows = 1,
    Linux = 2,
    Ios = 3,
    Android = 4,
    Unknown = 5,
}

public sealed class DeviceCapabilityProfileOsFamilyJsonConverter : JsonConverter<DeviceCapabilityProfileOsFamily>
{
    public override DeviceCapabilityProfileOsFamily Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "macos" => DeviceCapabilityProfileOsFamily.Macos,
        "windows" => DeviceCapabilityProfileOsFamily.Windows,
        "linux" => DeviceCapabilityProfileOsFamily.Linux,
        "ios" => DeviceCapabilityProfileOsFamily.Ios,
        "android" => DeviceCapabilityProfileOsFamily.Android,
        "unknown" => DeviceCapabilityProfileOsFamily.Unknown,
        _ => throw new JsonException("invalid DeviceCapabilityProfileOsFamily"),
    };
    public override void Write(Utf8JsonWriter writer, DeviceCapabilityProfileOsFamily value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        DeviceCapabilityProfileOsFamily.Macos => "macos",
        DeviceCapabilityProfileOsFamily.Windows => "windows",
        DeviceCapabilityProfileOsFamily.Linux => "linux",
        DeviceCapabilityProfileOsFamily.Ios => "ios",
        DeviceCapabilityProfileOsFamily.Android => "android",
        DeviceCapabilityProfileOsFamily.Unknown => "unknown",
        _ => throw new JsonException("invalid DeviceCapabilityProfileOsFamily"),
    });
}

[JsonConverter(typeof(DeviceCapabilityProfileFastMemoryKindJsonConverter))]
public enum DeviceCapabilityProfileFastMemoryKind
{
    Unified = 0,
    DedicatedVram = 1,
    System = 2,
    Unknown = 3,
}

public sealed class DeviceCapabilityProfileFastMemoryKindJsonConverter : JsonConverter<DeviceCapabilityProfileFastMemoryKind>
{
    public override DeviceCapabilityProfileFastMemoryKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "unified" => DeviceCapabilityProfileFastMemoryKind.Unified,
        "dedicatedVram" => DeviceCapabilityProfileFastMemoryKind.DedicatedVram,
        "system" => DeviceCapabilityProfileFastMemoryKind.System,
        "unknown" => DeviceCapabilityProfileFastMemoryKind.Unknown,
        _ => throw new JsonException("invalid DeviceCapabilityProfileFastMemoryKind"),
    };
    public override void Write(Utf8JsonWriter writer, DeviceCapabilityProfileFastMemoryKind value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        DeviceCapabilityProfileFastMemoryKind.Unified => "unified",
        DeviceCapabilityProfileFastMemoryKind.DedicatedVram => "dedicatedVram",
        DeviceCapabilityProfileFastMemoryKind.System => "system",
        DeviceCapabilityProfileFastMemoryKind.Unknown => "unknown",
        _ => throw new JsonException("invalid DeviceCapabilityProfileFastMemoryKind"),
    });
}

[JsonConverter(typeof(DeviceCapabilityProfileBandwidthClassJsonConverter))]
public enum DeviceCapabilityProfileBandwidthClass
{
    Low = 0,
    Moderate = 1,
    High = 2,
    Unknown = 3,
}

public sealed class DeviceCapabilityProfileBandwidthClassJsonConverter : JsonConverter<DeviceCapabilityProfileBandwidthClass>
{
    public override DeviceCapabilityProfileBandwidthClass Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "low" => DeviceCapabilityProfileBandwidthClass.Low,
        "moderate" => DeviceCapabilityProfileBandwidthClass.Moderate,
        "high" => DeviceCapabilityProfileBandwidthClass.High,
        "unknown" => DeviceCapabilityProfileBandwidthClass.Unknown,
        _ => throw new JsonException("invalid DeviceCapabilityProfileBandwidthClass"),
    };
    public override void Write(Utf8JsonWriter writer, DeviceCapabilityProfileBandwidthClass value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        DeviceCapabilityProfileBandwidthClass.Low => "low",
        DeviceCapabilityProfileBandwidthClass.Moderate => "moderate",
        DeviceCapabilityProfileBandwidthClass.High => "high",
        DeviceCapabilityProfileBandwidthClass.Unknown => "unknown",
        _ => throw new JsonException("invalid DeviceCapabilityProfileBandwidthClass"),
    });
}

[JsonConverter(typeof(DeviceCapabilityProfileBandwidthEvidenceJsonConverter))]
public enum DeviceCapabilityProfileBandwidthEvidence
{
    Inferred = 0,
    Unknown = 1,
}

public sealed class DeviceCapabilityProfileBandwidthEvidenceJsonConverter : JsonConverter<DeviceCapabilityProfileBandwidthEvidence>
{
    public override DeviceCapabilityProfileBandwidthEvidence Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "inferred" => DeviceCapabilityProfileBandwidthEvidence.Inferred,
        "unknown" => DeviceCapabilityProfileBandwidthEvidence.Unknown,
        _ => throw new JsonException("invalid DeviceCapabilityProfileBandwidthEvidence"),
    };
    public override void Write(Utf8JsonWriter writer, DeviceCapabilityProfileBandwidthEvidence value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        DeviceCapabilityProfileBandwidthEvidence.Inferred => "inferred",
        DeviceCapabilityProfileBandwidthEvidence.Unknown => "unknown",
        _ => throw new JsonException("invalid DeviceCapabilityProfileBandwidthEvidence"),
    });
}

[JsonConverter(typeof(DeviceCapabilityProfileMaximumLocalAiTierJsonConverter))]
public enum DeviceCapabilityProfileMaximumLocalAiTier
{
    TE = 0,
    TS = 1,
    TA = 2,
    TR = 3,
    TC = 4,
}

public sealed class DeviceCapabilityProfileMaximumLocalAiTierJsonConverter : JsonConverter<DeviceCapabilityProfileMaximumLocalAiTier>
{
    public override DeviceCapabilityProfileMaximumLocalAiTier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "T-E" => DeviceCapabilityProfileMaximumLocalAiTier.TE,
        "T-S" => DeviceCapabilityProfileMaximumLocalAiTier.TS,
        "T-A" => DeviceCapabilityProfileMaximumLocalAiTier.TA,
        "T-R" => DeviceCapabilityProfileMaximumLocalAiTier.TR,
        "T-C" => DeviceCapabilityProfileMaximumLocalAiTier.TC,
        _ => throw new JsonException("invalid DeviceCapabilityProfileMaximumLocalAiTier"),
    };
    public override void Write(Utf8JsonWriter writer, DeviceCapabilityProfileMaximumLocalAiTier value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        DeviceCapabilityProfileMaximumLocalAiTier.TE => "T-E",
        DeviceCapabilityProfileMaximumLocalAiTier.TS => "T-S",
        DeviceCapabilityProfileMaximumLocalAiTier.TA => "T-A",
        DeviceCapabilityProfileMaximumLocalAiTier.TR => "T-R",
        DeviceCapabilityProfileMaximumLocalAiTier.TC => "T-C",
        _ => throw new JsonException("invalid DeviceCapabilityProfileMaximumLocalAiTier"),
    });
}

[JsonConverter(typeof(DeviceCapabilityProfileDetectionStatusJsonConverter))]
public enum DeviceCapabilityProfileDetectionStatus
{
    Complete = 0,
    Partial = 1,
    Unknown = 2,
}

public sealed class DeviceCapabilityProfileDetectionStatusJsonConverter : JsonConverter<DeviceCapabilityProfileDetectionStatus>
{
    public override DeviceCapabilityProfileDetectionStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "complete" => DeviceCapabilityProfileDetectionStatus.Complete,
        "partial" => DeviceCapabilityProfileDetectionStatus.Partial,
        "unknown" => DeviceCapabilityProfileDetectionStatus.Unknown,
        _ => throw new JsonException("invalid DeviceCapabilityProfileDetectionStatus"),
    };
    public override void Write(Utf8JsonWriter writer, DeviceCapabilityProfileDetectionStatus value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        DeviceCapabilityProfileDetectionStatus.Complete => "complete",
        DeviceCapabilityProfileDetectionStatus.Partial => "partial",
        DeviceCapabilityProfileDetectionStatus.Unknown => "unknown",
        _ => throw new JsonException("invalid DeviceCapabilityProfileDetectionStatus"),
    });
}

[JsonConverter(typeof(DeviceCapabilityProfileLimitationsJsonConverter))]
public enum DeviceCapabilityProfileLimitations
{
    BandwidthUnavailable = 0,
    DedicatedVramUnavailable = 1,
    FastMemoryUnavailable = 2,
    HostUnavailable = 3,
    InvalidHostResponse = 4,
    MobileWorkingSetEstimated = 5,
    ProbeFailed = 6,
}

public sealed class DeviceCapabilityProfileLimitationsJsonConverter : JsonConverter<DeviceCapabilityProfileLimitations>
{
    public override DeviceCapabilityProfileLimitations Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "bandwidthUnavailable" => DeviceCapabilityProfileLimitations.BandwidthUnavailable,
        "dedicatedVramUnavailable" => DeviceCapabilityProfileLimitations.DedicatedVramUnavailable,
        "fastMemoryUnavailable" => DeviceCapabilityProfileLimitations.FastMemoryUnavailable,
        "hostUnavailable" => DeviceCapabilityProfileLimitations.HostUnavailable,
        "invalidHostResponse" => DeviceCapabilityProfileLimitations.InvalidHostResponse,
        "mobileWorkingSetEstimated" => DeviceCapabilityProfileLimitations.MobileWorkingSetEstimated,
        "probeFailed" => DeviceCapabilityProfileLimitations.ProbeFailed,
        _ => throw new JsonException("invalid DeviceCapabilityProfileLimitations"),
    };
    public override void Write(Utf8JsonWriter writer, DeviceCapabilityProfileLimitations value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        DeviceCapabilityProfileLimitations.BandwidthUnavailable => "bandwidthUnavailable",
        DeviceCapabilityProfileLimitations.DedicatedVramUnavailable => "dedicatedVramUnavailable",
        DeviceCapabilityProfileLimitations.FastMemoryUnavailable => "fastMemoryUnavailable",
        DeviceCapabilityProfileLimitations.HostUnavailable => "hostUnavailable",
        DeviceCapabilityProfileLimitations.InvalidHostResponse => "invalidHostResponse",
        DeviceCapabilityProfileLimitations.MobileWorkingSetEstimated => "mobileWorkingSetEstimated",
        DeviceCapabilityProfileLimitations.ProbeFailed => "probeFailed",
        _ => throw new JsonException("invalid DeviceCapabilityProfileLimitations"),
    });
}

[JsonConverter(typeof(DeviceCapabilityProfileJsonConverter))]
public sealed record DeviceCapabilityProfile
{
    [JsonPropertyName("schemaVersion")]
    public required DeviceCapabilityProfileSchemaVersion SchemaVersion { get; init; }
    private long _detectedAtMs;
    [JsonPropertyName("detectedAtMs")]
    public required long DetectedAtMs
    {
        get => _detectedAtMs;
        init
        {
            if (value < 0L || value > 9007199254740991L) throw new ArgumentOutOfRangeException(nameof(DetectedAtMs));
            _detectedAtMs = value;
        }
    }
    [JsonPropertyName("osFamily")]
    public required DeviceCapabilityProfileOsFamily OsFamily { get; init; }
    [JsonPropertyName("architecture")]
    public required string Architecture { get; init; }
    private long? _systemMemoryBytes;
    [JsonPropertyName("systemMemoryBytes")]
    public required long? SystemMemoryBytes
    {
        get => _systemMemoryBytes;
        init
        {
            if (value < 0L || value > 9007199254740991L) throw new ArgumentOutOfRangeException(nameof(SystemMemoryBytes));
            _systemMemoryBytes = value;
        }
    }
    private long? _fastMemoryBytes;
    [JsonPropertyName("fastMemoryBytes")]
    public required long? FastMemoryBytes
    {
        get => _fastMemoryBytes;
        init
        {
            if (value < 0L || value > 9007199254740991L) throw new ArgumentOutOfRangeException(nameof(FastMemoryBytes));
            _fastMemoryBytes = value;
        }
    }
    [JsonPropertyName("fastMemoryKind")]
    public required DeviceCapabilityProfileFastMemoryKind FastMemoryKind { get; init; }
    [JsonPropertyName("bandwidthClass")]
    public required DeviceCapabilityProfileBandwidthClass BandwidthClass { get; init; }
    [JsonPropertyName("bandwidthEvidence")]
    public required DeviceCapabilityProfileBandwidthEvidence BandwidthEvidence { get; init; }
    [JsonPropertyName("maximumLocalAiTier")]
    public required DeviceCapabilityProfileMaximumLocalAiTier MaximumLocalAiTier { get; init; }
    [JsonPropertyName("detectionStatus")]
    public required DeviceCapabilityProfileDetectionStatus DetectionStatus { get; init; }
    [JsonPropertyName("limitations")]
    public required IReadOnlyList<DeviceCapabilityProfileLimitations> Limitations { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DeviceCapabilityProfileWire
{
    [JsonPropertyName("schemaVersion")]
    public required DeviceCapabilityProfileSchemaVersion SchemaVersion { get; init; }
    [JsonPropertyName("detectedAtMs")]
    public required long DetectedAtMs { get; init; }
    [JsonPropertyName("osFamily")]
    public required DeviceCapabilityProfileOsFamily OsFamily { get; init; }
    [JsonPropertyName("architecture")]
    public required string Architecture { get; init; }
    [JsonPropertyName("systemMemoryBytes")]
    public required long? SystemMemoryBytes { get; init; }
    [JsonPropertyName("fastMemoryBytes")]
    public required long? FastMemoryBytes { get; init; }
    [JsonPropertyName("fastMemoryKind")]
    public required DeviceCapabilityProfileFastMemoryKind FastMemoryKind { get; init; }
    [JsonPropertyName("bandwidthClass")]
    public required DeviceCapabilityProfileBandwidthClass BandwidthClass { get; init; }
    [JsonPropertyName("bandwidthEvidence")]
    public required DeviceCapabilityProfileBandwidthEvidence BandwidthEvidence { get; init; }
    [JsonPropertyName("maximumLocalAiTier")]
    public required DeviceCapabilityProfileMaximumLocalAiTier MaximumLocalAiTier { get; init; }
    [JsonPropertyName("detectionStatus")]
    public required DeviceCapabilityProfileDetectionStatus DetectionStatus { get; init; }
    [JsonPropertyName("limitations")]
    public required IReadOnlyList<DeviceCapabilityProfileLimitations> Limitations { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

internal sealed class DeviceCapabilityProfileJsonConverter : JsonConverter<DeviceCapabilityProfile>
{
    public override DeviceCapabilityProfile Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateDeviceCapabilityProfile(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<DeviceCapabilityProfileWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("DeviceCapabilityProfile: expected object");
        return new DeviceCapabilityProfile
        {
            SchemaVersion = wire.SchemaVersion,
            DetectedAtMs = wire.DetectedAtMs,
            OsFamily = wire.OsFamily,
            Architecture = wire.Architecture,
            SystemMemoryBytes = wire.SystemMemoryBytes,
            FastMemoryBytes = wire.FastMemoryBytes,
            FastMemoryKind = wire.FastMemoryKind,
            BandwidthClass = wire.BandwidthClass,
            BandwidthEvidence = wire.BandwidthEvidence,
            MaximumLocalAiTier = wire.MaximumLocalAiTier,
            DetectionStatus = wire.DetectionStatus,
            Limitations = wire.Limitations,
            Extensions = wire.Extensions,
        };
    }
    public override void Write(Utf8JsonWriter writer, DeviceCapabilityProfile value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new DeviceCapabilityProfileWire
        {
            SchemaVersion = value.SchemaVersion,
            DetectedAtMs = value.DetectedAtMs,
            OsFamily = value.OsFamily,
            Architecture = value.Architecture,
            SystemMemoryBytes = value.SystemMemoryBytes,
            FastMemoryBytes = value.FastMemoryBytes,
            FastMemoryKind = value.FastMemoryKind,
            BandwidthClass = value.BandwidthClass,
            BandwidthEvidence = value.BandwidthEvidence,
            MaximumLocalAiTier = value.MaximumLocalAiTier,
            DetectionStatus = value.DetectionStatus,
            Limitations = value.Limitations,
            Extensions = value.Extensions,
        }, options);
    }

    private static void ValidateDeviceCapabilityProfile(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("DeviceCapabilityProfile: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "schemaVersion":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("DeviceCapabilityProfile.schemaVersion: value must not be null");
                    break;
                case "detectedAtMs":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("DeviceCapabilityProfile.detectedAtMs: value must not be null");
                    break;
                case "osFamily":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("DeviceCapabilityProfile.osFamily: value must not be null");
                    break;
                case "architecture":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("DeviceCapabilityProfile.architecture: value must not be null");
                    break;
                case "systemMemoryBytes":
                    break;
                case "fastMemoryBytes":
                    break;
                case "fastMemoryKind":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("DeviceCapabilityProfile.fastMemoryKind: value must not be null");
                    break;
                case "bandwidthClass":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("DeviceCapabilityProfile.bandwidthClass: value must not be null");
                    break;
                case "bandwidthEvidence":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("DeviceCapabilityProfile.bandwidthEvidence: value must not be null");
                    break;
                case "maximumLocalAiTier":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("DeviceCapabilityProfile.maximumLocalAiTier: value must not be null");
                    break;
                case "detectionStatus":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("DeviceCapabilityProfile.detectionStatus: value must not be null");
                    break;
                case "limitations":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("DeviceCapabilityProfile.limitations: value must not be null");
                    break;
                case "extensions":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("DeviceCapabilityProfile.extensions: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "schemaVersion", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DeviceCapabilityProfile.schemaVersion: property name must match exact wire casing");
                    if (string.Equals(property.Name, "detectedAtMs", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DeviceCapabilityProfile.detectedAtMs: property name must match exact wire casing");
                    if (string.Equals(property.Name, "osFamily", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DeviceCapabilityProfile.osFamily: property name must match exact wire casing");
                    if (string.Equals(property.Name, "architecture", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DeviceCapabilityProfile.architecture: property name must match exact wire casing");
                    if (string.Equals(property.Name, "systemMemoryBytes", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DeviceCapabilityProfile.systemMemoryBytes: property name must match exact wire casing");
                    if (string.Equals(property.Name, "fastMemoryBytes", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DeviceCapabilityProfile.fastMemoryBytes: property name must match exact wire casing");
                    if (string.Equals(property.Name, "fastMemoryKind", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DeviceCapabilityProfile.fastMemoryKind: property name must match exact wire casing");
                    if (string.Equals(property.Name, "bandwidthClass", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DeviceCapabilityProfile.bandwidthClass: property name must match exact wire casing");
                    if (string.Equals(property.Name, "bandwidthEvidence", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DeviceCapabilityProfile.bandwidthEvidence: property name must match exact wire casing");
                    if (string.Equals(property.Name, "maximumLocalAiTier", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DeviceCapabilityProfile.maximumLocalAiTier: property name must match exact wire casing");
                    if (string.Equals(property.Name, "detectionStatus", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DeviceCapabilityProfile.detectionStatus: property name must match exact wire casing");
                    if (string.Equals(property.Name, "limitations", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DeviceCapabilityProfile.limitations: property name must match exact wire casing");
                    if (string.Equals(property.Name, "extensions", StringComparison.OrdinalIgnoreCase)) throw new JsonException("DeviceCapabilityProfile.extensions: property name must match exact wire casing");
                    throw new JsonException($"DeviceCapabilityProfile: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("schemaVersion", out _)) throw new JsonException("DeviceCapabilityProfile: missing required property schemaVersion");
        if (!value.TryGetProperty("detectedAtMs", out _)) throw new JsonException("DeviceCapabilityProfile: missing required property detectedAtMs");
        if (!value.TryGetProperty("osFamily", out _)) throw new JsonException("DeviceCapabilityProfile: missing required property osFamily");
        if (!value.TryGetProperty("architecture", out _)) throw new JsonException("DeviceCapabilityProfile: missing required property architecture");
        if (!value.TryGetProperty("systemMemoryBytes", out _)) throw new JsonException("DeviceCapabilityProfile: missing required property systemMemoryBytes");
        if (!value.TryGetProperty("fastMemoryBytes", out _)) throw new JsonException("DeviceCapabilityProfile: missing required property fastMemoryBytes");
        if (!value.TryGetProperty("fastMemoryKind", out _)) throw new JsonException("DeviceCapabilityProfile: missing required property fastMemoryKind");
        if (!value.TryGetProperty("bandwidthClass", out _)) throw new JsonException("DeviceCapabilityProfile: missing required property bandwidthClass");
        if (!value.TryGetProperty("bandwidthEvidence", out _)) throw new JsonException("DeviceCapabilityProfile: missing required property bandwidthEvidence");
        if (!value.TryGetProperty("maximumLocalAiTier", out _)) throw new JsonException("DeviceCapabilityProfile: missing required property maximumLocalAiTier");
        if (!value.TryGetProperty("detectionStatus", out _)) throw new JsonException("DeviceCapabilityProfile: missing required property detectionStatus");
        if (!value.TryGetProperty("limitations", out _)) throw new JsonException("DeviceCapabilityProfile: missing required property limitations");
    }
}

[JsonConverter(typeof(PeerSyncConfigJsonConverter))]
public sealed record PeerSyncConfig
{
    [JsonPropertyName("enableMdns")]
    public required bool? EnableMdns { get; init; }
    [JsonPropertyName("peers")]
    public required IReadOnlyList<string> Peers { get; init; }
    [JsonPropertyName("bindAddress")]
    public required string? BindAddress { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PeerSyncConfigWire
{
    [JsonPropertyName("enableMdns")]
    public required bool? EnableMdns { get; init; }
    [JsonPropertyName("peers")]
    public required IReadOnlyList<string> Peers { get; init; }
    [JsonPropertyName("bindAddress")]
    public required string? BindAddress { get; init; }
}

internal sealed class PeerSyncConfigJsonConverter : JsonConverter<PeerSyncConfig>
{
    public override PeerSyncConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidatePeerSyncConfig(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<PeerSyncConfigWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("PeerSyncConfig: expected object");
        return new PeerSyncConfig
        {
            EnableMdns = wire.EnableMdns,
            Peers = wire.Peers,
            BindAddress = wire.BindAddress,
        };
    }
    public override void Write(Utf8JsonWriter writer, PeerSyncConfig value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new PeerSyncConfigWire
        {
            EnableMdns = value.EnableMdns,
            Peers = value.Peers,
            BindAddress = value.BindAddress,
        }, options);
    }

    private static void ValidatePeerSyncConfig(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("PeerSyncConfig: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "enableMdns":
                    break;
                case "peers":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("PeerSyncConfig.peers: value must not be null");
                    break;
                case "bindAddress":
                    break;
                default:
                    if (string.Equals(property.Name, "enableMdns", StringComparison.OrdinalIgnoreCase)) throw new JsonException("PeerSyncConfig.enableMdns: property name must match exact wire casing");
                    if (string.Equals(property.Name, "peers", StringComparison.OrdinalIgnoreCase)) throw new JsonException("PeerSyncConfig.peers: property name must match exact wire casing");
                    if (string.Equals(property.Name, "bindAddress", StringComparison.OrdinalIgnoreCase)) throw new JsonException("PeerSyncConfig.bindAddress: property name must match exact wire casing");
                    throw new JsonException($"PeerSyncConfig: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("enableMdns", out _)) throw new JsonException("PeerSyncConfig: missing required property enableMdns");
        if (!value.TryGetProperty("peers", out _)) throw new JsonException("PeerSyncConfig: missing required property peers");
        if (!value.TryGetProperty("bindAddress", out _)) throw new JsonException("PeerSyncConfig: missing required property bindAddress");
    }
}

[JsonConverter(typeof(RendererLogEntryJsonConverter))]
public sealed record RendererLogEntry
{
    [JsonPropertyName("level")]
    public required string Level { get; init; }
    [JsonPropertyName("message")]
    public required string Message { get; init; }
    [JsonPropertyName("stack")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stack { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RendererLogEntryWire
{
    [JsonPropertyName("level")]
    public required string Level { get; init; }
    [JsonPropertyName("message")]
    public required string Message { get; init; }
    [JsonPropertyName("stack")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stack { get; init; }
}

internal sealed class RendererLogEntryJsonConverter : JsonConverter<RendererLogEntry>
{
    public override RendererLogEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateRendererLogEntry(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<RendererLogEntryWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("RendererLogEntry: expected object");
        return new RendererLogEntry
        {
            Level = wire.Level,
            Message = wire.Message,
            Stack = wire.Stack,
        };
    }
    public override void Write(Utf8JsonWriter writer, RendererLogEntry value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new RendererLogEntryWire
        {
            Level = value.Level,
            Message = value.Message,
            Stack = value.Stack,
        }, options);
    }

    private static void ValidateRendererLogEntry(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("RendererLogEntry: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "level":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("RendererLogEntry.level: value must not be null");
                    break;
                case "message":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("RendererLogEntry.message: value must not be null");
                    break;
                case "stack":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("RendererLogEntry.stack: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "level", StringComparison.OrdinalIgnoreCase)) throw new JsonException("RendererLogEntry.level: property name must match exact wire casing");
                    if (string.Equals(property.Name, "message", StringComparison.OrdinalIgnoreCase)) throw new JsonException("RendererLogEntry.message: property name must match exact wire casing");
                    if (string.Equals(property.Name, "stack", StringComparison.OrdinalIgnoreCase)) throw new JsonException("RendererLogEntry.stack: property name must match exact wire casing");
                    throw new JsonException($"RendererLogEntry: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("level", out _)) throw new JsonException("RendererLogEntry: missing required property level");
        if (!value.TryGetProperty("message", out _)) throw new JsonException("RendererLogEntry: missing required property message");
    }
}

[JsonConverter(typeof(SyncPeerStatusStateJsonConverter))]
public enum SyncPeerStatusState
{
    Has = 0,
    Will = 1,
    Should = 2,
    Couldnt = 3,
}

public sealed class SyncPeerStatusStateJsonConverter : JsonConverter<SyncPeerStatusState>
{
    public override SyncPeerStatusState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "has" => SyncPeerStatusState.Has,
        "will" => SyncPeerStatusState.Will,
        "should" => SyncPeerStatusState.Should,
        "couldnt" => SyncPeerStatusState.Couldnt,
        _ => throw new JsonException("invalid SyncPeerStatusState"),
    };
    public override void Write(Utf8JsonWriter writer, SyncPeerStatusState value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        SyncPeerStatusState.Has => "has",
        SyncPeerStatusState.Will => "will",
        SyncPeerStatusState.Should => "should",
        SyncPeerStatusState.Couldnt => "couldnt",
        _ => throw new JsonException("invalid SyncPeerStatusState"),
    });
}

[JsonConverter(typeof(SyncPeerStatusJsonConverter))]
public sealed record SyncPeerStatus
{
    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }
    [JsonPropertyName("label")]
    public required string Label { get; init; }
    [JsonPropertyName("state")]
    public required SyncPeerStatusState State { get; init; }
    [JsonPropertyName("lastReachedAt")]
    public required string? LastReachedAt { get; init; }
    private long? _offlineDurationMs;
    [JsonPropertyName("offlineDurationMs")]
    public required long? OfflineDurationMs
    {
        get => _offlineDurationMs;
        init
        {
            if (value < 0L || value > 9007199254740991L) throw new ArgumentOutOfRangeException(nameof(OfflineDurationMs));
            _offlineDurationMs = value;
        }
    }
    [JsonPropertyName("isSecurityEvent")]
    public required bool IsSecurityEvent { get; init; }
    [JsonPropertyName("errorCode")]
    public required string? ErrorCode { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SyncPeerStatusWire
{
    [JsonPropertyName("deviceId")]
    public required string DeviceId { get; init; }
    [JsonPropertyName("label")]
    public required string Label { get; init; }
    [JsonPropertyName("state")]
    public required SyncPeerStatusState State { get; init; }
    [JsonPropertyName("lastReachedAt")]
    public required string? LastReachedAt { get; init; }
    [JsonPropertyName("offlineDurationMs")]
    public required long? OfflineDurationMs { get; init; }
    [JsonPropertyName("isSecurityEvent")]
    public required bool IsSecurityEvent { get; init; }
    [JsonPropertyName("errorCode")]
    public required string? ErrorCode { get; init; }
}

internal sealed class SyncPeerStatusJsonConverter : JsonConverter<SyncPeerStatus>
{
    public override SyncPeerStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateSyncPeerStatus(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<SyncPeerStatusWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("SyncPeerStatus: expected object");
        return new SyncPeerStatus
        {
            DeviceId = wire.DeviceId,
            Label = wire.Label,
            State = wire.State,
            LastReachedAt = wire.LastReachedAt,
            OfflineDurationMs = wire.OfflineDurationMs,
            IsSecurityEvent = wire.IsSecurityEvent,
            ErrorCode = wire.ErrorCode,
        };
    }
    public override void Write(Utf8JsonWriter writer, SyncPeerStatus value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new SyncPeerStatusWire
        {
            DeviceId = value.DeviceId,
            Label = value.Label,
            State = value.State,
            LastReachedAt = value.LastReachedAt,
            OfflineDurationMs = value.OfflineDurationMs,
            IsSecurityEvent = value.IsSecurityEvent,
            ErrorCode = value.ErrorCode,
        }, options);
    }

    private static void ValidateSyncPeerStatus(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("SyncPeerStatus: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "deviceId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("SyncPeerStatus.deviceId: value must not be null");
                    break;
                case "label":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("SyncPeerStatus.label: value must not be null");
                    break;
                case "state":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("SyncPeerStatus.state: value must not be null");
                    break;
                case "lastReachedAt":
                    break;
                case "offlineDurationMs":
                    break;
                case "isSecurityEvent":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("SyncPeerStatus.isSecurityEvent: value must not be null");
                    break;
                case "errorCode":
                    break;
                default:
                    if (string.Equals(property.Name, "deviceId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SyncPeerStatus.deviceId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "label", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SyncPeerStatus.label: property name must match exact wire casing");
                    if (string.Equals(property.Name, "state", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SyncPeerStatus.state: property name must match exact wire casing");
                    if (string.Equals(property.Name, "lastReachedAt", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SyncPeerStatus.lastReachedAt: property name must match exact wire casing");
                    if (string.Equals(property.Name, "offlineDurationMs", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SyncPeerStatus.offlineDurationMs: property name must match exact wire casing");
                    if (string.Equals(property.Name, "isSecurityEvent", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SyncPeerStatus.isSecurityEvent: property name must match exact wire casing");
                    if (string.Equals(property.Name, "errorCode", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SyncPeerStatus.errorCode: property name must match exact wire casing");
                    throw new JsonException($"SyncPeerStatus: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("deviceId", out _)) throw new JsonException("SyncPeerStatus: missing required property deviceId");
        if (!value.TryGetProperty("label", out _)) throw new JsonException("SyncPeerStatus: missing required property label");
        if (!value.TryGetProperty("state", out _)) throw new JsonException("SyncPeerStatus: missing required property state");
        if (!value.TryGetProperty("lastReachedAt", out _)) throw new JsonException("SyncPeerStatus: missing required property lastReachedAt");
        if (!value.TryGetProperty("offlineDurationMs", out _)) throw new JsonException("SyncPeerStatus: missing required property offlineDurationMs");
        if (!value.TryGetProperty("isSecurityEvent", out _)) throw new JsonException("SyncPeerStatus: missing required property isSecurityEvent");
        if (!value.TryGetProperty("errorCode", out _)) throw new JsonException("SyncPeerStatus: missing required property errorCode");
    }
}

[JsonConverter(typeof(SyncCadenceJsonConverter))]
public sealed record SyncCadence
{
    [JsonPropertyName("nextRoundAt")]
    public required string? NextRoundAt { get; init; }
    private long _roundIntervalSeconds;
    [JsonPropertyName("roundIntervalSeconds")]
    public required long RoundIntervalSeconds
    {
        get => _roundIntervalSeconds;
        init
        {
            if (value < 0L || value > 9007199254740991L) throw new ArgumentOutOfRangeException(nameof(RoundIntervalSeconds));
            _roundIntervalSeconds = value;
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SyncCadenceWire
{
    [JsonPropertyName("nextRoundAt")]
    public required string? NextRoundAt { get; init; }
    [JsonPropertyName("roundIntervalSeconds")]
    public required long RoundIntervalSeconds { get; init; }
}

internal sealed class SyncCadenceJsonConverter : JsonConverter<SyncCadence>
{
    public override SyncCadence Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateSyncCadence(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<SyncCadenceWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("SyncCadence: expected object");
        return new SyncCadence
        {
            NextRoundAt = wire.NextRoundAt,
            RoundIntervalSeconds = wire.RoundIntervalSeconds,
        };
    }
    public override void Write(Utf8JsonWriter writer, SyncCadence value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new SyncCadenceWire
        {
            NextRoundAt = value.NextRoundAt,
            RoundIntervalSeconds = value.RoundIntervalSeconds,
        }, options);
    }

    private static void ValidateSyncCadence(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("SyncCadence: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "nextRoundAt":
                    break;
                case "roundIntervalSeconds":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("SyncCadence.roundIntervalSeconds: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "nextRoundAt", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SyncCadence.nextRoundAt: property name must match exact wire casing");
                    if (string.Equals(property.Name, "roundIntervalSeconds", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SyncCadence.roundIntervalSeconds: property name must match exact wire casing");
                    throw new JsonException($"SyncCadence: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("nextRoundAt", out _)) throw new JsonException("SyncCadence: missing required property nextRoundAt");
        if (!value.TryGetProperty("roundIntervalSeconds", out _)) throw new JsonException("SyncCadence: missing required property roundIntervalSeconds");
    }
}

[JsonConverter(typeof(EnrollmentStatusJsonConverter))]
public sealed record EnrollmentStatus
{
    [JsonPropertyName("complete")]
    public required bool Complete { get; init; }
    [JsonPropertyName("joinedTeamId")]
    public required string? JoinedTeamId { get; init; }
    [JsonPropertyName("reason")]
    public required string? Reason { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EnrollmentStatusWire
{
    [JsonPropertyName("complete")]
    public required bool Complete { get; init; }
    [JsonPropertyName("joinedTeamId")]
    public required string? JoinedTeamId { get; init; }
    [JsonPropertyName("reason")]
    public required string? Reason { get; init; }
}

internal sealed class EnrollmentStatusJsonConverter : JsonConverter<EnrollmentStatus>
{
    public override EnrollmentStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateEnrollmentStatus(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<EnrollmentStatusWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("EnrollmentStatus: expected object");
        return new EnrollmentStatus
        {
            Complete = wire.Complete,
            JoinedTeamId = wire.JoinedTeamId,
            Reason = wire.Reason,
        };
    }
    public override void Write(Utf8JsonWriter writer, EnrollmentStatus value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new EnrollmentStatusWire
        {
            Complete = value.Complete,
            JoinedTeamId = value.JoinedTeamId,
            Reason = value.Reason,
        }, options);
    }

    private static void ValidateEnrollmentStatus(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("EnrollmentStatus: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "complete":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("EnrollmentStatus.complete: value must not be null");
                    break;
                case "joinedTeamId":
                    break;
                case "reason":
                    break;
                default:
                    if (string.Equals(property.Name, "complete", StringComparison.OrdinalIgnoreCase)) throw new JsonException("EnrollmentStatus.complete: property name must match exact wire casing");
                    if (string.Equals(property.Name, "joinedTeamId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("EnrollmentStatus.joinedTeamId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "reason", StringComparison.OrdinalIgnoreCase)) throw new JsonException("EnrollmentStatus.reason: property name must match exact wire casing");
                    throw new JsonException($"EnrollmentStatus: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("complete", out _)) throw new JsonException("EnrollmentStatus: missing required property complete");
        if (!value.TryGetProperty("joinedTeamId", out _)) throw new JsonException("EnrollmentStatus: missing required property joinedTeamId");
        if (!value.TryGetProperty("reason", out _)) throw new JsonException("EnrollmentStatus: missing required property reason");
    }
}

[JsonConverter(typeof(LastPeerExchangeJsonConverter))]
public sealed record LastPeerExchange
{
    [JsonPropertyName("peerDeviceId")]
    public required string PeerDeviceId { get; init; }
    [JsonPropertyName("peerLabel")]
    public required string PeerLabel { get; init; }
    [JsonPropertyName("exchangedAt")]
    public required string ExchangedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record LastPeerExchangeWire
{
    [JsonPropertyName("peerDeviceId")]
    public required string PeerDeviceId { get; init; }
    [JsonPropertyName("peerLabel")]
    public required string PeerLabel { get; init; }
    [JsonPropertyName("exchangedAt")]
    public required string ExchangedAt { get; init; }
}

internal sealed class LastPeerExchangeJsonConverter : JsonConverter<LastPeerExchange>
{
    public override LastPeerExchange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateLastPeerExchange(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<LastPeerExchangeWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("LastPeerExchange: expected object");
        return new LastPeerExchange
        {
            PeerDeviceId = wire.PeerDeviceId,
            PeerLabel = wire.PeerLabel,
            ExchangedAt = wire.ExchangedAt,
        };
    }
    public override void Write(Utf8JsonWriter writer, LastPeerExchange value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new LastPeerExchangeWire
        {
            PeerDeviceId = value.PeerDeviceId,
            PeerLabel = value.PeerLabel,
            ExchangedAt = value.ExchangedAt,
        }, options);
    }

    private static void ValidateLastPeerExchange(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("LastPeerExchange: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "peerDeviceId":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("LastPeerExchange.peerDeviceId: value must not be null");
                    break;
                case "peerLabel":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("LastPeerExchange.peerLabel: value must not be null");
                    break;
                case "exchangedAt":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("LastPeerExchange.exchangedAt: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "peerDeviceId", StringComparison.OrdinalIgnoreCase)) throw new JsonException("LastPeerExchange.peerDeviceId: property name must match exact wire casing");
                    if (string.Equals(property.Name, "peerLabel", StringComparison.OrdinalIgnoreCase)) throw new JsonException("LastPeerExchange.peerLabel: property name must match exact wire casing");
                    if (string.Equals(property.Name, "exchangedAt", StringComparison.OrdinalIgnoreCase)) throw new JsonException("LastPeerExchange.exchangedAt: property name must match exact wire casing");
                    throw new JsonException($"LastPeerExchange: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("peerDeviceId", out _)) throw new JsonException("LastPeerExchange: missing required property peerDeviceId");
        if (!value.TryGetProperty("peerLabel", out _)) throw new JsonException("LastPeerExchange: missing required property peerLabel");
        if (!value.TryGetProperty("exchangedAt", out _)) throw new JsonException("LastPeerExchange: missing required property exchangedAt");
    }
}

[JsonConverter(typeof(SyncRecencyJsonConverter))]
public sealed record SyncRecency
{
    [JsonPropertyName("basis")]
    public required string Basis { get; init; }
    [JsonPropertyName("currentness")]
    public required string Currentness { get; init; }
    [JsonPropertyName("lastExchange")]
    public required LastPeerExchange? LastExchange { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SyncRecencyWire
{
    [JsonPropertyName("basis")]
    public required string Basis { get; init; }
    [JsonPropertyName("currentness")]
    public required string Currentness { get; init; }
    [JsonPropertyName("lastExchange")]
    public required LastPeerExchange? LastExchange { get; init; }
}

internal sealed class SyncRecencyJsonConverter : JsonConverter<SyncRecency>
{
    public override SyncRecency Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateSyncRecency(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<SyncRecencyWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("SyncRecency: expected object");
        return new SyncRecency
        {
            Basis = wire.Basis,
            Currentness = wire.Currentness,
            LastExchange = wire.LastExchange,
        };
    }
    public override void Write(Utf8JsonWriter writer, SyncRecency value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new SyncRecencyWire
        {
            Basis = value.Basis,
            Currentness = value.Currentness,
            LastExchange = value.LastExchange,
        }, options);
    }

    private static void ValidateSyncRecency(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("SyncRecency: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "basis":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("SyncRecency.basis: value must not be null");
                    break;
                case "currentness":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("SyncRecency.currentness: value must not be null");
                    break;
                case "lastExchange":
                    break;
                default:
                    if (string.Equals(property.Name, "basis", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SyncRecency.basis: property name must match exact wire casing");
                    if (string.Equals(property.Name, "currentness", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SyncRecency.currentness: property name must match exact wire casing");
                    if (string.Equals(property.Name, "lastExchange", StringComparison.OrdinalIgnoreCase)) throw new JsonException("SyncRecency.lastExchange: property name must match exact wire casing");
                    throw new JsonException($"SyncRecency: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("basis", out _)) throw new JsonException("SyncRecency: missing required property basis");
        if (!value.TryGetProperty("currentness", out _)) throw new JsonException("SyncRecency: missing required property currentness");
        if (!value.TryGetProperty("lastExchange", out _)) throw new JsonException("SyncRecency: missing required property lastExchange");
    }
}

[JsonConverter(typeof(HarborlineSyncStatusAggregateJsonConverter))]
public enum HarborlineSyncStatusAggregate
{
    Has = 0,
    Will = 1,
    Should = 2,
    Couldnt = 3,
}

public sealed class HarborlineSyncStatusAggregateJsonConverter : JsonConverter<HarborlineSyncStatusAggregate>
{
    public override HarborlineSyncStatusAggregate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "has" => HarborlineSyncStatusAggregate.Has,
        "will" => HarborlineSyncStatusAggregate.Will,
        "should" => HarborlineSyncStatusAggregate.Should,
        "couldnt" => HarborlineSyncStatusAggregate.Couldnt,
        _ => throw new JsonException("invalid HarborlineSyncStatusAggregate"),
    };
    public override void Write(Utf8JsonWriter writer, HarborlineSyncStatusAggregate value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        HarborlineSyncStatusAggregate.Has => "has",
        HarborlineSyncStatusAggregate.Will => "will",
        HarborlineSyncStatusAggregate.Should => "should",
        HarborlineSyncStatusAggregate.Couldnt => "couldnt",
        _ => throw new JsonException("invalid HarborlineSyncStatusAggregate"),
    });
}

[JsonConverter(typeof(HarborlineSyncStatusJsonConverter))]
public sealed record HarborlineSyncStatus
{
    [JsonPropertyName("aggregate")]
    public required HarborlineSyncStatusAggregate Aggregate { get; init; }
    [JsonPropertyName("asOf")]
    public required string AsOf { get; init; }
    [JsonPropertyName("peers")]
    public required IReadOnlyList<SyncPeerStatus> Peers { get; init; }
    [JsonPropertyName("cadence")]
    public required SyncCadence Cadence { get; init; }
    [JsonPropertyName("recency")]
    public required SyncRecency Recency { get; init; }
    [JsonPropertyName("enrollment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EnrollmentStatus? Enrollment { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record HarborlineSyncStatusWire
{
    [JsonPropertyName("aggregate")]
    public required HarborlineSyncStatusAggregate Aggregate { get; init; }
    [JsonPropertyName("asOf")]
    public required string AsOf { get; init; }
    [JsonPropertyName("peers")]
    public required IReadOnlyList<SyncPeerStatus> Peers { get; init; }
    [JsonPropertyName("cadence")]
    public required SyncCadence Cadence { get; init; }
    [JsonPropertyName("recency")]
    public required SyncRecency Recency { get; init; }
    [JsonPropertyName("enrollment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EnrollmentStatus? Enrollment { get; init; }
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

internal sealed class HarborlineSyncStatusJsonConverter : JsonConverter<HarborlineSyncStatus>
{
    public override HarborlineSyncStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        ValidateHarborlineSyncStatus(value);
        var wireOptions = new JsonSerializerOptions(options) { PropertyNameCaseInsensitive = false };
        var wire = JsonSerializer.Deserialize<HarborlineSyncStatusWire>(value.GetRawText(), wireOptions) ?? throw new JsonException("HarborlineSyncStatus: expected object");
        return new HarborlineSyncStatus
        {
            Aggregate = wire.Aggregate,
            AsOf = wire.AsOf,
            Peers = wire.Peers,
            Cadence = wire.Cadence,
            Recency = wire.Recency,
            Enrollment = wire.Enrollment,
            Extensions = wire.Extensions,
        };
    }
    public override void Write(Utf8JsonWriter writer, HarborlineSyncStatus value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new HarborlineSyncStatusWire
        {
            Aggregate = value.Aggregate,
            AsOf = value.AsOf,
            Peers = value.Peers,
            Cadence = value.Cadence,
            Recency = value.Recency,
            Enrollment = value.Enrollment,
            Extensions = value.Extensions,
        }, options);
    }

    private static void ValidateHarborlineSyncStatus(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("HarborlineSyncStatus: expected object");
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "aggregate":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("HarborlineSyncStatus.aggregate: value must not be null");
                    break;
                case "asOf":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("HarborlineSyncStatus.asOf: value must not be null");
                    break;
                case "peers":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("HarborlineSyncStatus.peers: value must not be null");
                    break;
                case "cadence":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("HarborlineSyncStatus.cadence: value must not be null");
                    break;
                case "recency":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("HarborlineSyncStatus.recency: value must not be null");
                    break;
                case "enrollment":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("HarborlineSyncStatus.enrollment: value must not be null");
                    break;
                case "extensions":
                    if (property.Value.ValueKind == JsonValueKind.Null) throw new JsonException("HarborlineSyncStatus.extensions: value must not be null");
                    break;
                default:
                    if (string.Equals(property.Name, "aggregate", StringComparison.OrdinalIgnoreCase)) throw new JsonException("HarborlineSyncStatus.aggregate: property name must match exact wire casing");
                    if (string.Equals(property.Name, "asOf", StringComparison.OrdinalIgnoreCase)) throw new JsonException("HarborlineSyncStatus.asOf: property name must match exact wire casing");
                    if (string.Equals(property.Name, "peers", StringComparison.OrdinalIgnoreCase)) throw new JsonException("HarborlineSyncStatus.peers: property name must match exact wire casing");
                    if (string.Equals(property.Name, "cadence", StringComparison.OrdinalIgnoreCase)) throw new JsonException("HarborlineSyncStatus.cadence: property name must match exact wire casing");
                    if (string.Equals(property.Name, "recency", StringComparison.OrdinalIgnoreCase)) throw new JsonException("HarborlineSyncStatus.recency: property name must match exact wire casing");
                    if (string.Equals(property.Name, "enrollment", StringComparison.OrdinalIgnoreCase)) throw new JsonException("HarborlineSyncStatus.enrollment: property name must match exact wire casing");
                    if (string.Equals(property.Name, "extensions", StringComparison.OrdinalIgnoreCase)) throw new JsonException("HarborlineSyncStatus.extensions: property name must match exact wire casing");
                    throw new JsonException($"HarborlineSyncStatus: unexpected property {property.Name}");
            }
        }
        if (!value.TryGetProperty("aggregate", out _)) throw new JsonException("HarborlineSyncStatus: missing required property aggregate");
        if (!value.TryGetProperty("asOf", out _)) throw new JsonException("HarborlineSyncStatus: missing required property asOf");
        if (!value.TryGetProperty("peers", out _)) throw new JsonException("HarborlineSyncStatus: missing required property peers");
        if (!value.TryGetProperty("cadence", out _)) throw new JsonException("HarborlineSyncStatus: missing required property cadence");
        if (!value.TryGetProperty("recency", out _)) throw new JsonException("HarborlineSyncStatus: missing required property recency");
    }
}

public interface ICapabilityPort
{
    Task<AnnounceResult> AnnounceAsync(AnnounceRequest request, CancellationToken cancellationToken = default);
    Task<NegotiateResult> NegotiateAsync(NegotiateRequest request, CancellationToken cancellationToken = default);
    Task<AddressResult> AddressAsync(AddressRequest request, CancellationToken cancellationToken = default);
    Task<SecureResult> SecureAsync(SecureRequest request, CancellationToken cancellationToken = default);
    Task<CapabilityResult> InvokeAsync(CapabilityInvokeRequest request, CancellationToken cancellationToken = default);
    Task<HealthReport> ObserveAsync(ObserveRequest request, CancellationToken cancellationToken = default);
    Task<ResolutionResult> ResolveAsync(ResolveRequest request, CancellationToken cancellationToken = default);
    Task<ComposeResult> ComposeAsync(ComposeRequest request, CancellationToken cancellationToken = default);
}

public interface IHarborlineHostPort
{
    Task<CapabilityResult> CapabilityInvokeAsync(CapabilityHostInvokeRequest request, CancellationToken cancellationToken = default);
    Task<HealthReport> CapabilityHealthAsync(EmptyRequest request, CancellationToken cancellationToken = default);
    Task<CpDemoResult> CapabilityCpDemoExecuteAsync(CpDemoRequest request, CancellationToken cancellationToken = default);
    Task<Principal> CurrentPrincipalAsync(EmptyRequest request, CancellationToken cancellationToken = default);
    Task<NodeStatus> NodeStatusAsync(EmptyRequest request, CancellationToken cancellationToken = default);
    Task<DataLocationStatus> DataLocationStatusAsync(EmptyRequest request, CancellationToken cancellationToken = default);
    Task<DeviceCapabilityProfile> DeviceCapabilityProfileAsync(EmptyRequest request, CancellationToken cancellationToken = default);
    Task<PeerSyncConfig> GetPeerSyncConfigAsync(EmptyRequest request, CancellationToken cancellationToken = default);
    Task<EmptyResponse> SetPeerSyncConfigAsync(PeerSyncConfig request, CancellationToken cancellationToken = default);
    Task<EmptyResponse> AppendRendererLogAsync(RendererLogEntry request, CancellationToken cancellationToken = default);
}

public interface IHarborlineApplicationPort
{
    Task<HarborlineSyncStatus> GetSyncStatusAsync(EmptyRequest request, CancellationToken cancellationToken = default);
}

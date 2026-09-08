using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Recovery.LegalHold;

/// <summary>
/// A durable, append-only, restart-surviving <see cref="ILegalHoldStore"/> backed by
/// a single JSON-lines file (ADR 0142 §D4). Every placed hold and every release is a
/// line appended to the file and flushed to disk; on construction the file is
/// replayed to rebuild the in-memory indices, so the state survives a process restart
/// — a held subject stays held, and the fail-closed shred gate keeps refusing.
/// </summary>
/// <remarks>
/// <para>
/// This is the lightweight durable option (no database dependency) — suitable for a
/// single-node host. It is genuinely append-only (records are never rewritten or
/// removed) and it is NOT the restart-volatile in-memory default, so it satisfies the
/// <c>RequireDurableLegalHoldStore()</c> composition-time gate. A host backed by an
/// encrypted-database index (mirroring the EF-backed erasure stores) is an equally
/// valid durable choice; the gate accepts any non-default type.
/// </para>
/// <para>
/// A file/IO fault on the read path PROPAGATES to the caller. The registry
/// (<see cref="LegalHoldRegistry"/>) turns any such fault into a fail-closed
/// "held" answer on the shred gate — an unreadable hold store must never be
/// mistaken for "nothing is held".
/// </para>
/// </remarks>
public sealed class FileSystemLegalHoldStore : ILegalHoldStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<(string Tenant, string Hold), LegalHoldEntry> _holds = new();
    private readonly HashSet<(string Tenant, string Hold)> _released = new();

    /// <summary>Open (creating the parent directory if needed) and replay the hold log at <paramref name="path"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null/empty/whitespace.</exception>
    public FileSystemLegalHoldStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A hold-log file path is required.", nameof(path));
        }
        _path = path;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        Replay();
    }

    /// <inheritdoc />
    public ValueTask AppendHoldAsync(LegalHoldEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            AppendLine(Line.FromHold(entry));
            _holds[(entry.TenantId.Value, entry.HoldId.Value)] = entry;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask AppendReleaseAsync(LegalHoldRelease release, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        lock (_gate)
        {
            AppendLine(Line.FromRelease(release));
            _released.Add((release.TenantId.Value, release.HoldId.Value));
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> HasActiveHoldAsync(TenantId tenant, HeldRef heldRef, CancellationToken ct = default)
    {
        var canonical = heldRef.Canonical;
        lock (_gate)
        {
            var held = _holds.Values.Any(h =>
                h.TenantId.Value == tenant.Value
                && h.HeldRef.Canonical == canonical
                && !_released.Contains((h.TenantId.Value, h.HoldId.Value)));
            return ValueTask.FromResult(held);
        }
    }

    /// <inheritdoc />
    public ValueTask<LegalHoldEntry?> FindHoldAsync(TenantId tenant, LegalHoldId holdId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _holds.TryGetValue((tenant.Value, holdId.Value), out var entry);
            return ValueTask.FromResult(entry);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> IsReleasedAsync(TenantId tenant, LegalHoldId holdId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_released.Contains((tenant.Value, holdId.Value)));
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<LegalHoldEntry>> ListActiveAsync(TenantId tenant, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<LegalHoldEntry> active = _holds.Values
                .Where(h => h.TenantId.Value == tenant.Value
                            && !_released.Contains((h.TenantId.Value, h.HoldId.Value)))
                .ToList();
            return ValueTask.FromResult(active);
        }
    }

    private void AppendLine(Line line)
    {
        var json = JsonSerializer.Serialize(line, Json);
        // Append + flush so the record is durable before the call returns.
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(json);
        writer.Flush();
    }

    private void Replay()
    {
        if (!File.Exists(_path))
        {
            return;
        }
        foreach (var raw in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            var line = JsonSerializer.Deserialize<Line>(raw, Json)
                       ?? throw new InvalidDataException($"Corrupt legal-hold log line: {raw}");
            if (line.Kind == "release")
            {
                _released.Add((line.Tenant!, line.HoldId!));
            }
            else
            {
                var entry = line.ToHold();
                _holds[(entry.TenantId.Value, entry.HoldId.Value)] = entry;
            }
        }
    }

    /// <summary>The flat, string-only persistence DTO — one JSON object per log line.</summary>
    private sealed record Line
    {
        public string Kind { get; init; } = "hold";
        public string? HoldId { get; init; }
        public string? Tenant { get; init; }
        public string? RefKind { get; init; }
        public string? RefValue { get; init; }
        public string? Matter { get; init; }
        public string? PlacedBy { get; init; }
        public DateTimeOffset? PlacedAtUtc { get; init; }
        public string[]? Approvers { get; init; }
        public string? Reason { get; init; }
        public DateTimeOffset? ReleasedAtUtc { get; init; }

        public static Line FromHold(LegalHoldEntry e) => new()
        {
            Kind = "hold",
            HoldId = e.HoldId.Value,
            Tenant = e.TenantId.Value,
            RefKind = e.HeldRef.Kind.ToString(),
            RefValue = e.HeldRef.Value,
            Matter = e.Matter,
            PlacedBy = e.PlacedBy.Value,
            PlacedAtUtc = e.PlacedAtUtc,
        };

        public static Line FromRelease(LegalHoldRelease r) => new()
        {
            Kind = "release",
            HoldId = r.HoldId.Value,
            Tenant = r.TenantId.Value,
            Approvers = r.Approvers.Select(a => a.Value).ToArray(),
            Reason = r.Reason,
            ReleasedAtUtc = r.ReleasedAtUtc,
        };

        public LegalHoldEntry ToHold()
        {
            var kind = Enum.Parse<HeldRefKind>(RefKind ?? throw new InvalidDataException("hold line missing refKind"));
            return new LegalHoldEntry(
                new LegalHoldId(HoldId ?? throw new InvalidDataException("hold line missing holdId")),
                TenantId.FromString(Tenant ?? throw new InvalidDataException("hold line missing tenant")),
                HeldRef.Rehydrate(kind, RefValue ?? throw new InvalidDataException("hold line missing refValue")),
                Matter ?? string.Empty,
                new ActorId(PlacedBy ?? throw new InvalidDataException("hold line missing placedBy")),
                PlacedAtUtc ?? default);
        }
    }
}

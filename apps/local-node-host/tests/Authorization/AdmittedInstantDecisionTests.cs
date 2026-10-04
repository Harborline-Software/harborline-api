using System.Reflection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// T-1015 (DES-0029 ck-5 and ck-9, board finding F2): the decision instant is the server's. The oracle is the
/// test's fixed clock: a grant in force until <see cref="GrantEnds"/>, a clock that reads after it, and a stale
/// instant from before it that a caller would like the decision to rest on.
/// </summary>
public sealed class AdmittedInstantDecisionTests
{
    private static readonly TenantId Tenant = TenantId.FromString("tenant-t1015");
    private static readonly ActorId Principal = new("principal-t1015");
    private static readonly AuthorizationOperation RecordsWrite = AuthorizationOperation.Parse("records:write");
    private static readonly DateTimeOffset GrantEnds = DateTimeOffset.Parse("2020-03-01T00:00:00Z");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2020-03-01T01:00:00Z");
    private static readonly DateTimeOffset Stale = DateTimeOffset.Parse("2020-02-29T23:00:00Z");

    // The grant's in-force window, decided at whatever instant the request carries.
    private static AuthorizationGate WindowGate() => TestAuthorization.Gate(request => request.At < GrantEnds);

    [Fact]
    public async Task PlantedStaleCallerInstant_CannotChangeTheDecision()
    {
        var clock = new FixedClock(Now);
        var authority = Plant(new AuthorizationWriteContext(Principal, Tenant, AdmittedInstant.Read(clock)), Stale);
        var request = Plant(authority.Request(RecordsWrite, "record", "a"), Stale);

        var decision = await WindowGate().DecideAsync(request);

        Assert.Equal(AuthorizationVerdict.Denied, decision.Verdict);
        Assert.Equal(Now, decision.DecidedAt);
        Assert.Equal(Now, decision.Request.At);
        Assert.Equal(1, clock.Reads);

        // The oracle holds: had the decision rested on the stale instant, the grant would have allowed it.
        var atStale = await WindowGate().DecideAsync(new AuthorizationWriteContext(
            Principal, Tenant, AdmittedInstant.FromRecordedAct(Stale)).Request(RecordsWrite, "record", "a"));
        Assert.Equal(AuthorizationVerdict.Allowed, atStale.Verdict);
    }

    [Fact]
    public async Task PastAct_IsDecidedAsOfItsStoredRecordedAt_NotNow()
    {
        // L143: a past act is decided as of the instant the server recorded for it. The act was recorded while
        // the grant was in force; the clock now reads after the grant ended, and a past-act decision must not
        // care. All instants lie in the past, so a mint that read any clock instead would fall after GrantEnds.
        var clock = new FixedClock(Now);
        var stored = new StoredAct(Principal, Tenant, RecordedAt: Stale);

        var decision = await WindowGate().DecideAsync(new AuthorizationWriteContext(
            stored.Principal, stored.Tenant, AdmittedInstant.FromRecordedAct(stored.RecordedAt))
            .Request(RecordsWrite, "record", "a"));

        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
        Assert.Equal(Stale, decision.DecidedAt);
        Assert.Equal(0, clock.Reads);

        // The same act decided live, from the clock, is refused: the two instants really differ.
        var live = await WindowGate().DecideAsync(new AuthorizationWriteContext(
            Principal, Tenant, AdmittedInstant.Read(clock)).Request(RecordsWrite, "record", "a"));
        Assert.Equal(AuthorizationVerdict.Denied, live.Verdict);
    }

    private sealed record StoredAct(ActorId Principal, TenantId Tenant, DateTimeOffset RecordedAt);

    /// <summary>
    /// Sets every caller-reachable raw instant on <paramref name="value"/> (a public or internal settable
    /// <see cref="DateTimeOffset"/> property or field) to <paramref name="instant"/>, on a copy, as a caller's
    /// object initializer or <c>with</c> expression would.
    /// </summary>
    private static T Plant<T>(T value, DateTimeOffset instant) where T : notnull
    {
        object copy = typeof(T).IsValueType
            ? value
            : typeof(T).GetMethod("<Clone>$")!.Invoke(value, null)!;
        const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var property in typeof(T).GetProperties(Members)
            .Where(property => IsRawInstant(property.PropertyType) && property.SetMethod is { IsPrivate: false }))
            property.SetValue(copy, instant);
        foreach (var field in typeof(T).GetFields(Members)
            .Where(field => IsRawInstant(field.FieldType) && !field.IsPrivate && !field.IsInitOnly))
            field.SetValue(copy, instant);
        return (T)copy;
    }

    private static bool IsRawInstant(Type type) => type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public int Reads { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            return now;
        }
    }
}

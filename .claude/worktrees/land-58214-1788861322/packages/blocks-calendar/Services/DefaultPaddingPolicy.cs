using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Blocks.Calendar.Services;

/// <summary>
/// The default in-core <see cref="IPaddingPolicy"/> — returns one configured <see cref="EventPadding"/>
/// for every booking (or <see cref="EventPadding.None"/> when constructed with no padding). This is
/// the minimal common implementation: enough to say "every appointment on this tenant gets 5-min pre +
/// 30-min post" via DI configuration, without the core knowing about specialties or event types. A
/// vertical Pack registers a richer <see cref="IPaddingPolicy"/> (looking up padding by resource
/// specialty / event type / clinic config) over this one.
/// </summary>
/// <remarks>
/// The default registration (no configuration) is <see cref="EventPadding.None"/> — so the calendar
/// block ships with padding OFF (every event occupies exactly its visible interval, the
/// backward-compatible behavior) until a deployment opts in by configuring a non-zero default.
/// </remarks>
public sealed class DefaultPaddingPolicy : IPaddingPolicy
{
    private readonly EventPadding _default;

    /// <summary>Create a policy that returns <paramref name="defaultPadding"/> for every booking. Defaults to <see cref="EventPadding.None"/> (padding off).</summary>
    public DefaultPaddingPolicy(EventPadding defaultPadding = default)
    {
        // `default(EventPadding)` is (Zero, Zero) = None, so the parameterless / DI default is "off".
        _default = defaultPadding;
    }

    /// <inheritdoc />
    public EventPadding ResolveDefault(TenantId tenantId, ParticipantRef resourceRef)
    {
        ArgumentNullException.ThrowIfNull(resourceRef);
        return _default;
    }
}

namespace Harborline.Api.Foundation.Packs.Model;

/// <summary>
/// The scope-tier of a pack (ADR 0129 D1 <c>scope-tier</c>; ADR 0111 "compose over, never
/// beneath"). The kernel is the always-on floor and is never packaged, so a pack is either a
/// <see cref="Horizontal"/> (platform-service / business-domain) or a <see cref="Vertical"/>.
/// </summary>
public enum PackScopeTier
{
    /// <summary>A horizontal pack — a platform-service (1a) or business-domain (1b) horizontal
    /// that composes over the kernel floor.</summary>
    Horizontal = 0,

    /// <summary>A vertical pack — a domain vertical that composes over horizontals (ADR 0111).</summary>
    Vertical = 1,
}

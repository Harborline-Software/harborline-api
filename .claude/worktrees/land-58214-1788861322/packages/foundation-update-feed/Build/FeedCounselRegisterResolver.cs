using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.UpdateFeed.Contract;

namespace Harborline.Api.Foundation.UpdateFeed.Build;

/// <summary>
/// Resolves the DCP counsel register for one channel-root-signed feed policy. Ordinary feeds retain the
/// vendor-governed register; a self-governed feed uses its signed allowlist as its feed-local cleared set
/// (ADR 0153 D3). This changes no trust root: the policy is signed by and CID-coupled to the channel root.
/// </summary>
public sealed class FeedCounselRegisterResolver
{
    private readonly IDcpCounselRegister _governedRegister;

    /// <summary>Constructs a resolver over the register used by ordinary governed feeds.</summary>
    public FeedCounselRegisterResolver(IDcpCounselRegister governedRegister)
    {
        _governedRegister = governedRegister ?? throw new ArgumentNullException(nameof(governedRegister));
    }

    /// <summary>Resolves the register whose cleared set applies to <paramref name="policy"/>.</summary>
    public IDcpCounselRegister Resolve(FeedPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return policy.SelfGoverned
            ? new DcpCounselRegister(policy.AllowedRegulatoryClasses)
            : _governedRegister;
    }
}

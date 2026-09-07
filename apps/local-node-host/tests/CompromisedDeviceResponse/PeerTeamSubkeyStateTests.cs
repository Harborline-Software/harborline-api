using Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.CompromisedDeviceResponse;

public sealed class PeerTeamSubkeyStateTests
{
    [Fact]
    public void ApplyRotation_stops_using_the_subkey_shared_with_the_revoked_node()
    {
        byte[] exposedSubkey = [0x10, 0x20, 0x30, 0x40];
        byte[] replacementSubkey = [0x50, 0x60, 0x70, 0x80];
        var peer = new PeerTeamSubkeyState("team-a", epoch: 7, exposedSubkey);

        peer.ApplyRotation(new TeamSubkeyRotation("team-a", epoch: 8, replacementSubkey));

        Assert.False(peer.UsesSubkey(exposedSubkey));
        Assert.True(peer.UsesSubkey(replacementSubkey));
        Assert.Equal(8, peer.Epoch);
    }
}

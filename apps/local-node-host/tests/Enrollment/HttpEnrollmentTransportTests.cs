using System.Net;
using System.Text.Json;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Tests.Enrollment;

public sealed class HttpEnrollmentTransportTests
{
    [Fact(DisplayName = "HTTP enrollment transport carries both signed confidentiality keys")]
    public async Task SendAsync_Carries_Dm_And_XWing_Keys()
    {
        var handler = new RecordingHandler();
        var transport = new HttpEnrollmentTransport(
            new FixedHttpClientFactory(handler),
            () => new Uri("http://127.0.0.1:7474/"));
        var principal = KeyPair.Generate();
        var request = WireEnrollment.BuildRequest(
            Guid.NewGuid().ToString("N"),
            "joiner",
            KeyPair.Generate().PrincipalId.AsSpan().ToArray(),
            new Ed25519Signer(principal),
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            joiningDmPublicKey: KeyPair.Generate().PrincipalId.AsSpan().ToArray(),
            joiningXWingPublicKey: "xwing-public-key");

        var response = await transport.SendAsync(request, CancellationToken.None);

        Assert.Null(response);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(request.JoiningDmPublicKey, json.RootElement.GetProperty("joiningDmKey").GetString());
        Assert.Equal(request.JoiningXWingPublicKey, json.RootElement.GetProperty("joiningXWingKey").GetString());
    }

    private sealed class FixedHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }
    }
}

using System.Net;
using System.Text;
using Harborline.Api.Contracts;
using Harborline.Api.Protocol.Client;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class AuthorizationAdminClientTests
{
    [Fact]
    public async Task DtoBackedMethods_UseExactRoutesAndIdempotencyHeader()
    {
        var requests = new List<(HttpMethod Method, string Path, string? IdempotencyKey)>();
        using var http = new HttpClient(new RecordingHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null));
            var json = request.RequestUri.AbsolutePath switch
            {
                "/health" => "{}",
                var path when path.EndsWith("/role-vocabulary", StringComparison.Ordinal) => "[]",
                var path when path.EndsWith("/capability-definitions", StringComparison.Ordinal) => "[]",
                var path when path.EndsWith("/binding", StringComparison.Ordinal) && request.Method == HttpMethod.Get =>
                    "{\"revision\":0,\"effectiveRoles\":[],\"warning\":null}",
                var path when path.EndsWith("/binding", StringComparison.Ordinal) =>
                    "{\"definitionId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"revision\":1,\"effectiveRoles\":[],\"warning\":\"EmptyBinding\",\"changedBy\":\"actor\",\"changedAt\":\"2026-09-02T00:00:00Z\",\"reason\":\"clear\"}",
                var path when path.EndsWith("/standing-catalogue", StringComparison.Ordinal) => "[]",
                _ => throw new InvalidOperationException(request.RequestUri.AbsolutePath),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        })) { BaseAddress = new Uri("http://localhost") };
        using var client = new HarborlineClient(http);
        await client.BootstrapAsync(new Uri("http://localhost"), "token", default);
        var id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        Assert.Empty(await client.ListRoleVocabularyAsync());
        Assert.Empty(await client.ListAuthorizationDefinitionsAsync());
        Assert.Empty((await client.GetAuthorizationBindingAsync(id)).EffectiveRoles);
        Assert.Equal("EmptyBinding", (await client.NarrowAuthorizationBindingAsync(
            id, new NarrowAuthorizationBindingRequest([], "clear"), "client-key")).Warning);
        Assert.Empty(await client.ListStandingCatalogueAsync());

        Assert.Contains(requests, request => request.Path.EndsWith("/role-vocabulary", StringComparison.Ordinal));
        Assert.Contains(requests, request => request.Path.EndsWith("/capability-definitions", StringComparison.Ordinal));
        Assert.Contains(requests, request => request.Method == HttpMethod.Post
            && request.Path.EndsWith("/binding", StringComparison.Ordinal)
            && request.IdempotencyKey == "client-key");
        Assert.Contains(requests, request => request.Path.EndsWith("/standing-catalogue", StringComparison.Ordinal));
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}

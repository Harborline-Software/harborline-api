using Harborline.Api.Contracts;
using Harborline.Api.Testing;

IHarborlineApiClient client = new FixtureHarborlineApiClient();
var response = await client.SendAsync(new("GET", "/clean-consumer", new("tenant", "user")));
return response.Status == 404 ? 0 : 1;

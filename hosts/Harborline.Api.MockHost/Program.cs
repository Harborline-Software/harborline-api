using Harborline.Api.Contracts;
using Harborline.Api.Testing;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<FixtureHarborlineApiClient>();
builder.Services.AddSingleton<IHarborlineApiClient>(services => services.GetRequiredService<FixtureHarborlineApiClient>());
builder.Services.AddSingleton<IHarborlineRuntimeAdapter>(services => services.GetRequiredService<FixtureHarborlineApiClient>());

var app = builder.Build();
HarborlineRuntimeGuard.EnsureEnvironmentAllows(app.Environment.EnvironmentName, app.Services.GetServices<IHarborlineRuntimeAdapter>());

app.MapGet("/health", () => Results.Ok(new { status = "ready", adapter = "development-only-fixture" }));
app.Run();

public partial class Program;

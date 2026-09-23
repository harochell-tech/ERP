using Rochell.Platform.Hosting;

// PR-01: empty host. No endpoints until PR-18 (baseline §17).
var builder = WebApplication.CreateBuilder(args);
RochellEnvironments.EnsureSupported(builder.Environment.EnvironmentName);

var app = builder.Build();
await app.RunAsync();

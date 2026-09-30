using System.Text.Json;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>E-PAR-3: the deployment's top-bar label, readable before signing in.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class EnvironmentBadgeTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_label_comes_from_configuration_and_is_null_when_unset()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var plain = new ApiHost(h);
        using var parallel = new ApiHost(h, settings: new Dictionary<string, string?> { ["Rochell:EnvironmentBadge"] = " PARALELO " });

        var none = JsonDocument.Parse(await plain.Browser().GetStringAsync("/api/v1/environment")).RootElement;
        var badge = JsonDocument.Parse(await parallel.Browser().GetStringAsync("/api/v1/environment")).RootElement;

        Assert.Equal(JsonValueKind.Null, none.GetProperty("badge").ValueKind);
        Assert.Equal("PARALELO", badge.GetProperty("badge").GetString());
    }
}

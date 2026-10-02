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

    /// <summary>E-MAIL-01-4: the deployment says whether it sends mail, and with mail off the commands that send refuse.</summary>
    [Fact]
    public async Task The_mail_mode_comes_from_configuration_and_switches_the_send_commands()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var off = new ApiHost(h);
        using var redirect = new ApiHost(h, settings: new Dictionary<string, string?>
        {
            ["Rochell:Mail:Mode"] = "Redirect",
            ["Rochell:Mail:FromAddress"] = "industrias@rochell.com.do",
            ["Rochell:Mail:RedirectTo"] = "industrias@rochell.com.do",
            ["Rochell:Mail:RendererUrl"] = "http://localhost:9",
            ["Rochell:Mail:Smtp:Host"] = "localhost",
        });
        var cobros = await h.SessionWithRolesAsync("COBROS");
        var retry = new { mailId = Guid.CreateVersion7() };

        var whenOff = await (await off.SignInAsSessionUserAsync(cobros)).CommandAsync(h.CompanyId, "sales", "retry-document-email", retry, "off");
        var whenOn = await (await redirect.SignInAsSessionUserAsync(await h.SessionWithRolesAsync("COBROS"))).CommandAsync(h.CompanyId, "sales", "retry-document-email", retry, "on");

        Assert.Equal("OFF", JsonDocument.Parse(await off.Browser().GetStringAsync("/api/v1/environment")).RootElement.GetProperty("mailMode").GetString());
        Assert.Equal("REDIRECT", JsonDocument.Parse(await redirect.Browser().GetStringAsync("/api/v1/environment")).RootElement.GetProperty("mailMode").GetString());
        Assert.Equal("MAIL_DISABLED", (await whenOff.ProblemAsync()).Code);
        Assert.Equal("MAIL_NOT_RETRYABLE", (await whenOn.ProblemAsync()).Code);
    }
}

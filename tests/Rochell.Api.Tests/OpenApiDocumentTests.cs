using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>
/// E-PR18-7: the committed src/Rochell.Api/openapi.json is exactly what the host generates; web/ types are generated from it.
/// After an intended API change, regenerate it with: <c>ROCHELL_UPDATE_OPENAPI=1 dotnet test --filter OpenApiDocumentTests</c>.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class OpenApiDocumentTests(PostgresFixture postgres)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string CommittedPath
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rochell.slnx")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), "src", "Rochell.Api", "openapi.json");
        }
    }

    [Fact]
    public async Task The_committed_document_matches_the_generated_one()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);

        var generated = Normalize(await api.CreateClient().GetStringAsync("/openapi/v1.json"));

        if (Environment.GetEnvironmentVariable("ROCHELL_UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(CommittedPath, generated);
        }

        Assert.True(File.Exists(CommittedPath), "src/Rochell.Api/openapi.json is missing; generate it with ROCHELL_UPDATE_OPENAPI=1.");
        var committed = Normalize(await File.ReadAllTextAsync(CommittedPath));
        Assert.True(
            committed == generated,
            "src/Rochell.Api/openapi.json differs from the generated document. If the API change is intended, run "
            + "ROCHELL_UPDATE_OPENAPI=1 dotnet test tests/Rochell.Api.Tests --filter OpenApiDocumentTests and commit the file (then regenerate the web types).");
    }

    [Fact]
    public async Task The_document_states_the_transport_conventions()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);

        var document = JsonNode.Parse(await api.CreateClient().GetStringAsync("/openapi/v1.json"))!;

        var operation = document["paths"]!["/api/v1/companies/{companyId}/procurement/create-purchase-order"]!["post"]!;
        var headers = operation["parameters"]!.AsArray().Where(p => p!["in"]!.GetValue<string>() == "header").Select(p => p!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["Idempotency-Key", "X-Rochell-Csrf"], headers);
        var body = document["components"]!["schemas"]!["CreatePurchaseOrder"]!;
        Assert.Equal(["plantId", "partyId", "orderDate", "lines"], body["properties"]!.AsObject().Select(p => p.Key));
        var line = document["components"]!["schemas"]!["PurchaseOrderLineInput"]!["properties"]!;
        Assert.Equal("string", line["quantity"]!["type"]!.GetValue<string>());
        Assert.Equal("decimal", line["quantity"]!["format"]!.GetValue<string>());
        Assert.Null(document["servers"]);
    }

    /// <summary>Stable text: indented, LF line endings, final newline.</summary>
    private static string Normalize(string json)
        => JsonSerializer.Serialize(JsonNode.Parse(json), Indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
}

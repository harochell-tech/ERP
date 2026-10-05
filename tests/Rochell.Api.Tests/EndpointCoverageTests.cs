using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Rochell.Api.Endpoints;
using Rochell.Platform.Commands;
using Rochell.Platform.Queries;
using Rochell.TestInfrastructure;
using Xunit;

namespace Rochell.Api.Tests;

/// <summary>Every production command and query handler is reachable over HTTP, exactly once (E-PR18-3, E-PR18-4, E-PR17-6).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class EndpointCoverageTests(PostgresFixture postgres)
{
    private static readonly Assembly[] Modules =
    [
        typeof(Identity.AssemblyMarker).Assembly, typeof(MasterData.AssemblyMarker).Assembly, typeof(Finance.AssemblyMarker).Assembly,
        typeof(Inventory.AssemblyMarker).Assembly, typeof(Procurement.AssemblyMarker).Assembly, typeof(Tax.AssemblyMarker).Assembly,
        typeof(Audit.AssemblyMarker).Assembly, typeof(Reconciliation.AssemblyMarker).Assembly, typeof(Treasury.AssemblyMarker).Assembly, typeof(Sales.AssemblyMarker).Assembly,
        typeof(Manufacturing.AssemblyMarker).Assembly,
    ];

    private static List<Type> Handled(Type handlerInterface)
        => Modules.SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == handlerInterface)
            .Select(i => i.GetGenericArguments()[0])
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public async Task Every_command_and_query_has_one_endpoint()
    {
        await using var h = await TestHarness.CreateAsync(postgres);
        using var api = new ApiHost(h);
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var commands = endpoints.Select(e => e.Metadata.GetMetadata<CommandEndpointMetadata>()?.CommandType).OfType<Type>().OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var queries = endpoints.Select(e => e.Metadata.GetMetadata<QueryEndpointMetadata>()?.QueryName).OfType<string>().Order(StringComparer.Ordinal).ToList();

        Assert.Equal(Handled(typeof(ICommandHandler<>)), commands);
        Assert.Equal(Handled(typeof(IQueryHandler<>)).Select(t => t.Name), queries);
        Assert.Equal(224, commands.Count); // + the DUA and import settlement commands (USD1-04); + Create / UpdateForeignSupplierDraft (USD1-03); + Prepare / Approve / DiscardExchangeRate (USD1-02); + CreateFreightItem and the four zone commands (PRS-03); + Create / Deactivate / ReactivatePriceList (PRS-02); + 3 expense order commands (GAS1-05); + RegisterExpenseInvoice (GAS1-04); + 5 of GAS1-03 (expense categories); + CancelCashSale (CF1-03); + 7 of CF1-02; + 6 of MAIL-02; 44 of VS#1 + 5 of VS2-02 + 4 of VS2-03 + 1 of VS2-04 + 4 of VS2-05 + 1 of UI-01 + 11 of FIN1-02 + 2 of FIN1-03 + 19 of VS3-02 + 3 of VS3-02b + 6 of VS3-03 + 8 of VS3-04 + 4 of VS3-05 + 3 of VS3-06 + 9 of VS3-07 + 9 of MFG1-02 + 5 of MFG1-03 + 4 of MFG1-04 + 1 of MFG1-05 + 9 of FIS1-02 + 1 of FIS1-04 + 9 of QUO1-02 + 1 of QUO1-03 + 3 of UX2-01 + 1 of UX4-01 + 6 of IMP-01 + 2 of FIS1b-03 + 2 of FIS1b-04 + 4 of FIS1b-05
    }

    [Fact]
    public void Every_handler_the_host_registers_is_a_production_handler_with_a_permission()
    {
        Assert.All(CommandEndpoints.Handlers.Concat(QueryEndpoints.Handlers), t => Assert.Single(t.GetCustomAttributes<RequiresPermissionAttribute>()));
        Assert.Equal(CommandEndpoints.Handlers.Count, CommandEndpoints.Handlers.Distinct().Count());
        Assert.Equal(QueryEndpoints.Handlers.Count, QueryEndpoints.Handlers.Distinct().Count());
    }

    [Theory]
    [InlineData("CreatePurchaseOrder", "create-purchase-order")]
    [InlineData("ApproveValuationResidualAdjustment", "approve-valuation-residual-adjustment")]
    [InlineData("VerifyHashChain", "verify-hash-chain")]
    public void Command_segments_are_kebab_case(string type, string segment)
        => Assert.Equal(segment, CommandEndpoints.Kebab(type));
}

using System.Data.Common;
using System.Text.Json;
using Rochell.Finance.Ledger;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Procurement.Expenses;

/// <summary>One category of a pack: its code, name, the code of its account, its 606 type and class.</summary>
public sealed record ExpenseCategoryPackItem(string Code, string Name, string Account, string GoodsType606, string LineClass);

/// <summary>E-GAS-03-4: a company's expense categories as a reviewed file in the repository, prepared through PrepareExpenseCategory.</summary>
public sealed record ExpenseCategoryPack(string Pack, IReadOnlyList<ExpenseCategoryPackItem> Categories)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public static ExpenseCategoryPack Parse(string json)
    {
        var pack = JsonSerializer.Deserialize<ExpenseCategoryPack>(json, Options) ?? throw new FormatException("The pack is empty.");
        if (string.IsNullOrWhiteSpace(pack.Pack) || pack.Categories is not { Count: > 0 })
        {
            throw new FormatException("The pack needs its name and its categories.");
        }

        return pack.Categories.Select(c => c.Code).Distinct(StringComparer.Ordinal).Count() == pack.Categories.Count
            ? pack
            : throw new FormatException("Category codes are unique in a pack.");
    }
}

/// <summary>
/// E-GAS-03-4/6: prepares the categories of a pack as DRAFT on the session given (the configuration-load service identity); the
/// Controller approves them. Safe to run again: a code already in use is left as it is. A category whose account is not in the chart
/// is skipped and reported — the load creates no account for it.
/// </summary>
public sealed class ExpenseCategoryPackLoader(CommandPipeline pipeline, DbDataSource dataSource)
{
    public async Task<IReadOnlyList<LoadStep>> LoadAsync(Guid companyId, Guid sessionId, ExpenseCategoryPack pack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var (accounts, categories) = await ReadAsync(companyId, cancellationToken).ConfigureAwait(false);
        var run = Guid.CreateVersion7().ToString("N");
        var steps = new List<LoadStep>();
        foreach (var item in pack.Categories)
        {
            if (categories.TryGetValue(item.Code, out var status))
            {
                steps.Add(new LoadStep(item.Code, "EXISTS", $"A category with this code is already {status}."));
                continue;
            }

            if (!accounts.TryGetValue(item.Account, out var account))
            {
                steps.Add(new LoadStep(item.Code, "ACCOUNT_MISSING", $"Account {item.Account} is not in the chart: the category was not prepared (E-GAS-03-6)."));
                continue;
            }

            try
            {
                await pipeline.ExecuteAsync(
                    new PrepareExpenseCategory(companyId, sessionId, $"category-pack:{pack.Pack}:{item.Code}:{run}", item.Code, item.Name, account, item.GoodsType606, item.LineClass),
                    new PrepareExpenseCategoryHandler(), Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false);
                steps.Add(new LoadStep(item.Code, "DRAFT", $"{item.Name} → {item.Account}: prepared; the Controller approves it."));
            }
            catch (DomainException ex)
            {
                steps.Add(new LoadStep(item.Code, "REFUSED", $"{ex.Code}: {ex.Message}"));
            }
        }

        return steps;
    }

    private async Task<(Dictionary<string, Guid> Accounts, Dictionary<string, string> Categories)> ReadAsync(Guid companyId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(connection, transaction, "SELECT set_config('app.company_id', @company, true)", cancellationToken, ("company", companyId.ToString())).ConfigureAwait(false);
        var accounts = await Reading.ListAsync(
            connection, transaction, "SELECT code, account_id FROM fin.account WHERE company_id = @c", r => (Code: r.GetString(0), Id: r.GetGuid(1)), cancellationToken, ("c", companyId))
            .ConfigureAwait(false);
        var categories = await Reading.ListAsync(
            connection, transaction, "SELECT code, status FROM pur.expense_category WHERE company_id = @c AND status <> 'INACTIVE'", r => (Code: r.GetString(0), Status: r.GetString(1)),
            cancellationToken, ("c", companyId)).ConfigureAwait(false);
        return (accounts.ToDictionary(a => a.Code, a => a.Id, StringComparer.Ordinal), categories.ToDictionary(c => c.Code, c => c.Status, StringComparer.Ordinal));
    }
}

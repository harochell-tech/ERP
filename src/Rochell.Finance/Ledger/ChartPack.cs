using System.Data.Common;
using System.Text.Json;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;

namespace Rochell.Finance.Ledger;

/// <summary>One account of a chart pack: code, name, class (ASSET … EXPENSE) and whether it is a control account.</summary>
public sealed record ChartPackAccount(string Code, string Name, string AccountClass, bool IsControl);

/// <summary>E-GAS-03-7: a company's chart of accounts as a reviewed file in the repository, created through CreateAccount.</summary>
public sealed record ChartPack(string Pack, IReadOnlyList<ChartPackAccount> Accounts)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public static ChartPack Parse(string json)
    {
        var pack = JsonSerializer.Deserialize<ChartPack>(json, Options) ?? throw new FormatException("The pack is empty.");
        if (string.IsNullOrWhiteSpace(pack.Pack) || pack.Accounts is not { Count: > 0 })
        {
            throw new FormatException("The pack needs its name and its accounts.");
        }

        return pack.Accounts.Select(a => a.Code).Distinct(StringComparer.Ordinal).Count() == pack.Accounts.Count
            ? pack
            : throw new FormatException("Account codes are unique in a pack.");
    }
}

/// <summary>One line of what a load did: the subject, its outcome and why.</summary>
public sealed record LoadStep(string Subject, string Outcome, string Detail);

/// <summary>
/// E-GAS-03-7: creates the accounts of a pack through the same command as the screens, on the session given (the configuration-load
/// service identity). Safe to run again: an account whose code exists is left as it is — reported DIFFERENT when its name, class or
/// control mark differ from the pack, since the load never changes what is there.
/// </summary>
public sealed class ChartPackLoader(CommandPipeline pipeline, DbDataSource dataSource)
{
    public async Task<IReadOnlyList<LoadStep>> LoadAsync(Guid companyId, Guid sessionId, ChartPack pack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var existing = await ExistingAsync(companyId, cancellationToken).ConfigureAwait(false);
        var run = Guid.CreateVersion7().ToString("N");
        var steps = new List<LoadStep>();
        foreach (var account in pack.Accounts)
        {
            if (existing.TryGetValue(account.Code, out var there))
            {
                var same = there.Name == account.Name && there.Class == account.AccountClass && there.Control == account.IsControl;
                steps.Add(new LoadStep(
                    account.Code, same ? "EXISTS" : "DIFFERENT",
                    same ? "Already in the chart." : $"Already in the chart as «{there.Name}» ({there.Class ?? "sin clase"}, {(there.Control ? "control" : "normal")}): left as it is."));
                continue;
            }

            await pipeline.ExecuteAsync(
                new CreateAccount(companyId, sessionId, $"chart-pack:{pack.Pack}:{account.Code}:{run}", account.Code, account.Name, account.AccountClass, account.IsControl),
                new CreateAccountHandler(), Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false);
            steps.Add(new LoadStep(account.Code, "CREATED", $"{account.Name} ({account.AccountClass}{(account.IsControl ? ", control" : string.Empty)})."));
        }

        return steps;
    }

    private async Task<Dictionary<string, (string Name, string? Class, bool Control)>> ExistingAsync(Guid companyId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(connection, transaction, "SELECT set_config('app.company_id', @company, true)", cancellationToken, ("company", companyId.ToString())).ConfigureAwait(false);
        var rows = await Reading.ListAsync(
            connection, transaction, "SELECT code, name, account_class, is_control FROM fin.account WHERE company_id = @c",
            r => (Code: r.GetString(0), Name: r.GetString(1), Class: r.IsDBNull(2) ? null : r.GetString(2), Control: r.GetBoolean(3)), cancellationToken, ("c", companyId)).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Code, r => (r.Name, r.Class, r.Control), StringComparer.Ordinal);
    }
}

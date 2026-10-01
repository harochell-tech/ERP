using System.Data.Common;
using System.Text.RegularExpressions;
using Rochell.Platform.Data;

namespace Rochell.MasterData.Import;

/// <summary>
/// E-IMP-6, E-IMP-01-3: a party's e-mails, at most ten, in the order given; the first is the principal one (md.party.email).
/// Repeated addresses (ignoring case) are kept once.
/// </summary>
public static partial class PartyEmails
{
    public const int Max = 10;
    public const int MaxLength = 200;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex Address();

    /// <summary>The cleaned list, or the reason it is refused.</summary>
    public static (IReadOnlyList<string> Emails, string? Error) Normalize(IEnumerable<string?>? emails)
    {
        var list = new List<string>();
        foreach (var raw in emails ?? [])
        {
            var email = (raw ?? string.Empty).Trim();
            if (email.Length == 0 || list.Contains(email, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (email.Length > MaxLength || !Address().IsMatch(email))
            {
                return ([], $"The e-mail '{email}' is not valid.");
            }

            list.Add(email);
        }

        return list.Count > Max ? ([], $"A party keeps at most {Max} e-mails.") : (list, null);
    }

    /// <summary>The e-mails of one cell or field, separated by semicolons or commas.</summary>
    public static IEnumerable<string> Split(string? value) => (value ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static async Task<List<string>> ListAsync(DbConnection connection, DbTransaction transaction, Guid companyId, Guid partyId, CancellationToken cancellationToken)
        => await Reading.ListAsync(
            connection,
            transaction,
            "SELECT email FROM md.party_email WHERE company_id = @c AND party_id = @p ORDER BY position",
            r => r.GetString(0),
            cancellationToken,
            ("c", companyId),
            ("p", partyId)).ConfigureAwait(false);

    /// <summary>Replaces the party's list; the caller holds the party's row lock and writes md.party.email itself.</summary>
    public static async Task ReplaceAsync(DbConnection connection, DbTransaction transaction, Guid companyId, Guid partyId, IReadOnlyList<string> emails, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(emails);
        await Sql.ExecuteAsync(connection, transaction, "DELETE FROM md.party_email WHERE company_id = @c AND party_id = @p", cancellationToken, ("c", companyId), ("p", partyId)).ConfigureAwait(false);
        for (var i = 0; i < emails.Count; i++)
        {
            await Sql.ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO md.party_email (company_id, party_id, position, email) VALUES (@c, @p, @n, @e)",
                cancellationToken,
                ("c", companyId),
                ("p", partyId),
                ("n", (short)(i + 1)),
                ("e", emails[i])).ConfigureAwait(false);
        }
    }
}

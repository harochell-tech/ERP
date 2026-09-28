using System.Globalization;
using System.Text;
using System.Text.Json;
using Rochell.Platform.Json;

namespace Rochell.Finance.Ledger;

/// <summary>
/// E-FIN1-10, E-FIN1-03-10: CSV of the trial balance, the account ledger and the statements — comma separator, point decimal,
/// 2 decimals, Spanish headers. Built from the query's JSON so the file shows exactly what the screen shows.
/// </summary>
public static class LedgerCsv
{
    public static string TrialBalance(string json)
    {
        var tb = Read<TrialBalance>(json);
        var csv = new Writer("Código", "Cuenta", "Clase", "Saldo inicial", "Débitos", "Créditos", "Saldo final");
        foreach (var r in tb.Rows)
        {
            csv.Row(r.Code, r.Name, r.AccountClass, M(r.Opening), M(r.Debit), M(r.Credit), M(r.Closing));
        }

        csv.Row(string.Empty, "Totales", string.Empty, M(tb.TotalOpening), M(tb.TotalDebit), M(tb.TotalCredit), M(tb.TotalClosing));
        return csv.ToString();
    }

    public static string AccountLedger(string json)
    {
        var l = Read<AccountLedger>(json);
        var csv = new Writer("Fecha", "Tipo de asiento", "Evento", "Documento", "Número", "Regla", "Débito", "Crédito", "Saldo");
        csv.Row(D(l.From), "Saldo inicial", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, M(l.Opening));
        foreach (var m in l.Movements)
        {
            csv.Row(D(m.PostingDate), m.JournalType, m.EventType, m.DocumentKind, m.DocumentNumber, m.RuleLineCode, M(m.Debit), M(m.Credit), M(m.Balance));
        }

        csv.Row(D(l.To), "Saldo final", string.Empty, string.Empty, string.Empty, string.Empty, M(l.TotalDebit), M(l.TotalCredit), M(l.Closing));
        return csv.ToString();
    }

    public static string BalanceSheet(string json)
    {
        var b = Read<BalanceSheet>(json);
        var csv = Lines(b.Lines, b.UnassignedAccounts);
        csv.Row("RESULT-CY", "Resultado del ejercicio", string.Empty, M(b.CurrentYearResult));
        csv.Row("RESULT-PY", "Resultados de ejercicios anteriores", string.Empty, M(b.PriorYearsResult));
        csv.Row("TOTAL-A", "Total activo", string.Empty, M(b.TotalAssets));
        csv.Row("TOTAL-LE", "Total pasivo, patrimonio y resultados", string.Empty, M(b.TotalLiabilities + b.TotalEquity + b.CurrentYearResult + b.PriorYearsResult));
        csv.Row("DIFF", "Diferencia", string.Empty, M(b.Difference));
        return csv.ToString();
    }

    public static string IncomeStatement(string json)
    {
        var i = Read<IncomeStatement>(json);
        var csv = Lines(i.Lines, i.UnassignedAccounts);
        csv.Row("REVENUE", "Ingresos", string.Empty, M(i.Revenue));
        csv.Row("COST", "Costos", string.Empty, M(i.Cost));
        csv.Row("EXPENSES", "Gastos", string.Empty, M(i.Expenses));
        csv.Row("NET", "Resultado neto", string.Empty, M(i.NetIncome));
        return csv.ToString();
    }

    private static Writer Lines(IReadOnlyList<StatementLine> lines, IReadOnlyList<StatementAccount> unassigned)
    {
        var csv = new Writer("Línea", "Concepto", "Cuenta", "Monto");
        foreach (var line in lines)
        {
            csv.Row(line.LineCode, new string(' ', line.Depth * 2) + line.Caption, string.Empty, M(line.Amount));
            foreach (var a in line.Accounts)
            {
                csv.Row(line.LineCode, a.Name, a.Code, M(a.Amount));
            }
        }

        foreach (var a in unassigned)
        {
            csv.Row("SIN-LINEA", a.Name, a.Code, M(a.Amount));
        }

        return csv;
    }

    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, ApiJson.Options) ?? throw new InvalidOperationException("Empty query result.");

    private static string M(decimal value) => decimal.Round(value, 2).ToString("0.00", CultureInfo.InvariantCulture);

    private static string D(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The CSV writer of the reports (also used by the AR reports of Sales, E-VS3-09-4).</summary>
    public sealed class Writer
    {
        private readonly StringBuilder _text = new();

        public Writer(params string[] header) => Row(header);

        public void Row(params string?[] fields)
        {
            _text.AppendJoin(',', fields.Select(Escape)).Append("\r\n");
        }

        public override string ToString() => _text.ToString();

        private static string Escape(string? field)
        {
            var f = field ?? string.Empty;
            if (f.Length > 0 && f[0] is '=' or '+' or '@' or '\t')
            {
                f = "'" + f; // a caption is never read by a spreadsheet as a formula
            }

            return f.IndexOfAny([',', '"', '\r', '\n']) >= 0 || f.StartsWith(' ') ? "\"" + f.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : f;
        }
    }
}

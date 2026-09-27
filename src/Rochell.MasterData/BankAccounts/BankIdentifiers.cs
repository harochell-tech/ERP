using System.Text.RegularExpressions;
using Rochell.Platform.Commands;

namespace Rochell.MasterData.BankAccounts;

/// <summary>
/// E-VS2-01-3 / E-VS2-02-2: bank codes are stored upper case (2–20 characters); account numbers are digits only (5–30) after
/// removing spaces and hyphens. The database CHECKs enforce the stored form; this is where input is normalized.
/// </summary>
public static partial class BankIdentifiers
{
    public const string Invalid = "BANK_ACCOUNT_INVALID";

    public static string BankCode(string? value)
    {
        var code = (value ?? string.Empty).Trim().ToUpperInvariant();
        return code.Length is >= 2 and <= 20
            ? code
            : throw new DomainException(Invalid, "The bank code must have 2 to 20 characters.");
    }

    public static string AccountNumber(string? value)
    {
        var number = (value ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        return Digits().IsMatch(number)
            ? number
            : throw new DomainException(Invalid, "The account number must have 5 to 30 digits (spaces and hyphens are ignored).");
    }

    [GeneratedRegex("^[0-9]{5,30}$")]
    private static partial Regex Digits();
}

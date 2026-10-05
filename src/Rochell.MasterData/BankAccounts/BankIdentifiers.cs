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

    /// <summary>
    /// Digits only (5–30); a foreign supplier's account (E-USD1-05-6) may be an IBAN or a number with letters, 5–34 characters, stored upper case.
    /// </summary>
    public static string AccountNumber(string? value, bool foreign = false)
    {
        var number = (value ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        if (foreign)
        {
            number = number.ToUpperInvariant();
            return Alphanumeric().IsMatch(number)
                ? number
                : throw new DomainException(Invalid, "The account number or IBAN has 5 to 34 letters and digits (spaces and hyphens are ignored).");
        }

        return Digits().IsMatch(number)
            ? number
            : throw new DomainException(Invalid, "The account number must have 5 to 30 digits (spaces and hyphens are ignored).");
    }

    [GeneratedRegex("^[0-9]{5,30}$")]
    private static partial Regex Digits();

    [GeneratedRegex("^[A-Z0-9]{5,34}$")]
    private static partial Regex Alphanumeric();
}

namespace Rochell.MasterData.Suppliers;

/// <summary>Result of an RNC/cédula check. <see cref="ValidatedAt"/> is set only when an authoritative registry confirmed it.</summary>
public sealed record RncCheck(bool IsValid, string? Normalized, DateTime? ValidatedAt, string? Reason);

/// <summary>
/// Port for RNC validation (baseline §17 PR-04: "adaptador simulado"). Implementations must not perform I/O:
/// handlers run inside the command transaction. A DGII registry lookup will be an integration job after VS#1.
/// </summary>
public interface IRncRegistry
{
    RncCheck Check(string rnc);
}

/// <summary>
/// E-PR04-5: VS#1 validates format only — 9 digits (RNC) or 11 digits (cédula), separators removed.
/// Check digits are not enforced and nothing is marked as validated by DGII (<see cref="RncCheck.ValidatedAt"/> is null).
/// </summary>
public sealed class FormatOnlyRncRegistry : IRncRegistry
{
    public static FormatOnlyRncRegistry Instance { get; } = new();

    public RncCheck Check(string rnc)
    {
        if (string.IsNullOrWhiteSpace(rnc))
        {
            return new RncCheck(false, null, null, "RNC is required for local suppliers.");
        }

        var trimmed = rnc.Trim();
        if (trimmed.Any(c => !char.IsAsciiDigit(c) && c != '-' && c != ' '))
        {
            return new RncCheck(false, null, null, "RNC may contain only digits, dashes and spaces.");
        }

        var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray());
        return digits.Length is 9 or 11
            ? new RncCheck(true, digits, null, null)
            : new RncCheck(false, null, null, "RNC must have 9 digits (RNC) or 11 digits (cédula).");
    }
}

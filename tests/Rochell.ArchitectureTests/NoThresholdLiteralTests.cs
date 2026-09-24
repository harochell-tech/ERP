using System.Text.RegularExpressions;
using Xunit;

namespace Rochell.ArchitectureTests;

/// <summary>
/// ADR-039 / E-PR06-6: accounting thresholds live in versioned policies, never in code. Production code may use the
/// decimal literals 0m, 1m and 100m; any other decimal literal must be a column type limit marked "// type-limit".
/// </summary>
public sealed partial class NoThresholdLiteralTests
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "0", "1", "100" };

    [Fact]
    public void Production_code_has_no_decimal_threshold_literals()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Repo.Src, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (line.Contains("// type-limit", StringComparison.Ordinal))
                {
                    continue;
                }

                var code = line.Split("//", 2)[0];
                foreach (Match match in DecimalLiteral().Matches(code))
                {
                    var digits = match.Groups["value"].Value.Replace("_", string.Empty, StringComparison.Ordinal);
                    if (!Allowed.Contains(digits))
                    {
                        offenders.Add($"{Path.GetRelativePath(Repo.Root, file)}:{lineNumber}: {match.Value}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, "Decimal literals must come from accounting policies:\n" + string.Join("\n", offenders));
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])(?<value>[0-9][0-9_]*(\.[0-9][0-9_]*)?)[mM]\b")]
    private static partial Regex DecimalLiteral();
}

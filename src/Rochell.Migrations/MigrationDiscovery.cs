using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Rochell.Migrations;

/// <summary>
/// Loads migration files. Rules: file name NNNN__snake_case.sql, versions contiguous from 0001,
/// no duplicates, non-empty. Non-.sql files (README) are ignored.
/// </summary>
public static partial class MigrationDiscovery
{
    [GeneratedRegex(@"^(?<version>\d{4})__(?<name>[a-z0-9]+(?:_[a-z0-9]+)*)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    public static IReadOnlyList<MigrationScript> Discover(MigrationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!Directory.Exists(source.DirectoryPath))
        {
            throw new MigrationException(
                $"Migration directory for source '{source.Name}' not found: {source.DirectoryPath}");
        }

        var scripts = new List<MigrationScript>();
        foreach (var path in Directory.EnumerateFiles(source.DirectoryPath).Order(StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            if (!fileName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = FileNamePattern().Match(fileName);
            if (!match.Success)
            {
                throw new MigrationException(
                    $"Invalid migration file name '{fileName}' in source '{source.Name}'. Expected NNNN__snake_case_name.sql.");
            }

            var version = int.Parse(match.Groups["version"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
            if (version == 0)
            {
                throw new MigrationException($"Migration versions start at 0001; found '{fileName}'.");
            }

            var sql = Normalize(File.ReadAllText(path, Encoding.UTF8));
            if (string.IsNullOrWhiteSpace(sql))
            {
                throw new MigrationException($"Migration '{fileName}' in source '{source.Name}' is empty.");
            }

            scripts.Add(new MigrationScript(source.Name, version, match.Groups["name"].Value, fileName, sql, ComputeChecksum(sql)));
        }

        var duplicate = scripts.GroupBy(s => s.Version).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new MigrationException(
                $"Duplicate migration version {duplicate.Key:D4} in source '{source.Name}': {string.Join(", ", duplicate.Select(s => s.FileName))}.");
        }

        scripts.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < scripts.Count; i++)
        {
            if (scripts[i].Version != i + 1)
            {
                throw new MigrationException(
                    $"Migration versions in source '{source.Name}' must be contiguous from 0001; expected {i + 1:D4} but found '{scripts[i].FileName}'.");
            }
        }

        return scripts;
    }

    /// <summary>Normalizes line endings to LF and removes a UTF-8 BOM so checksums are identical on Windows, macOS and Linux.</summary>
    public static string Normalize(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return sql
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimStart('\uFEFF');
    }

    public static byte[] ComputeChecksum(string normalizedSql)
    {
        ArgumentNullException.ThrowIfNull(normalizedSql);
        return SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSql));
    }
}

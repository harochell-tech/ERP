namespace Rochell.Platform.Hosting;

/// <summary>
/// Hosting environments supported in VS#1 (configuration selection only).
/// This is NOT the source of truth for the fiscal gate: that is core.deployment_environment (Patch 1.1, correction 3).
/// </summary>
public static class RochellEnvironments
{
    public const string Development = "Development";
    public const string Test = "Test";
    public const string Staging = "Staging";

    public static IReadOnlyList<string> Supported { get; } = [Development, Test, Staging];

    public static void EnsureSupported(string? environmentName)
    {
        if (environmentName is null || !Supported.Contains(environmentName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported environment '{environmentName}'. Supported: {string.Join(", ", Supported)}.");
        }
    }
}

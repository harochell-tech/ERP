namespace Rochell.Identity;

/// <summary>Host configuration for identity (approved errata E-PR03-2, E-PR03-6, E-PR03-8).</summary>
public sealed record IdentityOptions
{
    /// <summary>Google Workspace hosted domain (claim "hd") accepted for office logins.</summary>
    public required string HostedDomain { get; init; }

    /// <summary>E-PR03-2: maximum age of the last re-authentication for commands marked StepUp.</summary>
    public TimeSpan StepUpMaxAge { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>E-PR03-6: absolute session lifetime.</summary>
    public TimeSpan SessionAbsoluteLifetime { get; init; } = TimeSpan.FromHours(12);

    /// <summary>E-PR03-6: inactivity timeout.</summary>
    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>last_activity_at is refreshed at most this often, to avoid locking the session row on every command.</summary>
    public TimeSpan ActivityTouchInterval { get; init; } = TimeSpan.FromSeconds(60);
}

public static class IdentityConstants
{
    /// <summary>Service identity used as granted_by for bootstrap grants made by the deployment role (E-PR03-5).</summary>
    public static readonly Guid DeploymentUserId = Guid.Parse("00000000-0000-7000-8000-00000000d001");

    public const string AuthMethodOidcGoogle = "OIDC_GOOGLE";
}

namespace Rochell.Api.Hosting;

/// <summary>
/// Host configuration ("Rochell" section). Secrets (connection strings, OIDC client secret, digest keys) come from environment
/// variables or the secret store, never from committed settings files.
/// </summary>
public sealed class ApiOptions
{
    public const string Section = "Rochell";

    /// <summary>Connection string of a login that is a member of rochell_app (commands and queries).</summary>
    public string? AppConnectionString { get; set; }

    /// <summary>Connection string of a login that is a member of rochell_sealer (sealer and digest, E-PR18-5).</summary>
    public string? SealerConnectionString { get; set; }

    /// <summary>
    /// Where the Data Protection keys (session cookie, OIDC state) persist. Without it they live in the default user profile
    /// location; losing them only forces users to sign in again.
    /// </summary>
    public string? DataProtectionKeysPath { get; set; }

    /// <summary>E-PR18b-2: directory of the web UI's static export (web/out), served from the API's own origin. Unset: no UI.</summary>
    public string? WebRoot { get; set; }

    public IdentitySettings Identity { get; set; } = new();

    public OidcSettings Oidc { get; set; } = new();

    public SealerSettings Sealer { get; set; } = new();

    public DigestSettings Digest { get; set; } = new();

    public AuditSettings Audit { get; set; } = new();
}

public sealed class IdentitySettings
{
    /// <summary>Google Workspace domain (claim "hd") accepted for office logins.</summary>
    public string? HostedDomain { get; set; }

    public TimeSpan StepUpMaxAge { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan SessionAbsoluteLifetime { get; set; } = TimeSpan.FromHours(12);

    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>E-PR18-2: OIDC authorization code against Google Workspace (A-03 pending; CI uses a simulated IdP).</summary>
public sealed class OidcSettings
{
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }
}

/// <summary>E-PR18-5: the sealing loop (ADR-037).</summary>
public sealed class SealerSettings
{
    public bool Enabled { get; set; }

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>E-PR18-5: the daily digest at <see cref="RunAt"/> local time (E-PR15-5). Needs WORM storage and the signing key.</summary>
public sealed class DigestSettings
{
    public bool Enabled { get; set; }

    public TimeOnly RunAt { get; set; } = new(0, 15);

    /// <summary>PKCS#8 PEM of the ECDSA P-256 signing key (secret store only).</summary>
    public string? SigningKeyPem { get; set; }
}

/// <summary>What verification (hash:verify) and the digest need to reach the WORM copies.</summary>
public sealed class AuditSettings
{
    /// <summary>Root directory of the file-system WORM store; accepted only when core.deployment_environment is TEST (E-PR15-4).</summary>
    public string? FileSystemWormRoot { get; set; }

    /// <summary>SubjectPublicKeyInfo PEM of the digest signing key, used by VerifyHashChain.</summary>
    public string? DigestPublicKeyPem { get; set; }
}

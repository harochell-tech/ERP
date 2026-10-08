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

    /// <summary>
    /// E-PAR-3: the label the web shows in its top bar for this deployment (staging runs the parallel run: "PARALELO"). Unset: the web
    /// decides from the host name (E-UX1-01-10).
    /// </summary>
    public string? EnvironmentBadge { get; set; }

    public IdentitySettings Identity { get; set; } = new();

    public OidcSettings Oidc { get; set; } = new();

    public SealerSettings Sealer { get; set; } = new();

    public DigestSettings Digest { get; set; } = new();

    public FiscalExpirySettings FiscalExpiry { get; set; } = new();

    public AuditSettings Audit { get; set; } = new();

    public MailSettings Mail { get; set; } = new();

    /// <summary>E-VS4-11: the e-CF gateway (Off unless configured).</summary>
    public Rochell.Tax.Ecf.EcfSettings Ecf { get; set; } = new();

    /// <summary>MFG2-02: the machines' portal (Off without a BaseUrl).</summary>
    public Rochell.Manufacturing.Portal.PortalSettings Portal { get; set; } = new();

    /// <summary>ENT1-02: the drivers' page (Off without a link key).</summary>
    public Rochell.Api.Deliveries.DeliveriesSettings Deliveries { get; set; } = new();

    public ReverseProxySettings ReverseProxy { get; set; } = new();
}

/// <summary>
/// E-B03-2: the reverse proxy (Caddy) in front of a staging or production host. Only requests arriving from these networks may set
/// the client address and scheme through X-Forwarded-For / X-Forwarded-Proto (one hop). Empty: forwarded headers are ignored.
/// </summary>
public sealed class ReverseProxySettings
{
    /// <summary>CIDR networks of the proxy, e.g. the compose network "172.30.0.0/24".</summary>
    public List<string> TrustedNetworks { get; set; } = [];
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

/// <summary>
/// E-MAIL-1…10, E-MAIL-01-1…11: outgoing mail. <see cref="Mode"/> Off (default): nothing is sent and queued messages wait.
/// Redirect: everything goes to <see cref="RedirectTo"/> (the parallel run, E-MAIL-01-4). Live: to the recipients, with a blind copy
/// to <see cref="ArchiveBcc"/> (E-MAIL-01-5). Outside Off the sender, the SMTP relay and the PDF renderer are required.
/// </summary>
public sealed class MailSettings
{
    public Rochell.Platform.Mail.MailMode Mode { get; set; } = Rochell.Platform.Mail.MailMode.Off;

    /// <summary>E-MAIL-01-1: the one sender; replies go to it.</summary>
    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "Industrias Rochell";

    public string? RedirectTo { get; set; }

    public string? ArchiveBcc { get; set; }

    /// <summary>E-MAIL-01-10: attempts before a message is FAILED.</summary>
    public int MaxAttempts { get; set; } = 5;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Base address of the Gotenberg container (E-MAIL-10), e.g. "http://pdf:3000".</summary>
    public string? RendererUrl { get; set; }

    public SmtpSettings Smtp { get; set; } = new();

    public Rochell.Platform.Mail.MailDelivery Delivery() => new(Mode, RedirectTo, ArchiveBcc, MaxAttempts);
}

public sealed class SmtpSettings
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    /// <summary>STARTTLS is required unless switched off (tests against a local SMTP catcher).</summary>
    public bool StartTls { get; set; } = true;

    /// <summary>The host name given in EHLO; the Workspace relay refuses a name that is not one.</summary>
    public string? LocalDomain { get; set; }

    /// <summary>Only for a relay that authenticates with a user and password (secret store only). The Workspace relay authorizes the server's address instead (E-MAIL-01-3).</summary>
    public string? User { get; set; }

    public string? Password { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>E-FIS1-04-5 / E-FIS1-04-7: the daily expiry of fiscal authorizations at <see cref="RunAt"/> local time.</summary>
public sealed class FiscalExpirySettings
{
    public bool Enabled { get; set; }

    public TimeOnly RunAt { get; set; } = new(0, 30);
}

/// <summary>E-PR18-5: the daily digest at <see cref="RunAt"/> local time (E-PR15-5). Needs WORM storage and the signing key.</summary>
public sealed class DigestSettings
{
    public bool Enabled { get; set; }

    public TimeOnly RunAt { get; set; } = new(0, 15);

    /// <summary>PKCS#8 PEM of the ECDSA P-256 signing key (secret store only).</summary>
    public string? SigningKeyPem { get; set; }

    /// <summary>E-B03-6: file holding <see cref="SigningKeyPem"/> (generated on the server, readable only by the host).</summary>
    public string? SigningKeyPemFile { get; set; }
}

/// <summary>What verification (hash:verify) and the digest need to reach the WORM copies.</summary>
public sealed class AuditSettings
{
    /// <summary>Root directory of the file-system WORM store; accepted only when core.deployment_environment is TEST (E-PR15-4).</summary>
    public string? FileSystemWormRoot { get; set; }

    /// <summary>SubjectPublicKeyInfo PEM of the digest signing key, used by VerifyHashChain.</summary>
    public string? DigestPublicKeyPem { get; set; }

    /// <summary>File holding <see cref="DigestPublicKeyPem"/>.</summary>
    public string? DigestPublicKeyPemFile { get; set; }

    /// <summary>S3 Object Lock WORM store (B-03, E-B03-3/4); any environment. Exclusive with <see cref="FileSystemWormRoot"/>.</summary>
    public S3WormSettings S3 { get; set; } = new();
}

/// <summary>
/// Bucket created with Object Lock enabled. Credentials: <see cref="AccessKeyId"/> / <see cref="SecretAccessKey"/> when both are
/// set, otherwise the AWS default chain (AWS_ACCESS_KEY_ID, …). <see cref="ServiceUrl"/> only for S3-compatible endpoints.
/// </summary>
public sealed class S3WormSettings
{
    public string? Bucket { get; set; }

    public string? Region { get; set; }

    public string? ServiceUrl { get; set; }

    /// <summary>COMPLIANCE retention of each digest, in days (staging: 7, E-B03-3).</summary>
    public int RetentionDays { get; set; }

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }
}

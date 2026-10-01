using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Rochell.TestInfrastructure;

/// <summary>
/// What outgoing mail needs for real (MAIL-01): an SMTP server that keeps what it receives (Mailpit, read back through its HTTP
/// API) and the PDF renderer of production (Gotenberg with Chromium, E-MAIL-10). Both pinned.
/// </summary>
public sealed class MailFixture : IAsyncLifetime
{
    public const string SmtpImage = "axllent/mailpit:v1.31.3";
    public const string RendererImage = "gotenberg/gotenberg:8.37.0-chromium";
    private const ushort SmtpPort = 1025;
    private const ushort SmtpApiPort = 8025;
    private const ushort RendererPort = 3000;

    private readonly IContainer _smtp = new ContainerBuilder()
        .WithImage(SmtpImage)
        .WithPortBinding(SmtpPort, assignRandomHostPort: true)
        .WithPortBinding(SmtpApiPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(SmtpApiPort).ForPath("/readyz")))
        .Build();

    private readonly IContainer _renderer = new ContainerBuilder()
        .WithImage(RendererImage)
        .WithPortBinding(RendererPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(RendererPort).ForPath("/health")))
        .Build();

    private readonly HttpClient _http = new();

    public string SmtpHost => _smtp.Hostname;

    public int SmtpPortOnHost => _smtp.GetMappedPublicPort(SmtpPort);

    public string RendererUrl => $"http://{_renderer.Hostname}:{_renderer.GetMappedPublicPort(RendererPort)}";

    private string SmtpApi => $"http://{_smtp.Hostname}:{_smtp.GetMappedPublicPort(SmtpApiPort)}";

    public Task InitializeAsync() => Task.WhenAll(_smtp.StartAsync(), _renderer.StartAsync());

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _smtp.DisposeAsync();
        await _renderer.DisposeAsync();
    }

    /// <summary>The messages the SMTP server holds whose subject contains <paramref name="subject"/> (Mailpit's summaries: To, Bcc, Subject, Attachments).</summary>
    public async Task<List<JsonElement>> ReceivedAsync(string subject)
    {
        var text = await _http.GetStringAsync(new Uri($"{SmtpApi}/api/v1/messages?limit=200"));
        return [.. JsonDocument.Parse(text).RootElement.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("Subject").GetString()!.Contains(subject, StringComparison.Ordinal))];
    }

    /// <summary>The whole message by its Mailpit id: text body and attachments (with their part ids).</summary>
    public async Task<JsonElement> MessageAsync(string id) => JsonDocument.Parse(await _http.GetStringAsync(new Uri($"{SmtpApi}/api/v1/message/{id}"))).RootElement;

    public Task<byte[]> AttachmentAsync(string id, string partId) => _http.GetByteArrayAsync(new Uri($"{SmtpApi}/api/v1/message/{id}/part/{partId}"));
}

using System.Text.Json.Nodes;
using Rochell.Platform.Commands;

namespace Rochell.Tax.Ecf;

/// <summary>E-VS4-11: the gateway switched Off — nothing is ever sent or read; a step asked for anyway is refused.</summary>
public sealed class OffEcfProvider : IEcfProvider, IEcfReception
{
    public static readonly OffEcfProvider Instance = new();

    public string Mode => EcfModes.Off;

    public Task<SubmitOutcome> SubmitAsync(string ecfType, JsonObject payload, CancellationToken cancellationToken) => throw Off();

    public Task<QueryOutcome> QueryAsync(string ecfType, string providerId, CancellationToken cancellationToken) => throw Off();

    public Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken) => throw Off();

    public Task<SubmitOutcome> CancelAsync(JsonObject payload, CancellationToken cancellationToken) => throw Off();

    public Task<ReceivedListOutcome> ListReceivedAsync(ReceivedListRequest request, CancellationToken cancellationToken) => throw Off();

    public Task<ReceivedGetOutcome> GetReceivedAsync(string providerId, CancellationToken cancellationToken) => throw Off();

    public Task<SubmitOutcome> RespondAsync(string providerId, bool accept, string? reason, CancellationToken cancellationToken) => throw Off();

    private static DomainException Off() => new(EcfErrors.GatewayOff, "The e-CF gateway is Off in this environment (E-VS4-11).");
}

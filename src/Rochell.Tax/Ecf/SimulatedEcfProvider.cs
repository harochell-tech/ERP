using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Rochell.Tax.Ecf;

/// <summary>
/// E-VS4-02-6: Alanube simulated, for the tests and the dev stack only (mode SIMULATED; never staging or production). It numbers nothing
/// and keeps its documents in memory by e-NCF. The case comes from a marker anywhere in the payload's text:
/// <list type="bullet">
/// <item>none — registered, accepted at the first status query (an e-CF 32: in the same response, as Alanube's synchronous flow);</item>
/// <item><c>[SIM:REJECT]</c> — the DGII rejects it;</item>
/// <item><c>[SIM:OBSERVED]</c> — accepted with observations;</item>
/// <item><c>[SIM:TIMEOUT]</c> — the first issuance registers it but answers nothing (the outcome is unknown); sending it again answers «in process with id»;</item>
/// <item><c>[SIM:SLOW]</c> — waits for the DGII three queries before accepting;</item>
/// <item><c>[SIM:INVALID]</c> — refused as a data error.</item>
/// </list>
/// <see cref="Down"/> makes every call fail as if Alanube did not answer (contingency).
/// </summary>
public sealed class SimulatedEcfProvider : IEcfProvider
{
    private readonly ConcurrentDictionary<string, Entry> _byEncf = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _encfById = new(StringComparer.Ordinal);
    private int _sequence;

    private sealed class Entry
    {
        public required string Id { get; init; }

        public required string Encf { get; init; }

        public required string Scenario { get; init; }

        public int Queries { get; set; }
    }

    public string Mode => EcfModes.Simulated;

    /// <summary>While true, every call is a network failure.</summary>
    public bool Down { get; set; }

    /// <summary>Calls received, for the tests.</summary>
    public int Submissions { get; private set; }

    public Task<SubmitOutcome> SubmitAsync(string ecfType, JsonObject payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Submissions++;
        if (Down)
        {
            return Task.FromResult(new SubmitOutcome(SubmitKind.Transient, null, null, null, null, "Simulated: Alanube does not answer."));
        }

        var text = payload.ToJsonString();
        var encf = payload["idDoc"]?["encf"]?.GetValue<string>() ?? throw new InvalidOperationException("The payload has no idDoc.encf.");
        if (text.Contains("[SIM:INVALID]", StringComparison.Ordinal))
        {
            return Task.FromResult(new SubmitOutcome(SubmitKind.Invalid, 400, null, null, "AP10067", "Simulated: a required field is missing."));
        }

        if (_byEncf.TryGetValue(encf, out var existing))
        {
            return Task.FromResult(new SubmitOutcome(SubmitKind.Duplicate, 400, existing.Id, null, "AP3011", $"ENCF document is in process with id: {existing.Id}"));
        }

        var scenario = text.Contains("[SIM:REJECT]", StringComparison.Ordinal) ? "REJECT"
            : text.Contains("[SIM:OBSERVED]", StringComparison.Ordinal) ? "OBSERVED"
            : text.Contains("[SIM:TIMEOUT]", StringComparison.Ordinal) ? "TIMEOUT"
            : text.Contains("[SIM:SLOW]", StringComparison.Ordinal) ? "SLOW"
            : "ACCEPT";
        var id = NewId();
        _byEncf[encf] = new Entry { Id = id, Encf = encf, Scenario = scenario };
        _encfById[id] = encf;
        if (scenario == "TIMEOUT")
        {
            return Task.FromResult(new SubmitOutcome(SubmitKind.Transient, null, null, null, null, "Simulated: no answer within the call timeout."));
        }

        // E-VS4 ECF-02: an e-CF 32 is answered in the same response (Alanube's synchronous consumer flow), unless it is slow.
        var synchronous = ecfType == "32" && scenario is "ACCEPT" or "REJECT" or "OBSERVED";
        if (synchronous)
        {
            EntryOf(id).Queries = 1;
        }

        return Task.FromResult(new SubmitOutcome(
            SubmitKind.Registered, 201, id,
            synchronous ? Final(id, scenario, encf) : Document(id, "REGISTERED", null, encf), null, null));
    }

    public Task<QueryOutcome> QueryAsync(string ecfType, string providerId, CancellationToken cancellationToken)
    {
        if (Down)
        {
            return Task.FromResult(new QueryOutcome(QueryKind.Transient, null, null, null, "Simulated: Alanube does not answer."));
        }

        if (!_encfById.TryGetValue(providerId, out var encf) || !_byEncf.TryGetValue(encf, out var entry))
        {
            return Task.FromResult(new QueryOutcome(QueryKind.NotFound, 404, null, "AP3009", "Simulated: not found."));
        }

        entry.Queries++;
        var document = entry.Scenario == "SLOW" && entry.Queries < 3 ? Document(entry.Id, "WAITING_RESPONSE", null, encf) : Final(entry.Id, entry.Scenario, encf);
        return Task.FromResult(new QueryOutcome(QueryKind.Found, 200, document, null, null));
    }

    /// <summary>The cancellations received, for the tests.</summary>
    public List<JsonObject> Cancellations { get; } = [];

    public Task<SubmitOutcome> CancelAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (Down)
        {
            return Task.FromResult(new SubmitOutcome(SubmitKind.Transient, null, null, null, null, "Simulated: Alanube does not answer."));
        }

        Cancellations.Add((JsonObject)payload.DeepClone());
        return Task.FromResult(new SubmitOutcome(SubmitKind.Registered, 201, NewId(), null, null, null));
    }

    public Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken)
        => Task.FromResult<byte[]?>(Down ? null : Encoding.UTF8.GetBytes($"simulated {url}"));

    private string NewId()
    {
        // A ULID-shaped id (26 characters of Crockford base 32).
        var n = Interlocked.Increment(ref _sequence);
        return ("01SIM" + n.ToString("D21", CultureInfo.InvariantCulture)).ToUpperInvariant();
    }

    private Entry EntryOf(string id) => _byEncf[_encfById[id]];

    private static ProviderDocument Final(string id, string scenario, string encf) => scenario switch
    {
        "REJECT" => Document(id, "FINISHED", "REJECTED", encf),
        "OBSERVED" => Document(id, "FINISHED", "ACCEPTED_WITH_OBSERVATIONS", encf),
        _ => Document(id, "FINISHED", "ACCEPTED", encf),
    };

    private static ProviderDocument Document(string id, string status, string? legal, string encf)
    {
        var final = legal is not null;
        var response = legal == "REJECTED"
            ? new JsonObject { ["code"] = 2, ["value"] = new JsonArray(new JsonObject { ["codigo"] = "1934", ["valor"] = "Simulado: el monto gravado no coincide con el detalle." }) }
            : null;
        return new ProviderDocument(
            id,
            status,
            legal,
            final ? $"track-{id}" : null,
            final && legal != "REJECTED" ? "SIM" + id[^3..] : null,
            final ? DateTimeOffset.UnixEpoch : null,
            final && legal != "REJECTED" ? $"https://ecf.dgii.gov.do/testecf/ConsultaTimbre?encf={encf}" : null,
            $"https://simulated.invalid/{encf}.xml",
            $"https://simulated.invalid/{encf}.pdf",
            response,
            null,
            null);
    }
}

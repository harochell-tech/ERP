using System.Collections.Concurrent;
using System.Globalization;
using System.Security;
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
/// <see cref="Down"/> makes every call fail as if Alanube did not answer (contingency). OCR1-02 (E-OCR1-02-10): suppliers' e-CF are
/// added with <see cref="AddReceived"/> and listed, read and answered as Alanube does.
/// </summary>
public sealed class SimulatedEcfProvider : IEcfProvider, IEcfReception
{
    private readonly ConcurrentDictionary<string, Entry> _byEncf = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReceivedDocument> _received = new(StringComparer.Ordinal);
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

    /// <summary>The commercial responses received, for the tests.</summary>
    public List<(string Id, bool Accept, string? Reason)> Responses { get; } = [];

    /// <summary>E-OCR1-02-10: a supplier's e-CF arrives (<paramref name="xml"/> is the e-CF itself, see <see cref="SampleXml"/>).</summary>
    public ReceivedDocument AddReceived(
        string issuerRnc, string buyerRnc, string encf, DateTimeOffset signed, string totalAmount, string xml, string status = ReceivedStatuses.Received,
        string commercialResponse = ReceivedStatuses.NotDeclared)
    {
        ArgumentNullException.ThrowIfNull(encf);
        var document = new ReceivedDocument(
            NewId(), issuerRnc, buyerRnc, encf.Substring(1, 2), encf, status, status == ReceivedStatuses.Received ? null : "Simulado: firma inválida.", commercialResponse, signed,
            totalAmount, xml);
        _received[document.Id] = document;
        return document;
    }

    public Task<ReceivedListOutcome> ListReceivedAsync(ReceivedListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Down)
        {
            return Task.FromResult(new ReceivedListOutcome(false, null, [], null, "Simulated: Alanube does not answer."));
        }

        var page = _received.Values
            .Where(d => d.Status == request.Status && d.CommercialResponse == request.CommercialResponse)
            .Where(d => d.SignatureDate is { } at && DateOnly.FromDateTime(at.UtcDateTime) >= request.Start && DateOnly.FromDateTime(at.UtcDateTime) <= request.End)
            .OrderByDescending(d => d.SignatureDate).ThenBy(d => d.Id, StringComparer.Ordinal)
            .Skip((request.Page - 1) * request.Limit).Take(request.Limit)
            .Select(d => d with { Xml = null })
            .ToList();
        return Task.FromResult(new ReceivedListOutcome(true, 200, page, null, null));
    }

    public Task<ReceivedGetOutcome> GetReceivedAsync(string providerId, CancellationToken cancellationToken)
        => Task.FromResult(
            Down ? new ReceivedGetOutcome(QueryKind.Transient, null, null, null, "Simulated: Alanube does not answer.")
            : _received.TryGetValue(providerId, out var document) ? new ReceivedGetOutcome(QueryKind.Found, 200, document, null, null)
            : new ReceivedGetOutcome(QueryKind.NotFound, 404, null, "AP3009", "Simulated: not found."));

    public Task<SubmitOutcome> RespondAsync(string providerId, bool accept, string? reason, CancellationToken cancellationToken)
    {
        if (Down)
        {
            return Task.FromResult(new SubmitOutcome(SubmitKind.Transient, null, null, null, null, "Simulated: Alanube does not answer."));
        }

        if (!_received.TryGetValue(providerId, out var document))
        {
            return Task.FromResult(new SubmitOutcome(SubmitKind.Invalid, 404, null, null, "AP3009", "Simulated: not found."));
        }

        if (document.CommercialResponse != ReceivedStatuses.NotDeclared)
        {
            return Task.FromResult(new SubmitOutcome(SubmitKind.Invalid, 400, null, null, "AP3020", "Simulated: the document was already answered."));
        }

        Responses.Add((providerId, accept, reason));
        _received[providerId] = document with { CommercialResponse = accept ? ReceivedStatuses.Accepted : ReceivedStatuses.Rejected };
        return Task.FromResult(new SubmitOutcome(SubmitKind.Registered, 200, NewId(), null, null, null));
    }

    /// <summary>
    /// A received e-CF in the DGII's XML format, with what Core reads: issuer, buyer, e-NCF, date, lines (code, name, quantity, unit,
    /// unit price, amount, billing indicator 1 taxed / 4 exempt), taxed amount, ITBIS, total, signature date and value. Amounts are
    /// the caller's invariant text.
    /// </summary>
    public static string SampleXml(
        string issuerRnc, string issuerName, string buyerRnc, string encf, DateOnly issued, DateTime signedLocal,
        IReadOnlyList<(string Name, string Quantity, string UnitPrice, string Amount, bool Taxed)> lines, string taxedAmount, string itbis, string total)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(encf);
        var items = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            items.Append(CultureInfo.InvariantCulture, $"<Item><NumeroLinea>{i + 1}</NumeroLinea>")
                .Append(CultureInfo.InvariantCulture, $"<TablaCodigosItem><CodigosItem><TipoCodigo>INTERNA</TipoCodigo><CodigoItem>P{i + 1}</CodigoItem></CodigosItem></TablaCodigosItem>")
                .Append(CultureInfo.InvariantCulture, $"<IndicadorFacturacion>{(l.Taxed ? 1 : 4)}</IndicadorFacturacion><NombreItem>{SecurityElement.Escape(l.Name)}</NombreItem>")
                .Append(CultureInfo.InvariantCulture, $"<IndicadorBienoServicio>1</IndicadorBienoServicio><CantidadItem>{l.Quantity}</CantidadItem><UnidadMedida>43</UnidadMedida>")
                .Append(CultureInfo.InvariantCulture, $"<PrecioUnitarioItem>{l.UnitPrice}</PrecioUnitarioItem><MontoItem>{l.Amount}</MontoItem></Item>");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <ECF><Encabezado><Version>1.0</Version><IdDoc><TipoeCF>{encf.Substring(1, 2)}</TipoeCF><eNCF>{encf}</eNCF></IdDoc>
            <Emisor><RNCEmisor>{issuerRnc}</RNCEmisor><RazonSocialEmisor>{SecurityElement.Escape(issuerName)}</RazonSocialEmisor><FechaEmision>{issued:dd-MM-yyyy}</FechaEmision></Emisor>
            <Comprador><RNCComprador>{buyerRnc}</RNCComprador></Comprador>
            <Totales><MontoGravadoTotal>{taxedAmount}</MontoGravadoTotal><TotalITBIS>{itbis}</TotalITBIS><MontoTotal>{total}</MontoTotal></Totales></Encabezado>
            <DetallesItems>{items}</DetallesItems>
            <FechaHoraFirma>{signedLocal:dd-MM-yyyy HH:mm:ss}</FechaHoraFirma>
            <Signature xmlns="http://www.w3.org/2000/09/xmldsig#"><SignatureValue>SIMxyzABCDEF0123</SignatureValue></Signature></ECF>
            """);
    }

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

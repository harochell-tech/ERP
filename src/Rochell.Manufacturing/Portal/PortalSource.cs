using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rochell.Manufacturing.Portal;

// MFG2-02 (E-MFG2-1): what Rochell Core reads from the machines' portal — data/exportar.php in harochell-tech/portal: per machine and
// resolved shift, cycles and blocks per mould, and the batch plants' latest posts. Times are the portal's local time (Santo Domingo).

/// <summary>Server settings (never in the repository): the portal's address and the key Core sends; Off without a BaseUrl.</summary>
public sealed class PortalSettings
{
    /// <summary>e.g. https://industriasrochell.com.do/</summary>
    public string? BaseUrl { get; set; }

    public string? Token { get; set; }

    /// <summary>A file on the server holding the token (read when <see cref="Token"/> is not set).</summary>
    public string? TokenFile { get; set; }

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Days before today read on every pass (a shift that ends after midnight, a late batch-plant post).</summary>
    public int LookbackDays { get; set; } = 1;

    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl);
}

/// <remarks>MFG3-00 adds stoppages, maintenance windows and daily reports; an older portal leaves them out (null).</remarks>
public sealed record PortalExport(
    [property: JsonPropertyName("generado_en")] string GeneratedAt,
    [property: JsonPropertyName("turnos")] IReadOnlyList<PortalShift> Shifts,
    [property: JsonPropertyName("consumos")] IReadOnlyList<PortalPost> Posts,
    [property: JsonPropertyName("paros")] IReadOnlyList<PortalStoppage>? Stoppages = null,
    [property: JsonPropertyName("mantenimientos")] IReadOnlyList<PortalMaintenance>? Maintenance = null,
    [property: JsonPropertyName("reportes")] IReadOnlyList<PortalDailyReport>? Reports = null);

/// <summary>MFG3-00 (E-MFG3-3): a stoppage as the portal recorded it; the end and the reason arrive later.</summary>
public sealed record PortalStoppage(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("planta")] string Machine,
    [property: JsonPropertyName("inicio")] string Start,
    [property: JsonPropertyName("fin")] string? End,
    [property: JsonPropertyName("duracion_segundos")] int? Seconds,
    [property: JsonPropertyName("razon")] string? Reason,
    [property: JsonPropertyName("detalle")] string? Detail);

/// <summary>MFG3-00 (E-MFG3-3/9): a maintenance window; <see cref="Task"/> names the Core task it did (MFG3-03).</summary>
public sealed record PortalMaintenance(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("planta")] string Machine,
    [property: JsonPropertyName("desde")] string From,
    [property: JsonPropertyName("hasta")] string? To,
    [property: JsonPropertyName("motivo")] string Reason,
    [property: JsonPropertyName("tarea")] string? Task = null);

/// <summary>MFG3-00 (E-MFG3-2, E-MFG3-00-3): the day's broken blocks and the next day's good blocks confirmed after curing, per size.</summary>
public sealed record PortalDailyReport(
    [property: JsonPropertyName("planta")] string Machine,
    [property: JsonPropertyName("fecha")] string Date,
    [property: JsonPropertyName("rechazos")] int? Broken,
    [property: JsonPropertyName("confirmado_curado")] IReadOnlyDictionary<string, int>? CuredGood,
    [property: JsonPropertyName("actualizado_en")] string? UpdatedAt);

public sealed record PortalShift(
    [property: JsonPropertyName("planta")] string Machine,
    [property: JsonPropertyName("fecha")] string Date,
    [property: JsonPropertyName("turno_num")] int ShiftNo,
    [property: JsonPropertyName("inicio")] string From,
    [property: JsonPropertyName("fin")] string To,
    [property: JsonPropertyName("cerrado")] bool Closed,
    [property: JsonPropertyName("ciclos")] int Cycles,
    [property: JsonPropertyName("sin_molde")] int CyclesWithoutMould,
    [property: JsonPropertyName("moldes")] IReadOnlyList<PortalMould> Moulds,
    [property: JsonPropertyName("tiempo_muerto_min")] int DeadMinutes,
    [property: JsonPropertyName("primer_ciclo")] string? FirstCycle,
    [property: JsonPropertyName("ultimo_ciclo")] string? LastCycle,
    [property: JsonPropertyName("ciclos_mantenimiento")] int MaintenanceCycles);

public sealed record PortalMould(
    [property: JsonPropertyName("molde")] string Mould,
    [property: JsonPropertyName("bloques_por_ciclo")] int BlocksPerCycle,
    [property: JsonPropertyName("ciclos")] int Cycles,
    [property: JsonPropertyName("bloques")] int Blocks);

public sealed record PortalPost(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("dosificadora")] string BatchPlant,
    [property: JsonPropertyName("fecha")] string Date,
    [property: JsonPropertyName("turno_num")] int ShiftNo,
    [property: JsonPropertyName("bachadas")] int? Batches,
    [property: JsonPropertyName("materiales")] IReadOnlyList<PortalMaterial> Materials,
    [property: JsonPropertyName("recibido_en")] string ReceivedAt);

public sealed record PortalMaterial(
    [property: JsonPropertyName("codigo")] string Code,
    [property: JsonPropertyName("cantidad")] string Quantity,
    [property: JsonPropertyName("unidad")] string Unit);

/// <summary>The portal, or a fake one in the tests.</summary>
public interface IPortalSource
{
    Task<PortalExport> FetchAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

/// <summary>GET data/exportar.php with the header X-Core-Token.</summary>
public sealed class PortalHttpSource(HttpClient http, PortalSettings settings) : IPortalSource
{
    public async Task<PortalExport> FetchAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var url = new Uri(new Uri(settings.BaseUrl!.EndsWith('/') ? settings.BaseUrl : settings.BaseUrl + "/"),
            $"data/exportar.php?desde={from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&hasta={to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Core-Token", settings.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The portal answered {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
        }

        return JsonSerializer.Deserialize<PortalExport>(body) ?? throw new HttpRequestException("The portal answered an empty export.");
    }
}

internal static class PortalTime
{
    /// <summary>The portal's "yyyy-MM-dd HH:mm:ss" (local) as a DateTime without zone.</summary>
    public static DateTime Parse(string value)
        => DateTime.ParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None);

    public static DateOnly Day(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}

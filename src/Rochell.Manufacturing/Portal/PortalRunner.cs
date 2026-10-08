using System.Globalization;
using System.Text.Json;
using Rochell.Manufacturing.Runs;
using Rochell.Platform.Commands;

namespace Rochell.Manufacturing.Portal;

/// <summary>
/// E-MFG2-1/4/6: one pass for a company, as the daily process: read the portal (yesterday and today by default), open the runs the
/// groups need (StartProductionRun with the ACTIVE recipe; a missing recipe or standard is a warning), and bring every group's drafts
/// up to date. Each step is its own command and transaction; nothing is posted.
/// </summary>
public static class PortalRunner
{
    public sealed record PassResult(bool Ok, int Readings, int Posts, int Written, int RunsOpened, IReadOnlyList<string> Warnings, string? Error);

    public static async Task<PassResult> RunOnceAsync(
        CommandPipeline pipeline, IPortalSource source, Guid companyId, Guid sessionId, DateOnly today, int lookbackDays, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var pass = Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        var imported = JsonSerializer.Deserialize<PortalImportResult>(
            (await pipeline.ExecuteAsync(new ImportPortalData(companyId, sessionId, $"portal:{pass}:import", today.AddDays(-lookbackDays), today), new ImportPortalDataHandler(source),
                Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false)).ResultPayload,
            PortalJson.Options)!;
        if (!imported.Ok)
        {
            return new PassResult(false, 0, 0, 0, 0, [], imported.Error);
        }

        var warnings = new List<string>(imported.Warnings);
        var written = 0;
        var opened = 0;
        var step = 0;
        foreach (var group in imported.Groups)
        {
            var result = await SyncAsync(pipeline, companyId, sessionId, group, $"portal:{pass}:{step++}", cancellationToken).ConfigureAwait(false);
            if (result.RunsNeeded.Count > 0)
            {
                foreach (var need in result.RunsNeeded)
                {
                    try
                    {
                        await pipeline.ExecuteAsync(
                            new StartProductionRun(companyId, sessionId, $"portal-run:{need.MachineId:N}:{need.ShiftId:N}:{need.Date:yyyyMMdd}:{need.ItemId:N}", need.PlantId, need.MachineId,
                                need.ShiftId, need.Date, need.ItemId),
                            new StartProductionRunHandler(),
                            Guid.CreateVersion7(),
                            cancellationToken).ConfigureAwait(false);
                        opened++;
                    }
                    catch (DomainException ex) when (ex.Code is ManufacturingErrors.RecipeNotActive or ManufacturingErrors.StandardCostMissing or ManufacturingErrors.MachineNotActive
                                                      or ManufacturingErrors.ShiftNotActive)
                    {
                        warnings.Add($"No se pudo abrir la corrida del {need.Date:yyyy-MM-dd}: {ex.Message}");
                    }
                }

                result = await SyncAsync(pipeline, companyId, sessionId, group, $"portal:{pass}:{step++}", cancellationToken).ConfigureAwait(false);
            }

            written += result.Written;
            warnings.AddRange(result.Warnings);
        }

        return new PassResult(true, imported.Readings, imported.Posts, written, opened, warnings.Distinct().ToList(), null);
    }

    private static async Task<PortalSyncResult> SyncAsync(CommandPipeline pipeline, Guid companyId, Guid sessionId, PortalGroup group, string key, CancellationToken cancellationToken)
        => JsonSerializer.Deserialize<PortalSyncResult>(
            (await pipeline.ExecuteAsync(new SyncPortalShift(companyId, sessionId, key, group.Date, group.ShiftNo, group.Group), new SyncPortalShiftHandler(), Guid.CreateVersion7(),
                cancellationToken).ConfigureAwait(false)).ResultPayload,
            PortalJson.Options)!;
}

using Microsoft.Extensions.DependencyInjection;
using Rochell.Api.Ecf;
using Rochell.Platform.Time;
using Rochell.Tax.Ecf;
using Rochell.TestInfrastructure;

namespace Rochell.DevStack;

/// <summary>
/// OCR1-03 (E-OCR1-02-10, E-OCR1-03-10): two e-CF that «Agregados del Este» (RNC 101000011) sends the company through the simulated
/// Alanube — an invoice of cement, and a credit note — read at once by the worker. The company gets the RNC of Block Rochell, so the
/// QRs the journeys paste name it as buyer. A development world, never a migration.
/// </summary>
internal static class ReceivedSeed
{
    public const string CompanyRnc = "131925332";
    public const string SupplierRnc = "101000011";

    public static async Task RunAsync(TestHarness h, IServiceProvider services)
    {
        await h.AdminRequireAsync(
            $"DO $$ BEGIN PERFORM set_config('session_replication_role', 'replica', true); UPDATE md.company SET rnc = '{CompanyRnc}' WHERE company_id = '{h.CompanyId}'; END $$");
        var alanube = services.GetRequiredService<SimulatedEcfProvider>();
        var today = BusinessCalendar.DefaultBusinessDate(DateTime.UtcNow);
        var signed = DateTimeOffset.UtcNow.AddMinutes(-30);
        var invoice = SimulatedEcfProvider.SampleXml(
            SupplierRnc, "Agregados del Este, S.R.L.", CompanyRnc, "E310000000501", today, today.ToDateTime(new TimeOnly(8, 0)),
            [("Cemento gris 42.5 kg", "10.00", "500.00", "5000.00", true)], "5000.00", "900.00", "5900.00");
        alanube.AddReceived(SupplierRnc, CompanyRnc, "E310000000501", signed, "5900.00", invoice);
        var note = SimulatedEcfProvider.SampleXml(
            SupplierRnc, "Agregados del Este, S.R.L.", CompanyRnc, "E340000000502", today, today.ToDateTime(new TimeOnly(8, 30)),
            [("Devolución de 2 sacos", "2.00", "500.00", "1000.00", true)], "1000.00", "180.00", "1180.00");
        alanube.AddReceived(SupplierRnc, CompanyRnc, "E340000000502", signed, "1180.00", note);
        services.GetRequiredService<ReceptionNudge>().Nudge();
    }
}

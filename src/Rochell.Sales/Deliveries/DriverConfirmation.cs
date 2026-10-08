using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rochell.Platform.Commands;
using Rochell.Platform.Data;
using Rochell.Platform.Queries;
using Rochell.Sales.Orders;

namespace Rochell.Sales.Deliveries;

// ENT1-02 (E-ENT-1…8, E-ENT1-01-1…10): the driver confirms the delivery from the delivery note's QR. Dispatch sets each driver's PIN;
// gate out opens one link per own-transport delivery; the public page (served by the API under the service identity «Confirmación
// de entrega») checks the PIN, then confirms: a full receipt becomes the POD, differences wait for Dispatch.

public static class DriverConfirmationErrors
{
    public const string PinInvalid = "DRIVER_PIN_INVALID";
    public const string LinkInvalid = "DELIVERY_LINK_INVALID";
    public const string ConfirmationInvalid = "DRIVER_CONFIRMATION_INVALID";
}

/// <summary>
/// E-ENT1-01-2: the server key of the drivers' links (a file on the server, like the Alanube token). A link is the delivery, its
/// generation and an HMAC-SHA256 of both under this key, so every reprint shows the same QR and the database stores no token.
/// </summary>
public sealed class DriverLinkKey
{
    private readonly byte[] _key;

    public DriverLinkKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 32)
        {
            throw new ArgumentException("The drivers' link key needs at least 32 bytes.", nameof(key));
        }

        _key = key;
    }

    public string Mac(Guid companyId, Guid deliveryId, int generation)
        => Base64Url(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{companyId:N}:{deliveryId:N}:{generation}"))));

    public bool Verify(Guid companyId, Guid deliveryId, int generation, string? mac)
        => mac is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(companyId, deliveryId, generation)), Encoding.ASCII.GetBytes(mac));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>E-ENT-2, E-ENT1-01-3: four digits, kept as PBKDF2-SHA256 with a salt per driver; the real defence is the attempt limit.</summary>
public static partial class DriverPins
{
    public const int Iterations = 210000;
    public const int MaxFailedPerLink = 5;
    public const int MaxFailedPerAddressPerHour = 30;

    [GeneratedRegex("^[0-9]{4}$")]
    private static partial Regex Format();

    public static bool IsValid(string? pin) => pin is not null && Format().IsMatch(pin);

    public static byte[] Hash(string pin, byte[] salt, int iterations) => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, 32);
}

/// <summary>The drivers' links: one per own-transport delivery, created at gate out, valid 7 days (E-ENT-1).</summary>
internal static class DriverLinks
{
    public const string Aggregate = "DeliveryLink";
    public const int ValidDays = 7;

    public sealed record Link(int Generation, string Status, DateTime ExpiresAt, int FailedAttempts, DateTime CreatedAt, long Version);

    public static async Task CreateAsync(CommandContext context, Guid deliveryId, CancellationToken cancellationToken)
    {
        var now = context.Clock.UtcNow;
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO log.delivery_link (company_id, delivery_id, generation, status, expires_at, failed_attempts, created_at, version)
            VALUES (@c, @d, 1, 'ACTIVE', @x, 0, @now, 1)
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("d", deliveryId),
            ("x", now.AddDays(ValidDays)),
            ("now", now)).ConfigureAwait(false);
    }

    public static Task<Link?> LockAsync(CommandContext context, Guid deliveryId, CancellationToken cancellationToken)
        => Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT generation, status, expires_at, failed_attempts, created_at, version FROM log.delivery_link WHERE company_id = @c AND delivery_id = @d FOR UPDATE",
            r => new Link(r.GetInt32(0), r.GetString(1), r.GetDateTime(2), r.GetInt32(3), r.GetDateTime(4), r.GetInt64(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", deliveryId));

    /// <summary>
    /// A POD recorded by Dispatch (or a return trip) ends the driver's link (E-ENT1-01-9) and, when the driver reported differences,
    /// is the completion of that confirmation (E-ENT1-01-10): returns its id.
    /// </summary>
    public static async Task<Guid?> CloseForPodAsync(CommandContext context, Guid deliveryId, CancellationToken cancellationToken)
    {
        var link = await LockAsync(context, deliveryId, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            return null;
        }

        if (link.Status is "ACTIVE" or "LOCKED")
        {
            await SetStatusAsync(context, deliveryId, link, "ANNULLED", "DeliveryLinkAnnulled", new { deliveryId }, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return await SalesSql.ScalarAsync<Guid?>(
            context,
            """
            SELECT confirmation_id FROM log.driver_confirmation WHERE company_id = @c AND delivery_id = @d AND outcome = 'DIFFERENCES'
            ORDER BY generation DESC LIMIT 1
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("d", deliveryId)).ConfigureAwait(false);
    }

    public static async Task SetStatusAsync(CommandContext context, Guid deliveryId, Link link, string status, string eventType, object payload, CancellationToken cancellationToken)
    {
        var version = link.Version + 1;
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE log.delivery_link SET status = @s, version = @v WHERE company_id = @c AND delivery_id = @d",
            cancellationToken,
            ("s", status),
            ("v", version),
            ("c", context.CompanyId),
            ("d", deliveryId)).ConfigureAwait(false);
        await context.AppendEventAsync(new EventDraft(eventType, 1, Aggregate, deliveryId, version, JsonSerializer.Serialize(payload), Publish: false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the public page may do with a link, read without changing anything.</summary>
    public static string State(Link? link, int generation, bool macOk, DateTime now) => link switch
    {
        null => "INVALID",
        _ when !macOk || link.Generation != generation => "INVALID",
        { Status: "ACTIVE" } when link.ExpiresAt <= now => "EXPIRED",
        _ => link.Status,
    };
}

/// <summary>E-ENT-2, E-ENT1-01-1: Dispatch sets (or changes) a driver's four-digit PIN.</summary>
public sealed record SetDriverPin(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DriverId, string Pin) : ICommand;

[RequiresPermission("driver_pin:manage")]
public sealed class SetDriverPinHandler : ICommandHandler<SetDriverPin>
{
    public string CommandType => "Sales.SetDriverPin";

    public async Task<string> HandleAsync(SetDriverPin command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        if (!DriverPins.IsValid(command.Pin))
        {
            throw new DomainException(DriverConfirmationErrors.PinInvalid, "The PIN is four digits.");
        }

        var status = await SalesSql.ScalarAsync<string>(
            context, "SELECT status FROM log.driver WHERE company_id = @c AND driver_id = @d", cancellationToken, ("c", context.CompanyId), ("d", command.DriverId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The driver does not exist.");
        if (status != "ACTIVE")
        {
            throw new DomainException(SalesErrors.InvalidState, "The driver is inactive.");
        }

        var setBy = await SalesSql.SessionUserAsync(context, cancellationToken).ConfigureAwait(false);
        var salt = RandomNumberGenerator.GetBytes(16);
        var version = await SalesSql.ScalarAsync<long?>(
            context,
            """
            INSERT INTO log.driver_pin (company_id, driver_id, pin_hash, pin_salt, iterations, set_by, set_at, version)
            VALUES (@c, @d, @h, @s, @i, @by, @at, 1)
            ON CONFLICT (company_id, driver_id) DO UPDATE
              SET pin_hash = EXCLUDED.pin_hash, pin_salt = EXCLUDED.pin_salt, iterations = EXCLUDED.iterations, set_by = EXCLUDED.set_by, set_at = EXCLUDED.set_at,
                  version = log.driver_pin.version + 1
            RETURNING version
            """,
            cancellationToken,
            ("c", context.CompanyId),
            ("d", command.DriverId),
            ("h", DriverPins.Hash(command.Pin, salt, DriverPins.Iterations)),
            ("s", salt),
            ("i", DriverPins.Iterations),
            ("by", setBy),
            ("at", context.Clock.UtcNow)).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft("DriverPinSet", 1, "DriverPin", command.DriverId, version!.Value, JsonSerializer.Serialize(new { driverId = command.DriverId }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { driverId = command.DriverId, version });
    }
}

/// <summary>E-ENT-2, E-ENT1-01-1/2: Dispatch reopens a locked or expired link — or reissues a lost QR — as a new generation.</summary>
public sealed record ReopenDeliveryLink(Guid CompanyId, Guid SessionId, string IdempotencyKey, Guid DeliveryId) : ICommand;

[RequiresPermission("delivery_link:reopen")]
public sealed class ReopenDeliveryLinkHandler : ICommandHandler<ReopenDeliveryLink>
{
    public string CommandType => "Sales.ReopenDeliveryLink";

    public async Task<string> HandleAsync(ReopenDeliveryLink command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var status = await SalesSql.ScalarAsync<string>(
            context, "SELECT status FROM log.delivery WHERE company_id = @c AND delivery_id = @d", cancellationToken, ("c", context.CompanyId), ("d", command.DeliveryId)).ConfigureAwait(false)
            ?? throw new DomainException(SalesErrors.NotFound, "The delivery does not exist.");
        var link = await DriverLinks.LockAsync(context, command.DeliveryId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(DriverConfirmationErrors.LinkInvalid, "The delivery has no driver's link (only own-transport deliveries past the gate have one).");
        if (status != "IN_TRANSIT" || link.Status is not ("ACTIVE" or "LOCKED"))
        {
            throw new DomainException(SalesErrors.InvalidState, "Only the link of a delivery in transit that the driver has not confirmed can be reopened.");
        }

        var now = context.Clock.UtcNow;
        var generation = link.Generation + 1;
        var version = link.Version + 1;
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE log.delivery_link SET generation = @g, status = 'ACTIVE', expires_at = @x, failed_attempts = 0, version = @v WHERE company_id = @c AND delivery_id = @d",
            cancellationToken,
            ("g", generation),
            ("x", now.AddDays(DriverLinks.ValidDays)),
            ("v", version),
            ("c", context.CompanyId),
            ("d", command.DeliveryId)).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft("DeliveryLinkReopened", 1, DriverLinks.Aggregate, command.DeliveryId, version, JsonSerializer.Serialize(new { deliveryId = command.DeliveryId, generation }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { deliveryId = command.DeliveryId, generation, version });
    }
}

/// <summary>What the public page sends to name a link: the delivery, its generation and the HMAC, plus the PIN and the client address.</summary>
public sealed record DriverLinkAccess(Guid DeliveryId, int Generation, string Mac, string Pin, string ClientAddress);

/// <summary>
/// E-ENT-2, E-ENT1-01-3: the public page checks the driver's PIN before taking the photo. Every try is kept; a wrong one counts
/// against the link (5 lock it) and the address (30 an hour). Never throws for a wrong PIN, so the try is committed.
/// </summary>
public sealed record VerifyDriverPin(Guid CompanyId, Guid SessionId, string IdempotencyKey, DriverLinkAccess Access) : ICommand;

[RequiresPermission("delivery:driver_confirm")]
public sealed class VerifyDriverPinHandler(DriverLinkKey? key = null) : ICommandHandler<VerifyDriverPin>
{
    public string CommandType => "Sales.VerifyDriverPin";

    public async Task<string> HandleAsync(VerifyDriverPin command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var (state, _, remaining) = await DriverAccess.CheckAsync(context, key, command.Access, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { state, remaining });
    }
}

internal static class DriverAccess
{
    private sealed record PinRow(byte[] Hash, byte[] Salt, int Iterations);

    /// <summary>
    /// The link's state after checking the PIN: OK, WRONG_PIN, THROTTLED, or why the link cannot be used (INVALID, EXPIRED, LOCKED,
    /// CONFIRMED, ANNULLED, NO_PIN). Records the try when the PIN was checked.
    /// </summary>
    public static async Task<(string State, DriverLinks.Link? Link, int Remaining)> CheckAsync(
        CommandContext context, DriverLinkKey? key, DriverLinkAccess access, CancellationToken cancellationToken)
    {
        if (key is null)
        {
            throw new DomainException(DriverConfirmationErrors.LinkInvalid, "The drivers' links are not configured on this server.");
        }

        ArgumentNullException.ThrowIfNull(access);
        if (!IPAddress.TryParse(access.ClientAddress, out var address))
        {
            throw new DomainException(DriverConfirmationErrors.LinkInvalid, "The client address is not valid.");
        }

        var now = context.Clock.UtcNow;
        var link = await DriverLinks.LockAsync(context, access.DeliveryId, cancellationToken).ConfigureAwait(false);
        var state = DriverLinks.State(link, access.Generation, key.Verify(context.CompanyId, access.DeliveryId, access.Generation, access.Mac), now);
        if (state != "ACTIVE" || link is null)
        {
            return (state, link, 0);
        }

        var failedHere = await SalesSql.ScalarAsync<long>(
            context, "SELECT count(*) FROM log.delivery_link_attempt WHERE client_address = @a AND attempted_at > @since AND NOT pin_ok", cancellationToken,
            ("a", address), ("since", now.AddHours(-1))).ConfigureAwait(false);
        if (failedHere >= DriverPins.MaxFailedPerAddressPerHour)
        {
            return ("THROTTLED", link, DriverPins.MaxFailedPerLink - link.FailedAttempts);
        }

        var pin = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT p.pin_hash, p.pin_salt, p.iterations FROM log.delivery d
            JOIN log.driver_pin p ON p.company_id = d.company_id AND p.driver_id = d.driver_id
            WHERE d.company_id = @c AND d.delivery_id = @d
            """,
            r => new PinRow(r.GetFieldValue<byte[]>(0), r.GetFieldValue<byte[]>(1), r.GetInt32(2)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", access.DeliveryId)).ConfigureAwait(false);
        if (pin is null)
        {
            return ("NO_PIN", link, 0);
        }

        var ok = DriverPins.IsValid(access.Pin) && CryptographicOperations.FixedTimeEquals(DriverPins.Hash(access.Pin, pin.Salt, pin.Iterations), pin.Hash);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO log.delivery_link_attempt (attempt_id, company_id, delivery_id, generation, attempted_at, client_address, pin_ok)
            VALUES (@id, @c, @d, @g, @at, @a, @ok)
            """,
            cancellationToken,
            ("id", context.Ids.NewId()),
            ("c", context.CompanyId),
            ("d", access.DeliveryId),
            ("g", link.Generation),
            ("at", now),
            ("a", address),
            ("ok", ok)).ConfigureAwait(false);
        if (ok)
        {
            return ("OK", link, DriverPins.MaxFailedPerLink - link.FailedAttempts);
        }

        var failed = link.FailedAttempts + 1;
        var locked = failed >= DriverPins.MaxFailedPerLink;
        var version = link.Version + 1;
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE log.delivery_link SET failed_attempts = @f, status = @s, version = @v WHERE company_id = @c AND delivery_id = @d",
            cancellationToken,
            ("f", failed),
            ("s", locked ? "LOCKED" : "ACTIVE"),
            ("v", version),
            ("c", context.CompanyId),
            ("d", access.DeliveryId)).ConfigureAwait(false);
        await context.AppendEventAsync(
            new EventDraft(locked ? "DeliveryLinkLocked" : "DriverPinRejected", 1, DriverLinks.Aggregate, access.DeliveryId, version,
                JsonSerializer.Serialize(new { deliveryId = access.DeliveryId, failed }), Publish: false),
            cancellationToken).ConfigureAwait(false);
        return (locked ? "LOCKED" : "WRONG_PIN", link with { FailedAttempts = failed, Version = version, Status = locked ? "LOCKED" : "ACTIVE" }, DriverPins.MaxFailedPerLink - failed);
    }
}

/// <summary>E-ENT-3, E-ENT1-01-7: what the driver says happened at the site; the evidence is already in the private bucket.</summary>
public sealed record DriverReport(
    string ReceiverName, string? ReceiverNationalId, string Outcome, string? Note, DateTime? PhoneAt, decimal? Latitude, decimal? Longitude, decimal? AccuracyM,
    string EvidenceKind, string EvidenceRef, string EvidenceSha256);

/// <summary>
/// E-ENT-4, E-ENT1-01-5/6: the driver confirms. FULL records the POD with everything received (as Dispatch's C-09 would);
/// DIFFERENCES records only the confirmation and waits for Dispatch. A wrong PIN is answered, not thrown, so the try is kept.
/// </summary>
public sealed record ConfirmDeliveryByDriver(Guid CompanyId, Guid SessionId, string IdempotencyKey, DriverLinkAccess Access, DriverReport Report) : ICommand;

[RequiresPermission("delivery:driver_confirm")]
public sealed class ConfirmDeliveryByDriverHandler(DriverLinkKey? key = null) : ICommandHandler<ConfirmDeliveryByDriver>
{
    private static readonly TimeSpan PhoneAhead = TimeSpan.FromMinutes(5);

    public string CommandType => "Sales.ConfirmDeliveryByDriver";

    public async Task<string> HandleAsync(ConfirmDeliveryByDriver command, CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        var report = command.Report ?? throw new DomainException(DriverConfirmationErrors.ConfirmationInvalid, "The confirmation is empty.");
        var receiver = SalesSql.Optional(report.ReceiverName, 200, "The receiver's name")
            ?? throw new DomainException(DriverConfirmationErrors.ConfirmationInvalid, "Write who received.");
        var nationalId = SalesSql.Optional(report.ReceiverNationalId, 11, "The receiver's cédula");
        if (nationalId is not null && !nationalId.All(char.IsAsciiDigit) || nationalId?.Length is not (null or 11))
        {
            throw new DomainException(DriverConfirmationErrors.ConfirmationInvalid, "The cédula is 11 digits.");
        }

        if (report.Outcome is not ("FULL" or "DIFFERENCES"))
        {
            throw new DomainException(DriverConfirmationErrors.ConfirmationInvalid, "Say whether everything was received.");
        }

        var note = SalesSql.Optional(report.Note, 1000, "The note");
        if (report.Outcome == "DIFFERENCES" && note is null)
        {
            throw new DomainException(DriverConfirmationErrors.ConfirmationInvalid, "Write what was different.");
        }

        if (report.EvidenceKind is not ("PHOTO" or "SIGNATURE"))
        {
            throw new DomainException(DriverConfirmationErrors.ConfirmationInvalid, "The evidence is a photo or a signature.");
        }

        var evidenceRef = SalesSql.Optional(report.EvidenceRef, 200, "The evidence reference")
            ?? throw new DomainException(DriverConfirmationErrors.ConfirmationInvalid, "The photo or signature is required.");
        var evidenceHash = Deliveries.Sha256(report.EvidenceSha256);
        if ((report.Latitude is null) != (report.Longitude is null)
            || report.Latitude is < -90m or > 90m || report.Longitude is < -180m or > 180m || report.AccuracyM is < 0m // type-limit: degrees on Earth
            || (report.AccuracyM is not null && report.Latitude is null))
        {
            throw new DomainException(DriverConfirmationErrors.ConfirmationInvalid, "The location is not valid.");
        }

        var (state, link, remaining) = await DriverAccess.CheckAsync(context, key, command.Access, cancellationToken).ConfigureAwait(false);
        if (state != "OK" || link is null)
        {
            return JsonSerializer.Serialize(new { state, remaining });
        }

        // E-ENT1-01-6: the phone's time between gate out and the server's time + 5 minutes; otherwise the server's.
        var now = context.Clock.UtcNow;
        var phoneAt = report.PhoneAt is { } p ? DateTime.SpecifyKind(p, DateTimeKind.Utc) : (DateTime?)null;
        var confirmedAt = phoneAt is { } t && t >= link.CreatedAt && t <= now + PhoneAhead ? t : now;
        var driver = await SalesSql.ScalarAsync<Guid?>(
            context, "SELECT driver_id FROM log.delivery WHERE company_id = @c AND delivery_id = @d", cancellationToken, ("c", context.CompanyId), ("d", command.Access.DeliveryId)).ConfigureAwait(false);
        var version = link.Version + 1;
        var confirmationId = context.Ids.NewId();
        var eventId = await context.AppendEventAsync(
            new EventDraft(
                report.Outcome == "FULL" ? "DriverConfirmedDelivery" : "DriverReportedDifferences", 1, DriverLinks.Aggregate, command.Access.DeliveryId, version,
                JsonSerializer.Serialize(new { deliveryId = command.Access.DeliveryId, confirmationId, receiver, outcome = report.Outcome, note, confirmedAt, evidenceRef }),
                Publish: true),
            cancellationToken).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            """
            INSERT INTO log.driver_confirmation (confirmation_id, company_id, delivery_id, generation, driver_id, receiver_name, receiver_national_id, outcome, note,
                                                 phone_at, server_at, confirmed_at, latitude, longitude, accuracy_m, evidence_kind, evidence_ref, evidence_sha256,
                                                 client_address, event_id)
            VALUES (@id, @c, @d, @g, @driver, @receiver, @nid, @outcome, @note, @phone, @server, @confirmed, @lat, @lon, @acc, @kind, @ref, @hash, @addr, @e)
            """,
            cancellationToken,
            ("id", confirmationId),
            ("c", context.CompanyId),
            ("d", command.Access.DeliveryId),
            ("g", link.Generation),
            ("driver", driver!.Value),
            ("receiver", receiver),
            ("nid", nationalId),
            ("outcome", report.Outcome),
            ("note", note),
            ("phone", phoneAt),
            ("server", now),
            ("confirmed", confirmedAt),
            ("lat", report.Latitude),
            ("lon", report.Longitude),
            ("acc", report.AccuracyM),
            ("kind", report.EvidenceKind),
            ("ref", evidenceRef),
            ("hash", evidenceHash),
            ("addr", IPAddress.Parse(command.Access.ClientAddress)),
            ("e", eventId)).ConfigureAwait(false);
        await Sql.ExecuteAsync(
            context.Connection,
            context.Transaction,
            "UPDATE log.delivery_link SET status = 'CONFIRMED', version = @v WHERE company_id = @c AND delivery_id = @d",
            cancellationToken,
            ("v", version),
            ("c", context.CompanyId),
            ("d", command.Access.DeliveryId)).ConfigureAwait(false);

        if (report.Outcome == "DIFFERENCES")
        {
            return JsonSerializer.Serialize(new { state = "DIFFERENCES_REPORTED", confirmationId });
        }

        // E-ENT-4: everything received — the POD, exactly as Dispatch would record it with every line received in full.
        var deliveryVersion = await SalesSql.ScalarAsync<long>(
            context, "SELECT version FROM log.delivery WHERE company_id = @c AND delivery_id = @d", cancellationToken, ("c", context.CompanyId), ("d", command.Access.DeliveryId)).ConfigureAwait(false);
        var lines = await Deliveries.LinesAsync(context, command.Access.DeliveryId, cancellationToken).ConfigureAwait(false);
        var pod = new RecordPod(
            command.CompanyId, command.SessionId, command.IdempotencyKey, command.Access.DeliveryId, deliveryVersion, receiver, confirmedAt > now ? now : confirmedAt, evidenceRef,
            report.EvidenceSha256, [.. lines.Select(l => new PodLine(l.Id, l.Issued, 0m))], null);
        var result = JsonDocument.Parse(await new RecordPodHandler().RecordAsync(pod, context, CommandType, confirmationId, cancellationToken).ConfigureAwait(false)).RootElement;
        return JsonSerializer.Serialize(new { state = "DELIVERED", confirmationId, status = result.GetProperty("status").GetString() });
    }
}

/// <summary>
/// E-ENT-1/3: what the public page shows of a delivery — number, customer, site, driver and lines without prices — and the link's state.
/// Read under the service identity; nothing about money.
/// </summary>
public sealed record GetDriverDelivery(Guid CompanyId, Guid SessionId, Guid DeliveryId, int Generation, string Mac) : IQuery;

public sealed record DriverDeliveryLine(string ItemCode, string Description, string Quantity, string Uom);

public sealed record DriverDeliveryView(
    string State, string? DeliveryNo, string? Customer, string? Site, string? Driver, string? Vehicle, IReadOnlyList<DriverDeliveryLine> Lines, string? RecordedOutcome);

[RequiresPermission("delivery:driver_confirm")]
public sealed class GetDriverDeliveryHandler(DriverLinkKey? key = null) : IQueryHandler<GetDriverDelivery>
{
    private sealed record Head(string No, string Customer, string? Site, string? Driver, string? Vehicle, string Status, string? Outcome);

    public string QueryType => "Sales.GetDriverDelivery";

    public async Task<string> HandleAsync(GetDriverDelivery query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var link = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT generation, status, expires_at, failed_attempts, created_at, version FROM log.delivery_link WHERE company_id = @c AND delivery_id = @d",
            r => new DriverLinks.Link(r.GetInt32(0), r.GetString(1), r.GetDateTime(2), r.GetInt32(3), r.GetDateTime(4), r.GetInt64(5)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", query.DeliveryId)).ConfigureAwait(false);
        var state = key is null ? "INVALID" : DriverLinks.State(link, query.Generation, key.Verify(context.CompanyId, query.DeliveryId, query.Generation, query.Mac), context.Clock.UtcNow);
        if (state == "INVALID")
        {
            return JsonSerializer.Serialize(new DriverDeliveryView(state, null, null, null, null, null, [], null));
        }

        // E-ENT1-01-9: an annulled link whose delivery Dispatch delivered says so.
        var head = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT d.delivery_no, p.legal_name, o.site_address, dr.full_name, v.plate, d.status,
                   (SELECT c.outcome FROM log.driver_confirmation c WHERE c.company_id = d.company_id AND c.delivery_id = d.delivery_id ORDER BY c.generation DESC LIMIT 1)
            FROM log.delivery d
            JOIN sal.sales_order o ON o.sales_order_id = d.sales_order_id
            JOIN md.party p ON p.party_id = o.party_id
            LEFT JOIN log.driver dr ON dr.driver_id = d.driver_id
            LEFT JOIN log.vehicle v ON v.vehicle_id = d.vehicle_id
            WHERE d.company_id = @c AND d.delivery_id = @d
            """,
            r => new Head(r.GetString(0), r.GetString(1), r.NullableString(2), r.NullableString(3), r.NullableString(4), r.GetString(5), r.NullableString(6)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", query.DeliveryId)).ConfigureAwait(false);
        if (head is null)
        {
            return JsonSerializer.Serialize(new DriverDeliveryView("INVALID", null, null, null, null, null, [], null));
        }

        if (state == "ANNULLED" && head.Status is "DELIVERED" or "DELIVERED_WITH_EXCEPTIONS")
        {
            state = "RECORDED_BY_DISPATCH";
        }

        var lines = await Reading.ListAsync(
            context.Connection,
            context.Transaction,
            """
            SELECT i.code, i.description, dl.qty_issued, dl.uom FROM log.delivery_line dl JOIN md.item i ON i.item_id = dl.item_id
            WHERE dl.company_id = @c AND dl.delivery_id = @d ORDER BY dl.line_no
            """,
            r => new DriverDeliveryLine(r.GetString(0), r.GetString(1), r.GetDecimal(2).ToString("0.######", CultureInfo.InvariantCulture), r.GetString(3)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", query.DeliveryId)).ConfigureAwait(false);
        return JsonSerializer.Serialize(new DriverDeliveryView(state, head.No, head.Customer, head.Site, head.Driver, head.Vehicle, lines, head.Outcome));
    }
}

/// <summary>ENT1-02 (E-ENT-5): Dispatch sees the driver's photo or signature of a delivery, read from the private bucket.</summary>
public sealed record GetDriverEvidence(Guid CompanyId, Guid SessionId, Guid DeliveryId) : IQuery;

public sealed record DriverEvidenceContent(string ContentType, string ContentBase64, string Sha256);

[RequiresPermission("sales:read")]
public sealed class GetDriverEvidenceHandler(Rochell.Platform.Files.IEvidenceStore? store = null) : IQueryHandler<GetDriverEvidence>
{
    private sealed record Evidence(string Ref, byte[] Sha256);

    public string QueryType => "Sales.GetDriverEvidence";

    public async Task<string> HandleAsync(GetDriverEvidence query, QueryContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var evidence = await Reading.SingleOrDefaultAsync(
            context.Connection,
            context.Transaction,
            "SELECT evidence_ref, evidence_sha256 FROM log.driver_confirmation WHERE company_id = @c AND delivery_id = @d ORDER BY generation DESC LIMIT 1",
            r => new Evidence(r.GetString(0), r.GetFieldValue<byte[]>(1)),
            cancellationToken,
            ("c", context.CompanyId),
            ("d", query.DeliveryId)).ConfigureAwait(false)
            ?? throw new DomainException(QueryErrors.NotFound, "The driver has not confirmed this delivery.");
        var file = store is null ? null : await store.GetAsync(evidence.Ref, cancellationToken).ConfigureAwait(false);
        if (file is null || !SHA256.HashData(file.Content).AsSpan().SequenceEqual(evidence.Sha256))
        {
            throw new DomainException(QueryErrors.NotFound, "The photo is not in the evidence store, or it is not the one recorded.");
        }

        return JsonSerializer.Serialize(new DriverEvidenceContent(file.ContentType, Convert.ToBase64String(file.Content), Convert.ToHexStringLower(evidence.Sha256)));
    }
}

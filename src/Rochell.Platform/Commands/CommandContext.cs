using System.Data.Common;
using Rochell.Platform.Data;
using Rochell.Platform.Hashing;
using Rochell.Platform.Ids;
using Rochell.Platform.Time;

namespace Rochell.Platform.Commands;

/// <summary>
/// Per-command unit of work: the single connection/transaction plus identifiers pre-assigned before any INSERT (Errata E-4).
/// Events are inserted immediately (documents reference them); outbox rows are written by the pipeline before COMMIT.
/// </summary>
public sealed class CommandContext
{
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly Dictionary<(string Type, Guid Id, long Version), short> _sequences = [];
    private readonly List<Guid> _published = [];
    private int _commandEventIndex;

    internal CommandContext(
        DbConnection connection,
        DbTransaction transaction,
        ICommand command,
        Guid commandId,
        Guid resultRef,
        Guid correlationId,
        IClock clock,
        IIdGenerator ids)
    {
        Connection = connection;
        Transaction = transaction;
        CompanyId = command.CompanyId;
        SessionId = command.SessionId;
        CommandId = commandId;
        ResultRef = resultRef;
        CorrelationId = correlationId;
        _clock = clock;
        _ids = ids;
    }

    public DbConnection Connection { get; }

    public DbTransaction Transaction { get; }

    public Guid CompanyId { get; }

    public Guid SessionId { get; }

    public Guid CommandId { get; }

    /// <summary>Pre-assigned id of the resource this command creates (Patch 1, P-3).</summary>
    public Guid ResultRef { get; }

    public Guid CorrelationId { get; }

    public IClock Clock => _clock;

    public IIdGenerator Ids => _ids;

    internal IReadOnlyList<Guid> PublishedEventIds => _published;

    /// <summary>Inserts a domain event with its canonical row hash. Returns the event id.</summary>
    public async Task<Guid> AppendEventAsync(EventDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var key = (draft.AggregateType, draft.AggregateId, draft.AggregateVersion);
        var sequence = (short)(_sequences.GetValueOrDefault(key) + 1);
        _sequences[key] = sequence;

        var recordedAt = _clock.UtcNow;
        var occurredAt = Precision.ToMicroseconds(draft.OccurredAt ?? recordedAt);
        var row = new DomainEventRow(
            EventId: _ids.NewId(),
            CompanyId: CompanyId,
            CommandId: CommandId,
            CommandEventIndex: ++_commandEventIndex,
            EventType: draft.EventType,
            SchemaVersion: draft.SchemaVersion,
            AggregateType: draft.AggregateType,
            AggregateId: draft.AggregateId,
            AggregateVersion: draft.AggregateVersion,
            EventSequence: sequence,
            OccurredAt: occurredAt,
            RecordedAt: recordedAt,
            BusinessDate: draft.BusinessDate ?? BusinessCalendar.DefaultBusinessDate(occurredAt),
            SessionId: SessionId,
            CorrelationId: CorrelationId,
            CausationId: draft.CausationId,
            Payload: JsonCanonicalizer.Canonicalize(draft.PayloadJson));

        await Sql.ExecuteAsync(
            Connection,
            Transaction,
            """
            INSERT INTO core.domain_event
              (event_id, company_id, command_id, command_event_index, event_type, schema_version,
               aggregate_type, aggregate_id, aggregate_version, event_sequence, occurred_at, recorded_at,
               business_date, session_id, correlation_id, causation_id, payload, row_hash)
            VALUES
              (@event_id, @company_id, @command_id, @command_event_index, @event_type, @schema_version,
               @aggregate_type, @aggregate_id, @aggregate_version, @event_sequence, @occurred_at, @recorded_at,
               @business_date, @session_id, @correlation_id, @causation_id, CAST(@payload AS jsonb), @row_hash)
            """,
            cancellationToken,
            ("event_id", row.EventId),
            ("company_id", row.CompanyId),
            ("command_id", row.CommandId),
            ("command_event_index", row.CommandEventIndex),
            ("event_type", row.EventType),
            ("schema_version", row.SchemaVersion),
            ("aggregate_type", row.AggregateType),
            ("aggregate_id", row.AggregateId),
            ("aggregate_version", row.AggregateVersion),
            ("event_sequence", row.EventSequence),
            ("occurred_at", row.OccurredAt),
            ("recorded_at", row.RecordedAt),
            ("business_date", row.BusinessDate),
            ("session_id", row.SessionId),
            ("correlation_id", row.CorrelationId),
            ("causation_id", row.CausationId),
            ("payload", row.Payload),
            ("row_hash", row.ComputeRowHash())).ConfigureAwait(false);

        if (draft.Publish)
        {
            _published.Add(row.EventId);
        }

        return row.EventId;
    }
}

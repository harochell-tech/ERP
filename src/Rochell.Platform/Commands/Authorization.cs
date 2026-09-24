using System.Data.Common;

namespace Rochell.Platform.Commands;

/// <summary>
/// Declares the permission a command handler requires (baseline §14). Mandatory on every production handler
/// (ArchitectureTests). <see cref="StepUp"/> = the session must have re-authenticated recently (§14 "S").
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RequiresPermissionAttribute : Attribute
{
    public RequiresPermissionAttribute(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        Permission = permission;
    }

    public string Permission { get; }

    public bool StepUp { get; init; }
}

/// <summary>A command scoped to one plant: the acting user needs an assignment for that plant or for the whole company.</summary>
public interface IPlantScopedCommand : ICommand
{
    Guid PlantId { get; }
}

/// <summary>
/// Evaluates session, user, role assignments, permission, plant scope and step-up inside the command transaction,
/// before anything is written. Throws <see cref="DomainException"/> when the command is not allowed.
/// </summary>
public interface ICommandAuthorizer
{
    Task AuthorizeAsync(
        DbConnection connection,
        DbTransaction transaction,
        ICommand command,
        RequiresPermissionAttribute requirement,
        CancellationToken cancellationToken);

    /// <summary>
    /// Conditional step-up requested by a handler (e.g. "re-authenticate above a policy threshold", E-PR08-2).
    /// Throws <see cref="DomainException"/> with <see cref="AuthorizationErrors.StepUpRequired"/> when the session's
    /// last re-authentication is too old.
    /// </summary>
    Task EnsureStepUpAsync(DbConnection connection, DbTransaction transaction, ICommand command, CancellationToken cancellationToken);
}

/// <summary>Error codes raised by authorization (request_log outcome REJECTED_DOMAIN).</summary>
public static class AuthorizationErrors
{
    public const string SessionInvalid = "SESSION_INVALID";
    public const string SessionExpired = "SESSION_EXPIRED";
    public const string NotAuthorized = "NOT_AUTHORIZED";
    public const string StepUpRequired = "STEP_UP_REQUIRED";
}

/// <summary>
/// The command runs in a SERIALIZABLE transaction instead of READ COMMITTED (T-13, CloseComponent). Serialization failures
/// are retried by the pipeline like any other retryable conflict.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SerializableTransactionAttribute : Attribute
{
}

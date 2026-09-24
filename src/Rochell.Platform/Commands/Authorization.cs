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
}

/// <summary>Error codes raised by authorization (request_log outcome REJECTED_DOMAIN).</summary>
public static class AuthorizationErrors
{
    public const string SessionInvalid = "SESSION_INVALID";
    public const string SessionExpired = "SESSION_EXPIRED";
    public const string NotAuthorized = "NOT_AUTHORIZED";
    public const string StepUpRequired = "STEP_UP_REQUIRED";
}

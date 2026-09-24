using Rochell.Platform.Commands;

namespace Rochell.Identity.RoleChanges;

/// <summary>Requests granting <paramref name="RoleCode"/> to <paramref name="TargetUserId"/> (T-15, step 1).</summary>
public sealed record RequestRoleAssignment(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid TargetUserId,
    string RoleCode,
    Guid? PlantId = null) : ICommand;

/// <summary>Requests revoking an active assignment of <paramref name="RoleCode"/> (T-15, step 1).</summary>
public sealed record RequestRoleRevocation(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid TargetUserId,
    string RoleCode,
    Guid? PlantId = null) : ICommand;

/// <summary>Second approval of a role change request; the change takes effect in this transaction (T-15, step 2).</summary>
public sealed record ApproveRoleChange(
    Guid CompanyId,
    Guid SessionId,
    string IdempotencyKey,
    Guid RequestId) : ICommand;

public static class RoleChangeErrors
{
    public const string RoleUnknown = "ROLE_UNKNOWN";
    public const string UserInvalid = "USER_INVALID";
    public const string SelfRequest = "SELF_REQUEST";
    public const string AssignmentNotFound = "ASSIGNMENT_NOT_FOUND";
    public const string AlreadyAssigned = "ALREADY_ASSIGNED";
    public const string RequestNotFound = "REQUEST_NOT_FOUND";
    public const string RequestNotPending = "REQUEST_NOT_PENDING";
    public const string SecondApproverRequired = "SECOND_APPROVER_REQUIRED";
    public const string SodConflict = "SOD_CONFLICT";
}

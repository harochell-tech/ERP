namespace Rochell.Migrations;

/// <summary>Raised when migrations cannot be applied safely. The database is left unchanged for the failing script.</summary>
public sealed class MigrationException : Exception
{
    public MigrationException()
    {
    }

    public MigrationException(string message)
        : base(message)
    {
    }

    public MigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

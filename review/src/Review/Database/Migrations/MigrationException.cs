namespace Review.Database.Migrations;

/// <summary>
/// A run of <c>migrate</c> that cannot go on: files it cannot order, files that disagree with the
/// ledger, or a file that failed. Nothing after the failing step was applied.
/// </summary>
public sealed class MigrationException : Exception
{
    public MigrationException(string message)
        : base(message)
    {
    }

    public MigrationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
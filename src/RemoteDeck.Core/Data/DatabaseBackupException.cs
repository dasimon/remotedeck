namespace RemoteDeck.Core.Data;

/// <summary>
/// The copy a migration takes first could not be written, so the migration did not run. The database
/// is still at the version the user had — the one their previous build can open.
/// </summary>
public sealed class DatabaseBackupException(string backupPath, Exception inner)
    : Exception($"The database was not upgraded: its backup could not be written to {backupPath}. {inner.Message}", inner)
{
    /// <summary>Where the copy was to be written.</summary>
    public string BackupPath { get; } = backupPath;
}

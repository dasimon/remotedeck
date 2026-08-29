using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RemoteDeck.Core.Data;

internal static class SqliteExtensions
{
    public static SqliteCommand Cmd(this SqliteConnection connection, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    public static void Add(this SqliteCommand cmd, string name, object? value)
        => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    public static string? GetStringOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long? GetInt64OrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    public static int? GetInt32OrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    public static DateTime GetUtc(this SqliteDataReader r, int i)
        => DateTime.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTime? GetUtcOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetUtc(i);

    /// <summary>ISO-8601 round-trip text; the only date format written to the database.</summary>
    public static string ToDb(this DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

using System.Globalization;
using RemoteDeck.Core.Data;

namespace RemoteDeck.Core.Tests.Data;

/// <summary>The reader/writer helpers are the only place dates cross the SQLite boundary, so they get real files.</summary>
public sealed class SqliteExtensionsTests
{
    [Fact]
    public void ToDb_round_trips_a_utc_datetime_through_a_text_column()
    {
        using var tmp = new TempDatabase();
        using var c = tmp.Db.Open();
        c.Cmd("CREATE TABLE T (Moment TEXT NOT NULL)").ExecuteNonQuery();
        var value = new DateTime(2026, 8, 29, 13, 45, 30, DateTimeKind.Utc).AddTicks(1234567);

        var insert = c.Cmd("INSERT INTO T(Moment) VALUES ($m)");
        insert.Add("$m", value.ToDb());
        insert.ExecuteNonQuery();

        using var r = c.Cmd("SELECT Moment FROM T").ExecuteReader();
        Assert.True(r.Read());
        var read = r.GetUtc(0);
        Assert.Equal(value, read);
        Assert.Equal(DateTimeKind.Utc, read.Kind);
    }

    [Fact]
    public void ToDb_treats_an_unspecified_kind_as_already_utc()
    {
        var value = new DateTime(2026, 8, 29, 13, 45, 30, DateTimeKind.Unspecified);

        var text = value.ToDb();

        Assert.Equal("2026-08-29T13:45:30.0000000Z", text);
        var parsed = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(value.Ticks, parsed.Ticks);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
    }

    [Fact]
    public void ToDb_converts_a_local_datetime_to_utc()
    {
        var value = new DateTime(2026, 8, 29, 13, 45, 30, DateTimeKind.Local);

        var text = value.ToDb();

        Assert.Equal(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), text);
    }

    [Fact]
    public void Nullable_readers_map_null_columns_to_null_and_values_otherwise()
    {
        using var tmp = new TempDatabase();
        using var c = tmp.Db.Open();
        c.Cmd("CREATE TABLE T (Label TEXT NULL, Big INTEGER NULL, Small INTEGER NULL, Moment TEXT NULL)").ExecuteNonQuery();
        var moment = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var withValues = c.Cmd("INSERT INTO T(Label, Big, Small, Moment) VALUES ($l, $b, $s, $m)");
        withValues.Add("$l", "label");
        withValues.Add("$b", 9_000_000_000L);
        withValues.Add("$s", 42);
        withValues.Add("$m", moment.ToDb());
        withValues.ExecuteNonQuery();

        var withNulls = c.Cmd("INSERT INTO T(Label, Big, Small, Moment) VALUES ($l, $b, $s, $m)");
        withNulls.Add("$l", null);
        withNulls.Add("$b", null);
        withNulls.Add("$s", null);
        withNulls.Add("$m", null);
        withNulls.ExecuteNonQuery();

        using var r = c.Cmd("SELECT Label, Big, Small, Moment FROM T ORDER BY rowid").ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("label", r.GetStringOrNull(0));
        Assert.Equal(9_000_000_000L, r.GetInt64OrNull(1));
        Assert.Equal(42, r.GetInt32OrNull(2));
        Assert.Equal(moment, r.GetUtcOrNull(3));
        Assert.True(r.Read());
        Assert.Null(r.GetStringOrNull(0));
        Assert.Null(r.GetInt64OrNull(1));
        Assert.Null(r.GetInt32OrNull(2));
        Assert.Null(r.GetUtcOrNull(3));
    }
}

using System.Text.Json;
using KaraokeDJ.Models;
using Microsoft.Data.Sqlite;

namespace KaraokeDJ.Services;

/// <summary>
/// Database locale della libreria (SQLite, %AppData%\KaraokeDJ\library.db): una riga per brano con il brano serializzato
/// in JSON più le colonne indicizzate. Ogni modifica a un brano aggiorna solo la sua riga: niente riscritture complete.
/// </summary>
public sealed class LibraryDb : IDisposable
{
    public static string DbPath => Path.Combine(AppPaths.Root, "library.db");
    private readonly SqliteConnection _con;
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public LibraryDb()
    {
        Directory.CreateDirectory(AppPaths.Root);
        _con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString());
        _con.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS tracks (
                id TEXT PRIMARY KEY,
                path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                artist TEXT, title TEXT, genre TEXT, year INTEGER, kind INTEGER,
                updated INTEGER NOT NULL,
                json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_tracks_artist ON tracks(artist COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_tracks_title ON tracks(title COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
            """);
    }

    private void Exec(string sql)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public int Count()
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tracks";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<Track> LoadAll()
    {
        var list = new List<Track>();
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT json FROM tracks";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            try
            {
                var t = JsonSerializer.Deserialize<Track>(r.GetString(0), Json);
                if (t != null && t.FilePath.Length > 0) list.Add(t);
            }
            catch { }
        }
        return list;
    }

    public void Upsert(Track t)
    {
        using var tx = _con.BeginTransaction();
        UpsertNoTx(t);
        tx.Commit();
    }

    public void UpsertAll(IEnumerable<Track> tracks)
    {
        using var tx = _con.BeginTransaction();
        foreach (var t in tracks) UpsertNoTx(t);
        tx.Commit();
    }

    private void UpsertNoTx(Track t)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tracks (id, path, artist, title, genre, year, kind, updated, json)
            VALUES ($id, $path, $artist, $title, $genre, $year, $kind, $updated, $json)
            ON CONFLICT(id) DO UPDATE SET path=excluded.path, artist=excluded.artist, title=excluded.title, genre=excluded.genre,
                year=excluded.year, kind=excluded.kind, updated=excluded.updated, json=excluded.json
            """;
        cmd.Parameters.AddWithValue("$id", t.Id);
        cmd.Parameters.AddWithValue("$path", t.FilePath);
        cmd.Parameters.AddWithValue("$artist", t.Artist);
        cmd.Parameters.AddWithValue("$title", t.Title);
        cmd.Parameters.AddWithValue("$genre", t.Genre);
        cmd.Parameters.AddWithValue("$year", t.Year);
        cmd.Parameters.AddWithValue("$kind", (int)t.Kind);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(t, Json));
        try { cmd.ExecuteNonQuery(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // stesso percorso con id diverso (es. file rientrato dopo una cancellazione): sostituisce la riga vecchia
            using var del = _con.CreateCommand();
            del.CommandText = "DELETE FROM tracks WHERE path = $path COLLATE NOCASE";
            del.Parameters.AddWithValue("$path", t.FilePath);
            del.ExecuteNonQuery();
            cmd.ExecuteNonQuery();
        }
    }

    public void Delete(string id)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "DELETE FROM tracks WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteMany(IEnumerable<string> ids)
    {
        using var tx = _con.BeginTransaction();
        foreach (var id in ids)
        {
            using var cmd = _con.CreateCommand();
            cmd.CommandText = "DELETE FROM tracks WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Manutenzione leggera all'avvio: statistiche per il planner e checkpoint del WAL.</summary>
    public void Optimize() { Exec("PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);"); }

    public string? GetMeta(string key)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }


    // ---------------------------------------------------------------- playlist

    public void EnsurePlaylistTables()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS playlists (id TEXT PRIMARY KEY, name TEXT NOT NULL, position INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS playlist_tracks (playlist_id TEXT NOT NULL, track_id TEXT NOT NULL, position INTEGER NOT NULL,
                PRIMARY KEY (playlist_id, position));
            """);
    }

    public List<Playlist> LoadPlaylists()
    {
        EnsurePlaylistTables();
        var list = new List<Playlist>();
        using (var cmd = _con.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name FROM playlists ORDER BY position";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new Playlist { Id = r.GetString(0), Name = r.GetString(1) });
        }
        foreach (var p in list)
        {
            using var cmd = _con.CreateCommand();
            cmd.CommandText = "SELECT track_id FROM playlist_tracks WHERE playlist_id = $p ORDER BY position";
            cmd.Parameters.AddWithValue("$p", p.Id);
            using var r = cmd.ExecuteReader();
            while (r.Read()) p.TrackIds.Add(r.GetString(0));
        }
        return list;
    }

    /// <summary>Riscrive una playlist (nome, ordine, brani): sono poche righe, si fa in una transazione.</summary>
    public void SavePlaylist(Playlist p, int position)
    {
        EnsurePlaylistTables();
        using var tx = _con.BeginTransaction();
        using (var cmd = _con.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO playlists (id, name, position) VALUES ($id, $name, $pos) ON CONFLICT(id) DO UPDATE SET name = excluded.name, position = excluded.position";
            cmd.Parameters.AddWithValue("$id", p.Id); cmd.Parameters.AddWithValue("$name", p.Name); cmd.Parameters.AddWithValue("$pos", position);
            cmd.ExecuteNonQuery();
        }
        using (var del = _con.CreateCommand())
        {
            del.CommandText = "DELETE FROM playlist_tracks WHERE playlist_id = $p";
            del.Parameters.AddWithValue("$p", p.Id);
            del.ExecuteNonQuery();
        }
        int i = 0;
        foreach (var id in p.TrackIds)
        {
            using var ins = _con.CreateCommand();
            ins.CommandText = "INSERT INTO playlist_tracks (playlist_id, track_id, position) VALUES ($p, $t, $i)";
            ins.Parameters.AddWithValue("$p", p.Id); ins.Parameters.AddWithValue("$t", id); ins.Parameters.AddWithValue("$i", i++);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void SaveAllPlaylists(IEnumerable<Playlist> playlists)
    {
        EnsurePlaylistTables();
        var list = playlists.ToList();
        using (var tx = _con.BeginTransaction())
        {
            using var del = _con.CreateCommand();
            del.CommandText = "DELETE FROM playlists WHERE id NOT IN (" + string.Join(",", list.Select((_, i) => "$i" + i)) + ")";
            for (int i = 0; i < list.Count; i++) del.Parameters.AddWithValue("$i" + i, list[i].Id);
            if (list.Count == 0) del.CommandText = "DELETE FROM playlists";
            del.ExecuteNonQuery();
            using var del2 = _con.CreateCommand();
            del2.CommandText = "DELETE FROM playlist_tracks WHERE playlist_id NOT IN (SELECT id FROM playlists)";
            del2.ExecuteNonQuery();
            tx.Commit();
        }
        for (int i = 0; i < list.Count; i++) SavePlaylist(list[i], i);
    }

    public void DeletePlaylist(string id)
    {
        EnsurePlaylistTables();
        using var tx = _con.BeginTransaction();
        foreach (var sql in new[] { "DELETE FROM playlist_tracks WHERE playlist_id = $id", "DELETE FROM playlists WHERE id = $id" })
        {
            using var cmd = _con.CreateCommand();
            cmd.CommandText = sql; cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Dispose() => _con.Dispose();
}

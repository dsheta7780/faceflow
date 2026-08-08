using Microsoft.Data.Sqlite;

namespace FaceFlow.Core.Data;

/// <summary>
/// SQLite access layer. Single serialised writer, many concurrent readers, WAL mode.
/// Embeddings are stored as raw float32 BLOBs (512 floats = 2048 bytes).
/// </summary>
public sealed class Db : IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection _write;
    private readonly object _writeGate = new();

    public Db(string? path = null)
    {
        var file = path ?? AppPaths.DbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();

        _write = new SqliteConnection(_connectionString);
        _write.Open();
        Exec(_write, "PRAGMA journal_mode=WAL;");
        Exec(_write, "PRAGMA synchronous=NORMAL;");
        Exec(_write, "PRAGMA temp_store=MEMORY;");
        Exec(_write, "PRAGMA mmap_size=268435456;");
        Exec(_write, "PRAGMA cache_size=-65536;");
        Exec(_write, "PRAGMA busy_timeout=15000;");
        CreateSchema();
    }

    public SqliteConnection OpenRead()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=15000;");
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void CreateSchema()
    {
        lock (_writeGate)
        {
            Exec(_write, @"
            CREATE TABLE IF NOT EXISTS libraries (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                path         TEXT NOT NULL UNIQUE,
                created_at   INTEGER NOT NULL,
                last_scan_at INTEGER
            );

            CREATE TABLE IF NOT EXISTS photos (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                library_id  INTEGER NOT NULL,
                path        TEXT NOT NULL UNIQUE,
                file_size   INTEGER NOT NULL,
                mtime       INTEGER NOT NULL,
                width       INTEGER NOT NULL DEFAULT 0,
                height      INTEGER NOT NULL DEFAULT 0,
                face_count  INTEGER NOT NULL DEFAULT -1,
                state       INTEGER NOT NULL DEFAULT 0,
                indexed_at  INTEGER,
                error       TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_photos_state   ON photos(state);
            CREATE INDEX IF NOT EXISTS ix_photos_library ON photos(library_id, state);
            CREATE INDEX IF NOT EXISTS ix_photos_faces   ON photos(face_count);

            CREATE TABLE IF NOT EXISTS people (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                name          TEXT NOT NULL,
                is_named      INTEGER NOT NULL DEFAULT 0,
                cover_face_id INTEGER,
                face_count    INTEGER NOT NULL DEFAULT 0,
                centroid      BLOB,
                centroid_n    INTEGER NOT NULL DEFAULT 0,
                created_at    INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_people_named ON people(is_named);

            CREATE TABLE IF NOT EXISTS faces (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                photo_id   INTEGER NOT NULL,
                person_id  INTEGER,
                x          REAL NOT NULL,
                y          REAL NOT NULL,
                w          REAL NOT NULL,
                h          REAL NOT NULL,
                det_score  REAL NOT NULL,
                quality    REAL NOT NULL,
                similarity REAL NOT NULL DEFAULT 0,
                status     INTEGER NOT NULL DEFAULT 0,
                embedding  BLOB NOT NULL,
                created_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_faces_photo  ON faces(photo_id);
            CREATE INDEX IF NOT EXISTS ix_faces_person ON faces(person_id);
            CREATE INDEX IF NOT EXISTS ix_faces_status ON faces(status);

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);
            ");

            using var check = _write.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM schema_info";
            if (Convert.ToInt64(check.ExecuteScalar()) == 0)
                Exec(_write, "INSERT INTO schema_info(version) VALUES (1)");
        }
    }

    // ------------------------------------------------------------- write API

    public T InWriteTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> body)
    {
        lock (_writeGate)
        {
            using var tx = _write.BeginTransaction();
            try
            {
                var result = body(_write, tx);
                tx.Commit();
                return result;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }
    }

    public void InWriteTransaction(Action<SqliteConnection, SqliteTransaction> body)
        => InWriteTransaction<object?>((c, t) => { body(c, t); return null; });

    public void Write(string sql, params (string, object?)[] p)
    {
        lock (_writeGate)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public long WriteScalar(string sql, params (string, object?)[] p)
    {
        lock (_writeGate)
        {
            using var cmd = _write.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            var r = cmd.ExecuteScalar();
            return r is null or DBNull ? 0 : Convert.ToInt64(r);
        }
    }

    // ---------------------------------------------------------------- blobs

    public static byte[] ToBlob(float[] v)
    {
        var bytes = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBlob(byte[] b)
    {
        var v = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, v, 0, b.Length);
        return v;
    }

    // -------------------------------------------------------------- settings

    public string? GetSetting(string key)
    {
        using var c = OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
        => Write("INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v",
                 ("$k", key), ("$v", value));

    public void Checkpoint()
    {
        lock (_writeGate) { try { Exec(_write, "PRAGMA wal_checkpoint(TRUNCATE);"); } catch { } }
    }

    public void Dispose()
    {
        try { Checkpoint(); _write.Dispose(); } catch { }
        SqliteConnection.ClearAllPools();
    }
}

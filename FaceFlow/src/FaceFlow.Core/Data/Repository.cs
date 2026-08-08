using Microsoft.Data.Sqlite;

namespace FaceFlow.Core.Data;

/// <summary>All queries the UI and the scanner need. Nothing here writes to your photo files.</summary>
public sealed class Repository
{
    private readonly Db _db;
    public Repository(Db db) => _db = db;
    public Db Database => _db;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static DateTime FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime;

    // ------------------------------------------------------------ libraries

    public long AddLibrary(string path)
    {
        path = Path.GetFullPath(path).TrimEnd('\\');
        _db.Write("INSERT OR IGNORE INTO libraries(path, created_at) VALUES($p,$c)",
                  ("$p", path), ("$c", Now()));
        return _db.WriteScalar("SELECT id FROM libraries WHERE path=$p", ("$p", path));
    }

    public void RemoveLibrary(long id)
    {
        _db.InWriteTransaction((c, tx) =>
        {
            Run(c, tx, "DELETE FROM faces WHERE photo_id IN (SELECT id FROM photos WHERE library_id=$i)", ("$i", id));
            Run(c, tx, "DELETE FROM photos WHERE library_id=$i", ("$i", id));
            Run(c, tx, "DELETE FROM libraries WHERE id=$i", ("$i", id));
        });
    }

    public List<LibraryRow> GetLibraries()
    {
        var list = new List<LibraryRow>();
        using var c = _db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            SELECT l.id, l.path, l.created_at, l.last_scan_at,
                   (SELECT COUNT(*) FROM photos p WHERE p.library_id = l.id),
                   (SELECT COUNT(*) FROM photos p WHERE p.library_id = l.id AND p.state = 0)
            FROM libraries l ORDER BY l.id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new LibraryRow
            {
                Id = r.GetInt64(0),
                Path = r.GetString(1),
                CreatedAt = FromUnix(r.GetInt64(2)),
                LastScanAt = r.IsDBNull(3) ? null : FromUnix(r.GetInt64(3)),
                PhotoCount = r.GetInt64(4),
                PendingCount = r.GetInt64(5)
            });
        return list;
    }

    public void TouchLibraryScan(long id) =>
        _db.Write("UPDATE libraries SET last_scan_at=$t WHERE id=$i", ("$t", Now()), ("$i", id));

    // --------------------------------------------------------------- photos

    /// <summary>Returns path -> (size, mtime, state) for fast incremental comparison.</summary>
    public Dictionary<string, (long Size, long MTime, int State)> GetPhotoFingerprints(long libraryId)
    {
        var map = new Dictionary<string, (long, long, int)>(StringComparer.OrdinalIgnoreCase);
        using var c = _db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, file_size, mtime, state FROM photos WHERE library_id=$l";
        cmd.Parameters.AddWithValue("$l", libraryId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2), r.GetInt32(3));
        return map;
    }

    /// <summary>Insert new files / reset changed files back to Pending. Returns how many need work.</summary>
    public int UpsertPendingPhotos(long libraryId, IEnumerable<(string Path, long Size, long MTime)> files)
    {
        var changed = 0;
        _db.InWriteTransaction((c, tx) =>
        {
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = @"
                INSERT INTO photos(library_id, path, file_size, mtime, state, face_count)
                VALUES($l,$p,$s,$m,0,-1)
                ON CONFLICT(path) DO UPDATE SET
                    file_size = $s,
                    mtime     = $m,
                    state     = 0,
                    face_count= -1,
                    error     = NULL
                WHERE photos.file_size <> $s OR photos.mtime <> $m";
            var pl = ins.Parameters.Add("$l", SqliteType.Integer);
            var pp = ins.Parameters.Add("$p", SqliteType.Text);
            var ps = ins.Parameters.Add("$s", SqliteType.Integer);
            var pm = ins.Parameters.Add("$m", SqliteType.Integer);

            foreach (var f in files)
            {
                pl.Value = libraryId; pp.Value = f.Path; ps.Value = f.Size; pm.Value = f.MTime;
                changed += ins.ExecuteNonQuery();
            }

            // A changed file's old faces are stale; drop them.
            Run(c, tx, @"DELETE FROM faces WHERE photo_id IN
                         (SELECT id FROM photos WHERE library_id=$l AND state=0)", ("$l", libraryId));
        });
        return changed;
    }

    public List<PhotoRow> TakePendingPhotos(long libraryId, int limit)
    {
        var list = new List<PhotoRow>(limit);
        using var c = _db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, path FROM photos WHERE library_id=$l AND state=0 ORDER BY id LIMIT $n";
        cmd.Parameters.AddWithValue("$l", libraryId);
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new PhotoRow { Id = r.GetInt64(0), Path = r.GetString(1), LibraryId = libraryId });
        return list;
    }

    public long CountPending(long libraryId)
    {
        using var c = _db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM photos WHERE library_id=$l AND state=0";
        cmd.Parameters.AddWithValue("$l", libraryId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ---------------------------------------------------------------- faces

    /// <summary>Persist one processed photo and its faces in a single transaction.</summary>
    public List<long> SavePhotoResult(long photoId, int width, int height,
                                      IReadOnlyList<FaceRow> faces, string? error)
    {
        return _db.InWriteTransaction((c, tx) =>
        {
            var ids = new List<long>(faces.Count);

            using (var up = c.CreateCommand())
            {
                up.Transaction = tx;
                up.CommandText = @"UPDATE photos SET width=$w, height=$h, face_count=$fc,
                                   state=$st, indexed_at=$t, error=$e WHERE id=$id";
                up.Parameters.AddWithValue("$w", width);
                up.Parameters.AddWithValue("$h", height);
                up.Parameters.AddWithValue("$fc", error is null ? faces.Count : -1);
                up.Parameters.AddWithValue("$st", error is null ? (int)PhotoState.Indexed : (int)PhotoState.Failed);
                up.Parameters.AddWithValue("$t", Now());
                up.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
                up.Parameters.AddWithValue("$id", photoId);
                up.ExecuteNonQuery();
            }

            if (faces.Count > 0)
            {
                using var ins = c.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = @"
                    INSERT INTO faces(photo_id, person_id, x, y, w, h, det_score, quality,
                                      similarity, status, embedding, created_at)
                    VALUES($pid,$per,$x,$y,$w,$h,$ds,$q,$sim,$st,$emb,$t);
                    SELECT last_insert_rowid();";
                foreach (var f in faces)
                {
                    ins.Parameters.Clear();
                    ins.Parameters.AddWithValue("$pid", photoId);
                    ins.Parameters.AddWithValue("$per", (object?)f.PersonId ?? DBNull.Value);
                    ins.Parameters.AddWithValue("$x", f.X);
                    ins.Parameters.AddWithValue("$y", f.Y);
                    ins.Parameters.AddWithValue("$w", f.W);
                    ins.Parameters.AddWithValue("$h", f.H);
                    ins.Parameters.AddWithValue("$ds", f.DetScore);
                    ins.Parameters.AddWithValue("$q", f.Quality);
                    ins.Parameters.AddWithValue("$sim", f.Similarity);
                    ins.Parameters.AddWithValue("$st", (int)f.Status);
                    ins.Parameters.AddWithValue("$emb", Db.ToBlob(f.EmbeddingRef ?? Array.Empty<float>()));
                    ins.Parameters.AddWithValue("$t", Now());
                    ids.Add(Convert.ToInt64(ins.ExecuteScalar()));
                }
            }
            return ids;
        });
    }

    // --------------------------------------------------------------- people

    public long CreatePerson(string name, bool isNamed, float[] centroidSum, int n, long? coverFaceId)
    {
        return _db.WriteScalar(@"
            INSERT INTO people(name, is_named, cover_face_id, face_count, centroid, centroid_n, created_at)
            VALUES($n,$in,$cf,$fc,$c,$cn,$t);
            SELECT last_insert_rowid();",
            ("$n", name), ("$in", isNamed ? 1 : 0), ("$cf", coverFaceId),
            ("$fc", n), ("$c", Db.ToBlob(centroidSum)), ("$cn", n), ("$t", Now()));
    }

    public void UpdatePersonCentroid(long id, float[] sum, int n, int faceCount, long? coverFaceId)
        => _db.Write(@"UPDATE people SET centroid=$c, centroid_n=$cn, face_count=$fc,
                       cover_face_id=COALESCE(cover_face_id,$cf) WHERE id=$i",
                     ("$c", Db.ToBlob(sum)), ("$cn", n), ("$fc", faceCount),
                     ("$cf", coverFaceId), ("$i", id));

    public List<(long Id, float[] Sum, int N)> LoadCentroids()
    {
        var list = new List<(long, float[], int)>();
        using var c = _db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, centroid, centroid_n FROM people WHERE centroid IS NOT NULL AND centroid_n > 0";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt64(0), Db.FromBlob((byte[])r["centroid"]), r.GetInt32(2)));
        return list;
    }

    public List<PersonRow> GetPeople(string? search = null, bool namedFirst = true)
    {
        var list = new List<PersonRow>();
        using var c = _db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            SELECT p.id, p.name, p.is_named, p.cover_face_id,
                   (SELECT COUNT(*) FROM faces f WHERE f.person_id = p.id AND f.status <> 3),
                   p.created_at
            FROM people p
            WHERE ($s IS NULL OR p.name LIKE '%' || $s || '%')
            ORDER BY " + (namedFirst ? "p.is_named DESC, " : "") + @"
                     (SELECT COUNT(*) FROM faces f WHERE f.person_id = p.id AND f.status <> 3) DESC";
        cmd.Parameters.AddWithValue("$s", (object?)search ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new PersonRow
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                IsNamed = r.GetInt32(2) == 1,
                CoverFaceId = r.IsDBNull(3) ? null : r.GetInt64(3),
                FaceCount = r.GetInt32(4),
                CreatedAt = FromUnix(r.GetInt64(5))
            });
        return list;
    }

    public void RenamePerson(long id, string name)
        => _db.Write("UPDATE people SET name=$n, is_named=1 WHERE id=$i", ("$n", name.Trim()), ("$i", id));

    public void SetCoverFace(long personId, long faceId)
        => _db.Write("UPDATE people SET cover_face_id=$f WHERE id=$i", ("$f", faceId), ("$i", personId));

    /// <summary>Move every face from <paramref name="sourceId"/> onto <paramref name="targetId"/>.</summary>
    public void MergePeople(long targetId, long sourceId)
    {
        if (targetId == sourceId) return;
        _db.InWriteTransaction((c, tx) =>
        {
            Run(c, tx, "UPDATE faces SET person_id=$t WHERE person_id=$s", ("$t", targetId), ("$s", sourceId));

            // Recombine the centroid sums so future matching stays correct.
            byte[]? a = null, b = null; int na = 0, nb = 0;
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT id, centroid, centroid_n FROM people WHERE id IN ($t,$s)";
                q.Parameters.AddWithValue("$t", targetId);
                q.Parameters.AddWithValue("$s", sourceId);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    var blob = r.IsDBNull(1) ? null : (byte[])r["centroid"];
                    if (r.GetInt64(0) == targetId) { a = blob; na = r.GetInt32(2); }
                    else { b = blob; nb = r.GetInt32(2); }
                }
            }

            if (a is not null && b is not null)
            {
                var va = Db.FromBlob(a); var vb = Db.FromBlob(b);
                for (int i = 0; i < va.Length && i < vb.Length; i++) va[i] += vb[i];
                Run(c, tx, "UPDATE people SET centroid=$c, centroid_n=$n WHERE id=$i",
                    ("$c", Db.ToBlob(va)), ("$n", na + nb), ("$i", targetId));
            }

            Run(c, tx, "DELETE FROM people WHERE id=$s", ("$s", sourceId));
            Run(c, tx, @"UPDATE people SET face_count =
                         (SELECT COUNT(*) FROM faces f WHERE f.person_id = people.id) WHERE id=$t",
                ("$t", targetId));
        });
    }

    /// <summary>Split the given faces out into a brand-new person.</summary>
    public long SplitFacesToNewPerson(IEnumerable<long> faceIds, string name)
    {
        var ids = faceIds.ToList();
        if (ids.Count == 0) return 0;
        var newId = CreatePerson(name, isNamed: true, new float[512], 0, ids[0]);
        _db.InWriteTransaction((c, tx) =>
        {
            foreach (var fid in ids)
                Run(c, tx, "UPDATE faces SET person_id=$p, status=1 WHERE id=$f", ("$p", newId), ("$f", fid));
        });
        RecomputeCentroid(newId);
        return newId;
    }

    public void RecomputeCentroid(long personId)
    {
        var sum = new float[512];
        int n = 0;
        using (var c = _db.OpenRead())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT embedding FROM faces WHERE person_id=$p AND status <> 3";
            cmd.Parameters.AddWithValue("$p", personId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var v = Db.FromBlob((byte[])r["embedding"]);
                if (v.Length != sum.Length) continue;
                for (int i = 0; i < v.Length; i++) sum[i] += v[i];
                n++;
            }
        }
        _db.Write("UPDATE people SET centroid=$c, centroid_n=$n, face_count=$n WHERE id=$i",
                  ("$c", Db.ToBlob(sum)), ("$n", n), ("$i", personId));
    }

    public void DeletePerson(long id)
    {
        _db.InWriteTransaction((c, tx) =>
        {
            Run(c, tx, "UPDATE faces SET person_id=NULL, status=3 WHERE person_id=$i", ("$i", id));
            Run(c, tx, "DELETE FROM people WHERE id=$i", ("$i", id));
        });
    }

    // ----------------------------------------------------------- face lists

    public List<FaceRow> GetPersonFaces(long personId, int limit = 500, int offset = 0)
        => QueryFaces(@"SELECT f.id, f.photo_id, f.person_id, f.x, f.y, f.w, f.h, f.det_score,
                               f.quality, f.similarity, f.status, p.path, NULL
                        FROM faces f JOIN photos p ON p.id = f.photo_id
                        WHERE f.person_id = $p AND f.status <> 3
                        ORDER BY f.status DESC, f.similarity DESC, f.quality DESC
                        LIMIT $l OFFSET $o",
                     ("$p", personId), ("$l", limit), ("$o", offset));

    public List<FaceRow> GetReviewQueue(int limit = 200)
        => QueryFaces(@"SELECT f.id, f.photo_id, f.person_id, f.x, f.y, f.w, f.h, f.det_score,
                               f.quality, f.similarity, f.status, p.path, pe.name
                        FROM faces f
                        JOIN photos p  ON p.id  = f.photo_id
                        LEFT JOIN people pe ON pe.id = f.person_id
                        WHERE f.status = 2
                        ORDER BY f.similarity DESC
                        LIMIT $l", ("$l", limit));

    private List<FaceRow> QueryFaces(string sql, params (string, object?)[] p)
    {
        var list = new List<FaceRow>();
        using var c = _db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new FaceRow
            {
                Id = r.GetInt64(0),
                PhotoId = r.GetInt64(1),
                PersonId = r.IsDBNull(2) ? null : r.GetInt64(2),
                X = r.GetFloat(3), Y = r.GetFloat(4), W = r.GetFloat(5), H = r.GetFloat(6),
                DetScore = r.GetFloat(7),
                Quality = r.GetFloat(8),
                Similarity = r.GetFloat(9),
                Status = (FaceStatus)r.GetInt32(10),
                PhotoPath = r.GetString(11),
                PersonName = r.IsDBNull(12) ? null : r.GetString(12)
            });
        return list;
    }

    public void SetFaceStatus(long faceId, FaceStatus status, long? personId)
        => _db.Write("UPDATE faces SET status=$s, person_id=$p WHERE id=$i",
                     ("$s", (int)status), ("$p", personId), ("$i", faceId));

    public void ConfirmFace(long faceId) => _db.Write("UPDATE faces SET status=1 WHERE id=$i", ("$i", faceId));

    public void RejectFace(long faceId)
        => _db.Write("UPDATE faces SET status=3, person_id=NULL WHERE id=$i", ("$i", faceId));

    // ------------------------------------------------------- photo browsing

    public List<PhotoRow> GetPhotos(int limit, int offset, int? faceCountFilter = null, string? search = null)
    {
        var list = new List<PhotoRow>();
        using var c = _db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT id, library_id, path, file_size, width, height, face_count, state
                            FROM photos
                            WHERE state = 1
                              AND ($fc IS NULL OR face_count = $fc)
                              AND ($s  IS NULL OR path LIKE '%' || $s || '%')
                            ORDER BY id LIMIT $l OFFSET $o";
        cmd.Parameters.AddWithValue("$fc", (object?)faceCountFilter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", (object?)search ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$l", limit);
        cmd.Parameters.AddWithValue("$o", offset);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new PhotoRow
            {
                Id = r.GetInt64(0), LibraryId = r.GetInt64(1), Path = r.GetString(2),
                FileSize = r.GetInt64(3), Width = r.GetInt32(4), Height = r.GetInt32(5),
                FaceCount = r.GetInt32(6), State = (PhotoState)r.GetInt32(7)
            });
        return list;
    }

    /// <summary>Distinct original photo paths that contain this person. Used by folder export.</summary>
    public List<string> GetPersonPhotoPaths(long personId)
    {
        var list = new List<string>();
        using var c = _db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT DISTINCT p.path FROM photos p
                            JOIN faces f ON f.photo_id = p.id
                            WHERE f.person_id = $i AND f.status <> 3 ORDER BY p.path";
        cmd.Parameters.AddWithValue("$i", personId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public List<string> GetNoFacePhotoPaths()
    {
        var list = new List<string>();
        using var c = _db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path FROM photos WHERE state=1 AND face_count=0 ORDER BY path";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    // ---------------------------------------------------------------- stats

    public LibraryStats GetStats()
    {
        var s = new LibraryStats();
        using var c = _db.OpenRead();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
            SELECT (SELECT COUNT(*) FROM photos),
                   (SELECT COUNT(*) FROM photos WHERE state=1),
                   (SELECT COUNT(*) FROM photos WHERE state=0),
                   (SELECT COUNT(*) FROM photos WHERE state=2),
                   (SELECT COUNT(*) FROM faces),
                   (SELECT COUNT(*) FROM people),
                   (SELECT COUNT(*) FROM people WHERE is_named=1),
                   (SELECT COUNT(*) FROM faces WHERE status=2),
                   (SELECT COUNT(*) FROM photos WHERE state=1 AND face_count=0)";
        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            s.Photos = r.GetInt64(0); s.Indexed = r.GetInt64(1); s.Pending = r.GetInt64(2);
            s.Failed = r.GetInt64(3); s.Faces = r.GetInt64(4); s.People = r.GetInt64(5);
            s.NamedPeople = r.GetInt64(6); s.NeedsReview = r.GetInt64(7); s.NoFacePhotos = r.GetInt64(8);
        }
        return s;
    }

    private static void Run(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] p)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}

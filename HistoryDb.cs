using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Asker;

// Chat history in %AppData%\Asker\history.db. Rows are written on create, when the answer finishes, and on pin/unpin.
// source, image, prompt and result are encrypted with Windows DPAPI (only this Windows user can read them);
// time, model and pinned stay plain because they are not sensitive and are used for sorting and cleanup.
public static class HistoryDb
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Asker", "history.db");
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Asker.History.v1");

    static byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    static byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
    static byte[] Encrypt(string text) => Protect(Encoding.UTF8.GetBytes(text));
    static string DecryptText(object blob) => Encoding.UTF8.GetString(Unprotect((byte[])blob));

    static SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var db = new SqliteConnection($"Data Source={FilePath}");
        db.Open();
        return db;
    }

    static SqliteCommand Cmd(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static void Init()
    {
        using var db = Open();
        Cmd(db, """
            CREATE TABLE IF NOT EXISTS history (
                id     INTEGER PRIMARY KEY AUTOINCREMENT,
                time   TEXT    NOT NULL,
                source BLOB    NOT NULL,
                image  BLOB,
                prompt BLOB    NOT NULL,
                model  TEXT    NOT NULL,
                result BLOB    NOT NULL,
                pinned INTEGER NOT NULL DEFAULT 0
            )
            """).ExecuteNonQuery();
        // name (custom title from renaming a pinned item, encrypted) was added after the first release.
        if ((long)Cmd(db, "SELECT COUNT(*) FROM pragma_table_info('history') WHERE name = 'name'").ExecuteScalar()! == 0)
            Cmd(db, "ALTER TABLE history ADD COLUMN name BLOB").ExecuteNonQuery();

        // One-time migration: rows saved before encryption hold plain text in source (encrypted rows hold a blob).
        using var tx = db.BeginTransaction();
        var plain = new List<(long Id, string Source, byte[]? Image, string Prompt, string Result)>();
        using (var r = Cmd(db, "SELECT id, source, image, prompt, result FROM history WHERE typeof(source) = 'text'").ExecuteReader())
            while (r.Read())
                plain.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : (byte[])r[2], r.GetString(3), r.GetString(4)));
        foreach (var p in plain)
            Cmd(db, "UPDATE history SET source = $s, image = $i, prompt = $p, result = $r WHERE id = $id",
                ("$s", Encrypt(p.Source)), ("$i", p.Image == null ? null : Protect(p.Image)), ("$p", Encrypt(p.Prompt)),
                ("$r", Encrypt(p.Result)), ("$id", p.Id)).ExecuteNonQuery();
        tx.Commit();
    }

    // Every row, oldest first.
    public static List<ResultItem> Load() => LoadPage(long.MaxValue, -1);

    // Up to `count` rows (-1 = all) with id < beforeId and id >= minId, the newest of them, returned oldest first.
    public static List<ResultItem> LoadPage(long beforeId, int count, long minId = 0) =>
        LoadWhere("id < $before AND id >= $min ORDER BY id DESC LIMIT $n", ("$before", beforeId), ("$min", minId), ("$n", count));

    public static List<ResultItem> LoadPinned() => LoadWhere("pinned = 1 ORDER BY id DESC");

    // Rows without image bytes (see LoadImage), oldest first. Rows that can't be decrypted
    // (e.g. after a Windows profile reset) are skipped.
    static List<ResultItem> LoadWhere(string where, params (string, object?)[] args)
    {
        // Read raw rows first, then decrypt in parallel: DPAPI costs ~0.65 ms per call and there are 3 per row
        // (measured 3.8 s for 2000 rows sequentially).
        var rows = new List<(long Id, string Time, bool HasImage, object Source, object Prompt, object Result, bool Pinned, string Model, object Name)>();
        using (var db = Open())
        using (var r = Cmd(db, "SELECT id, time, image IS NOT NULL, source, prompt, result, pinned, model, name FROM history WHERE " + where, args).ExecuteReader())
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetString(1), r.GetBoolean(2), r[3], r[4], r[5], r.GetInt64(6) == 1, r.GetString(7), r[8]));

        var items = new ResultItem?[rows.Count];
        System.Threading.Tasks.Parallel.For(0, rows.Count, i =>
        {
            var row = rows[i];
            try
            {
                items[i] = new ResultItem
                {
                    Id = row.Id,
                    Time = DateTime.Parse(row.Time),
                    HasImage = row.HasImage,
                    Source = DecryptText(row.Source),
                    Prompt = DecryptText(row.Prompt),
                    Result = DecryptText(row.Result),
                    Pinned = row.Pinned,
                    Model = row.Model,
                    Name = row.Name is DBNull ? "" : DecryptText(row.Name),
                };
            }
            catch (Exception e) when (e is CryptographicException or InvalidCastException or FormatException) { } // skipped
        });
        return items.Where(i => i != null).OrderBy(i => i!.Id).ToList()!;
    }

    public static byte[]? LoadImage(long id)
    {
        using var db = Open();
        return Cmd(db, "SELECT image FROM history WHERE id = $id", ("$id", id)).ExecuteScalar() is byte[] enc ? Unprotect(enc) : null;
    }

    public static void Insert(ResultItem item, string model)
    {
        using var db = Open();
        item.Id = (long)Cmd(db, """
            INSERT INTO history (time, source, image, prompt, model, result, pinned) VALUES ($t, $s, $i, $p, $m, $r, $pin);
            SELECT last_insert_rowid();
            """, ("$t", item.Time.ToString("o")), ("$s", Encrypt(item.Source)), ("$i", item.ImagePng == null ? null : Protect(item.ImagePng)),
                 ("$p", Encrypt(item.Prompt)), ("$m", model), ("$r", Encrypt(item.Result)), ("$pin", item.Pinned ? 1 : 0)).ExecuteScalar()!;
    }

    public static void Update(ResultItem item)
    {
        using var db = Open();
        Cmd(db, "UPDATE history SET result = $r, pinned = $pin, name = $n WHERE id = $id",
            ("$r", Encrypt(item.Result)), ("$pin", item.Pinned ? 1 : 0), ("$n", item.Name == "" ? null : Encrypt(item.Name)),
            ("$id", item.Id)).ExecuteNonQuery();
    }

    public static int Count()
    {
        Init(); // the file may not exist yet
        using var db = Open();
        return (int)(long)Cmd(db, "SELECT COUNT(*) FROM history").ExecuteScalar()!;
    }

    // SQLite's backup API: a consistent copy even of a file in use, without touching journal files by hand.
    static void CopyDatabase(string from, string to)
    {
        using var src = new SqliteConnection($"Data Source={from};Mode=ReadOnly;Pooling=False");
        using var dst = new SqliteConnection($"Data Source={to};Pooling=False");
        src.Open();
        dst.Open();
        src.BackupDatabase(dst);
    }

    // Saves the whole history to `path`. Rows stay DPAPI-encrypted, so only this Windows account can import it.
    public static int Export(string path)
    {
        int count = Count();
        if (File.Exists(path)) File.Delete(path); // the save dialog already asked before overwriting
        CopyDatabase(FilePath, path);
        return count;
    }

    // Checks that `path` is an Asker history this Windows account can decrypt; returns its row count.
    // Throws InvalidDataException with a message for the user, or SqliteException for files that aren't SQLite.
    public static int CheckImport(string path)
    {
        if (Path.GetFullPath(path).Equals(Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("That is the history Asker is using now.");
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        db.Open();
        var columns = new HashSet<string>();
        using (var r = Cmd(db, "SELECT name FROM pragma_table_info('history')").ExecuteReader())
            while (r.Read()) columns.Add(r.GetString(0));
        if (!new[] { "id", "time", "source", "image", "prompt", "model", "result", "pinned" }.All(columns.Contains))
            throw new InvalidDataException("This file is not an Asker history.");
        int count = (int)(long)Cmd(db, "SELECT COUNT(*) FROM history").ExecuteScalar()!;
        if (count == 0) return 0;
        // Encrypted rows only open on the Windows account that wrote them; plain text rows are from before encryption.
        using (var r = Cmd(db, "SELECT source FROM history LIMIT 20").ExecuteReader())
            while (r.Read())
                try
                {
                    if (r[0] is byte[] blob) Unprotect(blob);
                    return count;
                }
                catch (CryptographicException) { }
        throw new InvalidDataException("This history was exported from another Windows account or PC, so it can't be decrypted here.");
    }

    // Replaces the history with `path` (call CheckImport first). The current history is kept next to it; returns that backup's path.
    public static string Import(string path)
    {
        Init();
        SqliteConnection.ClearAllPools(); // release pooled handles on the file being replaced
        var backup = Path.Combine(Path.GetDirectoryName(FilePath)!, $"history-before-import-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        CopyDatabase(FilePath, backup);
        CopyDatabase(path, FilePath);
        SqliteConnection.ClearAllPools();
        Init(); // adds newer columns and encrypts rows from older versions
        return backup;
    }

    public static void Delete(long id)
    {
        using var db = Open();
        Cmd(db, "DELETE FROM history WHERE id = $id", ("$id", id)).ExecuteNonQuery();
    }

    // Deletes unpinned rows older than `days` (0 = keep forever). time is ISO 8601 with offset, which julianday() understands.
    public static void Cleanup(int days)
    {
        if (days <= 0) return;
        using var db = Open();
        Cmd(db, "DELETE FROM history WHERE pinned = 0 AND julianday(time) < julianday('now', $age)", ("$age", $"-{days} days")).ExecuteNonQuery();
    }
}

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

    // Every row, oldest first, without image bytes (see LoadImage). Rows that can't be decrypted
    // (e.g. after a Windows profile reset) are skipped.
    public static List<ResultItem> Load()
    {
        // Read raw rows first, then decrypt in parallel: DPAPI costs ~0.65 ms per call and there are 3 per row
        // (measured 3.8 s for 2000 rows sequentially).
        var rows = new List<(long Id, string Time, bool HasImage, object Source, object Prompt, object Result, bool Pinned)>();
        using (var db = Open())
        using (var r = Cmd(db, "SELECT id, time, image IS NOT NULL, source, prompt, result, pinned FROM history ORDER BY id").ExecuteReader())
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetString(1), r.GetBoolean(2), r[3], r[4], r[5], r.GetInt64(6) == 1));

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
                };
            }
            catch (Exception e) when (e is CryptographicException or InvalidCastException or FormatException) { } // skipped
        });
        return items.Where(i => i != null).ToList()!;
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
        Cmd(db, "UPDATE history SET result = $r, pinned = $pin WHERE id = $id",
            ("$r", Encrypt(item.Result)), ("$pin", item.Pinned ? 1 : 0), ("$id", item.Id)).ExecuteNonQuery();
    }

    // Deletes unpinned rows older than `days` (0 = keep forever). time is ISO 8601 with offset, which julianday() understands.
    public static void Cleanup(int days)
    {
        if (days <= 0) return;
        using var db = Open();
        Cmd(db, "DELETE FROM history WHERE pinned = 0 AND julianday(time) < julianday('now', $age)", ("$age", $"-{days} days")).ExecuteNonQuery();
    }
}

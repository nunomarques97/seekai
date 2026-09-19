using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace SeekAI.Core;

public record SearchResult(string Path, string Filename, string Snippet, string Match, double Score);
public record ScanReport(int Updated, int Unchanged, int Embedded, int Removed, int Skipped, string Message);
public sealed class SearchIndex
{
    private readonly string connectionString;
    private readonly IEmbeddings ai;
    private readonly SemaphoreSlim scanLock = new(1);
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".ts", ".tsx", ".js", ".jsx", ".py", ".cs", ".rs", ".json", ".yaml", ".yml", ".html", ".css", ".sql", ".ps1", ".sh", ".toml", ".xml", ".csv", ".log", ".pdf" };
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    { "node_modules", ".git", "build", "dist", "target", "bin", "obj", ".venv", "venv", "__pycache__", ".next", ".cache" };
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int MaxCharacters = 250_000;

    public SearchIndex(string database, IEmbeddings ai)
    {
        this.ai = ai;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(database))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = database, ForeignKeys = true }.ToString();
        using var db = Open();
        Execute(db, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS files(path TEXT PRIMARY KEY COLLATE NOCASE, name TEXT NOT NULL, modified INTEGER NOT NULL, size INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS chunks(id INTEGER PRIMARY KEY, path TEXT NOT NULL REFERENCES files(path) ON DELETE CASCADE, text TEXT NOT NULL, vector BLOB);
            CREATE INDEX IF NOT EXISTS chunks_path ON chunks(path);
            CREATE VIRTUAL TABLE IF NOT EXISTS fts USING fts5(text, content='chunks', content_rowid='id', tokenize='unicode61');
            CREATE TRIGGER IF NOT EXISTS chunks_ai AFTER INSERT ON chunks BEGIN INSERT INTO fts(rowid,text) VALUES(new.id,new.text); END;
            CREATE TRIGGER IF NOT EXISTS chunks_ad AFTER DELETE ON chunks BEGIN INSERT INTO fts(fts,rowid,text) VALUES('delete',old.id,old.text); END;
            """);
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    private static SqliteCommand Command(SqliteConnection db, string sql, params object[] args)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
        return cmd;
    }
    private static int Execute(SqliteConnection db, string sql, params object[] args)
    { using var cmd = Command(db, sql, args); return cmd.ExecuteNonQuery(); }
    public (int Files, int Chunks, int Vectors) Counts()
    {
        using var db = Open(); using var cmd = Command(db, "SELECT (SELECT count(*) FROM files), count(*), count(vector) FROM chunks");
        using var r = cmd.ExecuteReader(); r.Read(); return (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
    }
    public static bool UnderRoot(string path, string root) => path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public async Task<ScanReport> ScanAsync(IEnumerable<string> folders, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await scanLock.WaitAsync(ct);
        try { return await ScanCore(folders, progress, ct); }
        finally { scanLock.Release(); }
    }
    private async Task<ScanReport> ScanCore(IEnumerable<string> folders, IProgress<string>? progress, CancellationToken ct)
    {
        var roots = folders.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var protectedPaths = new List<string>();
        int updated = 0, unchanged = 0, embedded = 0, removed = 0, skipped = 0;
        bool semantic = (await ai.StatusAsync(ct)).ModelAvailable;
        using var db = Open();
        foreach (var root in roots)
        {
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var dir))
            {
                ct.ThrowIfCancellationRequested();
                string[] files, dirs;
                try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { skipped++; protectedPaths.Add(dir); continue; }
                foreach (var child in dirs)
                {
                    try { if (!Ignored.Contains(Path.GetFileName(child)) && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; protectedPaths.Add(child); }
                }
                foreach (var path in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Extensions.Contains(Path.GetExtension(path)) || !seen.Add(path)) continue;
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Length > MaxBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                        { skipped++; Execute(db, "DELETE FROM files WHERE path=$0", path); continue; }
                        long modified = info.LastWriteTimeUtc.Ticks, size = info.Length;
                        using var check = Command(db, "SELECT modified,size FROM files WHERE path=$0", path);
                        bool same;
                        using (var r = check.ExecuteReader()) same = r.Read() && r.GetInt64(0) == modified && r.GetInt64(1) == size;
                        if (same) unchanged++;
                        else
                        {
                            progress?.Report("Reading " + Path.GetFileName(path));
                            string text;
                            if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                            {
                                using var pdf = PdfDocument.Open(path);
                                var content = new StringBuilder();
                                foreach (var page in pdf.GetPages()) { content.AppendLine(page.Text); if (content.Length > MaxCharacters) break; }
                                text = content.ToString();
                            }
                            else text = await File.ReadAllTextAsync(path, ct);
                            if (text.Contains('\0')) { skipped++; Execute(db, "DELETE FROM files WHERE path=$0", path); continue; }
                            if (text.Length > MaxCharacters) { skipped++; Execute(db, "DELETE FROM files WHERE path=$0", path); continue; }
                            info.Refresh();
                            if (info.LastWriteTimeUtc.Ticks != modified || info.Length != size) { skipped++; continue; }
                            using var tx = db.BeginTransaction();
                            Execute(db, "DELETE FROM files WHERE path=$0", path);
                            Execute(db, "INSERT INTO files VALUES($0,$1,$2,$3)", path, Path.GetFileName(path), modified, size);
                            foreach (var chunk in Split(text)) Execute(db, "INSERT INTO chunks(path,text) VALUES($0,$1)", path, chunk);
                            tx.Commit(); updated++;
                        }
                        if (!semantic) continue;
                        var missing = new List<(long Id, string Text)>();
                        using (var cmd = Command(db, "SELECT id,text FROM chunks WHERE path=$0 AND vector IS NULL", path))
                        using (var r = cmd.ExecuteReader()) while (r.Read()) missing.Add((r.GetInt64(0), r.GetString(1)));
                        foreach (var batch in missing.Chunk(16))
                        {
                            progress?.Report("Embedding " + Path.GetFileName(path));
                            try
                            {
                                var vectors = await ai.EmbedAsync(batch.Select(x => x.Text).ToArray(), false, ct);
                                using var tx = db.BeginTransaction();
                                for (int i = 0; i < batch.Length; i++)
                                    Execute(db, "UPDATE chunks SET vector=$0 WHERE id=$1", Bytes(vectors[i]), batch[i].Id);
                                tx.Commit(); embedded += batch.Length;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                            { semantic = false; break; }
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    { skipped++; progress?.Report("Skipped " + Path.GetFileName(path) + ": " + ex.Message); }
                }
            }
        }
        var stale = new List<string>();
        using (var cmd = Command(db, "SELECT path FROM files"))
        using (var r = cmd.ExecuteReader()) while (r.Read())
        {
            var path = r.GetString(0);
            if (!roots.Any(root => UnderRoot(path, root)) || (!seen.Contains(path) && !protectedPaths.Any(root => UnderRoot(path, root)))) stale.Add(path);
        }
        using (var tx = db.BeginTransaction())
        { foreach (var path in stale) removed += Execute(db, "DELETE FROM files WHERE path=$0", path); tx.Commit(); }
        return new(updated, unchanged, embedded, removed, skipped,
            $"Ready · {updated} updated · {unchanged} unchanged · {embedded} embedded · {removed} removed · {skipped} skipped" + (semantic ? "" : " · semantic offline"));
    }
    public static IEnumerable<string> Split(string text)
    {
        for (int start = 0; start < text.Length; start += 1400)
        { var chunk = text.Substring(start, Math.Min(1600, text.Length - start)).Trim(); if (chunk.Length > 0) yield return chunk; }
    }
    private static byte[] Bytes(float[] vector) { var b = new byte[vector.Length * 4]; Buffer.BlockCopy(vector, 0, b, 0, b.Length); return b; }
    private static float[] Floats(byte[] bytes) { var v = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length); return v; }
    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, aa = 0, bb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        return aa * bb > 0 ? dot / Math.Sqrt(aa * bb) : 0;
    }
    public async Task<List<SearchResult>> SearchAsync(string query, bool semantic = true, CancellationToken ct = default)
    {
        query = query.Trim(); if (query.Length == 0) return [];
        if (query.Length > 1000) query = query[..1000];
        var hits = new Dictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);
        using var db = Open();
        void Add(string path, string name, string text, string source, double score)
        {
            var hit = new SearchResult(path, name, Snippet(text, query), source, score);
            if (!hits.TryGetValue(path, out var old)) hits[path] = hit;
            else if (score > old.Score) hits[path] = hit with { Score = score + .04 };
            else hits[path] = old with { Score = old.Score + .04 };
        }
        using (var cmd = Command(db, "SELECT path,name,coalesce((SELECT text FROM chunks WHERE chunks.path=files.path LIMIT 1),'') FROM files WHERE instr(lower(path),lower($0))>0 LIMIT 100", query))
        using (var r = cmd.ExecuteReader()) while (r.Read()) Add(r.GetString(0), r.GetString(1), r.GetString(2), "Filename", r.GetString(1).Contains(query, StringComparison.OrdinalIgnoreCase) ? 1.5 : 1.2);
        var tokens = Regex.Matches(query, @"[\p{L}\p{N}_]+").Select(m => m.Value).Distinct().Take(30).ToArray();
        if (tokens.Length > 0)
        {
            var ftsQuery = string.Join(" OR ", tokens.Select(t => "\"" + t + "\""));
            using var cmd = Command(db, "SELECT c.path,c.text FROM fts JOIN chunks c ON c.id=fts.rowid WHERE fts MATCH $0 ORDER BY bm25(fts) LIMIT 100", ftsQuery);
            using var r = cmd.ExecuteReader();
            var textSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (r.Read())
            {
                var path = r.GetString(0); if (!textSeen.Add(path)) continue;
                var text = r.GetString(1);
                double coverage = tokens.Count(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)) / (double)tokens.Length;
                Add(path, Path.GetFileName(path), text, "Text", text.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1.3 : .25 + .55 * coverage);
            }
        }
        if (semantic)
        {
            try
            {
                var vector = (await ai.EmbedAsync([query], true, ct))[0];
                var best = new Dictionary<string, (string Text, double Score)>(StringComparer.OrdinalIgnoreCase);
                using var cmd = Command(db, "SELECT path,text,vector FROM chunks WHERE vector IS NOT NULL");
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var score = Cosine(vector, Floats((byte[])r[2])); var path = r.GetString(0);
                    if (score >= .35 && (!best.TryGetValue(path, out var old) || score > old.Score)) best[path] = (r.GetString(1), score);
                }
                foreach (var (path, hit) in best.OrderByDescending(x => x.Value.Score).Take(50))
                    Add(path, Path.GetFileName(path), hit.Text, "Semantic", hit.Score * 1.35);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException) { }
        }
        return hits.Values.OrderByDescending(x => x.Score).ThenBy(x => x.Path).Take(30).ToList();
    }
    private static string Snippet(string text, string query)
    {
        text = Regex.Replace(text, @"\s+", " ");
        int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at < 0) at = Regex.Matches(query, @"[\p{L}\p{N}_]+").Select(m => text.IndexOf(m.Value, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        int start = Math.Max(0, at - 55); return (start > 0 ? "…" : "") + text.Substring(start, Math.Min(220, text.Length - start)) + (text.Length - start > 220 ? "…" : "");
    }
}

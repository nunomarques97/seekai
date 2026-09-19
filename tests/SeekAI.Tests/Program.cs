using SeekAI.Core;
using System.Text;

var corpus = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "tests/corpus");
var temp = Path.Combine(Path.GetTempPath(), "seekai-tests-" + Guid.NewGuid().ToString("N"));
var root = Path.Combine(temp, "corpus"); Directory.CreateDirectory(root);
foreach (var source in Directory.GetFiles(corpus)) File.Copy(source, Path.Combine(root, Path.GetFileName(source)));
var fake = new CountingAi(); var db = Path.Combine(temp, "test.db");
var index = new SearchIndex(db, fake);
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
var first = await index.ScanAsync([root]);
Check(first.Updated == 4 && first.Embedded == 4, "initial corpus indexing");
var hits = await index.SearchAsync("forja", false);
Check(hits[0].Filename == "forja-release.txt" && hits[0].Match == "Filename", "filename ranking");
hits = await index.SearchAsync("context threshold", false);
Check(hits[0].Filename == "session-notes.md" && hits[0].Match == "Text", "exact content search");
Check((await index.SearchAsync("\" * OR (x):", false)) is not null, "FTS punctuation is safely escaped");
var calls = fake.Calls;
index = new SearchIndex(db, fake);
var second = await index.ScanAsync([root]);
Check(second.Updated == 0 && second.Unchanged == 4 && fake.Calls == calls, "restart keeps unchanged files and embeddings");
await File.AppendAllTextAsync(Path.Combine(root, "session-notes.md"), "\nuniquechangedmarker");
var third = await index.ScanAsync([root]);
Check(third.Updated == 1 && third.Embedded == 1 && fake.Calls == calls + 1, "changed file gets reindexed and re-embedded");
Check((await index.SearchAsync("uniquechangedmarker", false))[0].Filename == "session-notes.md", "new text is searchable");
fake.Available = false;
Check((await index.SearchAsync("forja", true))[0].Filename == "forja-release.txt", "search survives unavailable Ollama");
await File.WriteAllTextAsync(Path.Combine(root, "offline.txt"), "offlinependingcontent");
var offline = await index.ScanAsync([root]);
Check(offline.Updated == 1 && offline.Embedded == 0 && (await index.SearchAsync("offlinependingcontent", false)).Count == 1, "indexing works without Ollama");
fake.Available = true;
var backfill = await index.ScanAsync([root]);
Check(backfill.Updated == 0 && backfill.Embedded == 1, "missing vectors backfill when Ollama returns");
Directory.CreateDirectory(Path.Combine(root, "node_modules"));
await File.WriteAllTextAsync(Path.Combine(root, "node_modules", "ignored.txt"), "excludedcontent");
await File.WriteAllTextAsync(Path.Combine(root, "huge.txt"), new string('x', 250001));
await index.ScanAsync([root]);
Check((await index.SearchAsync("excludedcontent", false)).Count == 0 && (await index.SearchAsync("huge.txt", false)).Count == 0, "generated folders and huge text excluded");
File.Delete(Path.Combine(root, "dinner.txt"));
var deletion = await index.ScanAsync([root]);
Check(deletion.Removed == 1 && (await index.SearchAsync("parmesan", false)).Count == 0, "deleted file and FTS entries pruned");
await File.WriteAllTextAsync(Path.Combine(root, "long.md"), new string(' ', 1700) + "tailneedle");
await index.ScanAsync([root]);
Check((await index.SearchAsync("tailneedle", false)).Count == 1, "later document chunks searchable");
var pdfPath = Path.Combine(root, "sample.pdf");
CreatePdf(pdfPath);
var pdfScan = await index.ScanAsync([root]);
Check((await index.SearchAsync("pdfverificationneedle", false)).Any(h => h.Filename == "sample.pdf"), "PDF text extraction");
var other = Path.Combine(temp, "other"); Directory.CreateDirectory(other);
await index.ScanAsync([root, other]);
await index.ScanAsync([other]);
Check(index.Counts().Files == 0 && (await index.SearchAsync("forja", false)).Count == 0, "removed folder clears stored content and vectors");
var settings = new Settings { Folders = [root], AltShortcut = true }; settings.Save(temp);
Check(Settings.Load(temp).Folders.Single() == root && Settings.Load(temp).AltShortcut, "settings persist");

if (args.Contains("--live"))
{
    var live = new Ollama(); Check((await live.StatusAsync()).ModelAvailable, "real Ollama embedding model available");
    var liveIndex = new SearchIndex(Path.Combine(temp, "live.db"), live);
    var liveScan = await liveIndex.ScanAsync([corpus]);
    Check(liveScan.Embedded == 4, "real local embeddings generated");
    foreach (var query in new[] { "where did I write about creating a new session when tokens get too high", "document where I talked about replacing the main session after context gets too large" })
    {
        var results = await liveIndex.SearchAsync(query);
        Console.WriteLine(string.Join("\n", results.Select(h => $"  {h.Score:F3} {h.Match} {h.Filename}")));
        Check(results[0].Filename == "session-notes.md", "semantic paraphrase: " + query);
    }
    Check((await liveIndex.SearchAsync("how to prepare an Italian vegetable meal"))[0].Filename == "dinner.txt", "semantic cooking paraphrase");
    var rerun = await new SearchIndex(Path.Combine(temp, "live.db"), live).ScanAsync([corpus]);
    Check(rerun.Embedded == 0 && rerun.Unchanged == 4, "real embeddings reused on restart");
}
Console.WriteLine($"{passed} tests passed. Artifacts: {temp}");

static void CreatePdf(string path)
{
    var stream = "BT /F1 12 Tf 50 750 Td (pdfverificationneedle local document) Tj ET";
    var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream" };
    var b = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
    for (int i = 0; i < objects.Length; i++) { offsets.Add(b.Length); b.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
    int xref = b.Length; b.Append("xref\n0 6\n0000000000 65535 f \n");
    foreach (int offset in offsets) b.Append($"{offset:0000000000} 00000 n \n");
    b.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF"); File.WriteAllText(path, b.ToString(), Encoding.ASCII);
}
sealed class CountingAi : IEmbeddings
{
    public int Calls; public bool Available = true;
    public Task<AiStatus> StatusAsync(CancellationToken ct = default) => Task.FromResult(new AiStatus(Available, Available, "test"));
    public Task<float[][]> EmbedAsync(string[] texts, bool query, CancellationToken ct = default)
    {
        if (!Available) throw new HttpRequestException("Offline");
        Calls++; return Task.FromResult(texts.Select(_ => new float[] { 1, 0, 0 }).ToArray());
    }
}

using System.Net.Http.Json;
using System.Text.Json;

namespace SeekAI.Core;

public record AiStatus(bool Running, bool ModelAvailable, string Message);
public interface IEmbeddings
{
    Task<AiStatus> StatusAsync(CancellationToken ct = default);
    Task<float[][]> EmbedAsync(string[] texts, bool query, CancellationToken ct = default);
}

public sealed class Ollama : IEmbeddings
{
    public const string Model = "nomic-embed-text";
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false })
    { BaseAddress = new Uri("http://127.0.0.1:11434"), Timeout = TimeSpan.FromMinutes(3) };

    public async Task<AiStatus> StatusAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var json = await http.GetFromJsonAsync<JsonElement>("/api/tags", timeout.Token);
            bool found = json.GetProperty("models").EnumerateArray().Any(x =>
                x.GetProperty("name").GetString() is string n && (n == Model || n == Model + ":latest"));
            return new(true, found, found ? "Ollama detected · semantic model ready" : "Ollama detected · model needs downloading");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        { return new(false, false, "Ollama unavailable · filename and text search ready"); }
    }

    public async Task<float[][]> EmbedAsync(string[] texts, bool query, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("/api/embed", new
        {
            model = Model,
            input = texts.Select(t => (query ? "search_query: " : "search_document: ") + t).ToArray(),
            truncate = true, keep_alive = "10m"
        }, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var vectors = json.GetProperty("embeddings").Deserialize<float[][]>()!;
        if (vectors.Length != texts.Length || vectors.Any(v => v.Length == 0 || v.Any(x => !float.IsFinite(x))))
            throw new InvalidDataException("Ollama returned invalid embeddings.");
        return vectors;
    }

    public async Task PullAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pull")
        { Content = JsonContent.Create(new { model = Model, stream = true }) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        while (await reader.ReadLineAsync(timeout.Token) is string line)
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("error", out var error)) throw new IOException(error.GetString());
        }
    }
}

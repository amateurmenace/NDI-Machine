using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace NDIMachine.Desktop;

public sealed class EngineClient : IDisposable
{
    private readonly HttpClient http;
    public EngineClient(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = new Uri("http://127.0.0.1:9860/api/v1/"), Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task<JsonObject> Request(HttpMethod method, string path, JsonObject? body = null,
        string? id = null, int milliseconds = 3000, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(milliseconds);
        using var request = new HttpRequestMessage(method, path);
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (id != null) request.Headers.Add("Idempotency-Key", id);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var buffer = new byte[262145]; var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), deadline.Token);
            if (read == 0) break;
            total += read;
        }
        if (total == buffer.Length) throw new InvalidDataException("Engine response exceeded 256 KiB");
        var value = total == 0 ? new JsonObject() : JsonNode.Parse(buffer.AsSpan(0, total))?.AsObject() ?? new JsonObject();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(value["error"]?.GetValue<string>() ?? $"Engine returned HTTP {(int)response.StatusCode}");
        return value;
    }
    public async Task<JsonObject> State(CancellationToken token = default)
    {
        var nonce = Guid.NewGuid().ToString();
        var value = await Request(HttpMethod.Get, "state?nonce=" + nonce, token: token);
        if (value["schemaVersion"]?.GetValue<int>() != 1 || value["nonce"]?.GetValue<string>() != nonce ||
            string.IsNullOrWhiteSpace(value["instanceId"]?.GetValue<string>()) || value["outputs"] is not JsonArray)
            throw new InvalidDataException("Engine state did not pass freshness and schema checks");
        return value;
    }
    public async Task<JsonObject> WaitOperation(string id, string host, CancellationToken token)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var value = await Request(HttpMethod.Get, "operations/" + id, token: token);
            if (value["instanceId"]?.GetValue<string>() != host) throw new InvalidOperationException("Control session changed; command outcome unknown");
            var status = value["state"]?.GetValue<string>();
            if (status == "committed") return value;
            if (status is "failed" or "unknown") throw new InvalidOperationException(value["error"]?.GetValue<string>() ?? status);
            await Task.Delay(500, token);
        }
        throw new TimeoutException("Command confirmation timed out. Check actual state; the command will not be replayed.");
    }
    public void Dispose() => http.Dispose();
}

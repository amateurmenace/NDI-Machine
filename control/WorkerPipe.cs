using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NDIMachine.Control;

public interface IWorkerPipe
{
    Task<JsonObject> Call(string name, JsonObject request, int timeoutMs = 700, CancellationToken cancellation = default);
    Task<JsonObject> State(int slot, CancellationToken cancellation = default);
}

public sealed class WorkerPipe : IWorkerPipe
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    public async Task<JsonObject> Call(string name, JsonObject request, int timeoutMs = 700,
        CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeoutMs);
        var token = deadline.Token;
        var gate = gates.GetOrAdd(name, _ => new(1));
        await gate.WaitAsync(token);
        try
        {
            request["schemaVersion"] = 1;
            var id = request["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
            request["id"] = id;
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(token);
            var body = JsonSerializer.SerializeToUtf8Bytes(request);
            if (body.Length > 65536) throw new InvalidDataException("IPC request exceeds bound");
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
            await pipe.WriteAsync(header, token); await pipe.WriteAsync(body, token);
            await pipe.ReadExactlyAsync(header, token);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 1 or > 65536) throw new InvalidDataException("IPC reply exceeds bound");
            var response = new byte[length]; await pipe.ReadExactlyAsync(response, token);
            await pipe.WriteAsync(new byte[] { 1 }, token);
            var reply = JsonNode.Parse(response)?.AsObject() ?? throw new InvalidDataException("Empty IPC reply");
            if (reply["schemaVersion"]?.GetValue<int>() != 1 || reply["id"]?.GetValue<string>() != id)
                throw new InvalidDataException("IPC schema or request ID mismatch");
            if (reply["ok"]?.GetValue<bool>() != true)
                throw new InvalidOperationException(reply["error"]?.GetValue<string>() ?? "Worker rejected request");
            return reply;
        }
        finally { gate.Release(); }
    }
    public Task<JsonObject> State(int slot, CancellationToken cancellation = default) =>
        Call($"NDIMachine.output.{slot}.v1", new() { ["method"] = "state" }, cancellation: cancellation);
}

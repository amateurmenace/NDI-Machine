using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace NDIMachine.Control;

// Separate from the UI and tested with a pipe double. No media runs in this layer.
public static class SourceSwitch
{
    public static void Validate(JsonObject request, string hostInstance, Commission commission)
    {
        if (request["schemaVersion"]?.GetValue<int>() != 1 ||
            request["expectedInstanceId"]?.GetValue<string>() != hostInstance)
            throw new InvalidOperationException("Control session changed; refresh before choosing a source");
        if (!commission.Outputs.Any(o => o.Slot == request["slot"]?.GetValue<int>()))
            throw new InvalidDataException("Output is not commissioned");
        var source = request["source"]?.GetValue<string>() ?? "";
        if (Encoding.UTF8.GetByteCount(source) is < 1 or > 512 || source.Any(char.IsControl) || source != source.Trim())
            throw new InvalidDataException("An exact NDI source name of 1 to 512 UTF-8 bytes is required");
        if (request["expectedRevision"]?.GetValue<long>() is not > 0 ||
            string.IsNullOrWhiteSpace(request["expectedWorkerInstanceId"]?.GetValue<string>()))
            throw new InvalidDataException("Fresh worker identity and revision are required");
    }

    public static async Task Apply(IWorkerPipe pipes, OutputAssignment output, string endpoint, JsonObject request,
        Action commitSent, CancellationToken token)
    {
        var pipe = $"NDIMachine.output.{output.Slot}.v1";
        void Check(JsonObject state)
        {
            if (state["workerInstanceId"]?.GetValue<string>() != request["expectedWorkerInstanceId"]?.GetValue<string>() ||
                state["configRevision"]?.GetValue<long>() != request["expectedRevision"]?.GetValue<long>() ||
                !string.Equals(state["deviceId"]?.GetValue<string>(), output.Device, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(state["audio"]?["endpointId"]?.GetValue<string>(), endpoint, StringComparison.OrdinalIgnoreCase) ||
                state["running"]?.GetValue<bool>() != true || state["backend"]?.GetValue<string>() != "hardware")
                throw new InvalidOperationException("Output changed or is unavailable; existing source preserved");
        }
        var before = (await pipes.State(output.Slot, token))["state"]!.AsObject();
        Check(before);
        if (before["source"]?.GetValue<string>() == request["source"]?.GetValue<string>()) return;
        var candidate = await pipes.Call(pipe, new() { ["method"] = "prepare", ["source"] = request["source"]!.DeepClone() }, 12000, token);
        Check((await pipes.State(output.Slot, token))["state"]!.AsObject());
        commitSent(); // An ACK timeout after this point is unknown, never a safe retry.
        var committed = await pipes.Call(pipe, new() { ["method"] = "commit", ["preparedOperationId"] = candidate["preparedOperationId"]!.DeepClone(),
            ["expectedRevision"] = request["expectedRevision"]!.DeepClone() }, 2000, token);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            var actual = (await pipes.State(output.Slot, token))["state"]!.AsObject();
            if (actual["workerInstanceId"]?.GetValue<string>() != request["expectedWorkerInstanceId"]?.GetValue<string>())
                throw new InvalidOperationException("Worker restarted during source change; outcome unknown");
            if (actual["committedOperationId"]?.GetValue<string>() == committed["operationId"]?.GetValue<string>() &&
                actual["configRevision"]?.GetValue<long>() == committed["revision"]?.GetValue<long>() &&
                actual["source"]?.GetValue<string>() == request["source"]?.GetValue<string>() &&
                actual["running"]?.GetValue<bool>() == true && actual["scheduleAgeMs"]?.GetValue<long>() is >= 0 and < 500)
                return;
            await Task.Delay(80, token);
        }
        throw new TimeoutException("Source commit was sent but displayed state was not confirmed; refresh, do not replay");
    }
}

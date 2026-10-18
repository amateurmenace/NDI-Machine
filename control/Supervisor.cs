using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NDIMachine.Control;

public sealed class Supervisor : BackgroundService
{
    private readonly Commission config;
    private readonly WorkerPipe pipes;
    private readonly SemaphoreSlim mutations = new(1);
    private readonly object sync = new();
    private readonly Dictionary<string, JsonObject> operations = new();
    private readonly Dictionary<string, Process> launched = new();
    private readonly Dictionary<string, (DateTime Next, int Failures)> retries = new();
    private readonly string intentPath;
    private bool armed;
    private string? pending;
    private string lastError = "";
    public string InstanceId { get; } = Guid.NewGuid().ToString();
    public Supervisor(Commission config, WorkerPipe pipes)
    {
        this.config = config; this.pipes = pipes;
        intentPath = Path.Combine(config.Root, "config", "local.supervisor.json");
        // Load only a validated intent for this exact commission. A changed config
        // requires a deliberate Prepare; no guessing a new device after restart.
        foreach (var path in new[] { intentPath, intentPath + ".lkg" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var value = JsonNode.Parse(File.ReadAllText(path))!;
                if (value["schemaVersion"]?.GetValue<int>() != 1) continue;
                armed = value["fingerprint"]?.GetValue<string>() == config.Fingerprint && value["armed"]?.GetValue<bool>() == true;
                break;
            }
            catch (Exception error) { lastError = $"Cannot load saved intent: {error.Message}"; }
        }
    }
    private void SaveIntent(bool value)
    {
        var content = new JsonObject { ["schemaVersion"] = 1, ["fingerprint"] = config.Fingerprint, ["armed"] = value };
        Directory.CreateDirectory(Path.GetDirectoryName(intentPath)!);
        var temp = intentPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(content); stream.Write(bytes); stream.Flush(true); }
        if (File.Exists(intentPath)) File.Replace(temp, intentPath, intentPath + ".lkg");
        else File.Move(temp, intentPath);
        armed = value;
    }
    public JsonObject AcceptPrepare(string id, JsonObject plan)
    {
        if (!Guid.TryParse(id, out _)) throw new InvalidDataException("Idempotency-Key must be a UUID");
        config.ValidatePlan(plan);
        lock (sync)
        {
            if (operations.TryGetValue(id, out var old)) return (JsonObject)old.DeepClone();
            if (pending != null) throw new InvalidOperationException("A prepare operation is already pending");
            if (operations.Count >= 128) operations.Remove(operations.Keys.First());
            SaveIntent(true);
            var operation = new JsonObject { ["operationId"] = id, ["accepted"] = true, ["state"] = "accepted", ["instanceId"] = InstanceId };
            operations.Add(id, operation); pending = id;
            return (JsonObject)operation.DeepClone();
        }
    }
    public JsonObject? Operation(string id) { lock (sync) return operations.TryGetValue(id, out var value) ? (JsonObject)value.DeepClone() : null; }
    public async Task<JsonObject> State(string nonce, CancellationToken token)
    {
        var start = Stopwatch.GetTimestamp();
        var samples = await Task.WhenAll(config.Outputs.Select(async output =>
        {
            try
            {
                var reply = await pipes.State(output.Slot, token);
                return (Value: (JsonObject)reply["state"]!.DeepClone(), At: Stopwatch.GetTimestamp());
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                return (Value: new JsonObject { ["slot"] = output.Slot, ["running"] = false, ["health"] = "unavailable",
                    ["error"] = error is OperationCanceledException ? "Worker state deadline exceeded" : error.Message,
                    ["physicalSignalVerified"] = "Unknown" }, At: Stopwatch.GetTimestamp());
            }
        }));
        var result = new JsonArray();
        foreach (var sample in samples)
        {
            var elapsed = (long)Stopwatch.GetElapsedTime(sample.At).TotalMilliseconds;
            foreach (var key in new[] { "videoAgeMs", "scheduleAgeMs" })
                if (sample.Value[key] != null) sample.Value[key] = sample.Value[key]!.GetValue<long>() + elapsed;
            if (sample.Value["audio"] is JsonObject audio && audio["ageMs"] != null) audio["ageMs"] = audio["ageMs"]!.GetValue<long>() + elapsed;
            result.Add(sample.Value);
        }
        lock (sync) return new JsonObject { ["schemaVersion"] = 1, ["nonce"] = nonce, ["instanceId"] = InstanceId,
            ["outputs"] = result, ["armed"] = armed, ["lastError"] = lastError, ["pendingOperationId"] = pending,
            ["sampleDurationMs"] = Stopwatch.GetElapsedTime(start).TotalMilliseconds, ["physicalSignalVerified"] = "Unknown" };
    }
    private bool CanLaunch(string key, string mutexName)
    {
        if (launched.TryGetValue(key, out var process))
        {
            if (!process.HasExited) return false;
            process.Dispose(); launched.Remove(key);
        }
        // A timeout is not proof a worker is gone. Its ownership mutex forbids
        // takeover while the existing owner is alive, including across host restart.
        if (Mutex.TryOpenExisting(mutexName, out var owner)) { owner.Dispose(); return false; }
        return !retries.TryGetValue(key, out var retry) || DateTime.UtcNow >= retry.Next;
    }
    private void Launch(string key, string executable, params string[] arguments)
    {
        var previous = retries.GetValueOrDefault(key);
        var failures = Math.Min(previous.Failures + 1, 6);
        retries[key] = (DateTime.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, failures))), failures);
        var file = Path.Combine(config.Root, "build", "Release", executable);
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = config.Root };
        info.Environment["PATH"] = @"C:\Program Files\NDI\NDI 6 Runtime\v6;" + Environment.GetEnvironmentVariable("PATH");
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        launched[key] = Process.Start(info) ?? throw new IOException($"Could not start {executable}");
    }
    private async Task<bool> AudioReady(CancellationToken token)
    {
        JsonObject? state = null;
        try { state = await pipes.Call("NDIMachine.audio.v1", new() { ["method"] = "state" }, cancellation: token); }
        catch (Exception) when (!token.IsCancellationRequested) { }
        if (state != null)
        {
            if (!string.Equals(state["endpointId"]?.GetValue<string>(), config.Endpoint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Another audio host owns a different endpoint; no substitution or forced stop");
            if (state["fresh"]?.GetValue<bool>() == true && state["frames"]!.GetValue<long>() >= 10000)
            { retries.Remove("audio"); return true; }
        }
        else if (CanLaunch("audio", @"Global\NDIMachine.AudioHost")) Launch("audio", "audio-host.exe", "--endpoint", config.Endpoint, "--daemon");
        return false;
    }
    private async Task EnsureOutput(OutputAssignment output, bool allowSwitch, CancellationToken token)
    {
        JsonObject? state = null;
        try { state = (await pipes.State(output.Slot, token))["state"]!.AsObject(); }
        catch (Exception) when (!token.IsCancellationRequested) { }
        var key = $"output{output.Slot}";
        if (state == null)
        {
            if (CanLaunch(key, $"Global\\NDIMachine.slot.{output.Slot}"))
                Launch(key, "media-worker.exe", "--slot", output.Slot.ToString(), "--device", output.Device, "--source", output.Source, "--audio-endpoint", config.Endpoint);
            return;
        }
        if (!string.Equals(state["deviceId"]?.GetValue<string>(), output.Device, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(state["audio"]?["endpointId"]?.GetValue<string>(), config.Endpoint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Output {output.Slot} identity differs; existing output preserved");
        retries.Remove(key);
        // The IPC server becomes available before the first SDI frame completes.
        // An empty startup source is not a request to replace the initial receiver.
        if (state["configRevision"]?.GetValue<long>() == 0 || state["running"]?.GetValue<bool>() != true) return;
        if (state["source"]?.GetValue<string>() == output.Source) return;
        if (!allowSwitch) throw new InvalidOperationException($"Output {output.Slot} uses another source; deliberate Prepare required");
        var prepared = await pipes.Call($"NDIMachine.output.{output.Slot}.v1", new() { ["method"] = "prepare", ["source"] = output.Source }, 12000, token);
        await pipes.Call($"NDIMachine.output.{output.Slot}.v1", new() { ["method"] = "commit",
            ["preparedOperationId"] = prepared["preparedOperationId"]!.DeepClone(), ["expectedRevision"] = state["configRevision"]!.DeepClone() }, 2000, token);
    }
    private bool AllReady(JsonObject state) => config.Outputs.All(output => state["outputs"]!.AsArray().Any(value =>
        value?["slot"]?.GetValue<int>() == output.Slot && value["source"]?.GetValue<string>() == output.Source &&
        value["health"]?.GetValue<string>() == "healthy" && value["videoAgeMs"]!.GetValue<long>() < 500 &&
        value["scheduleAgeMs"]!.GetValue<long>() < 500 && value["audio"]!["ageMs"]!.GetValue<long>() < 500));
    private async Task EnsureOutputs(bool allowSwitch, CancellationToken token)
    {
        var errors = new List<string>();
        foreach (var output in config.Outputs)
        {
            try { await EnsureOutput(output, allowSwitch, token); }
            catch (Exception error) when (!token.IsCancellationRequested) { errors.Add($"Output {output.Slot}: {error.Message}"); }
        }
        // A configuration conflict or failed preparation on one slot must not
        // prevent the other independent slots from being observed/recovered.
        if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors));
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            string? operation; bool enabled;
            lock (sync) { operation = pending; enabled = armed; }
            if (enabled)
            {
                await mutations.WaitAsync(stoppingToken);
                try
                {
                    if (operation != null)
                    {
                        var expires = Stopwatch.StartNew();
                        while (expires.Elapsed < TimeSpan.FromSeconds(30))
                        {
                            if (await AudioReady(stoppingToken))
                                await EnsureOutputs(true, stoppingToken);
                            if (AllReady(await State("prepare", stoppingToken))) break;
                            await Task.Delay(250, stoppingToken);
                        }
                        if (!AllReady(await State("prepare-final", stoppingToken))) throw new InvalidOperationException("Preparation deadline: all four exact outputs are not healthy");
                        lock (sync) { operations[operation]["state"] = "committed"; pending = null; lastError = ""; }
                    }
                    else if (await AudioReady(stoppingToken))
                    {
                        await EnsureOutputs(false, stoppingToken);
                    }
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    lock (sync)
                    {
                        lastError = error.Message;
                        if (operation != null) { operations[operation]["state"] = "failed"; operations[operation]["error"] = lastError; pending = null; }
                    }
                }
                finally { mutations.Release(); }
            }
            await Task.Delay(1000, stoppingToken);
        }
    }
    public override void Dispose()
    {
        // Disposing process handles never stops independently running media.
        foreach (var child in launched.Values) child.Dispose();
        base.Dispose();
    }
}

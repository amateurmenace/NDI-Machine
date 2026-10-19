using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json.Nodes;
using NDIMachine.Control;
using NDIMachine.Desktop;

var passed = 0;
async Task Test(string name, Func<Task> test) { await test(); Console.WriteLine("PASS " + name); passed++; }
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Throws(Func<Task> action) { try { await action(); } catch (Exception) { return; } throw new Exception("Expected rejection"); }
JsonObject Request() => JsonNode.Parse("""{"schemaVersion":1,"slot":1,"source":"Camera B","expectedInstanceId":"host","expectedWorkerInstanceId":"worker","expectedRevision":1}""")!.AsObject();
var assignment = new OutputAssignment(1, "0x123", "Camera A");

await Test("prepare failure preserves source and never commits", async () => {
    var pipe = new FakePipe { FailPrepare = true };
    await Throws(() => SourceSwitch.Apply(pipe, assignment, "usb", Request(), () => {}, default));
    Check(pipe.Commits == 0 && pipe.Source == "Camera A", "Old source was not preserved");
});
await Test("revision conflict is rejected before preparing", async () => {
    var pipe = new FakePipe { Revision = 9 };
    await Throws(() => SourceSwitch.Apply(pipe, assignment, "usb", Request(), () => {}, default));
    Check(pipe.Prepares == 0 && pipe.Commits == 0, "Stale command reached worker");
});
await Test("worker replacement during preparation prevents commit", async () => {
    var pipe = new FakePipe { RestartAfterPrepare = true };
    await Throws(() => SourceSwitch.Apply(pipe, assignment, "usb", Request(), () => {}, default));
    Check(pipe.Commits == 0, "Committed to another worker");
});
await Test("external source edit during preparation prevents commit", async () => {
    var pipe = new FakePipe { ChangeAfterPrepare = true };
    await Throws(() => SourceSwitch.Apply(pipe, assignment, "usb", Request(), () => {}, default));
    Check(pipe.Commits == 0, "Overwrote another operator's change");
});
await Test("commit requires actual displayed operation confirmation", async () => {
    var pipe = new FakePipe(); var sent = false;
    await SourceSwitch.Apply(pipe, assignment, "usb", Request(), () => sent = true, default);
    Check(sent && pipe.Commits == 1 && pipe.Reads >= 3 && pipe.Source == "Camera B", "Not confirmed from displayed state");
});
await Test("lost commit ACK remains unknown and is not replayed", async () => {
    var pipe = new FakePipe { LoseCommitAck = true }; var sent = false;
    await Throws(() => SourceSwitch.Apply(pipe, assignment, "usb", Request(), () => sent = true, default));
    Check(sent && pipe.Commits == 1, "Command was replayed or misclassified");
});
await Test("accepted but never displayed is not success", async () => {
    var pipe = new FakePipe { NeverDisplay = true };
    await Throws(() => SourceSwitch.Apply(pipe, assignment, "usb", Request(), () => {}, default));
    Check(pipe.Commits == 1, "Commit replayed");
});
await Test("same-source request is a verified no-op", async () => {
    var pipe = new FakePipe(); var request = Request(); request["source"] = "Camera A";
    await SourceSwitch.Apply(pipe, assignment, "usb", request, () => {}, default);
    Check(pipe.Prepares == 0 && pipe.Commits == 0, "Same source reinitialized");
});
await Test("wrong device and audio identity block before preparation", async () => {
    foreach (var pipe in new[] { new FakePipe { Device = "0x456" }, new FakePipe { Endpoint = "other" } }) {
        await Throws(() => SourceSwitch.Apply(pipe, assignment, "usb", Request(), () => {}, default));
        Check(pipe.Prepares == 0, "Wrong device was touched");
    }
});
await Test("UI cannot show simulated or mismatched media as backup-ready", () => {
    var pipe = new FakePipe(); var output = new OutputModel(1); var state = pipe.Snapshot();
    var path = new JsonObject { ["source"] = "Camera A", ["device"] = "0x123" };
    output.Update(state, path, true, true); Check(output.PlanReady("usb"), "Healthy exact state rejected");
    state["backend"] = "simulation"; output.Update(state, path, true, true); Check(!output.PlanReady("usb"), "Simulation passed");
    state["backend"] = "hardware"; state["source"] = "Other"; output.Update(state, path, true, true); Check(!output.PlanReady("usb"), "Wrong source passed");
    return Task.CompletedTask;
});
await Test("stale UI disables mutations and clears green readiness", () => {
    var output = new OutputModel(1); var state = new FakePipe().Snapshot();
    output.Update(state, new() { ["source"] = "Camera A", ["device"] = "0x123" }, true, true); output.SelectedSource = "Camera B";
    Check(output.CanApply, "Fresh source selection unavailable");
    output.Update(state, output.Path, false, true);
    Check(!output.CanApply && !output.SoftwareHealthy && !output.AudioFresh && output.Completed == "—", "Stale feedback retained"); return Task.CompletedTask;
});
await Test("refresh preserves an operator's pending source selection", () => {
    var output = new OutputModel(1); output.Update(new FakePipe().Snapshot(), null, true, true); output.SelectedSource = "Pending";
    output.Update(new FakePipe().Snapshot(), null, true, true); Check(output.SelectedSource == "Pending", "Polling overwrote user input"); return Task.CompletedTask;
});
await Test("HTTP client rejects stale nonce and never retries mutations", async () => {
    var handler = new FakeHttp(); using var client = new EngineClient(handler);
    await Throws(() => client.State()); Check(handler.Calls == 1, "State retried unexpectedly");
    handler.Fail = true; await Throws(() => client.Request(HttpMethod.Put, "outputs/1/route", Request(), Guid.NewGuid().ToString()));
    Check(handler.Calls == 2, "Mutation was automatically replayed");
});
await Test("HTTP client rejects oversized responses", async () => {
    using var client = new EngineClient(new FakeHttp { Large = true }); await Throws(() => client.State());
});
await Test("copied desktop is recognized without accepting another executable",()=>{
    var folder=Path.Combine(Path.GetTempPath(),"ndi-identity-"+Guid.NewGuid());Directory.CreateDirectory(folder);
    try {
        var installed=Path.Combine(folder,"installed.exe");var copy=Path.Combine(folder,"copy.exe");var other=Path.Combine(folder,"other.exe");
        File.WriteAllText(installed,"identical gui");File.Copy(installed,copy);File.WriteAllText(other,"unrelated exe");
        var identity=new NDIMachine.Launcher.ExecutableIdentity();
        Check(identity.Matches(installed,copy,true),"Portable GUI rejected");
        Check(!identity.Matches(installed,copy,false),"Copied host accepted");
        Check(!identity.Matches(installed,other,true),"Unrelated executable accepted");
    }finally{Directory.Delete(folder,true);}return Task.CompletedTask;
});
await Test("idle outputs do not display a selected receiver or enable Stop",()=>{
    var output=new OutputModel(1);output.Update(new(){["running"]=false,["health"]="idle"},new(){["source"]="Emergency preset"},true,true);
    Check(output.Status=="IDLE" && !output.CanStop && output.Source=="No source · idle","Preset shown as running");return Task.CompletedTask;
});
await ConfigurationChecks.Run(Test);
Console.WriteLine($"{passed} tests passed. Simulated control/GUI checks; no hardware writes.");

sealed class FakeHttp : HttpMessageHandler
{
    public int Calls; public bool Fail; public bool Large;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++; if (Fail) throw new HttpRequestException("Disconnected");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Large ? new string('x', 300000) : "{\"schemaVersion\":1,\"nonce\":\"stale\",\"instanceId\":\"host\",\"outputs\":[]}") });
    }
}
sealed class FakePipe : IWorkerPipe
{
    public int Prepares, Commits, Reads;
    public long Revision = 1;
    public string Source = "Camera A", Device = "0x123", Endpoint = "usb", Worker = "worker";
    public bool FailPrepare, RestartAfterPrepare, ChangeAfterPrepare, LoseCommitAck, NeverDisplay;
    public JsonObject Snapshot() => JsonNode.Parse(new JsonObject {
        ["slot"] = 1, ["running"] = true, ["backend"] = "hardware", ["deviceId"] = Device, ["source"] = Source,
        ["workerInstanceId"] = Worker, ["configRevision"] = Revision, ["committedOperationId"] = Source == "Camera B" ? "commit" : "old",
        ["format"] = "1920x1080p30000/1001", ["health"] = "healthy", ["fallback"] = false, ["videoAgeMs"] = 10, ["scheduleAgeMs"] = 10,
        ["audio"] = new JsonObject { ["endpointId"] = Endpoint, ["ageMs"] = 10, ["muted"] = false, ["mode"] = "fixed", ["channels"] = new JsonArray(1,2) },
        ["counters"] = new JsonObject { ["completed"] = 100, ["late"] = 0, ["dropped"] = 0 }
    }.ToJsonString())!.AsObject();
    public Task<JsonObject> State(int slot, CancellationToken cancellation = default) { Reads++; return Task.FromResult(new JsonObject { ["state"] = Snapshot() }); }
    public Task<JsonObject> Call(string name, JsonObject request, int timeoutMs = 700, CancellationToken cancellation = default)
    {
        if (request["method"]!.GetValue<string>() == "prepare") {
            Prepares++; if (FailPrepare) throw new InvalidOperationException("Missing source");
            if (RestartAfterPrepare) Worker = "replacement"; if (ChangeAfterPrepare) Revision++;
            return Task.FromResult(new JsonObject { ["preparedOperationId"] = "candidate" });
        }
        Commits++;
        if (!NeverDisplay) { Source = "Camera B"; Revision++; }
        if (LoseCommitAck) throw new IOException("Lost ACK");
        return Task.FromResult(new JsonObject { ["operationId"] = "commit", ["revision"] = 2L });
    }
}

using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NDIMachine.Control;

// Serialized control operations only. Media and capture processes survive the host.
public sealed class OutputManager(Commission commission, IWorkerPipe pipes, IInventory inventory)
{
    private readonly Dictionary<string,Process> launched = new();
    private readonly Dictionary<string,DateTime> nextLaunch = new();
    private bool Ownable(string name) {
        if (Mutex.TryOpenExisting(name,out var mutex)) {mutex.Dispose();return false;}
        return true;
    }
    private void Launch(string key,string exe,params string[] arguments) {
        if(nextLaunch.TryGetValue(key,out var next) && next>DateTime.UtcNow) throw new InvalidOperationException("Restart backoff active for " + key);
        if(launched.TryGetValue(key,out var old)) { if(!old.HasExited) throw new InvalidOperationException("Process still running: " + key);old.Dispose(); }
        var start=new ProcessStartInfo(Path.Combine(commission.Root,"build","Release",exe)){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=commission.Root};
        start.Environment["PATH"]=@"C:\Program Files\NDI\NDI 6 Runtime\v6;"+Environment.GetEnvironmentVariable("PATH");
        foreach(var arg in arguments) start.ArgumentList.Add(arg);
        nextLaunch[key]=DateTime.UtcNow.AddSeconds(10);
        launched[key]=Process.Start(start) ?? throw new IOException("Cannot start " + exe);
    }
    public async Task<JsonObject?> State(int slot,CancellationToken token) {
        try { return (JsonObject)(await pipes.State(slot,token))["state"]!.DeepClone(); }
        catch(Exception) when(!token.IsCancellationRequested) { return null; }
    }
    private async Task Audio(JsonObject settings,CancellationToken token) {
        if(settings["audioMode"]!.GetValue<string>()!="fixed") return;
        var key=settings["audioKey"]!.GetValue<string>();
        var endpoint=settings["audioEndpoint"]!.GetValue<string>();
        var pipe=$"NDIMachine.audio.{key}.v1";
        var timer=Stopwatch.StartNew();
        while(timer.ElapsedMilliseconds<7000) {
            JsonObject? state=null;
            try {state=await pipes.Call(pipe,new(){["method"]="state"},cancellation:token);}catch(Exception) when(!token.IsCancellationRequested){}
            if(state!=null) {
                if(state["endpointId"]?.GetValue<string>()!=endpoint || state["channels"]?.ToJsonString()!=$"[{settings["left"]},{settings["right"]}]")
                    throw new InvalidOperationException("Capture identity mismatch");
                if(state["fresh"]?.GetValue<bool>()==true && state["frames"]!.GetValue<long>()>=10000) return;
            } else if(Ownable($"Global\\NDIMachine.AudioHost.{key}") && (!launched.TryGetValue("audio"+key,out var running) || running.HasExited))
                Launch("audio"+key,"audio-host.exe","--endpoint",endpoint,"--key",key,"--left",settings["left"]!.ToString(),"--right",settings["right"]!.ToString(),"--daemon");
            await Task.Delay(100,token);
        }
        throw new InvalidOperationException("Selected Windows audio is not delivering fresh samples");
    }
    public Task MaintainAudio(JsonObject settings,CancellationToken token)=>Audio(settings,token);
    private void Start(int slot,JsonObject settings) {
        if(!Ownable($"Global\\NDIMachine.slot.{slot}")) throw new InvalidOperationException("Existing slot owner remains alive; no forced takeover");
        var path=Path.Combine(commission.Root,"config",$"local.output-{slot}.json");
        File.WriteAllText(path,settings.ToJsonString());
        Launch($"output{slot}","media-worker.exe","--slot",slot.ToString(),"--device",settings["device"]!.GetValue<string>(),"--config",path);
    }
    public static void CheckExpected(JsonObject? state,JsonObject request) {
        var epoch=request["expectedWorkerInstanceId"]?.GetValue<string>() ?? "";
        var revision=request["expectedRevision"]?.GetValue<long>() ?? 0;
        if(state==null ? epoch.Length!=0 || revision!=0 : state["workerInstanceId"]?.GetValue<string>()!=epoch || state["configRevision"]?.GetValue<long>()!=revision)
            throw new InvalidOperationException("Worker changed since the editor was opened; reload actual settings");
    }
    // A deliberate group restore may reclaim this application's own connectors,
    // including cycles. Unknown owners and the reserved OBS card remain protected.
    public static int[] ValidateGroup(IReadOnlyDictionary<int,JsonObject> targets, IReadOnlyDictionary<int,JsonObject?> states, JsonObject inventory) {
        if(targets.Values.Select(s=>s["device"]!.GetValue<string>()).Distinct().Count()!=targets.Count)
            throw new InvalidDataException("Duplicate DeckLink target");
        if(states.Values.Any(s=>s!=null && (s["backend"]?.GetValue<string>()!="hardware" || string.IsNullOrEmpty(s["workerInstanceId"]?.GetValue<string>()))))
            throw new InvalidOperationException("Only identified hardware workers can be restored");
        foreach(var settings in targets.Values) {
            var owners=states.Values.Where(s=>s?["deviceId"]?.GetValue<string>()==settings["device"]!.GetValue<string>()).ToArray();
            if(owners.Length>1 || owners.Any(s=>s?["backend"]?.GetValue<string>()!="hardware" || string.IsNullOrEmpty(s?["workerInstanceId"]?.GetValue<string>())))
                throw new InvalidOperationException("DeckLink ownership is ambiguous");
            Inventory.ValidateTarget(settings,inventory,owners.SingleOrDefault(),[]);
        }
        return targets.Keys.Where(slot=>states[slot]!=null && states[slot]!["deviceId"]?.GetValue<string>()!=targets[slot]["device"]!.GetValue<string>()).ToArray();
    }
    public async Task ApplyGroup(IReadOnlyDictionary<int,JsonObject> targets, JsonObject? expected, Action sent, CancellationToken token) {
        var states=new Dictionary<int,JsonObject?>();
        foreach(var slot in targets.Keys) {
            states[slot]=await State(slot,token);
            if(expected!=null) CheckExpected(states[slot],expected[slot.ToString()]!.AsObject());
        }
        var release=ValidateGroup(targets,states,await inventory.Get(token));
        // Validate every slot again before releasing any connector.
        foreach(var slot in targets.Keys) CheckExpected(await State(slot,token),Expected(states[slot]));
        foreach(var slot in release) {
            CheckExpected(await State(slot,token),Expected(states[slot]));
            sent();
            await pipes.Call($"NDIMachine.output.{slot}.v1",new(){["method"]="stop"},2000,token);
            var timer=Stopwatch.StartNew();
            while(!Ownable($"Global\\NDIMachine.slot.{slot}")) {
                if(timer.ElapsedMilliseconds>5000)throw new InvalidOperationException("Worker did not release connector; no forced termination");
                await Task.Delay(50,token);
            }
        }
        foreach(var pair in targets) await Apply(pair.Key,pair.Value,release.Contains(pair.Key)?Expected(null):Expected(states[pair.Key]),true,sent,token);
    }
    // Locally created JSON values keep their CLR numeric type. Idle revision
    // zero must match the Int64 revision reader just like a parsed worker reply.
    private static JsonObject Expected(JsonObject? state)=>new(){["expectedWorkerInstanceId"]=state?["workerInstanceId"]?.DeepClone() ?? JsonValue.Create(""),["expectedRevision"]=state?["configRevision"]?.DeepClone() ?? JsonValue.Create(0L)};
    public async Task Stop(int slot,JsonObject expected,Action sent,CancellationToken token) {
        var state=await State(slot,token);CheckExpected(state,expected);
        if(state==null) {
            if(!Ownable($"Global\\NDIMachine.slot.{slot}"))throw new InvalidOperationException("Output owner is unresponsive; cannot confirm stopped");
            return;
        }
        if(state["backend"]?.GetValue<string>()!="hardware")throw new InvalidOperationException("Identified hardware worker required");
        sent();
        await pipes.Call($"NDIMachine.output.{slot}.v1",new(){["method"]="stop"},2000,token);
        var timer=Stopwatch.StartNew();
        while(!Ownable($"Global\\NDIMachine.slot.{slot}")) {
            if(timer.ElapsedMilliseconds>5000)throw new InvalidOperationException("Output did not stop; no forced termination");
            await Task.Delay(50,token);
        }
    }
    public async Task Apply(int slot,JsonObject settings,JsonObject? expected,bool deliberate,Action sent,CancellationToken token) {
        var initial=await State(slot,token);
        if(expected!=null) CheckExpected(initial,expected);
        if(initial!=null && initial["backend"]?.GetValue<string>()!="hardware") throw new InvalidOperationException("Real hardware worker required");
        if(initial!=null && OutputConfiguration.Matches(settings,initial) && initial["running"]?.GetValue<bool>()==true) return;
        if(initial!=null && !deliberate) throw new InvalidOperationException("Actual output differs from saved intent; use Apply or Prepare backups");
        var devices=await inventory.Get(token);
        Inventory.ValidateTarget(settings,devices,initial,[]);
        await Audio(settings,token);
        if(initial!=null) {
            var prepare=new JsonObject{["method"]="prepare"};
            var modern=initial["settings"] is JsonObject;
            if(modern) prepare["settings"]=settings.DeepClone(); else prepare["source"]=settings["source"]!.DeepClone();
            var prepared=await pipes.Call($"NDIMachine.output.{slot}.v1",prepare,12000,token);
            var again=await State(slot,token);
            CheckExpected(again,new(){["expectedWorkerInstanceId"]=initial["workerInstanceId"]!.DeepClone(),["expectedRevision"]=initial["configRevision"]!.DeepClone()});
            if(modern && initial["deviceId"]?.GetValue<string>()==settings["device"]?.GetValue<string>()) {
                sent();
                await pipes.Call($"NDIMachine.output.{slot}.v1",new(){["method"]="commit",["preparedOperationId"]=prepared["preparedOperationId"]!.DeepClone(),["expectedRevision"]=initial["configRevision"]!.DeepClone()},2000,token);
                await Wait(slot,settings,initial["workerInstanceId"]!.GetValue<string>(),prepared["preparedOperationId"]!.GetValue<string>(),token);
                return;
            }
            // Only an explicit configuration command can interrupt this slot.
            sent();
            await pipes.Call($"NDIMachine.output.{slot}.v1",new(){["method"]="stop"},2000,token);
            var timer=Stopwatch.StartNew();
            while(!Ownable($"Global\\NDIMachine.slot.{slot}")) {
                if(timer.ElapsedMilliseconds>5000) throw new InvalidOperationException("Worker did not release slot; no forced termination");
                await Task.Delay(50,token);
            }
        }
        sent(); Start(slot,settings);
        await Wait(slot,settings,null,null,token);
    }
    private async Task Wait(int slot,JsonObject settings,string? epoch,string? operation,CancellationToken token) {
        var timer=Stopwatch.StartNew();
        while(timer.ElapsedMilliseconds<14000) {
            var state=await State(slot,token);
            if(state!=null) {
                if(epoch!=null && state["workerInstanceId"]?.GetValue<string>()!=epoch) throw new InvalidOperationException("Worker restarted during commit; outcome unknown");
                if(state["backend"]?.GetValue<string>()=="hardware" && OutputConfiguration.Matches(settings,state) && state["running"]?.GetValue<bool>()==true && state["configRevision"]!.GetValue<long>()>0 &&
                    (operation==null || state["committedOperationId"]?.GetValue<string>()==operation) && state["scheduleAgeMs"]!.GetValue<long>()<500) return;
            }
            await Task.Delay(100,token);
        }
        throw new InvalidOperationException("Configuration was sent but displayed output was not confirmed; no replay");
    }
    public async Task CleanupAudio(IEnumerable<JsonObject> settings,CancellationToken token) {
        var keep=settings.Where(s=>s["audioMode"]?.GetValue<string>()=="fixed").Select(s=>"audio"+s["audioKey"]!.GetValue<string>()).ToHashSet();
        foreach(var output in commission.Outputs) {
            var actual=await State(output.Slot,token);
            if(actual==null)return; // An unavailable worker may still own a reader.
            if(actual["settings"] is JsonObject active && active["audioMode"]?.GetValue<string>()=="fixed") keep.Add("audio"+active["audioKey"]?.GetValue<string>());
        }
        foreach(var pair in launched.Where(p=>p.Key.StartsWith("audio") && !keep.Contains(p.Key)).ToArray()) {
            try { if(!pair.Value.HasExited) await pipes.Call($"NDIMachine.audio.{pair.Key[5..]}.v1",new(){["method"]="stop"},cancellation:token); }
            catch(Exception) when(!token.IsCancellationRequested) {}
            pair.Value.Dispose(); launched.Remove(pair.Key);
        }
    }
    public void DisposeHandles() {foreach(var process in launched.Values) process.Dispose();}
}

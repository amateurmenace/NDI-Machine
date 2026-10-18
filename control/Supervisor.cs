using System.Diagnostics;
using System.Text.Json.Nodes;
namespace NDIMachine.Control;

public sealed class Supervisor : BackgroundService
{
    private readonly Commission config;
    private readonly OutputManager manager;
    private readonly object sync=new();
    private readonly Dictionary<string,JsonObject> operations=new(),requests=new();
    private readonly Dictionary<int,JsonObject> desired=new();
    private readonly string intentPath;
    private string? pending;
    private bool armed;
    private string lastError="";
    public string InstanceId {get;}=Guid.NewGuid().ToString();
    public Supervisor(Commission config,IWorkerPipe pipes,Inventory inventory) {
        this.config=config;manager=new(config,pipes,inventory);
        foreach(var output in config.Outputs) desired[output.Slot]=OutputConfiguration.Validate(OutputConfiguration.Default(output,config.Endpoint));
        intentPath=Path.Combine(config.Root,"config","local.supervisor.json");
        foreach(var path in new[]{intentPath,intentPath+".lkg"}) {
            if(!File.Exists(path)) continue;
            try {
                var saved=JsonNode.Parse(File.ReadAllText(path))!;
                if(saved["schemaVersion"]?.GetValue<int>()!=1) continue;
                armed=saved["fingerprint"]?.GetValue<string>()==config.Fingerprint && saved["armed"]?.GetValue<bool>()==true;
                if(armed && saved["outputs"] is JsonObject outputs) {
                    foreach(var output in config.Outputs) if(outputs[output.Slot.ToString()] is JsonObject value) desired[output.Slot]=OutputConfiguration.Validate(value);
                } else if(armed && saved["sources"] is JsonObject sources) {
                    foreach(var assignment in config.Outputs) if(sources[assignment.Slot.ToString()] is JsonValue source) {desired[assignment.Slot]["source"]=source.DeepClone();desired[assignment.Slot]=OutputConfiguration.Validate(desired[assignment.Slot]);}
                }
                if(desired.Values.Select(v=>v["device"]!.GetValue<string>()).Distinct().Count()!=desired.Count) throw new InvalidDataException("Saved device assignments conflict");
                break;
            }catch(Exception error){armed=false;lastError="Cannot restore saved intent: "+error.Message;}
        }
    }
    private void SaveIntent() {
        var content=new JsonObject{["schemaVersion"]=1,["fingerprint"]=config.Fingerprint,["armed"]=armed,
            ["outputs"]=new JsonObject(desired.Select(p=>KeyValuePair.Create<string,JsonNode?>(p.Key.ToString(),p.Value.DeepClone())))};
        Directory.CreateDirectory(Path.GetDirectoryName(intentPath)!);
        var temp=intentPath+".tmp";
        using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) {
            stream.Write(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(content));stream.Flush(true);
        }
        if(File.Exists(intentPath)) File.Replace(temp,intentPath,intentPath+".lkg");else File.Move(temp,intentPath);
    }
    private JsonObject Accept(string id,JsonObject request) {
        if(!Guid.TryParse(id,out _)) throw new InvalidDataException("Idempotency-Key must be a UUID");
        lock(sync) {
            if(operations.TryGetValue(id,out var old)) {
                if(!JsonNode.DeepEquals(requests[id],request)) throw new InvalidOperationException("Idempotency key reused with a different command");
                return (JsonObject)old.DeepClone();
            }
            if(pending!=null) throw new InvalidOperationException("Another media operation is pending");
            if(operations.Count>=128){var oldest=operations.Keys.First();operations.Remove(oldest);requests.Remove(oldest);}
            var result=new JsonObject{["operationId"]=id,["instanceId"]=InstanceId,["accepted"]=true,["state"]="accepted"};
            operations[id]=result;requests[id]=(JsonObject)request.DeepClone();pending=id;
            return (JsonObject)result.DeepClone();
        }
    }
    public JsonObject AcceptPrepare(string id,JsonObject plan) {config.ValidatePlan(plan);return Accept(id,new(){["kind"]="prepare",["plan"]=plan.DeepClone()});}
    public JsonObject AcceptConfiguration(string id,JsonObject request) {
        var slot=request["slot"]?.GetValue<int>() ?? 0;
        if(request["schemaVersion"]?.GetValue<int>()!=1 || request["expectedInstanceId"]?.GetValue<string>()!=InstanceId || !desired.ContainsKey(slot))
            throw new InvalidDataException("Current host and configured slot are required");
        if(request["expectedWorkerInstanceId"] is not JsonValue || request["expectedRevision"]?.GetValue<long>() is not >=0)
            throw new InvalidDataException("Expected worker identity and revision are required");
        if(request["settings"] is not JsonObject supplied)throw new InvalidDataException("Output settings object required");
        var candidate=OutputConfiguration.Validate(supplied);
        lock(sync) if(desired.Any(p=>p.Key!=slot && p.Value["device"]!.GetValue<string>()==candidate["device"]!.GetValue<string>()))
            throw new InvalidOperationException("DeckLink output is assigned to another slot");
        var command=(JsonObject)request.DeepClone();command["settings"]=candidate;command["kind"]="configure";
        return Accept(id,command);
    }
    public JsonObject AcceptRoute(string id,JsonObject request) {
        SourceSwitch.Validate(request,InstanceId,config);
        var slot=request["slot"]!.GetValue<int>();
        JsonObject settings;lock(sync)settings=(JsonObject)desired[slot].DeepClone();
        settings["source"]=request["source"]!.DeepClone();settings["sourceUrl"]="";
        var command=(JsonObject)request.DeepClone();command["settings"]=settings;
        return AcceptConfiguration(id,command);
    }
    public JsonObject? Operation(string id){lock(sync)return operations.TryGetValue(id,out var value)?(JsonObject)value.DeepClone():null;}
    public async Task<JsonObject> State(string nonce,CancellationToken token) {
        var timer=Stopwatch.StartNew();
        var samples=await Task.WhenAll(config.Outputs.Select(async output=>{
            var value=await manager.State(output.Slot,token) ?? new JsonObject{["slot"]=output.Slot,["running"]=false,["health"]="unavailable",["physicalSignalVerified"]="Unknown"};
            return (Value:value,At:Stopwatch.GetTimestamp());
        }));
        var outputs=new JsonArray();
        foreach(var sample in samples) {
            var elapsed=(long)Stopwatch.GetElapsedTime(sample.At).TotalMilliseconds;
            foreach(var key in new[]{"videoAgeMs","scheduleAgeMs"}) if(sample.Value[key]!=null)sample.Value[key]=sample.Value[key]!.GetValue<long>()+elapsed;
            if(sample.Value["audio"] is JsonObject audio && audio["ageMs"]!=null)audio["ageMs"]=audio["ageMs"]!.GetValue<long>()+elapsed;
            lock(sync)sample.Value["desiredSettings"]=desired[sample.Value["slot"]!.GetValue<int>()].DeepClone();
            outputs.Add(sample.Value);
        }
        lock(sync)return new(){["schemaVersion"]=1,["nonce"]=nonce,["instanceId"]=InstanceId,["outputs"]=outputs,["armed"]=armed,["lastError"]=lastError,["pendingOperationId"]=pending,
            ["capabilities"]=new JsonArray("source-routing","source-discovery","output-settings","inventory","manual-discovery"),["sampleDurationMs"]=timer.Elapsed.TotalMilliseconds,["physicalSignalVerified"]="Unknown"};
    }
    protected override async Task ExecuteAsync(CancellationToken token) {
        while(!token.IsCancellationRequested) {
            string? operation;bool enabled;JsonObject? request;
            lock(sync){operation=pending;enabled=armed;request=operation==null?null:requests[operation];}
            var sent=false;
            try {
                if(operation!=null && request!=null) {
                    lock(sync)operations[operation]["state"]="preparing";
                    if(request["kind"]!.GetValue<string>()=="configure") {
                        var slot=request["slot"]!.GetValue<int>();var settings=request["settings"]!.AsObject();
                        await manager.Apply(slot,settings,request,true,()=>sent=true,token);
                        lock(sync){desired[slot]=(JsonObject)settings.DeepClone();armed=true;SaveIntent();}
                    } else {
                        foreach(var output in config.Outputs) {
                            var settings=OutputConfiguration.Validate(OutputConfiguration.Default(output,config.Endpoint));
                            lock(sync)if(desired.Any(p=>p.Key!=output.Slot && p.Value["device"]!.GetValue<string>()==output.Device))
                                throw new InvalidOperationException("Restore conflicting DeckLink assignments individually first");
                            await manager.Apply(output.Slot,settings,null,true,()=>sent=true,token);
                            lock(sync){desired[output.Slot]=settings;armed=true;SaveIntent();}
                        }
                    }
                    lock(sync){operations[operation]["state"]="committed";pending=null;lastError="";}
                    await manager.CleanupAudio(desired.Values,token);
                } else if(enabled) {
                    var errors=new List<string>();
                    foreach(var output in config.Outputs) {
                        try {
                            var state=await manager.State(output.Slot,token);
                            if(state==null) await manager.Apply(output.Slot,desired[output.Slot],null,false,()=>{},token);
                            else if(OutputConfiguration.Matches(desired[output.Slot],state)) await manager.MaintainAudio(desired[output.Slot],token);
                            // Preserve existing workers even across software upgrades.
                        }catch(Exception error)when(!token.IsCancellationRequested){errors.Add($"Output {output.Slot}: {error.Message}");}
                    }
                    lock(sync)if(errors.Count>0)lastError=string.Join("; ",errors);
                }
            }catch(Exception error)when(!token.IsCancellationRequested) {
                lock(sync){lastError=error.Message;if(operation!=null){operations[operation]["state"]=sent?"unknown":"failed";operations[operation]["error"]=lastError;pending=null;}}
            }
            await Task.Delay(1000,token);
        }
    }
    public override void Dispose(){manager.DisposeHandles();base.Dispose();}
}

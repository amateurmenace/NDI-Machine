using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
namespace NDIMachine.Control;
public sealed class Discovery
{
    private readonly Commission commission;
    private readonly SemaphoreSlim gate=new(1);
    private readonly object sync=new();
    private JsonObject settings=new(){["extraIps"]="",["groups"]=""};
    private JsonObject? cached;
    private long sampled;
    private readonly string path;
    private readonly Func<IEnumerable<string>,CancellationToken,Task<JsonObject>> probe;
    public Discovery(Commission commission,Func<IEnumerable<string>,CancellationToken,Task<JsonObject>>? probe=null) {
        this.commission=commission;path=Path.Combine(commission.Root,"config","local.discovery.json");
        this.probe=probe ?? (async (args,token)=>(await NativeTools.Run(commission,"ndi-probe.exe",args,token)).AsObject());
        if(File.Exists(path)) settings=Validate(JsonNode.Parse(File.ReadAllText(path))!.AsObject());
    }
    public static JsonObject Validate(JsonObject value) {
        var ips=value["extraIps"]?.GetValue<string>() ?? "";var groups=value["groups"]?.GetValue<string>() ?? "";
        var addresses=ips.Split(new[]{',',';','\r','\n',' '},StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        if(addresses.Length>32 || addresses.Any(a=>!IPAddress.TryParse(a,out _))) throw new InvalidDataException("Use up to 32 IP addresses separated by commas");
        if(groups.Length>512 || groups.Any(char.IsControl)) throw new InvalidDataException("NDI group list must be at most 512 characters");
        return new(){["schemaVersion"]=1,["extraIps"]=string.Join(',',addresses),["groups"]=groups.Trim()};
    }
    public JsonObject Settings(){lock(sync)return (JsonObject)settings.DeepClone();}
    public JsonObject Set(JsonObject value) {
        var next=Validate(value);
        lock(sync){File.WriteAllText(path+".tmp",next.ToJsonString());File.Move(path+".tmp",path,true);settings=next;cached=null;return (JsonObject)next.DeepClone();}
    }
    public async Task<JsonObject> Get(CancellationToken token) {
        await gate.WaitAsync(token); // Concurrent GUI/Companion readers share the completed cached scan.
        try {
            JsonObject options;
            lock(sync){if(cached!=null && Stopwatch.GetElapsedTime(sampled).TotalSeconds<5)return (JsonObject)cached.DeepClone();options=(JsonObject)settings.DeepClone();}
            var arguments=new List<string>();
            foreach(var (key,flag) in new[]{("extraIps","--extra-ips"),("groups","--groups")}) {
                var text=options[key]!.GetValue<string>();if(text.Length>0){arguments.Add(flag);arguments.Add(text);}
            }
            var result=await probe(arguments,token);
            result["schemaVersion"]=1;result["observedAtUtc"]=DateTimeOffset.UtcNow.ToString("O");result["discovery"]=options.DeepClone();
            lock(sync){if(JsonNode.DeepEquals(options,settings)){cached=(JsonObject)result.DeepClone();sampled=Stopwatch.GetTimestamp();}}
            return result;
        }finally{gate.Release();}
    }
}

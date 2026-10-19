using System.IO;
using System.Text.Json.Nodes;
using NDIMachine.Control;
using NDIMachine.Desktop;

internal static class AudioRecoveryChecks
{
    private static JsonObject Device(string id,string name="Dante USB Receive (Dante USB I/O Module)",int state=1,int channels=2)=>new(){
        ["id"]=id,["name"]=name,["state"]=state,["mixFormat"]=new JsonObject{["channels"]=channels}};
    private static void Require(bool value){if(!value)throw new Exception("Audio identity recovery assertion failed");}
    private static void Reject(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("Unsafe audio recovery accepted");}
    public static async Task Run(Func<string,Func<Task>,Task> test)
    {
        await test("Dante recovery keeps a valid exact ID and recovers one renumbered USB receive endpoint",()=>{
            Require(AudioEndpointResolver.Resolve("saved",new(Device("saved"),Device("other")))=="saved");
            Require(AudioEndpointResolver.Resolve("old",new(Device("old",state:4),Device("new","Dante USB Receive (2- Dante USB I/O Module)")))=="new");
            Require(AudioEndpointResolver.Resolve("old",new(Device("new","Dante USB Receive (12- Dante USB I/O Module)")))=="new");
            return Task.CompletedTask;
        });
        await test("Dante recovery rejects missing, ambiguous, disabled, mono, playback and Virtual Soundcard substitutes",()=>{
            foreach(var devices in new[]{new JsonArray(),new JsonArray(Device("a"),Device("b")),new JsonArray(Device("a",state:2)),
                new JsonArray(Device("a",channels:1)),new JsonArray(Device("a","Dante USB Transmit (Dante USB I/O Module)")),
                new JsonArray(Device("a","Dante Receive (Dante Virtual Soundcard)")),new JsonArray(Device("old","Other audio"),Device("new"))})
                Reject(()=>AudioEndpointResolver.Resolve("old",devices));
            return Task.CompletedTask;
        });
        await test("recovery is opt-in, preserves the commissioned plan, and invalidates a lost or ambiguous binding",()=>{
            var root=Path.Combine(Path.GetTempPath(),"ndi-audio-recovery-"+Guid.NewGuid());Directory.CreateDirectory(root);
            try {
                var file=Path.Combine(root,"commission.json");
                var config=JsonNode.Parse("""{"schemaVersion":1,"audio":{"policy":"fixed","backend":"wasapi","endpointId":"saved","displayName":"Dante USB Receive (Dante USB I/O Module)","channels":[1,2],"calibration":"unity"},"outputs":[{"slot":1,"device":"0x123","source":"Camera A"}],"videohub":{"paths":[{"slot":1,"destination":33,"normalInput":9,"backupInput":8}]}}""")!;
                File.WriteAllText(file,config.ToJsonString());var fixedId=new Commission(file,root);
                fixedId.ResolveAudio(new(Device("new")));Require(fixedId.EffectiveEndpoint=="saved");
                config["audio"]!["endpointRecovery"]="unique-dante-usb";File.WriteAllText(file,config.ToJsonString());
                var commission=new Commission(file,root);var plan=commission.Plan.DeepClone();
                commission.ResolveAudio(new(Device("new")));Require(commission.EffectiveEndpoint=="new" && commission.AudioAvailable);
                Require(JsonNode.DeepEquals(plan,commission.Plan));commission.ValidatePlan(plan.AsObject());
                var forged=plan.DeepClone();forged["audioEndpointId"]="other";
                try{commission.ValidatePlan(forged.AsObject());throw new Exception("Plan guard bypassed");}catch(InvalidDataException){}
                Reject(()=>commission.ResolveAudio(new(Device("a"),Device("b"))));Require(!commission.AudioAvailable);
                commission.ResolveAudio(new(Device("reconnected")));Require(commission.EffectiveEndpoint=="reconnected" && commission.AudioAvailable);
                var model=new DashboardModel();model.SetMode(false);model.SetPlan(commission.Plan);
                var output=new FakePipe{Endpoint="reconnected"}.Snapshot();output["emergencyConfigured"]=true;
                model.Apply(new(){["instanceId"]="host",["capabilities"]=new JsonArray("audio-endpoint-binding"),["audioBinding"]=commission.AudioState(),["outputs"]=new JsonArray(output)});
                Require(model.Readiness=="1 / 1");Reject(()=>commission.ResolveAudio(new()));
                model.Apply(new(){["instanceId"]="host",["capabilities"]=new JsonArray("audio-endpoint-binding"),["audioBinding"]=commission.AudioState(),["outputs"]=new JsonArray(output.DeepClone())});
                Require(model.Readiness=="0 / 1");
            } finally{Directory.Delete(root,true);}
            return Task.CompletedTask;
        });
    }
}

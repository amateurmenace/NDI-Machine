using System.Text.Json.Nodes;
using NDIMachine.Control;
using NDIMachine.Desktop;
using System.IO;

internal static class ConfigurationChecks
{
    private static void Require(bool value){if(!value)throw new Exception("Configuration assertion failed");}
    private static async Task Reject(Func<Task> work){try{await work();}catch(Exception){return;}throw new Exception("Expected rejection");}
    internal static JsonObject Settings(string source="Camera A")=>OutputConfiguration.Validate(OutputConfiguration.Default(new(1,"0x123",source,"silent"),"usb"));
    public static async Task Run(Func<string,Func<Task>,Task> test) {
        await test("Production mode reports actual output health independently of emergency assignments",()=>{
            var dashboard=new DashboardModel();dashboard.SetPlan(new(){["paths"]=new JsonArray(new JsonObject{["slot"]=1,["source"]="Emergency",["device"]="0x123",["audioMode"]="silent"})});
            dashboard.Apply(new(){["instanceId"]="host",["outputs"]=new JsonArray(new ConfigurationPipe().Snapshot())});
            Require(dashboard.ProductionMode && dashboard.Readiness=="1 / 1");
            dashboard.SetMode(false);Require(dashboard.Readiness=="0 / 1");
            dashboard.SetMode(true);Require(dashboard.Outputs[0].Source=="Camera A" && dashboard.Readiness=="1 / 1");return Task.CompletedTask;
        });
        await test("group restore accepts connector cycles only among identified app workers",async()=>{
            var a=Settings();var b=Settings("Camera B");b["device"]="0x456";
            var state1=new ConfigurationPipe().Snapshot();var state2=new ConfigurationPipe().Snapshot();state2["deviceId"]="0x456";state2["workerInstanceId"]="second";
            var targets=new Dictionary<int,JsonObject>{{1,b},{2,a}};var states=new Dictionary<int,JsonObject?>{{1,state1},{2,state2}};
            var inventory=await new FakeInventory().Get(default);var devices=inventory["decklink"]!["devices"]!.AsArray();devices[0]!["busyFlags"]=2;
            var other=(JsonObject)devices[0]!.DeepClone();other["persistentId"]="0x456";devices.Add(other);
            Require(OutputManager.ValidateGroup(targets,states,inventory).SequenceEqual(new[]{1,2}));
            states[2]=null;await Reject(()=>Task.FromResult(OutputManager.ValidateGroup(targets,states,inventory)));
            states[2]=state2;other["deviceGroupId"]="0x86346c40";await Reject(()=>Task.FromResult(OutputManager.ValidateGroup(targets,states,inventory)));
            other["deviceGroupId"]="0x100";state2["backend"]="simulation";await Reject(()=>Task.FromResult(OutputManager.ValidateGroup(targets,states,inventory)));
        });
        await test("audio policy and bounded settings reject invalid devices, addresses, channels and delay",async()=>{
            Require(Settings()["audioMode"]!.GetValue<string>()=="silent");
            foreach(var (key,value) in new (string,JsonNode)[]{("left",JsonValue.Create(0)!),("delayMs",JsonValue.Create(501)!),("gainDb",JsonValue.Create(13)!),("device",JsonValue.Create("index:0")!),("sourceUrl",JsonValue.Create("https://example.test")!)}) {
                var settings=Settings();settings[key]=value;await Reject(()=>Task.FromResult(OutputConfiguration.Validate(settings)));
            }
            var remote=Settings();remote["sourceUrl"]="192.0.2.1:5961";Require(OutputConfiguration.Validate(remote)["sourceUrl"]!.GetValue<string>()=="192.0.2.1:5961");
            remote["sourceUrl"]="NDI-HX-SONY-PTZ-V1://192.0.2.1";Require(OutputConfiguration.Validate(remote)["sourceUrl"]!.GetValue<string>()=="NDI-HX-SONY-PTZ-V1://192.0.2.1");
        });
        await test("manual discovery validates addresses and preserves groups",async()=>{
            var result=Discovery.Validate(new(){["extraIps"]="192.0.2.1,192.0.2.1\n2001:db8::1",["groups"]="Public,Remote"});
            Require(result["extraIps"]!.GetValue<string>()=="192.0.2.1,2001:db8::1");
            await Reject(()=>Task.FromResult(Discovery.Validate(new(){["extraIps"]="192.0.2.1; & bad-command"})));
        });
        await test("native floating point settings compare by value across JSON number spelling",()=>{
            var expected=Settings();var actual=new ConfigurationPipe().Snapshot();
            actual["settings"]!["gainDb"]=JsonNode.Parse("0.0");
            Require(OutputConfiguration.Matches(expected,actual));
            actual["settings"]!["gainDb"]=JsonNode.Parse("-3.0");
            Require(!OutputConfiguration.Matches(expected,actual));return Task.CompletedTask;
        });
        await test("reserved, occupied, duplicate and unavailable hardware cannot be selected",async()=>{
            var inventory=new FakeInventory();
            foreach(var field in new[]{"reserved","busy","duplicate","audio"}) {
                var data=await inventory.Get(default);var target=Settings();var others=Array.Empty<JsonObject>();
                if(field=="reserved")data["decklink"]!["devices"]![0]!["deviceGroupId"]="0x86346c40";
                if(field=="busy")data["decklink"]!["devices"]![0]!["busyFlags"]=2;
                if(field=="duplicate")others=[Settings()];
                if(field=="audio"){target["audioMode"]="fixed";target["audioEndpoint"]="missing";}
                await Reject(()=>{Inventory.ValidateTarget(target,data,null,others);return Task.CompletedTask;});
            }
        });
        await test("silent commissioned outputs are ready without a capture device",()=>{
            var output=new OutputModel(2);var state=new ConfigurationPipe().Snapshot();state["slot"]=2;
            state["audio"]=new JsonObject{["mode"]="silent",["muted"]=true,["ageMs"]=2147483647};
            var path=new JsonObject{["source"]="Camera A",["device"]="0x123",["audioMode"]="silent"};
            output.Update(state,path,true,true);Require(output.PlanReady("usb"));state["audio"]!["mode"]="fixed";output.Update(state,path,true,true);Require(!output.PlanReady("usb"));return Task.CompletedTask;
        });
        var root=Path.Combine(Path.GetTempPath(),"NDIMachine-tests-"+Guid.NewGuid());Directory.CreateDirectory(root);
        var path=Path.Combine(root,"commission.json");
        File.WriteAllText(path,"""{"schemaVersion":1,"audio":{"endpointId":"usb","policy":"fixed","backend":"wasapi","channels":[1,2],"calibration":"unity"},"outputs":[{"slot":1,"device":"0x123","source":"Camera A","audioMode":"silent"}],"videohub":{"paths":[{"slot":1,"destination":33,"normalInput":9,"backupInput":6}]}}""");
        try {
            var commission=new Commission(path,root);
            await test("physical ATEM audio destination is preserved and invalid maps are rejected",async()=>{
                var original=File.ReadAllText(path);var configured=JsonNode.Parse(original)!;
                try {
                    configured["videohub"]!["paths"]![0]!["atemInput"]=3;
                    configured["outputs"]![0]!["audioMode"]="fixed";
                    File.WriteAllText(path,configured.ToJsonString());var mapped=new Commission(path,root);
                    Require(mapped.Plan["paths"]![0]!["atemInput"]!.GetValue<int>()==3);
                    var dashboard=new DashboardModel();dashboard.SetPlan(mapped.Plan);Require(dashboard.NetworkAudioSummary.Contains("Input 3 only"));
                    configured["videohub"]!["paths"]![0]!["atemInput"]=1;
                    configured["audio"]!["networkAtemInput"]=3;
                    File.WriteAllText(path,configured.ToJsonString());var explicitAudio=new Commission(path,root);
                    Require(explicitAudio.Plan["paths"]![0]!["atemInput"]!.GetValue<int>()==1);
                    Require(explicitAudio.Plan["paths"]![0]!["backupInput"]!.GetValue<int>()==6);
                    Require(explicitAudio.Plan["networkAudioInput"]!.GetValue<int>()==3);
                    dashboard.SetPlan(explicitAudio.Plan);Require(dashboard.NetworkAudioSummary.Contains("Input 3 only"));
                    configured["audio"]!["networkAtemInput"]=9;File.WriteAllText(path,configured.ToJsonString());
                    await Reject(()=>Task.FromResult(new Commission(path,root)));
                    configured["audio"]!["networkAtemInput"]=3;
                    configured["videohub"]!["paths"]![0]!["atemInput"]=9;File.WriteAllText(path,configured.ToJsonString());
                    await Reject(()=>Task.FromResult(new Commission(path,root)));
                }finally{File.WriteAllText(path,original);}
            });
            await test("supervisor snapshots detach worker replies and can be sampled repeatedly",async()=>{
                using var supervisor=new Supervisor(commission,new ConfigurationPipe(),new Inventory(commission));
                for(var i=0;i<2;i++) {
                    var snapshot=await supervisor.State("snapshot-"+i,default);
                    Require(snapshot["outputs"]!.AsArray().Count==1);
                    Require(snapshot["outputs"]![0]!["source"]!.GetValue<string>()=="Camera A");
                    Require(snapshot["outputs"]![0]!["desiredSettings"]!["audioMode"]!.GetValue<string>()=="silent");
                }
            });
            await test("general output changes commit once and confirm displayed settings",async()=>{
                var pipe=new ConfigurationPipe();var manager=new OutputManager(commission,pipe,new FakeInventory());var sent=false;
                await manager.Apply(1,Settings("Camera B"),pipe.Expected(),true,()=>sent=true,default);
                Require(sent && pipe.Commits==1 && pipe.Reads>=3 && pipe.Current["source"]!.GetValue<string>()=="Camera B");
            });
            await test("group restores custom sources once and rejects stale expectations before writes",async()=>{
                var pipe=new ConfigurationPipe();var manager=new OutputManager(commission,pipe,new FakeInventory());var targets=new Dictionary<int,JsonObject>{{1,Settings("Camera B")}};
                var expected=new JsonObject{["1"]=pipe.Expected()};
                await manager.ApplyGroup(targets,expected,()=>{},default);Require(pipe.Commits==1 && pipe.Current["source"]!.GetValue<string>()=="Camera B");
                await Reject(()=>manager.ApplyGroup(new Dictionary<int,JsonObject>{{1,Settings()}},expected,()=>{},default));Require(pipe.Commits==1);
            });
            await test("general output preparation failure and revision races preserve old settings",async()=>{
                foreach(var mutation in new[]{"prepare","revision","epoch"}) {
                    var pipe=new ConfigurationPipe{Failure=mutation};var manager=new OutputManager(commission,pipe,new FakeInventory());
                    await Reject(()=>manager.Apply(1,Settings("Camera B"),pipe.Expected(),true,()=>{},default));
                    Require(pipe.Commits==0 && pipe.Current["source"]!.GetValue<string>()=="Camera A");
                }
            });
            await test("lost general configuration acknowledgement is never replayed",async()=>{
                var pipe=new ConfigurationPipe{Failure="ack"};var manager=new OutputManager(commission,pipe,new FakeInventory());var sent=false;
                await Reject(()=>manager.Apply(1,Settings("Camera B"),pipe.Expected(),true,()=>sent=true,default));Require(sent && pipe.Commits==1);
            });
            await test("background recovery never replaces an existing custom configuration",async()=>{
                var pipe=new ConfigurationPipe();var manager=new OutputManager(commission,pipe,new FakeInventory());
                await Reject(()=>manager.Apply(1,Settings("Camera B"),null,false,()=>{},default));Require(pipe.Prepares==0 && pipe.Commits==0);
            });
        }finally{File.Delete(path);Directory.Delete(root);}
    }
    private sealed class FakeInventory:IInventory {
        public Task<JsonObject> Get(CancellationToken token)=>Task.FromResult(JsonNode.Parse("""{"decklink":{"devices":[{"persistentId":"0x123","deviceGroupId":"0x100","supports1080p2997Uyvy":true,"busyFlags":0}]},"audio":[]}""")!.AsObject());
    }
    private sealed class ConfigurationPipe:IWorkerPipe {
        public JsonObject Current=Settings();private JsonObject? candidate;
        public string Failure="",Epoch="worker";public long Revision=1;public int Prepares,Commits,Reads;
        public JsonObject Expected()=>JsonNode.Parse(new JsonObject{["expectedWorkerInstanceId"]=Epoch,["expectedRevision"]=Revision}.ToJsonString())!.AsObject();
        public JsonObject Snapshot()=>JsonNode.Parse(new JsonObject{["slot"]=1,["deviceId"]="0x123",["source"]=Current["source"]!.DeepClone(),["backend"]="hardware",["running"]=true,["workerInstanceId"]=Epoch,["configRevision"]=Revision,["committedOperationId"]=Commits>0?"candidate":"old",["scheduleAgeMs"]=10,["videoAgeMs"]=10,["health"]="healthy",["fallback"]=false,["format"]="1920x1080p30000/1001",["settings"]=Current.DeepClone()}.ToJsonString())!.AsObject();
        public Task<JsonObject> State(int slot,CancellationToken cancellation=default){Reads++;return Task.FromResult(new JsonObject{["state"]=Snapshot()});}
        public Task<JsonObject> Call(string pipe,JsonObject request,int timeoutMs=700,CancellationToken cancellation=default) {
            if(request["method"]!.GetValue<string>()=="prepare") {
                Prepares++;if(Failure=="prepare")throw new IOException("Source missing");
                candidate=(JsonObject)request["settings"]!.DeepClone();if(Failure=="revision")Revision++;if(Failure=="epoch")Epoch="replacement";
                return Task.FromResult(new JsonObject{["preparedOperationId"]="candidate"});
            }
            if(request["method"]!.GetValue<string>()!="commit")throw new Exception("Unexpected hardware command");
            Commits++;Current=candidate!;Revision++;if(Failure=="ack")throw new IOException("ACK lost");
            return Task.FromResult(new JsonObject{["operationId"]="candidate"});
        }
    }
}

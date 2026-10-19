using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using NDIMachine.Desktop;

internal static class ProductionRoutingChecks
{
    private static JsonObject Routing()=>new(){["schemaVersion"]=1,["instanceId"]=Guid.NewGuid().ToString(),["ready"]=true,["busy"]=false,
        ["defaults"]=new JsonArray(new[]{9,10,11,12,18,17,33,34}.Select((source,index)=>(JsonNode?)new JsonObject{["input"]=index+1,["destination"]=index+33,["source"]=source}).ToArray())};
    private static void Require(bool condition){if(!condition)throw new Exception("Desktop routing assertion failed");}
    private static async Task Reject(Func<Task> action){try{await action();}catch(Exception){return;}throw new Exception("Expected routing rejection");}
    public static async Task Run(Func<string,Func<Task>,Task> test){
        await test("destination selection survives polling and defaults remain available for idle sources",()=>{
            var model=new DashboardModel();var output=model.Outputs.Single(o=>o.Slot==1);
            var map=new JsonArray(new JsonObject{["slot"]=1,["atemInput"]=3,["hubInput"]=8,["destinations"]=new JsonArray()});
            model.SetRouting(map);model.SetProductionRouting(Routing());
            Require(output.RouteTarget==3 && output.CanReturnDefault && !output.CanSendToAtem && output.DefaultRouteLabel.Contains("11 → 35"));
            output.RouteTarget=4;model.SetRouting(map);Require(output.RouteTarget==4 && output.DefaultRouteLabel.Contains("12 → 36"));
            model.SetBusy(true,"Routing");Require(!output.CanReturnDefault);model.SetBusy(false,"");Require(output.CanReturnDefault);
            model.SetProductionRouting(null);Require(!output.CanReturnDefault && !output.CanSendToAtem);
            return Task.CompletedTask;
        });
        await test("routing client rejects stale or malformed defaults and sends a failed command only once",async()=>{
            var handler=new RoutingHttp(Routing());using var client=new ProductionClient(handler);
            var state=await client.State();Require(state["defaults"]!.AsArray().Count==8);
            handler.Stale=true;await Reject(()=>client.State());handler.Stale=false;
            handler.Fail=true;await Reject(()=>client.Send("return-defaults",new(){["inputs"]=new JsonArray(JsonValue.Create(3))},state));Require(handler.Posts==1);
            handler.Fail=false;handler.State["defaults"]![0]!["destination"]=40;await Reject(()=>client.State());
        });
        await test("routing client rejects a replaced module confirmation",async()=>{
            var handler=new RoutingHttp(Routing()){Replace=true};using var client=new ProductionClient(handler);
            var state=await client.State();await Reject(()=>client.Send("return-defaults",new(){["inputs"]=new JsonArray(JsonValue.Create(3))},state));Require(handler.Posts==1);
        });
    }
    private sealed class RoutingHttp(JsonObject state):HttpMessageHandler {
        public JsonObject State=state;public bool Stale,Fail,Replace;public int Posts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
            JsonObject result;
            if(request.Method==HttpMethod.Get){result=(JsonObject)State.DeepClone();result["nonce"]=Stale?"old":request.RequestUri!.Query.Split('=')[1];}
            else {Posts++;Require(request.Headers.Contains("Idempotency-Key"));if(Fail)throw new HttpRequestException("Lost response");result=new(){["confirmed"]=true,["instanceId"]=Replace?Guid.NewGuid().ToString():OutputModel.Text(State,"instanceId")};}
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(result.ToJsonString())});
        }
    }
}

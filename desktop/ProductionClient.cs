using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace NDIMachine.Desktop;

public sealed record AtemDestination(int Input) { public string Label=>$"ATEM Input {Input}"; }
public sealed class ProductionClient : IDisposable
{
    private readonly EngineClient client;
    public ProductionClient(HttpMessageHandler? handler=null) {client=new EngineClient(handler,"http://127.0.0.1:9862/api/v1/");}
    public async Task<JsonObject> State(CancellationToken token=default) {
        var nonce=Guid.NewGuid().ToString();
        var state=await client.Request(HttpMethod.Get,"routing?nonce="+nonce,token:token);
        if(state["schemaVersion"]?.GetValue<int>()!=1 || OutputModel.Text(state,"nonce")!=nonce || !Guid.TryParse(OutputModel.Text(state,"instanceId"),out _) ||
            state["defaults"] is not JsonArray defaults || defaults.Count!=8 || defaults.OfType<JsonObject>().Count()!=8 ||
            !defaults.OfType<JsonObject>().Select(d=>d["input"]?.GetValue<int>() ?? 0).Order().SequenceEqual(Enumerable.Range(1,8)) ||
            defaults.OfType<JsonObject>().Any(d=>d["destination"]?.GetValue<int>()!=d["input"]!.GetValue<int>()+32 || d["source"]?.GetValue<int>() is not (>=1 and <=40)))
            throw new InvalidDataException("Update the NDI Machine Companion module to 0.6.0 for desktop routing.");
        return state;
    }
    public async Task Send(string path,JsonObject request,JsonObject routing,CancellationToken token=default) {
        if(routing["ready"]?.GetValue<bool>()!=true || routing["busy"]?.GetValue<bool>()==true)throw new InvalidOperationException("Videohub / ATEM routing is unavailable or busy.");
        request=(JsonObject)request.DeepClone();request["schemaVersion"]=1;request["routerInstanceId"]=OutputModel.Text(routing,"instanceId");
        var result=await client.Request(HttpMethod.Post,path,request,Guid.NewGuid().ToString(),150000,token);
        if(result["confirmed"]?.GetValue<bool>()!=true || OutputModel.Text(result,"instanceId")!=OutputModel.Text(routing,"instanceId"))
            throw new InvalidOperationException("Routing confirmation is unknown. Check the live routes; the command was not retried.");
    }
    public void Dispose()=>client.Dispose();
}

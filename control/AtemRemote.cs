using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

namespace NDIMachine.Control;

// One official SDK connection, shared by cloud discovery and explicit routing.
// Never replays a write after timeout, disconnect or process replacement.
public sealed class AtemRemote(Commission commission) : IDisposable
{
    private readonly SemaphoreSlim gate=new(1);
    private Process? process;
    private string host="", epoch="";
    private Task<string>? stderr;
    private bool uncertain;
    public async Task<JsonObject> Call(JsonObject request,CancellationToken token) {
        var target=request["host"]?.GetValue<string>() ?? "";
        if(!IPAddress.TryParse(target,out var address) || IPAddress.IsLoopback(address) || address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidDataException("ATEM IPv4 address required");
        if(!await gate.WaitAsync(0,token))throw new InvalidOperationException("ATEM SDK request in progress");
        try {
            var method=request["method"]?.GetValue<string>() ?? "snapshot";
            if(method is not ("snapshot" or "input"))throw new InvalidDataException("Unsupported ATEM command");
            if(uncertain && process is {HasExited:false})throw new InvalidOperationException("ATEM SDK reply timed out; restart engine before another command");
            if(process==null || process.HasExited) {
                if(method!="snapshot")throw new InvalidOperationException("Read fresh ATEM state before changing inputs");
                process?.Dispose();host=target;epoch=Guid.NewGuid().ToString();uncertain=false;
                var start=new ProcessStartInfo(Path.Combine(commission.Root,"build","Release","atem-remote.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
                start.ArgumentList.Add(target);start.ArgumentList.Add("--server");
                process=Process.Start(start) ?? throw new IOException("Cannot start ATEM SDK bridge");stderr=process.StandardError.ReadToEndAsync();
            }
            if(host!=target)throw new InvalidOperationException("ATEM host changed; restart engine to use another address");
            if(method=="input" && request["expected"]?["epoch"]?.GetValue<string>()!=epoch)throw new InvalidOperationException("ATEM connection changed; select again");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(6000);
            try {
                await process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(),timeout.Token);
                var line=await process.StandardOutput.ReadLineAsync(timeout.Token);
                if(line==null)throw new IOException("ATEM SDK disconnected: "+(stderr?.IsCompleted==true?await stderr:"No response"));
                var reply=JsonNode.Parse(line)?.AsObject() ?? throw new IOException("Invalid SDK response");
                if(reply["error"]!=null)throw new InvalidOperationException(reply["error"]!.GetValue<string>());
                reply["epoch"]=epoch;return reply;
            }catch(OperationCanceledException){uncertain=true;process.StandardInput.Close();throw new IOException("ATEM request timed out; outcome unknown and no command replayed");}
        }finally{gate.Release();}
    }
    public void Dispose(){if(process!=null){try{process.StandardInput.Close();}catch(Exception){}process.Dispose();}gate.Dispose();}
}

using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NDIMachine.Control;

public static class NativeTools
{
    public static async Task<JsonNode> Run(Commission commission, string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(Path.Combine(commission.Root,"build","Release",executable)) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=commission.Root
        };
        info.Environment["PATH"] = @"C:\Program Files\NDI\NDI 6 Runtime\v6;" + Environment.GetEnvironmentVariable("PATH");
        foreach(var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start " + executable);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(12000);
        try {
            var stdout=Read(process.StandardOutput,deadline.Token); var stderr=Read(process.StandardError,deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var text=await stdout; var error=await stderr;
            if(process.ExitCode!=0) throw new IOException(executable + ": " + error);
            return JsonNode.Parse(text) ?? throw new InvalidDataException("Empty inventory");
        } finally { if(!process.HasExited) process.Kill(); }
    }
    private static async Task<string> Read(StreamReader reader,CancellationToken token) {
        var buffer=new char[262145]; var total=0;
        while(total<buffer.Length) {
            var count=await reader.ReadAsync(buffer.AsMemory(total),token);
            if(count==0) return new string(buffer,0,total);
            total+=count;
        }
        throw new InvalidDataException("Native response exceeded 256 KiB");
    }
}

public interface IInventory {Task<JsonObject> Get(CancellationToken token);}
public sealed class Inventory(Commission commission) : IInventory
{
    private readonly SemaphoreSlim gate=new(1);
    public async Task<JsonObject> Get(CancellationToken token) {
        if(!await gate.WaitAsync(0,token)) throw new InvalidOperationException("Inventory already running");
        try {
            var hardware=await NativeTools.Run(commission,"decklink-sdk-check.exe",[],token);
            var audio=await NativeTools.Run(commission,"audio-host.exe",["--list"],token);
            return new() { ["schemaVersion"]=1,["decklink"]=hardware,["audio"]=audio };
        } finally { gate.Release(); }
    }
    public static void ValidateTarget(JsonObject settings,JsonObject inventory,JsonObject? current,IEnumerable<JsonObject> otherSettings) {
        var device=settings["device"]!.GetValue<string>();
        var selected=inventory["decklink"]!["devices"]!.AsArray().OfType<JsonObject>().SingleOrDefault(d=>d["persistentId"]?.GetValue<string>()==device)
            ?? throw new InvalidOperationException("Selected DeckLink device is missing");
        if(selected["deviceGroupId"]?.GetValue<string>()=="0x86346c40") throw new InvalidOperationException("OBS Key/Fill card is reserved");
        if(selected["supports1080p2997Uyvy"]?.GetValue<bool>()!=true) throw new InvalidOperationException("Selected device does not support the ATEM output standard");
        if(current?["deviceId"]?.GetValue<string>()!=device && selected["busyFlags"]?.GetValue<int>()!=0) throw new InvalidOperationException("Selected DeckLink device is busy or ownership is unknown");
        if(otherSettings.Any(o=>o["device"]?.GetValue<string>()==device)) throw new InvalidOperationException("Another output slot owns this DeckLink device");
        if(settings["audioMode"]?.GetValue<string>()=="fixed") {
            var endpoint=inventory["audio"]!.AsArray().OfType<JsonObject>().SingleOrDefault(a=>a["id"]?.GetValue<string>()==settings["audioEndpoint"]?.GetValue<string>())
                ?? throw new InvalidOperationException("Selected Windows audio device is not active");
            var channels=endpoint["mixFormat"]?["channels"]?.GetValue<int>() ?? 0;
            if(Math.Max(settings["left"]!.GetValue<int>(),settings["right"]!.GetValue<int>())>channels) throw new InvalidOperationException("Selected audio channel is unavailable on this device");
        }
    }
}

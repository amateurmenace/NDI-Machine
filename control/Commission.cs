using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NDIMachine.Control;

public sealed record OutputAssignment(int Slot, string Device, string Source, string AudioMode = "fixed");
public sealed class Commission
{
    public string FilePath { get; }
    public string Root { get; }
    public string Fingerprint { get; }
    public string Endpoint { get; }
    public bool RecoverDanteUsb { get; }
    private sealed record AudioBinding(string Endpoint, bool Available, string Message);
    private AudioBinding binding = new("", false, "Not resolved");
    public string EffectiveEndpoint => binding.Endpoint;
    public bool AudioAvailable => !RecoverDanteUsb || binding.Available;
    public JsonObject AudioState() {
        var value=binding;
        return new(){["configuredEndpointId"]=Endpoint,["resolvedEndpointId"]=value.Endpoint,["available"]=value.Available,["message"]=value.Message};
    }
    public void ResolveAudio(JsonArray inventory) {
        if(!RecoverDanteUsb)return;
        try {
            var resolved=AudioEndpointResolver.Resolve(Endpoint,inventory);
            binding=new(resolved,true,resolved.Equals(Endpoint,StringComparison.OrdinalIgnoreCase)
                ? "Dante USB receive 1/2 matched the saved ID"
                : "Dante USB receive 1/2 recovered after Windows changed its device ID");
        } catch(Exception error){binding=new(Endpoint,false,error.Message);throw;}
    }
    public async Task RefreshAudio(CancellationToken token) {
        if(!RecoverDanteUsb)return;
        try {ResolveAudio((await NativeTools.Run(this,"audio-host.exe",["--list"],token)).AsArray());}
        catch(Exception error){binding=new(Endpoint,false,error.Message);throw;}
    }
    public OutputAssignment[] Outputs { get; }
    public JsonObject Plan { get; }
    public Commission(string filePath, string root)
    {
        FilePath = Path.GetFullPath(filePath); Root = Path.GetFullPath(root);
        var text = File.ReadAllText(FilePath);
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var config = JsonNode.Parse(text)!.AsObject();
        if (config["schemaVersion"]?.GetValue<int>() != 1) throw new InvalidDataException("Commission schema must be 1");
        var audio = config["audio"]!;
        Endpoint = audio["endpointId"]!.GetValue<string>();
        RecoverDanteUsb=audio["endpointRecovery"]?.GetValue<string>()=="unique-dante-usb";
        if(audio["endpointRecovery"]!=null && (!RecoverDanteUsb || !AudioEndpointResolver.IsDanteUsbReceive(audio["displayName"]?.GetValue<string>() ?? "")))
            throw new InvalidDataException("Endpoint recovery requires the explicitly commissioned Dante USB receive device");
        binding=new(Endpoint,!RecoverDanteUsb,RecoverDanteUsb ? "Dante USB identity not resolved yet" : "Fixed commissioned endpoint");
        if (Endpoint.Length is < 1 or > 500 || audio["policy"]?.GetValue<string>() != "fixed" ||
            audio["backend"]?.GetValue<string>() != "wasapi" || audio["channels"]?.ToJsonString() != "[1,2]" ||
            audio["calibration"]?.GetValue<string>() != "unity")
            throw new InvalidDataException("This build requires explicit fixed WASAPI stereo 1/2 and unity PCM calibration");
        Outputs = config["outputs"]!.AsArray().Select(o => new OutputAssignment(o!["slot"]!.GetValue<int>(),
            o["device"]!.GetValue<string>().ToLowerInvariant(), o["source"]!.GetValue<string>(), o["audioMode"]?.GetValue<string>() ?? "fixed")).ToArray();
        if (Outputs.Length is < 1 or > 4 || Outputs.Any(o => o.Slot is < 1 or > 4 ||
                !Regex.IsMatch(o.Device, "^0x[0-9a-f]{1,16}$") || o.AudioMode is not ("fixed" or "silent") || Encoding.UTF8.GetByteCount(o.Source) is < 1 or > 512) ||
            Outputs.Select(o => o.Slot).Distinct().Count() != Outputs.Length ||
            Outputs.Select(o => o.Device).Distinct().Count() != Outputs.Length)
            throw new InvalidDataException("Invalid or duplicate output assignments");
        var paths = new JsonArray();
        foreach (var assignment in Outputs)
        {
            var route = config["videohub"]!["paths"]!.AsArray().Single(p => p!["slot"]!.GetValue<int>() == assignment.Slot)!;
            var destination = route["destination"]!.GetValue<int>();
            var normal = route["normalInput"]!.GetValue<int>(); var backup = route["backupInput"]!.GetValue<int>();
            if (new[] { destination, normal, backup }.Any(n => n is < 1 or > 4096) || normal == backup)
                throw new InvalidDataException("Invalid commissioned Videohub ports");
            paths.Add(new JsonObject { ["slot"] = assignment.Slot, ["destination"] = destination,
                ["normalInput"] = normal, ["backupInput"] = backup, ["device"] = assignment.Device, ["source"] = assignment.Source });
            if (config["outputs"]!.AsArray().Any(o=>o?["slot"]?.GetValue<int>()==assignment.Slot && o["audioMode"]!=null)) paths.Last()!["audioMode"] = assignment.AudioMode;
            if(route["atemInput"] is JsonNode atemInput) {
                if(atemInput.GetValue<int>() is <1 or >8)throw new InvalidDataException("Commission an ATEM SDI input from 1 through 8");
                paths.Last()!["atemInput"]=atemInput.DeepClone();
            }
        }
        if (paths.Select(p => p!["destination"]!.GetValue<int>()).Distinct().Count() != paths.Count ||
            paths.Select(p => p!["backupInput"]!.GetValue<int>()).Distinct().Count() != paths.Count)
            throw new InvalidDataException("Duplicate commissioned Videohub destinations or backups");
        var atemInputs=paths.Where(p=>p!["atemInput"]!=null).Select(p=>p!["atemInput"]!.GetValue<int>()).ToArray();
        if(atemInputs.Distinct().Count()!=atemInputs.Length)throw new InvalidDataException("Duplicate commissioned ATEM input");
        Plan = new() { ["schemaVersion"] = 1, ["name"] = "Fiber to Network", ["audioEndpointId"] = Endpoint, ["paths"] = paths };
        if(audio["networkAtemInput"] is JsonNode networkInput) {
            if(networkInput.GetValue<int>() is <1 or >8 || Outputs.Count(o=>o.AudioMode=="fixed")!=1)
                throw new InvalidDataException("An explicit network ATEM audio input requires one fixed-audio output and input 1 through 8");
            Plan["networkAudioInput"]=networkInput.DeepClone();
        }
    }
    public void ValidatePlan(JsonObject candidate)
    {
        // The API cannot claim arbitrary hardware supplied by a client. Plans must
        // exactly match locally commissioned identities, channels and crosspoints.
        if (!JsonNode.DeepEquals(candidate, Plan)) throw new InvalidDataException("Plan differs from the local commissioned plan");
    }
}

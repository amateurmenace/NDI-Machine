using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NDIMachine.Control;

public sealed record OutputAssignment(int Slot, string Device, string Source);
public sealed class Commission
{
    public string FilePath { get; }
    public string Root { get; }
    public string Fingerprint { get; }
    public string Endpoint { get; }
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
        if (Endpoint.Length is < 1 or > 500 || audio["policy"]?.GetValue<string>() != "fixed" ||
            audio["backend"]?.GetValue<string>() != "wasapi" || audio["channels"]?.ToJsonString() != "[1,2]" ||
            audio["calibration"]?.GetValue<string>() != "unity")
            throw new InvalidDataException("This build requires explicit fixed WASAPI stereo 1/2 and unity PCM calibration");
        Outputs = config["outputs"]!.AsArray().Select(o => new OutputAssignment(o!["slot"]!.GetValue<int>(),
            o["device"]!.GetValue<string>().ToLowerInvariant(), o["source"]!.GetValue<string>())).ToArray();
        if (Outputs.Length is < 1 or > 4 || Outputs.Any(o => o.Slot is < 1 or > 4 ||
                !Regex.IsMatch(o.Device, "^0x[0-9a-f]{1,16}$") || Encoding.UTF8.GetByteCount(o.Source) is < 1 or > 512) ||
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
        }
        if (paths.Select(p => p!["destination"]!.GetValue<int>()).Distinct().Count() != paths.Count ||
            paths.Select(p => p!["backupInput"]!.GetValue<int>()).Distinct().Count() != paths.Count)
            throw new InvalidDataException("Duplicate commissioned Videohub destinations or backups");
        Plan = new() { ["schemaVersion"] = 1, ["name"] = "Fiber to Network", ["audioEndpointId"] = Endpoint, ["paths"] = paths };
    }
    public void ValidatePlan(JsonObject candidate)
    {
        // The API cannot claim arbitrary hardware supplied by a client. Plans must
        // exactly match locally commissioned identities, channels and crosspoints.
        if (!JsonNode.DeepEquals(candidate, Plan)) throw new InvalidDataException("Plan differs from the local commissioned plan");
    }
}

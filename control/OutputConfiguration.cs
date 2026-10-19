using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NDIMachine.Control;

public static class OutputConfiguration
{
    public static JsonObject Default(OutputAssignment assignment, string endpoint) => new() {
        ["device"] = assignment.Device, ["source"] = assignment.Source, ["sourceUrl"] = "",
        ["bandwidth"] = "highest", ["sizing"] = "fit", ["audioMode"] = assignment.AudioMode,
        ["audioEndpoint"] = assignment.AudioMode == "fixed" ? endpoint : "", ["left"] = 1, ["right"] = 2,
        ["gainDb"] = 0.0, ["muted"] = false, ["delayMs"] = 0
    };
    public static JsonObject Validate(JsonObject value)
    {
        var result = (JsonObject)value.DeepClone();
        string Text(string key) => result[key]?.GetValue<string>() ?? "";
        var source = Text("source"); var endpoint = Text("audioEndpoint");
        if (string.IsNullOrWhiteSpace(source) || source != source.Trim() || Encoding.UTF8.GetByteCount(source) > 512 || source.Any(char.IsControl))
            throw new InvalidDataException("An exact NDI source name is required (up to 512 UTF-8 bytes)");
        if (!Regex.IsMatch(Text("device"), "^0x[0-9a-fA-F]{1,16}$")) throw new InvalidDataException("A persistent DeckLink device ID is required");
        result["device"] = Text("device").ToLowerInvariant();
        if (Text("bandwidth") is not ("highest" or "lowest") || Text("sizing") is not ("fit" or "fill" or "stretch") ||
            Text("audioMode") is not ("fixed" or "ndi" or "silent")) throw new InvalidDataException("Invalid receiver or audio mode");
        var url = Text("sourceUrl");
        if(Regex.IsMatch(url,@":5353$"))throw new InvalidDataException("Port 5353 is NDI discovery, not a media address. Refresh sources and choose the sender, or leave its address blank for discovery.");
        // The SDK finder also returns vendor HX transport identifiers. Preserve
        // those exact addresses; they are not HTTP URLs or file paths.
        var transport=Regex.IsMatch(url,"^ndi-hx-[a-z0-9-]+://",RegexOptions.IgnoreCase) ? url : "ndi://"+url;
        if (url.Length != 0 && (url.Length>512 || !Uri.TryCreate(transport, UriKind.Absolute, out var address) ||
            !IPAddress.TryParse(address.Host.Trim('[', ']'), out _) || address.UserInfo.Length != 0 || address.AbsolutePath != "/" ||
            address.Query.Length != 0 || address.Fragment.Length != 0)) throw new InvalidDataException("Use an IP:port or a discovered NDI HX transport address");
        if (endpoint.Length > 500 || endpoint.Any(char.IsControl) || (Text("audioMode") == "fixed" && endpoint.Length == 0))
            throw new InvalidDataException("Select an explicit Windows audio endpoint");
        var left = result["left"]?.GetValue<int>() ?? 0; var right = result["right"]?.GetValue<int>() ?? 0;
        var gain = result["gainDb"]?.GetValue<double>() ?? double.NaN; var delay = result["delayMs"]?.GetValue<int>() ?? -1;
        if (left is < 1 or > 64 || right is < 1 or > 64 || !double.IsFinite(gain) || gain is < -60 or > 12 || delay is < 0 or > 500 || result["muted"] is null)
            throw new InvalidDataException("Audio requires channels 1-64, gain -60 to +12 dB and delay 0-500 ms");
        _ = result["muted"]!.GetValue<bool>();
        result["audioKey"] = Text("audioMode") == "fixed" ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.ToLowerInvariant() + $"|{left}|{right}"))) : "";
        if (Text("audioMode") != "fixed") result["audioEndpoint"] = "";
        return result;
    }
    public static bool Matches(JsonObject settings, JsonObject state) =>
        state["deviceId"]?.GetValue<string>().ToLowerInvariant() == settings["device"]?.GetValue<string>() &&
        state["settings"] is JsonObject actual && settings.Where(pair => pair.Key != "device").All(pair =>
            pair.Key == "gainDb" ? actual[pair.Key] != null && pair.Value!.GetValue<double>() == actual[pair.Key]!.GetValue<double>() :
            JsonNode.DeepEquals(pair.Value, actual[pair.Key]));
}

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NDIMachine.Control;

public static class AudioEndpointResolver
{
    // Windows may add an enumeration prefix after USB reconnect/reboot. Match
    // this capture product only, never DVS, a render endpoint or Windows default.
    public static bool IsDanteUsbReceive(string name) => Regex.IsMatch(name,
        @"^Dante USB Receive \((?:[0-9]+- )?Dante USB I/O Module\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Resolve(string configured, JsonArray inventory)
    {
        var active=inventory.OfType<JsonObject>().Where(d=>d["state"]?.GetValue<int>()==1).ToArray();
        var exact=active.Where(d=>string.Equals(d["id"]?.GetValue<string>(),configured,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(exact.Length==1 && IsDanteUsbReceive(exact[0]["name"]?.GetValue<string>() ?? ""))return Stereo(exact[0]);
        if(exact.Length!=0)throw new InvalidOperationException("Commissioned Dante USB identity is ambiguous or belongs to another device");
        var candidates=active.Where(d=>IsDanteUsbReceive(d["name"]?.GetValue<string>() ?? "")).ToArray();
        if(candidates.Length!=1)throw new InvalidOperationException(candidates.Length==0
            ? "Dante USB receive device is not active. Connect the commissioned USB module and retry PRIME."
            : "Multiple Dante USB receive devices are active. Select the commissioned device explicitly; automatic recovery refused.");
        return Stereo(candidates[0]);
    }

    private static string Stereo(JsonObject device)
    {
        if((device["mixFormat"]?["channels"]?.GetValue<int>() ?? 0)<2 || string.IsNullOrWhiteSpace(device["id"]?.GetValue<string>()))
            throw new InvalidOperationException("Dante USB receive channels 1/2 are unavailable");
        return device["id"]!.GetValue<string>();
    }
}

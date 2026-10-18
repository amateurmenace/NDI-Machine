using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NDIMachine.Desktop;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}

public sealed class OutputModel(int slot) : Observable
{
    public System.Windows.Media.ImageSource? Preview { get; private set; }
    public string PreviewCaption { get; private set; } = "Waiting for preview";
    public void SetPreview(System.Windows.Media.ImageSource? image, string caption)
    {
        if (Preview == image && PreviewCaption == caption) return;
        Preview = image; PreviewCaption = caption; Changed();
    }
    public int Slot { get; } = slot;
    public string Number => Slot.ToString("00");
    public string Name => $"NDI Machine {Slot}";
    public JsonObject? State { get; private set; }
    public JsonObject? Path { get; private set; }
    public bool Fresh { get; private set; }
    public bool CanRoute { get; private set; }
    private string selectedSource = "";
    public string SelectedSource { get => selectedSource; set { selectedSource = value; Changed(); } }
    public ObservableCollection<string> Sources { get; } = new();
    public string Source => Text(State, "source", "Waiting for worker");
    public string ExpectedSource => Text(Path, "source", "Not commissioned");
    public string Device => Text(State, "deviceId", Text(Path, "device", "Unassigned"));
    public string Format => Text(State, "format") == "1920x1080p30000/1001" ? "1080p 29.97  /  UYVY" : Text(State, "format", "Format unknown");
    public long Revision => NumberAt(State, "configRevision");
    public bool SoftwareHealthy => Fresh && State?["backend"]?.GetValue<string>() == "hardware" && State?["running"]?.GetValue<bool>() == true &&
        Text(State, "health") == "healthy" && Revision > 0 && Text(State, "committedOperationId").Length > 0 &&
        NumberAt(State, "videoAgeMs", long.MaxValue) is >= 0 and < 500 && NumberAt(State, "scheduleAgeMs", long.MaxValue) is >= 0 and < 500;
    public bool AudioFresh => Fresh && NumberAt(State?["audio"] as JsonObject, "ageMs", long.MaxValue) is >= 0 and < 500 && State?["audio"]?["muted"]?.GetValue<bool>() == false;
    public bool PlanReady(string? endpoint) => State?["emergencyConfigured"]?.GetValue<bool>()!=false && SoftwareHealthy && Source == ExpectedSource &&
        Device.Equals(Text(Path, "device"), StringComparison.OrdinalIgnoreCase) && Format == "1080p 29.97  /  UYVY" &&
        State?["fallback"]?.GetValue<bool>() == false && (Text(Path,"audioMode")=="silent" ?
            Text(State?["audio"] as JsonObject,"mode")=="silent" && State?["audio"]?["muted"]?.GetValue<bool>()==true :
            AudioFresh && Text(State?["audio"] as JsonObject,"endpointId").Equals(endpoint,StringComparison.OrdinalIgnoreCase) &&
            Text(State?["audio"] as JsonObject,"mode")=="fixed" && State?["audio"]?["channels"]?.ToJsonString()=="[1,2]");
    public string Status => !Fresh ? "STALE" : SoftwareHealthy ? "RUNNING" : State?["fallback"]?.GetValue<bool>() == true ? "FALLBACK" : "NOT READY";
    public string StatusColor => !Fresh ? "#A3B1C1" : SoftwareHealthy ? "#7DE1CC" : "#F6BD75";
    public string AudioStatus => !Fresh ? "Audio unknown" : Text(State?["audio"] as JsonObject,"mode")=="silent" ? "Audio · silent" : State?["audio"]?["muted"]?.GetValue<bool>()==true ? "Audio · muted" : AudioFresh ? Text(State?["audio"] as JsonObject,"mode")=="ndi" ? "NDI audio · fresh" : "Windows audio · fresh" : "Audio not ready";
    private double receiveFps;
    private long previousReceived,previousSample;
    public string NdiStats => !Fresh || State?["ndi"] is not JsonObject ndi ? "Receiver statistics need the updated worker." :
        $"INPUT   {NumberAt(ndi,"width")} × {NumberAt(ndi,"height")}   {(NumberAt(ndi,"fpsD")>0?(double)NumberAt(ndi,"fpsN")/NumberAt(ndi,"fpsD"):0):0.###} fps\n"+
        $"ARRIVAL   {receiveFps:0.0} fps   ·   {NumberAt(ndi,"connections")} connection(s)\n"+
        $"RECEIVED   video {NumberAt(ndi,"videoReceived"):N0}   audio {NumberAt(ndi,"audioReceived"):N0}\n"+
        $"RX DROPPED   video {NumberAt(ndi,"videoDropped")}   audio {NumberAt(ndi,"audioDropped")}\n"+
        $"QUEUE   video {NumberAt(ndi,"videoQueue")}   audio {NumberAt(ndi,"audioQueue")}\n"+
        $"BANDWIDTH   {Text(ndi,"bandwidth")}   ·   codec / decode managed by NDI runtime\n"+OtherCounters;
    public string AudioStats {
        get {
            if(!Fresh || State?["audio"] is not JsonObject audio)return "Audio telemetry unavailable";
            string Db(int channel) {var value=(audio["peak"] as JsonArray)?[channel]?.GetValue<double>() ?? 0;return value>0?$"{20*Math.Log10(value):0.0} dBFS":"−∞ dBFS";}
            return $"PEAK   L {Db(0)}   R {Db(1)}\nGAIN   {audio["gainDb"] ?? JsonValue.Create(0)} dB   ·   DELAY {audio["delayMs"] ?? JsonValue.Create(0)} ms\n"+
                $"BUFFER   {audio["bufferMs"]?.GetValue<double>() ?? 0:0.0} ms   ·   CLOCK {audio["clockCorrectionPpm"]?.GetValue<double>() ?? 0:0.0} ppm\nCLIPPED SAMPLES   {audio["clippedSamples"] ?? JsonValue.Create(0)}";
        }
    }
    public string Timing => !Fresh ? "Telemetry unavailable" : $"Video {Age("videoAgeMs")}  ·  SDI {Age("scheduleAgeMs")}";
    private string Age(string key) { var age = NumberAt(State, key, long.MaxValue); return age > 9999 ? "stale" : $"{age} ms"; }
    public string Completed => Fresh ? NumberAt(State?["counters"] as JsonObject, "completed").ToString("N0") : "—";
    public string Deadlines => Fresh ? $"{NumberAt(State?["counters"] as JsonObject, "late")} late / {NumberAt(State?["counters"] as JsonObject, "dropped")} dropped" : "—";
    public string OtherCounters => $"Fallback {NumberAt(State?["counters"] as JsonObject, "fallback"):N0}  ·  Input repeats {NumberAt(State?["counters"] as JsonObject, "inputRepeats"):N0}";
    public string Mapping => Path == null ? "Mapping unavailable" : $"Fiber {NumberAt(Path, "normalInput")}   /   Network {NumberAt(Path, "backupInput")}   →   Out {NumberAt(Path, "destination")}" +
        (Path["atemInput"] is JsonNode input ? $"   →   ATEM {input}" : "");
    public string Detail => !Fresh ? "Last source shown. Commands disabled until fresh state returns." : Text(State, "error", "Edit output for device, audio and receiver controls.");
    public bool CanApply => CanRoute && Fresh && SelectedSource.Trim().Length > 0 && SelectedSource != Source && State?["running"]?.GetValue<bool>() == true;
    public void Update(JsonObject? state, JsonObject? path, bool fresh, bool canRoute)
    {
        var stamp=Stopwatch.GetTimestamp();var received=NumberAt(state?["ndi"] as JsonObject,"videoReceived");
        if(previousSample>0 && received>=previousReceived && Text(state,"workerInstanceId")==Text(State,"workerInstanceId"))receiveFps=(received-previousReceived)/Stopwatch.GetElapsedTime(previousSample,stamp).TotalSeconds;
        else receiveFps=0;
        previousReceived=received;previousSample=stamp;
        State = state; Path = path; Fresh = fresh; CanRoute = canRoute;
        if (selectedSource.Length == 0 && state != null) selectedSource = Source;
        AddSource(ExpectedSource); if (state != null) AddSource(Source);
        Changed();
    }
    public void AddSource(string value) { if (value.Length > 0 && value is not "Not commissioned" and not "Waiting for worker" && !Sources.Contains(value)) Sources.Add(value); }
    public static string Text(JsonObject? node, string key, string fallback = "") => node?[key]?.GetValue<string>() ?? fallback;
    public static long NumberAt(JsonObject? node, string key, long fallback = 0) => node?[key]?.GetValue<long>() ?? fallback;
}

public sealed class DashboardModel : Observable
{
    public bool ProductionMode {get;private set;}=true;
    public bool FailoverMode=>!ProductionMode;
    public string ModeHeading=>ProductionMode ? "Production routing" : "Failover outputs";
    public string ModeDescription=>ProductionMode ? "Choose any discovered NDI source and an available DeckLink output. Apply changes per output." : "Saved emergency assignments. The Companion failover button restores this preset before taking Network.";
    public void SetMode(bool production){ProductionMode=production;Changed();}
    public bool PreviewsEnabled { get; set; } = true;
    public ObservableCollection<OutputModel> Outputs { get; } = new(Enumerable.Range(1, 4).Select(n => new OutputModel(n)));
    public ObservableCollection<string> Events { get; } = new();
    public JsonObject? State { get; private set; }
    public JsonObject? Plan { get; private set; }
    public string NetworkAudioSummary {
        get {
            var paths=(Plan?["paths"] as JsonArray)?.OfType<JsonObject>().Where(p=>OutputModel.Text(p,"audioMode")=="fixed").ToArray();
            var input=Plan?["networkAudioInput"] ?? (paths?.Length==1 ? paths[0]["atemInput"] : null);
            return paths?.Length==1 && input!=null ? $"ZOOM DANTE · ATEM Input {input} only" : "ZOOM DANTE · audio destination needs commissioning";
        }
    }
    private long received;
    public bool Connected { get; private set; }
    public bool Busy { get; private set; }
    public bool Offline => !Connected;
    public string ConnectionLabel => Connected ? "ENGINE CONNECTED" : "ENGINE OFFLINE / STALE";
    public string ConnectionColor => Connected ? "#7DE1CC" : "#F6BD75";
    public string Host => OutputModel.Text(State, "instanceId");
    public string Notice { get; private set; } = "Connecting to the local engine…";
    public string SourceNotice { get; private set; } = "Refresh to discover sources on the network.";
    public string CompanionStatus { get; private set; } = "Checking Companion…";
    public string CompanionReason { get; private set; } = "";
    public string CompanionColor { get; private set; } = "#A3B1C1";
    public void SetCompanion(string status, string reason, bool ready)
    {
        CompanionStatus = status; CompanionReason = reason; CompanionColor = ready ? "#7DE1CC" : "#F6BD75"; Changed();
    }
    public string Readiness => $"{Outputs.Count(o => ProductionMode ? o.SoftwareHealthy : o.PlanReady(OutputModel.Text(Plan, "audioEndpointId")))} / {(Plan?["paths"] is JsonArray paths ? paths.Count : 4)}";
    public string ReadyCaption => !Connected ? "READINESS UNKNOWN" : ProductionMode ? "OUTPUTS RUNNING" : "BACKUPS MATCHING PLAN";
    public string AudioCaption=>ProductionMode ? "OUTPUT AUDIO" : "DANTE USB AUDIO";
    public string AudioSummary => !Connected ? "Unknown" : ProductionMode ? $"{Outputs.Count(o=>o.AudioFresh)} active / {Outputs.Count(o=>OutputModel.Text(o.State?["audio"] as JsonObject,"mode")=="silent")} silent" : Outputs[0].AudioFresh ? "Output 1 ready" : "Needs attention";
    public string Armed => !Connected ? "Unknown" : State?["armed"]?.GetValue<bool>() == true ? "Armed" : "Not armed";
    public bool CanPrepare => Connected && !Busy && Plan != null && State?["pendingOperationId"] == null;
    public bool CanDiscover => Connected && !Busy && HasCapability("source-discovery");
    public bool HasCapability(string value) => State?["capabilities"] is JsonArray caps && caps.Any(c => c?.GetValue<string>() == value);
    public void SetPlan(JsonObject plan) { Plan = plan; RefreshOutputs(); }
    public void Apply(JsonObject state)
    {
        var wasConnected = Connected; var oldHost = Host;
        State = state; received = Stopwatch.GetTimestamp(); Connected = true;
        if (!wasConnected || oldHost != Host) Log("Connected to local engine. Actual output state refreshed.");
        if (!Busy) Notice = OutputModel.Text(state, "lastError", "");
        if (string.IsNullOrEmpty(Notice)) Notice = "Live worker telemetry · physical SDI and listening checks remain unverified.";
        RefreshOutputs();
    }
    public void CheckAge()
    {
        if (Connected && Stopwatch.GetElapsedTime(received).TotalMilliseconds > 2500) Disconnect("Engine telemetry expired. Waiting for a fresh connection.");
    }
    public void Disconnect(string reason)
    {
        if (Connected) Log(reason);
        Connected = false; Notice = reason; RefreshOutputs();
    }
    public void SetBusy(bool busy, string message) { Busy = busy; Notice = message; RefreshOutputs(); }
    public void Message(string message) { Notice = message; Log(message); Changed(); }
    public void SetSources(IEnumerable<string> sources)
    {
        var names = sources.Distinct().OrderBy(s => s).ToArray();
        foreach (var output in Outputs) { output.Sources.Clear(); output.AddSource(output.ExpectedSource); output.AddSource(output.Source); foreach (var source in names) output.AddSource(source); }
        SourceNotice = $"{names.Length} discovered at {DateTime.Now:HH:mm:ss}. Discovery does not prove media readiness.";
        Changed();
    }
    public void Log(string message)
    {
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss}   {message}");
        while (Events.Count > 80) Events.RemoveAt(Events.Count - 1);
    }
    private void RefreshOutputs()
    {
        foreach (var output in Outputs)
        {
            var state = (State?["outputs"] as JsonArray)?.OfType<JsonObject>().SingleOrDefault(o => o["slot"]?.GetValue<int>() == output.Slot);
            var path = (Plan?["paths"] as JsonArray)?.OfType<JsonObject>().SingleOrDefault(o => o["slot"]?.GetValue<int>() == output.Slot);
            output.Update(state, path, Connected, !Busy && State?["pendingOperationId"] == null && HasCapability("source-routing"));
        }
        Changed();
    }
}

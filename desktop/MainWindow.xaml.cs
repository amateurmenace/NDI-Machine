using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NDIMachine.Desktop;

public partial class MainWindow : Window
{
    private readonly string root;
    private readonly EngineClient client = new();
    private readonly DashboardModel model = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer freshness = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly PreviewReader[] previews = Enumerable.Range(0, 4).Select(_ => new PreviewReader()).ToArray();
    private Task? polling;
    private Task? companionPolling;
    private string planHost = "";
    private bool launching;
    private SourceChoice[] discovered=[];
    private Task<SourceChoice[]>? sourceFetch;
    public MainWindow(string root)
    {
        this.root = root; InitializeComponent(); DataContext = model;
        try{var preference=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"config","local.desktop.json")));model.SetMode(preference?["mode"]?.GetValue<string>()!="failover");}catch(Exception){model.SetMode(true);}
        freshness.Tick += (_, _) => { model.CheckAge(); foreach(var output in model.Outputs) previews[output.Slot-1].Update(output, model.PreviewsEnabled); };
        Loaded += (_, _) => { freshness.Start(); polling = Poll(); companionPolling = PollCompanion(); };
        Closed += async (_, _) => { freshness.Stop(); foreach (var preview in previews) preview.Dispose(); lifetime.Cancel(); if (polling != null) await polling; if (companionPolling != null) await companionPolling; if(sourceFetch!=null)try{await sourceFetch;}catch(Exception){} client.Dispose(); lifetime.Dispose(); };
    }
    private async Task PollCompanion()
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 65536 };
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                var routing=JsonNode.Parse(await http.GetStringAsync("http://127.0.0.1:9862/api/v1/state",lifetime.Token))!.AsObject();
                model.SetRouting(routing["outputMappings"] as JsonArray);
                if(model.ProductionMode){await Task.Delay(2000,lifetime.Token);continue;}
                var json = await http.GetStringAsync("http://127.0.0.1:8000/api/connections", lifetime.Token);
                var connection = JsonNode.Parse(json)!.AsArray().OfType<JsonObject>().SingleOrDefault(c => c["label"]?.GetValue<string>() == "ndi_machine");
                var ready = connection?["enabled"]?.GetValue<bool>() == true && connection["status"]?["level"]?.GetValue<string>() == "ok";
                model.SetCompanion(ready ? "Companion ready" : "Companion blocked / unavailable",
                    connection?["status"]?["message"]?.GetValue<string>() ?? (ready ? "Use the failover toggle in Companion or on your Stream Deck." : "Check the ndi_machine connection in Companion."), ready);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (Exception) { model.SetRouting(null);model.SetCompanion("Companion unavailable", "Cannot read current failover status. Open Companion to inspect.", false); }
            try { await Task.Delay(2000, lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task Poll()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                var state = await client.State(lifetime.Token);
                var instance = OutputModel.Text(state, "instanceId");
                if (planHost != instance)
                {
                    var plan = await client.Request(HttpMethod.Get, "commission", token: lifetime.Token);
                    if (plan["schemaVersion"]?.GetValue<int>() != 1 || plan["paths"] is not JsonArray) throw new InvalidDataException("Commissioning plan is unavailable");
                    model.SetPlan(plan); planHost = instance;
                    model.Apply(state); _ = RefreshSources();
                    // Refresh after the extra request; a plan round trip is not fresh media.
                    continue;
                }
                model.Apply(state);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (Exception error) { model.Disconnect("Engine unavailable: " + Friendly(error)); }
            try { await Task.Delay(750, lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private static string Friendly(Exception error) => error is OperationCanceledException ? "request deadline exceeded" : error.Message;
    private void SelectMode(object sender,RoutedEventArgs e) {
        if(sender is not RadioButton button)return;
        model.SetMode((string)button.Tag=="production");
        try{File.WriteAllText(Path.Combine(root,"config","local.desktop.json"),new JsonObject{["mode"]=model.ProductionMode?"production":"failover"}.ToJsonString());}
        catch(Exception error){model.Message("Mode selected; could not save preference: "+error.Message);}
    }
    private async Task Command(string label, Func<Task> work)
    {
        if (model.Busy) return;
        model.SetBusy(true, label);
        model.Log(label);
        try { await work(); model.Message(label + " — completed."); }
        catch (Exception error) when (!lifetime.IsCancellationRequested)
        { model.Message(label + " — " + Friendly(error)); }
        catch (OperationCanceledException) { }
        finally { if (!lifetime.IsCancellationRequested) model.SetBusy(false, model.Notice); }
    }
    private async void PrepareBackups(object sender, RoutedEventArgs e)
    {
        if (!model.CanPrepare) return;
        var plan = (JsonObject)model.Plan!.DeepClone();
        // This explicit operator command restores the commissioned sources. It does
        // not infer readiness from HTTP 202, nor does it send a router crosspoint.
        await Command("Preparing commissioned backups", async () =>
        {
            var host = model.Host; var id = Guid.NewGuid().ToString();
            var accepted = await client.Request(HttpMethod.Post, "emergency/prepare", new() { ["schemaVersion"] = 1, ["plan"] = plan }, id, token: lifetime.Token);
            if (OutputModel.Text(accepted, "instanceId") != host) throw new InvalidOperationException("Host changed; preparation outcome unknown");
            await client.WaitOperation(id, host, lifetime.Token);
        });
    }
    private async void StopOutput(object sender,RoutedEventArgs e) {
        if(sender is not Button {DataContext:OutputModel output} || !output.CanStop)return;
        if(!model.HasCapability("output-stop")){model.Message("The control host needs the Stop output update.");return;}
        var host=model.Host;var id=Guid.NewGuid().ToString();
        var request=new JsonObject{["schemaVersion"]=1,["slot"]=output.Slot,["expectedInstanceId"]=host,
            ["expectedWorkerInstanceId"]=OutputModel.Text(output.State,"workerInstanceId"),["expectedRevision"]=output.Revision};
        await Command($"Stopping output {output.Slot}",async()=>{
            await client.Request(HttpMethod.Post,$"outputs/{output.Slot}/stop",request,id,token:lifetime.Token);
            await client.WaitOperation(id,host,lifetime.Token);
        });
    }
    private async Task StopAllSources() {
        var current=await client.State(lifetime.Token);
        var expected=new JsonObject();
        foreach(var output in current["outputs"]!.AsArray().OfType<JsonObject>())expected[output["slot"]!.ToString()]=new JsonObject{
            ["expectedWorkerInstanceId"]=OutputModel.Text(output,"workerInstanceId"),["expectedRevision"]=OutputModel.NumberAt(output,"configRevision")};
        var host=OutputModel.Text(current,"instanceId");var id=Guid.NewGuid().ToString();
        await client.Request(HttpMethod.Post,"outputs/stop",new(){["schemaVersion"]=1,["expectedInstanceId"]=host,["expectedWorkers"]=expected},id,token:lifetime.Token);
        await client.WaitOperation(id,host,lifetime.Token);
    }
    private void EngineControls(object sender,RoutedEventArgs e) {
        var dialog=new Window{Title="NDI Machine engine",Owner=this,Width=560,Height=410,WindowStartupLocation=WindowStartupLocation.CenterOwner,Style=(Style)FindResource(typeof(Window))};
        var panel=new StackPanel{Margin=new Thickness(24)};
        panel.Children.Add(new TextBlock{Text="Background engine",FontSize=25,FontWeight=FontWeights.SemiBold});
        panel.Children.Add(new TextBlock{Text="Stopping sources releases NDI receivers and SDI outputs. Their destinations will lose video. Router crosspoints stay where you set them. Restart starts idle; use PRIME or choose sources to resume.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,18)});
        foreach(var item in new[]{("Stop all sources","sources"),("Stop sources and background engine","stop"),("Restart background engine · starts idle","restart")}) {
            var button=new Button{Content=item.Item1,Margin=new Thickness(0,0,0,10),IsEnabled=!model.Busy};
            button.Click+=async(_,_)=>{dialog.Close();await Command(item.Item1,async()=>{
                if(model.Connected){if(!model.HasCapability("output-stop"))throw new InvalidOperationException("Activate the new engine first");await StopAllSources();}
                if(item.Item2!="sources"){
                    using var http=new HttpClient(new SocketsHttpHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(15)};
                    using var response=await http.PostAsync("http://127.0.0.1:9861/api/v1/control/"+item.Item2,null,lifetime.Token);
                    if(!response.IsSuccessStatusCode)throw new IOException(await response.Content.ReadAsStringAsync(lifetime.Token));
                }
            });};panel.Children.Add(button);
        }
        dialog.Content=panel;dialog.ShowDialog();
    }
    private async void ReturnSdi(object sender,RoutedEventArgs e) {
        if(MessageBox.Show(this,"Restore ATEM inputs 1–8 to normal SDI?\n\nVideohub 33–40 → 9, 10, 11, 12, 18, 17, 33, 34.\nCloud links on these inputs will be removed. NDI receivers keep running until stopped.","Return to SDI",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
        await Command("Returning ATEM inputs to SDI",async()=>{
            using var http=new HttpClient(new SocketsHttpHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(45)};
            using var response=await http.PostAsync("http://127.0.0.1:9862/api/v1/return-sdi",null,lifetime.Token);
            var result=await response.Content.ReadAsStringAsync(lifetime.Token);if(!response.IsSuccessStatusCode)throw new IOException(result);
        });
    }
    private async void ApplySource(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: OutputModel output } || !output.CanApply) return;
        var source = output.SelectedSource;
        var host = model.Host;
        var request = new JsonObject { ["schemaVersion"] = 1, ["slot"] = output.Slot, ["source"] = source,
            ["expectedInstanceId"] = host, ["expectedWorkerInstanceId"] = OutputModel.Text(output.State, "workerInstanceId"), ["expectedRevision"] = output.Revision };
        await Command($"Output {output.Slot}: prepare {source}", async () =>
        {
            var id = Guid.NewGuid().ToString();
            var accepted = await client.Request(HttpMethod.Put, $"outputs/{output.Slot}/route", request, id, token: lifetime.Token);
            if (OutputModel.Text(accepted, "instanceId") != host) throw new InvalidOperationException("Host changed; route outcome unknown");
            await client.WaitOperation(id, host, lifetime.Token);
        });
    }
    private async void DiscoverSources(object sender, RoutedEventArgs e) => await RefreshSources();
    private async Task RefreshSources()
    {
        if (!model.Connected || !model.HasCapability("source-discovery")) return;
        try
        {
            await LoadSources();
            model.Message($"{discovered.Length} NDI sources discovered. Select any source in an output editor.");
        }
        catch (Exception error) when (!lifetime.IsCancellationRequested) { model.Message("Discovery failed: " + Friendly(error)); }
        catch (OperationCanceledException) { }
    }
    private Task<SourceChoice[]> LoadSources()=>sourceFetch ??= FetchSources();
    private async Task<SourceChoice[]> FetchSources() {
        try {
            var result=await client.Request(HttpMethod.Get,"sources",milliseconds:15000,token:lifetime.Token);
            discovered=(result["sources"] as JsonArray ?? new()).OfType<JsonObject>().Select(s=>new SourceChoice(OutputModel.Text(s,"name"),OutputModel.Text(s,"endpoint"))).OrderBy(s=>s.Name).ToArray();
            model.SetSources(discovered.Select(s=>s.Name));return discovered;
        }finally{sourceFetch=null;}
    }
    private void OpenCompanion(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("http://127.0.0.1:8000/buttons/1") { UseShellExecute = true }); }
        catch (Exception error) { model.Message("Cannot open Companion: " + error.Message); }
    }
    private async void EditOutput(object sender,RoutedEventArgs e)
    {
        if(sender is not Button {DataContext:OutputModel output} || !model.Connected || model.Busy)return;
        if(!model.HasCapability("output-settings")){model.Message("Start the updated control host to use output settings.");return;}
        model.SetBusy(true,"Reading available Windows audio and DeckLink devices…");
        try {
            try{await LoadSources();}catch(Exception error)when(!lifetime.IsCancellationRequested){model.Message("Discovery unavailable; saved sources and manual entry remain available: "+Friendly(error));}
            var inventory=await client.Request(HttpMethod.Get,"inventory",milliseconds:15000,token:lifetime.Token);
            var choices=discovered.Concat(output.Sources.Where(n=>!discovered.Any(s=>s.Name==n)).Select(n=>new SourceChoice(n,""))).ToArray();
            var host=model.Host;var epoch=OutputModel.Text(output.State,"workerInstanceId");var revision=output.Revision;
            var editor=new OutputEditor(output,inventory,choices,LoadSources){Owner=this};
            if(editor.ShowDialog()!=true || editor.Settings==null)return;
            var request=new JsonObject{["schemaVersion"]=1,["slot"]=output.Slot,["settings"]=editor.Settings.DeepClone(),
                ["expectedInstanceId"]=host,["expectedWorkerInstanceId"]=epoch,["expectedRevision"]=revision};
            model.SetBusy(false,model.Notice);
            await Command($"Applying output {output.Slot} settings",async()=>{
                var id=Guid.NewGuid().ToString();
                var accepted=await client.Request(HttpMethod.Put,$"outputs/{output.Slot}/settings",request,id,token:lifetime.Token);
                if(OutputModel.Text(accepted,"instanceId")!=host)throw new InvalidOperationException("Host changed; configuration outcome unknown");
                await client.WaitOperation(id,host,lifetime.Token);
            });
        }catch(Exception error)when(!lifetime.IsCancellationRequested){model.Message("Output settings: "+Friendly(error));}
        catch(OperationCanceledException){}
        finally{if(!lifetime.IsCancellationRequested)model.SetBusy(false,model.Notice);}
    }
    private async void DiscoverySettings(object sender,RoutedEventArgs e)
    {
        if(!model.CanDiscover)return;
        model.SetBusy(true,"Reading discovery settings…");
        try {
            var settings=await client.Request(HttpMethod.Get,"discovery",token:lifetime.Token);
            var dialog=new Window{Title="NDI discovery / external sources",Owner=this,Width=720,Height=480,MinWidth=600,MinHeight=420,WindowStartupLocation=WindowStartupLocation.CenterOwner,Style=(Style)FindResource(typeof(Window))};
            var panel=new StackPanel{Margin=new Thickness(26)};
            panel.Children.Add(new TextBlock{Text="Discover external NDI sources",FontSize=24,FontWeight=FontWeights.SemiBold,Foreground=(System.Windows.Media.Brush)FindResource("Ink")});
            panel.Children.Add(new TextBlock{Text="Add remote sender IP addresses. Discovery asks those machines for their sources; then select the source in an output editor. IP reachability and sender NDI settings still apply.",Margin=new Thickness(0,12,0,16),Foreground=(System.Windows.Media.Brush)FindResource("Muted")});
            panel.Children.Add(new TextBlock{Text="Extra sender IPs (comma separated, up to 32)",Foreground=(System.Windows.Media.Brush)FindResource("Ink")});
            var ips=new TextBox{Text=OutputModel.Text(settings,"extraIps"),Margin=new Thickness(0,6,0,16)};System.Windows.Automation.AutomationProperties.SetName(ips,"External NDI IP addresses");panel.Children.Add(ips);
            panel.Children.Add(new TextBlock{Text="NDI groups (comma separated; blank uses runtime defaults)",Foreground=(System.Windows.Media.Brush)FindResource("Ink")});
            var groups=new TextBox{Text=OutputModel.Text(settings,"groups"),Margin=new Thickness(0,6,0,16)};System.Windows.Automation.AutomationProperties.SetName(groups,"NDI groups");panel.Children.Add(groups);
            var save=new Button{Content="Save and discover",Style=(Style)FindResource("Primary"),HorizontalAlignment=HorizontalAlignment.Right};save.Click+=(_,_)=>dialog.DialogResult=true;panel.Children.Add(save);dialog.Content=panel;
            if(dialog.ShowDialog()!=true)return;
            await client.Request(HttpMethod.Put,"discovery",new(){["extraIps"]=ips.Text,["groups"]=groups.Text},token:lifetime.Token);
            model.SetBusy(false,model.Notice);await RefreshSources();
        }catch(Exception error)when(!lifetime.IsCancellationRequested){model.Message("Discovery settings: "+Friendly(error));}
        catch(OperationCanceledException){}
        finally{if(!lifetime.IsCancellationRequested)model.SetBusy(false,model.Notice);}
    }
    private async void StartControl(object sender, RoutedEventArgs e)
    {
        if (launching || model.Connected) return;
        launching = true;
        try
        {
            try {
                using var launcher=new HttpClient(new SocketsHttpHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(3)};
                using var launched=await launcher.PostAsync("http://127.0.0.1:9861/api/v1/control/start",null,lifetime.Token);
                if(launched.IsSuccessStatusCode){model.Message("Engine started; sources remain idle until selected or primed.");return;}
            }catch(HttpRequestException){}catch(OperationCanceledException)when(!lifetime.IsCancellationRequested){}
            var config = Path.Combine(root, "config", "local.commissioning.json");
            var bundled = Path.Combine(root, "engine", "NDIMachine.Control.exe");
            var development = Path.Combine(root, "control", "bin", "Release", "net8.0-windows", "win-x64", "NDIMachine.Control.exe");
            var exe = File.Exists(bundled) ? bundled : development;
            if (!File.Exists(config) || !File.Exists(exe)) throw new FileNotFoundException("The commissioned engine package is missing. Open this app from its complete package folder.");
            // One final read-only probe prevents duplicate host launches after a transient delay.
            try { model.Apply(await client.State(lifetime.Token)); return; } catch (HttpRequestException) { } catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
            info.ArgumentList.Add("--root"); info.ArgumentList.Add(root); info.ArgumentList.Add("--commission"); info.ArgumentList.Add(config);
            using var process = Process.Start(info) ?? throw new IOException("Control host did not launch");
            model.Message("Control host launched. Missing sources stay idle until you start them or press PRIME.");
            await Task.Delay(3000, lifetime.Token);
            if (process.HasExited) model.Message($"Control host exited ({process.ExitCode}). Check commissioning and dependencies.");
        }
        catch (Exception error) when (!lifetime.IsCancellationRequested) { model.Message(Friendly(error)); }
        catch (OperationCanceledException) { }
        finally { launching = false; }
    }
    private void ShowDiagnostics(object sender, RoutedEventArgs e)
    {
        var window = new Window { Title = "NDI Machine — Diagnostics", Owner = this, Width = 860, Height = 650, MinWidth = 600, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(24) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        var save = new Button { Content = "Save diagnostic snapshot…" };
        save.Click += (_, _) => SaveDiagnostics(); buttons.Children.Add(save);
        var note = new TextBlock { Text = "Includes local device IDs and source names. Review before sharing.", Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = 330 };
        buttons.Children.Add(note); DockPanel.SetDock(buttons, Dock.Top); panel.Children.Add(buttons);
        var text = new TextBox { Text = DiagnosticText(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, Background = (System.Windows.Media.Brush)FindResource("Canvas"), Foreground = (System.Windows.Media.Brush)FindResource("Ink"), BorderThickness = new Thickness(0) };
        panel.Children.Add(text); window.Content = panel; window.ShowDialog();
    }
    private JsonObject DiagnosticSnapshot() => new() { ["schemaVersion"] = 1, ["applicationVersion"] = "0.5.0", ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        ["controlConnected"] = model.Connected, ["physicalSignalVerified"] = "Unknown", ["state"] = model.State?.DeepClone(), ["plan"] = model.Plan?.DeepClone(),
        ["events"] = new JsonArray(model.Events.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) };
    private string DiagnosticText() => DiagnosticSnapshot().ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    private void SaveDiagnostics()
    {
        var dialog = new SaveFileDialog { Title = "Save NDI Machine diagnostic snapshot", FileName = $"NDI-Machine-{DateTime.Now:yyyyMMdd-HHmmss}.json", Filter = "JSON snapshot (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, DiagnosticText()); model.Message("Diagnostic snapshot saved: " + dialog.FileName); }
        catch (Exception error) { model.Message("Cannot save diagnostics: " + error.Message); }
    }
}

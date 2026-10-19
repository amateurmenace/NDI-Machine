using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace NDIMachine.Desktop;
public sealed record DeviceChoice(string Id,string Label);
public sealed record SourceChoice(string Name,string Address);
public partial class OutputEditor : Window
{
    public JsonObject? Settings {get;private set;}
    private readonly string previousDevice;
    private bool loading=true;
    private readonly Func<Task<SourceChoice[]>>? refreshSources;
    public OutputEditor(OutputModel output,JsonObject inventory,IEnumerable<SourceChoice> sources,Func<Task<SourceChoice[]>>? refreshSources=null) {
        this.refreshSources=refreshSources;
        InitializeComponent();Heading.Text=$"Output {output.Slot} settings";
        var current=output.State?["settings"] as JsonObject ?? output.State?["desiredSettings"] as JsonObject ?? new JsonObject();
        var configuredDevice=OutputModel.Text(current,"device",output.Device);previousDevice=output.Device;
        Source.ItemsSource=sources.ToArray(); Source.Text=output.Running ? OutputModel.Text(current,"source",output.Source) : "";
        SourceCount.Text=$"{Source.Items.Count} sources available · type a name or choose from network discovery";
        RefreshSourceButton.IsEnabled=refreshSources!=null;
        SourceAddress.Text=output.Running ? OutputModel.Text(current,"sourceUrl") : "";
        Bandwidth.SelectedValue=OutputModel.Text(current,"bandwidth","highest");Sizing.SelectedValue=OutputModel.Text(current,"sizing","fit");
        Device.ItemsSource=(inventory["decklink"]?["devices"] as JsonArray ?? new()).OfType<JsonObject>().Select(d=> {
            var id=OutputModel.Text(d,"persistentId"); var busy=d["busyFlags"]?.GetValue<int>()!=0;
            var suffix=OutputModel.Text(d,"deviceGroupId")=="0x86346c40" ? " · RESERVED OBS" : busy && id!=output.Device ? " · IN USE" : "";
            if(d["supports1080p2997Uyvy"]?.GetValue<bool>()!=true)suffix+=" · unsupported mode";
            return new DeviceChoice(id,$"{OutputModel.Text(d,"displayName")}  ({id}){suffix}");
        }).ToArray();Device.SelectedValue=configuredDevice;
        AudioDevice.ItemsSource=(inventory["audio"] as JsonArray ?? new()).OfType<JsonObject>().Select(d=>new DeviceChoice(OutputModel.Text(d,"id"),
            $"{OutputModel.Text(d,"name")} · {OutputModel.Text(d,"flow")} · {d["mixFormat"]?["channels"]} ch")).ToArray();
        AudioDevice.SelectedValue=OutputModel.Text(current,"audioEndpoint");
        AudioMode.SelectedValue=OutputModel.Text(current,"audioMode","silent");
        LeftChannel.Text=(current["left"]?.GetValue<int>() ?? 1).ToString();RightChannel.Text=(current["right"]?.GetValue<int>() ?? 2).ToString();
        Gain.Text=(current["gainDb"]?.GetValue<double>() ?? 0).ToString(CultureInfo.InvariantCulture);
        Delay.Text=(current["delayMs"]?.GetValue<int>() ?? 0).ToString();Muted.IsChecked=current["muted"]?.GetValue<bool>()==true;
        loading=false;AudioChanged(this,new RoutedEventArgs());
    }
    private async void RefreshSourceList(object sender,RoutedEventArgs e) {
        if(refreshSources==null)return;
        RefreshSourceButton.IsEnabled=false;SourceCount.Text="Discovering network sources…";
        var selected=Source.Text;var address=SourceAddress.Text;
        try {
            var sources=await refreshSources();loading=true;
            Source.ItemsSource=sources.Any(s=>s.Name==selected) ? sources : sources.Append(new SourceChoice(selected,address)).ToArray();
            Source.Text=selected;SourceAddress.Text=address;
            SourceCount.Text=$"{sources.Length} network sources found · current selection preserved";
        }catch(Exception error){SourceCount.Text="Discovery failed: "+error.Message;}
        finally{loading=false;RefreshSourceButton.IsEnabled=true;}
    }
    private void SourceChanged(object sender,SelectionChangedEventArgs e) {
        if(!loading && Source.SelectedItem is SourceChoice source)SourceAddress.Text=source.Address;
    }
    private void AudioChanged(object sender,RoutedEventArgs e) {if(AudioDevice!=null)AudioDevice.IsEnabled=AudioMode.SelectedValue as string=="fixed";}
    private void Apply(object sender,RoutedEventArgs e) {
        // Resolve editable ComboBox text at Apply as well as SelectionChanged.
        var known=Source.Items.OfType<SourceChoice>().SingleOrDefault(s=>s.Name==Source.Text.Trim());
        if(known!=null && known.Address.Length>0)SourceAddress.Text=known.Address;
        if(string.IsNullOrWhiteSpace(Source.Text) || Device.SelectedValue is not string device ||
            !int.TryParse(LeftChannel.Text,out var left) || left is <1 or >64 || !int.TryParse(RightChannel.Text,out var right) || right is <1 or >64 ||
            !double.TryParse(Gain.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var gain) || !double.IsFinite(gain) || gain is <-60 or >12 ||
            !int.TryParse(Delay.Text,out var delay) || delay is <0 or >500) {Error.Text="Select a source and connector, then enter valid channels, gain and delay.";return;}
        var mode=AudioMode.SelectedValue as string ?? "silent";var endpoint=AudioDevice.SelectedValue as string ?? "";
        if(mode=="fixed" && endpoint.Length==0){Error.Text="Select a Windows audio device.";return;}
        if(device!=previousDevice && MessageBox.Show(this,"Move this slot to the selected DeckLink connector? This briefly stops this output. Other slots keep running.","Change SDI connector",MessageBoxButton.OKCancel,MessageBoxImage.Information)!=MessageBoxResult.OK)return;
        Settings=new(){["device"]=device,["source"]=Source.Text.Trim(),["sourceUrl"]=SourceAddress.Text.Trim(),["bandwidth"]=Bandwidth.SelectedValue as string ?? "highest",["sizing"]=Sizing.SelectedValue as string ?? "fit",
            ["audioMode"]=mode,["audioEndpoint"]=mode=="fixed"?endpoint:"",["left"]=left,["right"]=right,["gainDb"]=gain,["delayMs"]=delay,["muted"]=Muted.IsChecked==true};
        DialogResult=true;
    }
}

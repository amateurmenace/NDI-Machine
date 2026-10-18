using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.Json.Nodes;
using NDIMachine.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length is < 2 or > 3) throw new ArgumentException("Pass project root, PNG destination and optional real inventory JSON for editor rendering");
        // Render our own real WPF visual tree. This is an application integration
        // test, not a mockup or a desktop screen capture. It sends no commands.
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/NDI Machine;component/Theme.xaml", UriKind.Relative) });
        Window window;
        if(args.Length==3) {
            using var client=new EngineClient();var actual=client.State().GetAwaiter().GetResult();
            var output=new OutputModel(1);output.Update(actual["outputs"]!.AsArray().OfType<JsonObject>().Single(o=>o["slot"]!.GetValue<int>()==1),null,true,true);
            var discovery=client.Request(HttpMethod.Get,"sources",milliseconds:15000).GetAwaiter().GetResult();
            var sources=discovery["sources"]!.AsArray().OfType<JsonObject>().Select(s=>new SourceChoice(s["name"]!.GetValue<string>(),s["endpoint"]!.GetValue<string>())).OrderBy(s=>s.Name).ToArray();
            window=new OutputEditor(output,JsonNode.Parse(File.ReadAllText(args[2]))!.AsObject(),sources);
        }else window = new MainWindow(args[0]);
        window.ShowActivated=false;window.ShowInTaskbar=false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var content = (FrameworkElement)window.Content;
                content.UpdateLayout();
                var image = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                image.Render(content);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(args[1]); png.Save(file);
                Console.WriteLine($"Rendered actual WPF dashboard: {content.ActualWidth}x{content.ActualHeight}");
            }
            finally { window.Close(); }
        };
        window.Loaded += (_, _) => timer.Start();
        app.Run(window);
    }
}

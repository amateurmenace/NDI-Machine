using System.IO;
using System.Windows;

namespace NDIMachine.Desktop;

public partial class App : Application
{
    private Mutex? owner;
    private readonly CancellationTokenSource activationLifetime = new();
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var options = DesktopArguments.Parse(e.Args);
            owner=new Mutex(true,@"Local\NDIMachine.Desktop",out var first);
            if(!first){
                owner.Dispose();owner=null;
                if(options.Failover){using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));await DesktopActivation.ShowFailover(deadline.Token);}
                Shutdown();return;
            }
            var root = options.Root ?? FindRoot();
            MainWindow = new MainWindow(root);
            MainWindow.Show();
            if(options.Failover)((MainWindow)MainWindow).ShowFailover();
            _ = DesktopActivation.Listen(() => Dispatcher.InvokeAsync(() => ((MainWindow)MainWindow).ShowFailover()).Task, activationLifetime.Token);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "NDI Machine could not open", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) {activationLifetime.Cancel();owner?.Dispose();base.OnExit(e);}
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "config", "local.commissioning.json"))) return directory.FullName;
        // An unattached copy can still monitor the localhost engine. Launching a host
        // requires the full commissioned package and is separately validated.
        return AppContext.BaseDirectory;
    }
}

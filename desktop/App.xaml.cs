using System.IO;
using System.Windows;

namespace NDIMachine.Desktop;

public partial class App : Application
{
    private Mutex? owner;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            owner=new Mutex(true,@"Local\NDIMachine.Desktop",out var first);
            if(!first){owner.Dispose();owner=null;Shutdown();return;}
            var root = FindRoot(e.Args);
            MainWindow = new MainWindow(root);
            MainWindow.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "NDI Machine could not open", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) {owner?.Dispose();base.OnExit(e);}
    private static string FindRoot(string[] args)
    {
        if (args.Length == 2 && args[0] == "--root") return Path.GetFullPath(args[1]);
        if (args.Length != 0) throw new ArgumentException("Usage: NDI Machine.exe [--root project-or-package-directory]");
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "config", "local.commissioning.json"))) return directory.FullName;
        // An unattached copy can still monitor the localhost engine. Launching a host
        // requires the full commissioned package and is separately validated.
        return AppContext.BaseDirectory;
    }
}

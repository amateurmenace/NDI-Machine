using System.IO;

namespace NDIMachine.Desktop;

public sealed record DesktopArguments(string? Root, bool Failover)
{
    public static DesktopArguments Parse(string[] args)
    {
        string? root = null; var failover = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--failover" && !failover) failover = true;
            else if (args[i] == "--root" && root == null && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                root = Path.GetFullPath(args[++i]);
            else throw new ArgumentException("Usage: NDI Machine.exe [--root project-or-package-directory] [--failover]");
        }
        return new(root, failover);
    }
}

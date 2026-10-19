using System.Security.Cryptography;
using System.IO;

namespace NDIMachine.Launcher;

// A copied single-file GUI shares the desktop mutex with the installed GUI.
// Accept only byte-identical copies; an unrelated process name is not identity.
public sealed class ExecutableIdentity
{
    private readonly Dictionary<string,(long Length,DateTime Modified,string Hash)> cache = new(StringComparer.OrdinalIgnoreCase);
    private string Hash(string path)
    {
        var file=new FileInfo(path);
        if(cache.TryGetValue(path,out var saved) && saved.Length==file.Length && saved.Modified==file.LastWriteTimeUtc)return saved.Hash;
        using var stream=File.OpenRead(path);
        var hash=Convert.ToHexString(SHA256.HashData(stream));
        cache[path]=(file.Length,file.LastWriteTimeUtc,hash);
        return hash;
    }
    public bool Matches(string installed,string candidate,bool allowCopy)
    {
        lock(cache) {
        if(string.Equals(Path.GetFullPath(installed),Path.GetFullPath(candidate),StringComparison.OrdinalIgnoreCase))return true;
        return allowCopy && File.Exists(installed) && File.Exists(candidate) && new FileInfo(installed).Length==new FileInfo(candidate).Length && Hash(installed)==Hash(candidate);
        }
    }
}

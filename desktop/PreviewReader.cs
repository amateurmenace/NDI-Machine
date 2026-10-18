using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NDIMachine.Desktop;

public sealed class PreviewReader : IDisposable
{
    private MemoryMappedFile? mapping;
    private MemoryMappedViewAccessor? view;
    private Mutex? mutex;
    private string currentName = "";
    private long sequence;
    private long displayedAt;
    private readonly byte[] pixels = new byte[320 * 180 * 4];
    public void Update(OutputModel output, bool enabled)
    {
        if (!enabled || !output.Fresh) { output.SetPreview(null, enabled ? "Preview stale" : "Preview paused"); return; }
        var name = OutputModel.Text(output.State, "previewMapping");
        if (!name.StartsWith($"Local\\NDIMachine.Preview.{output.Slot}.", StringComparison.Ordinal) || name.Length > 128)
        { output.SetPreview(null, "Preview needs updated worker"); return; }
        var acquired = false;
        try
        {
            if (currentName != name)
            {
                Dispose(); currentName = name; sequence = 0;
                mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                view = mapping.CreateViewAccessor(0, 64 + pixels.Length, MemoryMappedFileAccess.Read);
                mutex = Mutex.OpenExisting(name + ".lock");
            }
            try { acquired = mutex!.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; output.SetPreview(null,"Preview publisher unavailable"); return; }
            if (!acquired) { if(displayedAt==0 || Stopwatch.GetElapsedTime(displayedAt).TotalMilliseconds>1500) output.SetPreview(null,"Preview stale"); return; }
            if (view!.ReadInt32(0) != 1 || view.ReadInt32(4) != 320 || view.ReadInt32(8) != 180 || view.ReadInt32(12) != 1280)
                throw new InvalidDataException("Preview layout mismatch");
            var revision = view.ReadInt64(16); var timestamp = view.ReadInt64(24); var next = view.ReadInt64(32);
            var age = (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;
            if (age is < 0 or > 1500 || revision != output.Revision || view.ReadInt32(40) != 1)
            { output.SetPreview(null, "No fresh preview for this source"); return; }
            if (next == sequence && output.Preview != null) return;
            view.ReadArray(64, pixels, 0, pixels.Length); sequence = next;
            displayedAt=timestamp;
            var image = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32, null, pixels, 1280); image.Freeze();
            output.SetPreview(image, "320 × 180 · ≤4 fps");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException or InvalidOperationException or ArgumentException)
        { output.SetPreview(null, "Preview unavailable"); if (acquired) {mutex!.ReleaseMutex(); acquired = false;} Dispose(); }
        finally { if (acquired) mutex!.ReleaseMutex(); }
    }
    public void Dispose()
    {
        view?.Dispose(); mapping?.Dispose(); mutex?.Dispose(); view = null; mapping = null; mutex = null; currentName = "";
    }
}

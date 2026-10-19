using System.IO;
using System.IO.Pipes;

namespace NDIMachine;

// UI activation only: this channel cannot prepare media, change routes or run commands.
public static class DesktopActivation
{
    public const string PipeName = "NDIMachine.Desktop.Activation.v1";
    public static async Task ShowFailover(CancellationToken cancellation, string pipeName = PipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(cancellation);
        await pipe.WriteAsync(new byte[] { 1 }, cancellation);
        var reply = new byte[1];
        if (await pipe.ReadAsync(reply, cancellation) != 1 || reply[0] != 1)
            throw new IOException("NDI Machine did not confirm the Failover view");
    }

    public static async Task Listen(Func<Task> showFailover, CancellationToken cancellation, string pipeName = PipeName)
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellation);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var request = new byte[1];
                if (await pipe.ReadAsync(request, deadline.Token) != 1 || request[0] != 1) continue;
                await showFailover().WaitAsync(deadline.Token);
                await pipe.WriteAsync(new byte[] { 1 }, deadline.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { } // A disconnected caller never kills the window's listener.
        }
    }
}

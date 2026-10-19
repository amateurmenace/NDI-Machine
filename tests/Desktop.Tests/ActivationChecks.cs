using System.IO;
using System.IO.Pipes;
using NDIMachine;
using NDIMachine.Desktop;

internal static class ActivationChecks
{
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("explicit failover launch preserves root and ordinary launch preserves saved mode", () => {
            var root=Path.GetFullPath(".");
            if(DesktopArguments.Parse([]) != new DesktopArguments(null,false) ||
                DesktopArguments.Parse(["--root",root,"--failover"]) != new DesktopArguments(root,true) ||
                DesktopArguments.Parse(["--failover","--root",root]) != new DesktopArguments(root,true))
                throw new Exception("Incorrect desktop launch intent");
            foreach(var args in new[]{new[]{"--root"},new[]{"--root","--failover"},new[]{"--failover","--failover"},new[]{"--take"}}) {
                try{DesktopArguments.Parse(args);}catch(ArgumentException){continue;}
                throw new Exception("Invalid desktop argument accepted");
            }
            return Task.CompletedTask;
        });
        await test("existing desktop acknowledges each explicit Failover activation after displaying it", async () => {
            var pipe="ndi-activation-test-"+Guid.NewGuid();var calls=0;
            using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var listener=DesktopActivation.Listen(()=>{calls++;return Task.CompletedTask;},lifetime.Token,pipe);
            try {
                await DesktopActivation.ShowFailover(lifetime.Token,pipe);
                if(calls!=1)throw new Exception("Window not activated before acknowledgement");
                await DesktopActivation.ShowFailover(lifetime.Token,pipe);
                if(calls!=2)throw new Exception("Existing window activation was lost");
            } finally {lifetime.Cancel();await listener;}
        });
        await test("desktop activation rejects unknown commands and survives a disconnected caller", async () => {
            var pipe="ndi-activation-test-"+Guid.NewGuid();var calls=0;
            using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var listener=DesktopActivation.Listen(()=>{calls++;return Task.CompletedTask;},lifetime.Token,pipe);
            try {
                using(var invalid=new NamedPipeClientStream(".",pipe,PipeDirection.InOut,PipeOptions.Asynchronous)) {
                    await invalid.ConnectAsync(lifetime.Token);await invalid.WriteAsync(new byte[]{99},lifetime.Token);
                    if(await invalid.ReadAsync(new byte[1],lifetime.Token)!=0)throw new Exception("Invalid command acknowledged");
                }
                using(var disconnected=new NamedPipeClientStream(".",pipe,PipeDirection.InOut,PipeOptions.Asynchronous))await disconnected.ConnectAsync(lifetime.Token);
                await DesktopActivation.ShowFailover(lifetime.Token,pipe);
                if(calls!=1)throw new Exception("Invalid/disconnected caller activated the window");
            } finally {lifetime.Cancel();await listener;}
        });
        await test("missing desktop activation times out without reporting success", async () => {
            using var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try{await DesktopActivation.ShowFailover(deadline.Token,"ndi-missing-"+Guid.NewGuid());}
            catch(OperationCanceledException){return;}
            throw new Exception("Missing desktop falsely acknowledged activation");
        });
    }
}

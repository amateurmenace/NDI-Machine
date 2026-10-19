using System.Diagnostics;
using System.Net;
using NDIMachine.Launcher;

// Independent local bootstrap: only these two fixed, sibling executables can be
// launched. No shell, arbitrary paths, router writes or automatic media prepare.
var builder=WebApplication.CreateBuilder(args);
var root=Path.GetFullPath(builder.Configuration["root"] ?? Path.Combine(AppContext.BaseDirectory,".."));
var gui=Path.Combine(root,"NDI Machine.exe");
var engine=Path.Combine(root,"engine","NDIMachine.Control.exe");
using var owner=new Mutex(true,@"Local\NDIMachine.Launcher",out var first);
if(!first)return;
if(!File.Exists(gui) || !File.Exists(engine))throw new FileNotFoundException("Complete NDI Machine package required");
builder.WebHost.ConfigureKestrel(options=>{options.Listen(IPAddress.Loopback,9861);options.Limits.MaxRequestBodySize=1024;options.Limits.MaxConcurrentConnections=8;});
builder.Logging.ClearProviders();
var app=builder.Build();
var gate=new SemaphoreSlim(1);
var lastGui=DateTime.MinValue;var lastEngine=DateTime.MinValue;
var identities=new ExecutableIdentity();
bool Running(string path) {
    foreach(var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path))) {
        using(process)try {if(!process.HasExited && process.MainModule?.FileName is string actual && identities.Matches(path,actual,path==gui))return true;}catch(Exception){}
    }
    return false;
}
void Launch(string path,bool control) {
    var info=new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=root};
    if(control){info.ArgumentList.Add("--root");info.ArgumentList.Add(root);}
    using var process=Process.Start(info) ?? throw new IOException("Cannot launch NDI Machine");
}
app.Use(async(context,next)=>{
    context.Response.Headers.CacheControl="no-store";
    var host=context.Request.Host;var origin=context.Request.Headers.Origin.ToString();
    if(host.Host is not ("127.0.0.1" or "localhost") || host.Port!=9861 ||
        (origin.Length!=0 && origin is not ("http://127.0.0.1:9861" or "http://localhost:9861"))) {context.Response.StatusCode=403;return;}
    try{await next(context);}catch(Exception error){context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{error=error.Message});}
});
app.MapGet("/api/v1/state",(HttpContext context)=>{
    var nonce=context.Request.Query["nonce"].ToString();
    if(nonce.Length is <1 or >128)return Results.BadRequest();
    return Results.Json(new{schemaVersion=1,nonce,desktopRunning=Running(gui),controlRunning=Running(engine)});
});
app.MapPost("/api/v1/prime",async()=>{
    if(!await gate.WaitAsync(0))return Results.Conflict(new{error="Launch already in progress"});
    try {
        if(!Running(engine) && DateTime.UtcNow-lastEngine>TimeSpan.FromSeconds(5)){lastEngine=DateTime.UtcNow;Launch(engine,true);}
        if(!Running(gui) && DateTime.UtcNow-lastGui>TimeSpan.FromSeconds(5)){lastGui=DateTime.UtcNow;Launch(gui,false);}
        return Results.Json(new{schemaVersion=1,accepted=true});
    }finally{gate.Release();}
});
app.MapPost("/api/v1/control/{action}",async(string action)=>{
    if(action is not ("start" or "stop" or "restart"))return Results.BadRequest();
    if(!await gate.WaitAsync(0))return Results.Conflict(new{error="Launcher operation in progress"});
    try {
        if(action is "stop" or "restart" && Running(engine)) {
            using var http=new HttpClient(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(4)};
            using var reply=await http.PostAsync("http://127.0.0.1:9860/api/v1/lifecycle/stop",null);
            if(!reply.IsSuccessStatusCode)return Results.Conflict(new{error="Stop sources first. Engine did not accept shutdown."});
            var limit=DateTime.UtcNow.AddSeconds(8);
            while(Running(engine) && DateTime.UtcNow<limit)await Task.Delay(100);
            if(Running(engine))return Results.Conflict(new{error="Engine has not exited; no forced termination"});
        }
        if(action is "start" or "restart" && !Running(engine))Launch(engine,true);
        return Results.Json(new{accepted=true});
    }finally{gate.Release();}
});
await app.RunAsync();

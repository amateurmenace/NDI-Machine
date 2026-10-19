using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using NDIMachine.Control;

var builder = WebApplication.CreateBuilder(args);
var root = builder.Configuration["root"] ?? throw new ArgumentException("--root project directory required");
var path = builder.Configuration["commission"] ?? Path.Combine(root, "config", "local.commissioning.json");
var commission = new Commission(path, root);
// Read-only discovery. Receivers still require explicit Apply/PRIME. If USB is
// late at sign-in, keep the API available and resolve again on explicit PRIME.
try { await commission.RefreshAudio(CancellationToken.None); } catch(Exception) { }
// Deliberately loopback only. Ignore ASPNETCORE_URLS and no automatic LAN binding.
builder.WebHost.ConfigureKestrel(options => {
    options.Listen(IPAddress.Loopback, 9860);
    options.Limits.MaxRequestBodySize = 65536;
    options.Limits.MaxConcurrentConnections = 32;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
});
builder.Logging.ClearProviders();
builder.Services.AddSingleton(commission);
builder.Services.AddSingleton<WorkerPipe>();
builder.Services.AddSingleton<IWorkerPipe>(provider => provider.GetRequiredService<WorkerPipe>());
builder.Services.AddSingleton<Discovery>();
builder.Services.AddSingleton<Inventory>();
builder.Services.AddSingleton<AtemRemote>();
builder.Services.AddSingleton<Supervisor>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<Supervisor>());
var app = builder.Build();
app.Use(async (context, next) => {
    context.Response.Headers.CacheControl = "no-store";
    var host = context.Request.Host;
    var origin = context.Request.Headers.Origin.ToString();
    if (host.Host is not ("127.0.0.1" or "localhost") || host.Port != 9860 ||
        (origin.Length != 0 && origin is not ("http://127.0.0.1:9860" or "http://localhost:9860")))
    { context.Response.StatusCode = 403; return; }
    try { await next(context); }
    catch (Exception error) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested) {
        context.Response.StatusCode = error is InvalidOperationException ? 409 : error is InvalidDataException or JsonException ? 400 : 503;
        await context.Response.WriteAsJsonAsync(new { schemaVersion = 1, error = error.Message });
    }
});
app.UseWebSockets();
app.MapGet("/api/v1/state", async (HttpContext context, Supervisor supervisor) => {
    var nonce = context.Request.Query["nonce"].ToString();
    if (nonce.Length is < 1 or > 128) return Results.BadRequest(new { error = "Fresh bounded nonce required" });
    return Results.Json(await supervisor.State(nonce, context.RequestAborted));
});
app.MapGet("/api/v1/commission", () => Results.Json(commission.Plan));
app.MapPost("/api/v1/atem",async(HttpContext context,AtemRemote atem)=>{
    if(!context.Request.HasJsonContentType())return Results.StatusCode(415);
    var request=await context.Request.ReadFromJsonAsync<JsonObject>() ?? throw new InvalidDataException("ATEM request required");
    return Results.Json(await atem.Call(request,context.RequestAborted));
});
app.MapGet("/api/v1/sources", async (Discovery discovery, HttpContext context) => Results.Json(await discovery.Get(context.RequestAborted)));
app.MapGet("/api/v1/inventory", async (Inventory inventory,HttpContext context) => Results.Json(await inventory.Get(context.RequestAborted)));
app.MapGet("/api/v1/discovery", (Discovery discovery)=>Results.Json(discovery.Settings()));
app.MapPut("/api/v1/discovery",async (Discovery discovery,HttpContext context)=> {
    if(!context.Request.HasJsonContentType())return Results.StatusCode(415);
    return Results.Json(discovery.Set(await context.Request.ReadFromJsonAsync<JsonObject>() ?? throw new InvalidDataException("Discovery settings required")));
});
app.MapPut("/api/v1/outputs/{slot:int}/settings", async (int slot,HttpContext context,Supervisor supervisor)=>{
    if(!context.Request.HasJsonContentType())return Results.StatusCode(415);
    var request=await context.Request.ReadFromJsonAsync<JsonObject>() ?? throw new InvalidDataException("Settings required");
    if(request["slot"]?.GetValue<int>()!=slot)return Results.BadRequest(new{error="Output path and body differ"});
    return Results.Json(supervisor.AcceptConfiguration(context.Request.Headers["Idempotency-Key"].ToString(),request),statusCode:202);
});
app.MapPut("/api/v1/outputs/{slot:int}/route", async (int slot, HttpContext context, Supervisor supervisor) => {
    if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
    var request = await context.Request.ReadFromJsonAsync<JsonObject>() ?? throw new InvalidDataException("Request required");
    if (request["slot"]?.GetValue<int>() != slot) return Results.BadRequest(new { error = "Output path and body differ" });
    return Results.Json(supervisor.AcceptRoute(context.Request.Headers["Idempotency-Key"].ToString(), request), statusCode: 202);
});
app.MapPost("/api/v1/emergency/prepare", async (HttpContext context, Supervisor supervisor) => {
    if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
    var request = await context.Request.ReadFromJsonAsync<JsonObject>();
    if (request?["schemaVersion"]?.GetValue<int>() != 1 || request["plan"] is not JsonObject plan)
        return Results.BadRequest(new { error = "Versioned commissioned plan required" });
    var result = supervisor.AcceptPrepare(context.Request.Headers["Idempotency-Key"].ToString(), plan);
    return Results.Json(result, statusCode: 202);
});
app.MapGet("/api/v1/operations/{id}", (string id, Supervisor supervisor) => supervisor.Operation(id) is { } operation ? Results.Json(operation) : Results.NotFound());
app.MapPost("/api/v1/outputs/{slot:int}/stop",async(int slot,HttpContext context,Supervisor supervisor)=>{
    if(!context.Request.HasJsonContentType())return Results.StatusCode(415);
    var request=await context.Request.ReadFromJsonAsync<JsonObject>() ?? throw new InvalidDataException("Expected state required");
    if(request["slot"]?.GetValue<int>()!=slot)return Results.BadRequest(new{error="Output path and body differ"});
    return Results.Json(supervisor.AcceptStop(context.Request.Headers["Idempotency-Key"].ToString(),request),statusCode:202);
});
app.MapPost("/api/v1/outputs/restore",async(HttpContext context,Supervisor supervisor)=>{
    if(!context.Request.HasJsonContentType())return Results.StatusCode(415);
    var request=await context.Request.ReadFromJsonAsync<JsonObject>() ?? throw new InvalidDataException("Output snapshot required");
    return Results.Json(supervisor.AcceptRestore(context.Request.Headers["Idempotency-Key"].ToString(),request),statusCode:202);
});
app.MapPost("/api/v1/outputs/stop",async(HttpContext context,Supervisor supervisor)=>{
    if(!context.Request.HasJsonContentType())return Results.StatusCode(415);
    var request=await context.Request.ReadFromJsonAsync<JsonObject>() ?? throw new InvalidDataException("Expected workers required");
    return Results.Json(supervisor.AcceptStopAll(context.Request.Headers["Idempotency-Key"].ToString(),request),statusCode:202);
});
app.MapPost("/api/v1/lifecycle/stop",async(HttpContext context,Supervisor supervisor,IHostApplicationLifetime lifetime)=>{
    var state=await supervisor.State("shutdown-check",context.RequestAborted);
    if(state["pendingOperationId"]!=null || state["outputs"]!.AsArray().Any(o=>o?["running"]?.GetValue<bool>()==true))
        return Results.Conflict(new{error="Stop all sources and wait for confirmation before stopping the engine"});
    context.Response.OnCompleted(()=>{lifetime.StopApplication();return Task.CompletedTask;});
    return Results.Json(new{accepted=true});
});
var sockets = new SemaphoreSlim(4);
app.Map("/api/v1/events", async (HttpContext context, Supervisor supervisor) => {
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    if (!await sockets.WaitAsync(0)) { context.Response.StatusCode = 429; return; }
    try
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
        var receive = Task.Run(async () => { try { var bytes = new byte[256]; await socket.ReceiveAsync(bytes, lifetime.Token); } catch (Exception) { } finally { lifetime.Cancel(); } });
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var state = await supervisor.State(Guid.NewGuid().ToString(), lifetime.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(1000);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(state), WebSocketMessageType.Text, true, deadline.Token);
                await Task.Delay(500, lifetime.Token);
            }
        }
        catch (Exception) when (lifetime.IsCancellationRequested || socket.State != WebSocketState.Open) { }
        finally { lifetime.Cancel(); socket.Abort(); await receive; }
    }
    finally { sockets.Release(); }
});
await app.RunAsync();

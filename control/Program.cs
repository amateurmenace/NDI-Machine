using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using NDIMachine.Control;

var builder = WebApplication.CreateBuilder(args);
var root = builder.Configuration["root"] ?? throw new ArgumentException("--root project directory required");
var path = builder.Configuration["commission"] ?? Path.Combine(root, "config", "local.commissioning.json");
var commission = new Commission(path, root);
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
app.MapPost("/api/v1/emergency/prepare", async (HttpContext context, Supervisor supervisor) => {
    if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
    var request = await context.Request.ReadFromJsonAsync<JsonObject>();
    if (request?["schemaVersion"]?.GetValue<int>() != 1 || request["plan"] is not JsonObject plan)
        return Results.BadRequest(new { error = "Versioned commissioned plan required" });
    var result = supervisor.AcceptPrepare(context.Request.Headers["Idempotency-Key"].ToString(), plan);
    return Results.Json(result, statusCode: 202);
});
app.MapGet("/api/v1/operations/{id}", (string id, Supervisor supervisor) => supervisor.Operation(id) is { } operation ? Results.Json(operation) : Results.NotFound());
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

using System.Text.Json.Serialization;
using ScreenEase.Core;
using ScreenEase.CoreService;

var pipeOnly = args.Any(argument => string.Equals(argument, "--pipe-only", StringComparison.OrdinalIgnoreCase))
               || string.Equals(Environment.GetEnvironmentVariable("ScreenEase__Transport"), "pipe", StringComparison.OrdinalIgnoreCase);

if (pipeOnly)
{
    var hostArgs = args
        .Where(argument => !string.Equals(argument, "--pipe-only", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var hostBuilder = Host.CreateApplicationBuilder(hostArgs);
    AddCoreServices(hostBuilder.Services, hostBuilder.Configuration);
    hostBuilder.Services.AddHostedService<CareLoopService>();
    hostBuilder.Services.AddHostedService<NamedPipeCommandService>();
    await hostBuilder.Build().RunAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "frontend",
        policy => policy
            .WithOrigins(
                "http://localhost:3000",
                "http://localhost:5173",
                "http://127.0.0.1:3000",
                "http://127.0.0.1:5173")
            .AllowAnyHeader()
            .AllowAnyMethod());
});

AddCoreServices(builder.Services, builder.Configuration);
builder.Services.AddHostedService<CareLoopService>();
builder.Services.AddHostedService<NamedPipeCommandService>();

var app = builder.Build();

app.UseCors("frontend");

app.MapGet("/", () => Results.Ok(new
{
    name = "ScreenEase Core Service",
    version = "0.1.0",
    api = "/api/state",
    pipe = NamedPipeCommandService.DefaultPipeName
}));

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

var api = app.MapGroup("/api");

api.MapGet("/state", async (EyeCareController controller, CancellationToken cancellationToken) =>
    Results.Ok(await controller.GetStateAsync(cancellationToken)));

api.MapGet("/settings", async (EyeCareController controller, CancellationToken cancellationToken) =>
{
    var state = await controller.GetStateAsync(cancellationToken);
    return Results.Ok(state.Settings);
});

api.MapPut("/settings", async (
    EyeCareSettings settings,
    EyeCareController controller,
    CancellationToken cancellationToken) =>
{
    var updated = await controller.UpdateSettingsAsync(settings, cancellationToken);
    return Results.Ok(updated);
});

api.MapGet("/profiles", async (EyeCareController controller, CancellationToken cancellationToken) =>
{
    var state = await controller.GetStateAsync(cancellationToken);
    return Results.Ok(state.Settings.Profiles);
});

api.MapPost("/apply", async (
    ApplyEffectCommand command,
    EyeCareController controller,
    CancellationToken cancellationToken) =>
{
    var effect = await controller.ApplyAsync(command, cancellationToken);
    return Results.Ok(effect);
});

api.MapPost("/disable", async (EyeCareController controller, CancellationToken cancellationToken) =>
{
    var effect = await controller.DisableAsync(cancellationToken);
    return Results.Ok(effect);
});

api.MapGet("/monitors", async (EyeCareController controller, CancellationToken cancellationToken) =>
{
    var state = await controller.GetStateAsync(cancellationToken);
    return Results.Ok(state.Monitors);
});

api.MapGet("/overlay", async (EyeCareController controller, CancellationToken cancellationToken) =>
{
    var state = await controller.GetStateAsync(cancellationToken);
    return Results.Ok(state.Overlay);
});

api.MapPut("/overlay", async (
    UpdateOverlayCommand command,
    EyeCareController controller,
    CancellationToken cancellationToken) =>
{
    var overlay = await controller.UpdateOverlayAsync(command, cancellationToken);
    return Results.Ok(overlay);
});

api.MapGet("/hotkeys", async (EyeCareController controller, CancellationToken cancellationToken) =>
{
    var state = await controller.GetStateAsync(cancellationToken);
    return Results.Ok(new
    {
        configured = state.Settings.Hotkeys,
        active = state.Hotkeys
    });
});

api.MapPut("/hotkeys", async (
    IReadOnlyList<HotkeyBinding> bindings,
    EyeCareController controller,
    CancellationToken cancellationToken) =>
{
    var active = await controller.UpdateHotkeysAsync(bindings, cancellationToken);
    return Results.Ok(active);
});

api.MapGet("/rest-timer", async (EyeCareController controller, CancellationToken cancellationToken) =>
{
    var state = await controller.GetStateAsync(cancellationToken);
    return Results.Ok(state.RestTimer);
});

api.MapPost("/rest-timer/start", async (EyeCareController controller, CancellationToken cancellationToken) =>
    Results.Ok(await controller.StartRestTimerAsync(cancellationToken)));

api.MapPost("/rest-timer/pause", async (EyeCareController controller, CancellationToken cancellationToken) =>
    Results.Ok(await controller.PauseRestTimerAsync(cancellationToken)));

api.MapPost("/rest-timer/resume", async (EyeCareController controller, CancellationToken cancellationToken) =>
    Results.Ok(await controller.ResumeRestTimerAsync(cancellationToken)));

api.MapPost("/rest-timer/reset", async (EyeCareController controller, CancellationToken cancellationToken) =>
    Results.Ok(await controller.ResetRestTimerAsync(cancellationToken)));

api.MapPost("/import/legacy-settings", async (
    LegacyImportRequest request,
    EyeCareController controller,
    CancellationToken cancellationToken) =>
{
    var settings = await controller.ImportLegacySettingsAsync(request.Path, cancellationToken);
    return Results.Ok(settings);
});

app.Run();

static void AddCoreServices(IServiceCollection services, IConfiguration configuration)
{
    services.AddSingleton<ISettingsRepository>(_ =>
    {
        var configuredPath = configuration["ScreenEase:SettingsPath"];
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenEase",
                "settings.json")
            : configuredPath;
        return new JsonSettingsRepository(path);
    });
    services.AddSingleton<IDisplayDriver>(_ =>
    {
        var driver = configuration["ScreenEase:Driver"];
        if (string.Equals(driver, "memory", StringComparison.OrdinalIgnoreCase))
        {
            return new InMemoryDisplayDriver();
        }

        if (string.Equals(driver, "hybrid", StringComparison.OrdinalIgnoreCase))
        {
            return OperatingSystem.IsWindows() ? new WindowsHybridDisplayDriver() : new InMemoryDisplayDriver();
        }

        return OperatingSystem.IsWindows() ? new WindowsGammaDisplayDriver() : new InMemoryDisplayDriver();
    });
    services.AddSingleton<IOverlayDriver>(_ =>
    {
        var driver = configuration["ScreenEase:Driver"];
        if (string.Equals(driver, "memory", StringComparison.OrdinalIgnoreCase))
        {
            return new InMemoryOverlayDriver();
        }

        return OperatingSystem.IsWindows() ? new WindowsLayeredOverlayDriver() : new InMemoryOverlayDriver();
    });
    services.AddSingleton<IHotkeyManager>(_ =>
    {
        var driver = configuration["ScreenEase:Driver"];
        if (string.Equals(driver, "memory", StringComparison.OrdinalIgnoreCase))
        {
            return new InMemoryHotkeyManager();
        }

        return OperatingSystem.IsWindows() ? new WindowsHotkeyManager() : new InMemoryHotkeyManager();
    });
    services.AddSingleton<EyeCareController>();
}

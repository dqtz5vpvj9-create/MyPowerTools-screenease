using ScreenEase.Core;

var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var driverName = ReadOption(args, "--driver")
                 ?? Environment.GetEnvironmentVariable("ScreenEase__Driver")
                 ?? "windows";
var useMemory = string.Equals(driverName, "memory", StringComparison.OrdinalIgnoreCase)
                || args.Any(argument => string.Equals(argument, "--memory", StringComparison.OrdinalIgnoreCase));

var settingsPath = ReadOption(args, "--settings")
                   ?? Environment.GetEnvironmentVariable("ScreenEase__SettingsPath")
                   ?? Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                       "ScreenEase",
                       "native-host-settings.json");

IDisplayDriver display = useMemory || !OperatingSystem.IsWindows()
    ? new InMemoryDisplayDriver()
    : new WindowsGammaDisplayDriver();
IOverlayDriver overlay = useMemory || !OperatingSystem.IsWindows()
    ? new InMemoryOverlayDriver()
    : new WindowsLayeredOverlayDriver();
IHotkeyManager hotkeys = useMemory || !OperatingSystem.IsWindows()
    ? new InMemoryHotkeyManager()
    : new WindowsHotkeyManager();

var controller = new EyeCareController(
    new JsonSettingsRepository(settingsPath),
    display,
    overlay,
    hotkeys);
var handler = new NativeCommandHandler(controller);

try
{
    await controller.InitializeAsync(cancellation.Token);
    await NativeHostLoop.RunAsync(
        Console.OpenStandardInput(),
        Console.OpenStandardOutput(),
        handler,
        cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
}
finally
{
    await controller.ShutdownAsync(CancellationToken.None);
    (overlay as IDisposable)?.Dispose();
    (hotkeys as IDisposable)?.Dispose();
}

static string? ReadOption(string[] args, string name)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[index + 1];
        }
    }

    return null;
}



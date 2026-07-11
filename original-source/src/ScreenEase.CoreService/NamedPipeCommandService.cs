using System.IO.Pipes;
using ScreenEase.Core;

namespace ScreenEase.CoreService;

public sealed class NamedPipeCommandService(
    EyeCareController controller,
    IConfiguration configuration,
    ILogger<NamedPipeCommandService> logger) : BackgroundService
{
    public const string DefaultPipeName = "screenease.core";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = configuration.GetValue("ScreenEase:NamedPipe:Enabled", true);
        if (!enabled)
        {
            logger.LogInformation("Named pipe IPC is disabled.");
            return;
        }

        var pipeName = configuration["ScreenEase:NamedPipe:Name"];
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            pipeName = DefaultPipeName;
        }

        logger.LogInformation("Named pipe IPC listening on {PipeName}.", pipeName);
        var handler = new NativeCommandHandler(controller);

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                await NativeHostLoop.RunAsync(pipe, pipe, handler, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException exception)
            {
                logger.LogDebug(exception, "Named pipe client disconnected.");
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Named pipe IPC request failed.");
            }
        }
    }
}

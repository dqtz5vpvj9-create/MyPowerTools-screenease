using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace ScreenEase.Desktop;

public static class CoreServiceLauncher
{
    private const string PipePrefix = "pipe:";
    private const string PipePathPrefix = "\\\\.\\pipe\\";
    private const string ServiceExeName = "ScreenEase.CoreService.exe";
    private const string ServiceProjectPath = "src\\ScreenEase.CoreService\\ScreenEase.CoreService.csproj";

    public static bool CanLaunch(string endpoint) =>
        TryReadPipeName(endpoint, out _);

    public static async Task EnsureRunningAsync(
        string endpoint,
        Action? onStarting,
        CancellationToken cancellationToken)
    {
        if (!TryReadPipeName(endpoint, out var pipeName))
        {
            return;
        }

        if (await TryConnectPipeAsync(pipeName, TimeSpan.FromMilliseconds(300), cancellationToken))
        {
            return;
        }

        onStarting?.Invoke();
        var process = StartService(pipeName);
        await WaitForPipeAsync(pipeName, process, TimeSpan.FromSeconds(18), cancellationToken);
    }

    private static Process StartService(string pipeName)
    {
        var executablePath = FindServiceExecutable();
        if (executablePath is not null)
        {
            var startInfo = CreateHiddenStartInfo(
                executablePath,
                Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                pipeName);
            startInfo.ArgumentList.Add("--pipe-only");
            return StartProcess(startInfo);
        }

        var projectPath = FindServiceProject();
        if (projectPath is not null)
        {
            var startInfo = CreateHiddenStartInfo(
                "dotnet",
                FindRepositoryRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory,
                pipeName);
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(projectPath);
            startInfo.ArgumentList.Add("--configuration");
            startInfo.ArgumentList.Add(BuildConfigurationName);
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("--pipe-only");
            return StartProcess(startInfo);
        }

        throw new FileNotFoundException("找不到 ScreenEase 后端程序。请重新解压完整发布包，或先执行 dotnet build。");
    }

    private static ProcessStartInfo CreateHiddenStartInfo(
        string fileName,
        string workingDirectory,
        string pipeName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        startInfo.Environment["ScreenEase__Transport"] = "pipe";
        startInfo.Environment["ScreenEase__NamedPipe__Name"] = pipeName;
        if (!startInfo.Environment.ContainsKey("ScreenEase__Driver")
            || string.IsNullOrWhiteSpace(startInfo.Environment["ScreenEase__Driver"]))
        {
            startInfo.Environment["ScreenEase__Driver"] = "windows";
        }

        return startInfo;
    }

    private static Process StartProcess(ProcessStartInfo startInfo) =>
        Process.Start(startInfo)
        ?? throw new InvalidOperationException("后端进程启动失败。");

    private static async Task WaitForPipeAsync(
        string pipeName,
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"后端进程已退出，退出码 {process.ExitCode}。");
            }

            if (await TryConnectPipeAsync(pipeName, TimeSpan.FromMilliseconds(500), cancellationToken))
            {
                await Task.Delay(150, cancellationToken);
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("后端启动超时。");
    }

    private static async Task<bool> TryConnectPipeAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string? FindServiceExecutable()
    {
        foreach (var path in GetServiceExecutableCandidates())
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetServiceExecutableCandidates()
    {
        var appDirectory = AppContext.BaseDirectory;
        yield return Path.GetFullPath(Path.Combine(appDirectory, "..", "ScreenEase.CoreService", ServiceExeName));
        yield return Path.GetFullPath(Path.Combine(appDirectory, ServiceExeName));

        var repositoryRoot = FindRepositoryRoot(appDirectory);
        if (repositoryRoot is null)
        {
            yield break;
        }

        yield return Path.Combine(
            repositoryRoot,
            "src",
            "ScreenEase.CoreService",
            "bin",
            BuildConfigurationName,
            "net8.0",
            ServiceExeName);
    }

    private static string? FindServiceProject()
    {
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        if (repositoryRoot is null)
        {
            return null;
        }

        var projectPath = Path.Combine(repositoryRoot, ServiceProjectPath);
        return File.Exists(projectPath) ? projectPath : null;
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ScreenEase.sln"))
                || File.Exists(Path.Combine(directory.FullName, "ScreenEase.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool TryReadPipeName(string endpoint, out string pipeName)
    {
        var normalized = string.IsNullOrWhiteSpace(endpoint)
            ? "pipe:screenease.core"
            : endpoint.Trim();

        if (normalized.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase))
        {
            pipeName = normalized[PipePrefix.Length..].Trim();
            return !string.IsNullOrWhiteSpace(pipeName);
        }

        if (normalized.StartsWith(PipePathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            pipeName = normalized[PipePathPrefix.Length..].Trim();
            return !string.IsNullOrWhiteSpace(pipeName);
        }

        pipeName = string.Empty;
        return false;
    }

    private static string BuildConfigurationName
    {
        get
        {
#if DEBUG
            return "Debug";
#else
            return "Release";
#endif
        }
    }
}

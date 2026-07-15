namespace ScreenEase.Surface;

/// <summary>
/// Minimal fault-boundary stub for dotnet-surface tools. In the Shell, ShellCommandFaultBoundary
/// routes exceptions to the Shell's fault sink; in a surface context we simply invoke the action
/// and swallow exceptions to prevent a single tool fault from crashing the Shell.
/// </summary>
internal static class ShellCommandFaultBoundary
{
    public static void Run(object? source, string operationName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[{operationName}] {ex.Message}");
        }
    }

    public static void Run(object? source, string operationName, Func<Task> action)
    {
        try
        {
            action().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[{operationName}] {ex.Message}");
        }
    }
}

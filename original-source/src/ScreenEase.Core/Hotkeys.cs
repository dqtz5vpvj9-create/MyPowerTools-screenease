using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenEase.Core;

public interface IHotkeyManager
{
    Task ConfigureAsync(
        IReadOnlyList<HotkeyBinding> bindings,
        Func<HotkeyAction, CancellationToken, Task> handler,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<HotkeyBinding>> GetActiveBindingsAsync(CancellationToken cancellationToken);

    Task ResetAsync(CancellationToken cancellationToken);
}

public sealed class InMemoryHotkeyManager : IHotkeyManager
{
    private IReadOnlyList<HotkeyBinding> activeBindings = [];
    private Func<HotkeyAction, CancellationToken, Task>? handler;

    public Task ConfigureAsync(
        IReadOnlyList<HotkeyBinding> bindings,
        Func<HotkeyAction, CancellationToken, Task> nextHandler,
        CancellationToken cancellationToken)
    {
        handler = nextHandler;
        activeBindings = bindings.Where(binding => binding.Enabled).ToArray();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HotkeyBinding>> GetActiveBindingsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(activeBindings);

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        activeBindings = [];
        handler = null;
        return Task.CompletedTask;
    }

    public Task TriggerAsync(HotkeyAction action, CancellationToken cancellationToken) =>
        handler?.Invoke(action, cancellationToken) ?? Task.CompletedTask;
}

[SupportedOSPlatform("windows")]
public sealed class WindowsHotkeyManager : IHotkeyManager, IDisposable
{
    private const int BaseHotkeyId = 0x4FCE;
    private readonly BlockingCollection<WorkItem> queue = [];
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private readonly Dictionary<int, HotkeyBinding> registered = [];
    private readonly object stateLock = new();
    private IReadOnlyList<HotkeyBinding> activeBindings = [];
    private Func<HotkeyAction, CancellationToken, Task> handler = (_, _) => Task.CompletedTask;
    private bool disposed;

    public WindowsHotkeyManager()
    {
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ScreenEase hotkeys"
        };
        thread.Start();
        ready.Task.GetAwaiter().GetResult();
    }

    public Task ConfigureAsync(
        IReadOnlyList<HotkeyBinding> bindings,
        Func<HotkeyAction, CancellationToken, Task> nextHandler,
        CancellationToken cancellationToken)
    {
        handler = nextHandler;
        return Enqueue(
            () =>
            {
                UnregisterAll();

                var next = new List<HotkeyBinding>();
                var id = BaseHotkeyId;
                foreach (var binding in bindings.Where(binding => binding.Enabled))
                {
                    if (!HotkeyParser.TryParse(binding.Gesture, out var parsed))
                    {
                        continue;
                    }

                    if (Native.RegisterHotKey(IntPtr.Zero, id, parsed.Modifiers | Native.ModNoRepeat, parsed.VirtualKey))
                    {
                        registered[id] = binding;
                        next.Add(binding);
                        id++;
                    }
                }

                SetActiveBindings(next);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<HotkeyBinding>> GetActiveBindingsAsync(CancellationToken cancellationToken)
    {
        lock (stateLock)
        {
            return Task.FromResult(activeBindings);
        }
    }

    public Task ResetAsync(CancellationToken cancellationToken) =>
        Enqueue(
            () =>
            {
                UnregisterAll();
                SetActiveBindings([]);
            },
            cancellationToken);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        queue.CompleteAdding();
        thread.Join(TimeSpan.FromSeconds(2));
        queue.Dispose();
    }

    private Task Enqueue(Action action, CancellationToken cancellationToken)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsHotkeyManager));
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        queue.Add(new WorkItem(action, completion), CancellationToken.None);
        return completion.Task;
    }

    private void Run()
    {
        ready.SetResult();

        while (!queue.IsCompleted)
        {
            if (queue.TryTake(out var item, TimeSpan.FromMilliseconds(25)))
            {
                try
                {
                    item.Action();
                    item.Completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    item.Completion.TrySetException(exception);
                }
            }

            while (Native.PeekMessage(out var message, IntPtr.Zero, 0, 0, Native.PmRemove))
            {
                if (message.MessageId == Native.WmHotkey && registered.TryGetValue(message.WParam.ToInt32(), out var binding))
                {
                    _ = Task.Run(() => handler(binding.Action, CancellationToken.None));
                    continue;
                }

                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
        }

        UnregisterAll();
    }

    private void UnregisterAll()
    {
        foreach (var id in registered.Keys.ToArray())
        {
            Native.UnregisterHotKey(IntPtr.Zero, id);
        }

        registered.Clear();
    }

    private void SetActiveBindings(IReadOnlyList<HotkeyBinding> bindings)
    {
        lock (stateLock)
        {
            activeBindings = bindings.ToArray();
        }
    }

    private sealed record WorkItem(Action Action, TaskCompletionSource Completion);

    private static partial class Native
    {
        public const uint WmHotkey = 0x0312;
        public const uint ModAlt = 0x0001;
        public const uint ModControl = 0x0002;
        public const uint ModShift = 0x0004;
        public const uint ModWin = 0x0008;
        public const uint ModNoRepeat = 0x4000;
        public const uint PmRemove = 0x0001;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PeekMessage(out Message message, IntPtr window, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref Message message);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref Message message);

        [StructLayout(LayoutKind.Sequential)]
        public struct Message
        {
            public IntPtr Window;
            public uint MessageId;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int PointX;
            public int PointY;
        }
    }
}

public readonly record struct ParsedHotkey(uint Modifiers, uint VirtualKey);

public static class HotkeyParser
{
    private static readonly IReadOnlyDictionary<string, uint> NamedKeys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    {
        ["UP"] = 0x26,
        ["DOWN"] = 0x28,
        ["LEFT"] = 0x25,
        ["RIGHT"] = 0x27,
        ["PAGEUP"] = 0x21,
        ["PAGEDOWN"] = 0x22,
        ["HOME"] = 0x24,
        ["END"] = 0x23,
        ["INSERT"] = 0x2D,
        ["DELETE"] = 0x2E,
        ["SPACE"] = 0x20
    };

    public static bool TryParse(string gesture, out ParsedHotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        var modifiers = 0u;
        uint? virtualKey = null;

        foreach (var part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModWin;
                    break;
                default:
                    if (TryParseKey(part, out var parsedKey))
                    {
                        virtualKey = parsedKey;
                    }

                    break;
            }
        }

        if (virtualKey is null || modifiers == 0)
        {
            return false;
        }

        hotkey = new ParsedHotkey(modifiers, virtualKey.Value);
        return true;
    }

    private static bool TryParseKey(string value, out uint virtualKey)
    {
        virtualKey = 0;
        if (NamedKeys.TryGetValue(value, out virtualKey))
        {
            return true;
        }

        if (value.Length == 1)
        {
            var character = char.ToUpperInvariant(value[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = character;
                return true;
            }
        }

        if (value.Length is 2 or 3
            && value.StartsWith('F')
            && int.TryParse(value[1..], out var functionKey)
            && functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKey - 1);
            return true;
        }

        return false;
    }

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
}



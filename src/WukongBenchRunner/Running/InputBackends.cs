using System.Runtime.InteropServices;

namespace WukongBenchRunner.Running;

public interface IInputBackend
{
    string Name { get; }
    bool Activate(WindowInfo window);
    bool ClickRelative(WindowInfo window, double relativeX, double relativeY);
    bool PressReturn();
}

public static class InputBackendFactory
{
    public static IInputBackend Create()
    {
        if (XTestInput.IsAvailable)
            return new XTestInput();

        if (Shell.HasExecutable("xdotool"))
            return new XdotoolInput();

        throw new InvalidOperationException(
            "Нет доступного способа эмуляции ввода: не работает XTEST в XWayland и нет xdotool."
            + Environment.NewLine + "Установите: sudo apt install xdotool");
    }
}

public sealed class XTestInput : IInputBackend
{
    private const ulong ReturnKeysym = 0xFF0D;
    private const int RevertToParent = 2;
    private const ulong CurrentTime = 0;

    private static IntPtr? _display;
    private static readonly object DisplayLock = new();
    private static readonly XErrorHandler ErrorHandlerDelegate = IgnoreXError;
    private static readonly IntPtr ErrorHandler = Marshal.GetFunctionPointerForDelegate(ErrorHandlerDelegate);
    private static bool _errorHandlerInstalled;

    public string Name => "XTEST (libX11/libXtst)";

    public static bool IsAvailable => TryOpenDisplay() is not null;

    public bool Activate(WindowInfo window)
    {
        if (TryOpenDisplay() is not { } display)
            return false;

        XSetInputFocus(display, window.Id, RevertToParent, CurrentTime);
        XFlush(display);
        return true;
    }

    public bool ClickRelative(WindowInfo window, double relativeX, double relativeY)
    {
        if (!TryOpenDisplay().HasValue)
            return false;

        var display = TryOpenDisplay()!.Value;

        var offsetX = (int)Math.Round(window.Width * relativeX);
        var offsetY = (int)Math.Round(window.Height * relativeY);

        XWarpPointer(display, 0, window.Id, 0, 0, 0, 0, offsetX, offsetY, 0, 0);
        XFlush(display);
        Thread.Sleep(150);

        XTestFakeButtonEvent(display, 1, true, CurrentTime);
        XFlush(display);
        Thread.Sleep(60);
        XTestFakeButtonEvent(display, 1, false, CurrentTime);
        XFlush(display);
        XSync(display, 0);
        return true;
    }

    public bool PressReturn()
    {
        if (!TryOpenDisplay().HasValue)
            return false;

        var display = TryOpenDisplay()!.Value;

        var keycode = (uint)XKeysymToKeycode(display, ReturnKeysym);
        if (keycode == 0)
            return false;

        XTestFakeKeyEvent(display, keycode, true, CurrentTime);
        XFlush(display);
        Thread.Sleep(60);
        XTestFakeKeyEvent(display, keycode, false, CurrentTime);
        XFlush(display);
        XSync(display, 0);
        return true;
    }

    private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    private static int IgnoreXError(IntPtr display, IntPtr errorEvent)
    {
        Console.Error.WriteLine("X11: ошибка протокола (окно, вероятно, пересоздано) - игнорирую.");
        return 0;
    }

    private static IntPtr? TryOpenDisplay()
    {
        lock (DisplayLock)
        {
            if (_display is { } cached)
                return cached;

            var name = Environment.GetEnvironmentVariable("DISPLAY");
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var display = XOpenDisplay(name);
            if (display == IntPtr.Zero)
                return null;

            if (!_errorHandlerInstalled)
            {
                XSetErrorHandler(ErrorHandler);
                _errorHandlerInstalled = true;
            }

            _display = display;
            return _display;
        }
    }

    [DllImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
    private static extern IntPtr XOpenDisplay(string? name);

    [DllImport("libX11.so.6", EntryPoint = "XSetInputFocus")]
    private static extern int XSetInputFocus(IntPtr display, ulong window, int revertTo, ulong time);

    [DllImport("libX11.so.6", EntryPoint = "XWarpPointer")]
    private static extern int XWarpPointer(IntPtr display, ulong source, ulong destination, int sourceX, int sourceY, uint sourceWidth, uint sourceHeight, int destinationX, int destinationY, uint destinationWidth, uint destinationHeight);

    [DllImport("libX11.so.6", EntryPoint = "XKeysymToKeycode")]
    private static extern ulong XKeysymToKeycode(IntPtr display, ulong keysym);

    [DllImport("libX11.so.6", EntryPoint = "XSetErrorHandler")]
    private static extern IntPtr XSetErrorHandler(IntPtr handler);

    [DllImport("libX11.so.6", EntryPoint = "XFlush")]
    private static extern int XFlush(IntPtr display);

    [DllImport("libX11.so.6", EntryPoint = "XSync")]
    private static extern int XSync(IntPtr display, int discard);

    [DllImport("libXtst.so.6", EntryPoint = "XTestFakeButtonEvent")]
    private static extern int XTestFakeButtonEvent(IntPtr display, uint button, [MarshalAs(UnmanagedType.I1)] bool isPress, ulong delay);

    [DllImport("libXtst.so.6", EntryPoint = "XTestFakeKeyEvent")]
    private static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, [MarshalAs(UnmanagedType.I1)] bool isPress, ulong delay);
}

public sealed class XdotoolInput : IInputBackend
{
    public string Name => "xdotool";

    public bool Activate(WindowInfo window)
    {
        var (exitCode, _, _) = Shell.Run("xdotool", ["windowactivate", "--sync", window.Id.ToString("x")], timeoutMs: 5_000);
        return exitCode == 0;
    }

    public bool ClickRelative(WindowInfo window, double relativeX, double relativeY)
    {
        var offsetX = (int)Math.Round(window.Width * relativeX);
        var offsetY = (int)Math.Round(window.Height * relativeY);
        var id = window.Id.ToString("x");

        Shell.Run("xdotool", ["mousemove", "--window", id, offsetX.ToString(), offsetY.ToString()]);
        Thread.Sleep(150);

        var (exitCode, _, _) = Shell.Run("xdotool", ["click", "--window", id, "1"]);
        return exitCode == 0;
    }

    public bool PressReturn()
    {
        var (exitCode, _, _) = Shell.Run("xdotool", ["key", "--clearmodifiers", "Return"]);
        return exitCode == 0;
    }
}

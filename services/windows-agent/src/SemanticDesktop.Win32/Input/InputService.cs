using System.Runtime.InteropServices;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Win32.Native;

namespace SemanticDesktop.Win32.Input;

public sealed class InputService
{
    public const string Provider = "SendInput";
    public const string DefaultFallbackReason = "semantic_interfaces_unavailable";

    public object MouseMove(int x, int y, double? dpiScale = null, string? fallbackReason = null)
    {
        var point = ResolvePoint(x, y, dpiScale);
        Send(MoveAbsolute(point.X, point.Y));
        return Result("input.mouse_move", new { x = point.X, y = point.Y, virtualScreen = ScreenCoordinates.GetVirtualScreen() }, fallbackReason);
    }

    public object MouseClick(int x, int y, string button = "left", int clickCount = 1, double? dpiScale = null, string? fallbackReason = null)
    {
        var point = ResolvePoint(x, y, dpiScale);
        var (down, up) = ButtonFlags(button);
        var inputs = new List<NativeMethods.INPUT> { MoveAbsolute(point.X, point.Y) };
        var count = Math.Clamp(clickCount, 1, 3);
        for (var i = 0; i < count; i++)
        {
            inputs.Add(Mouse(down));
            inputs.Add(Mouse(up));
        }

        Send(inputs.ToArray());
        return Result("input.mouse_click", new { x = point.X, y = point.Y, button, clickCount = count }, fallbackReason);
    }

    public object MouseDrag(int fromX, int fromY, int toX, int toY, string button = "left", double? dpiScale = null, string? fallbackReason = null)
    {
        var from = ResolvePoint(fromX, fromY, dpiScale);
        var to = ResolvePoint(toX, toY, dpiScale);
        var (down, up) = ButtonFlags(button);
        Send(
            MoveAbsolute(from.X, from.Y),
            Mouse(down),
            MoveAbsolute(to.X, to.Y),
            Mouse(up));
        return Result("input.mouse_drag", new { fromX = from.X, fromY = from.Y, toX = to.X, toY = to.Y, button }, fallbackReason);
    }

    public object Scroll(int? x, int? y, int delta = 120, string axis = "vertical", double? dpiScale = null, string? fallbackReason = null)
    {
        var inputs = new List<NativeMethods.INPUT>();
        if (x is not null && y is not null)
        {
            var point = ResolvePoint(x.Value, y.Value, dpiScale);
            inputs.Add(MoveAbsolute(point.X, point.Y));
        }

        var flags = axis.Equals("horizontal", StringComparison.OrdinalIgnoreCase)
            ? NativeMethods.MOUSEEVENTF_HWHEEL
            : NativeMethods.MOUSEEVENTF_WHEEL;
        inputs.Add(Mouse(flags, unchecked((uint)delta)));
        Send(inputs.ToArray());
        return Result("input.scroll", new { x, y, delta, axis }, fallbackReason);
    }

    public object Key(string key, string action = "press", string? fallbackReason = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException($"{ErrorCodes.InvalidArgument}: key is required.");
        }

        var vk = ResolveVirtualKey(key);
        var inputs = action.ToLowerInvariant() switch
        {
            "down" => new[] { KeyDown(vk) },
            "up" => new[] { KeyUp(vk) },
            _ => new[] { KeyDown(vk), KeyUp(vk) }
        };
        Send(inputs);
        return Result("input.key", new { key, action, vk }, fallbackReason);
    }

    public object Hotkey(IReadOnlyList<string> keys, string? fallbackReason = null)
    {
        if (keys is null || keys.Count == 0)
        {
            throw new ArgumentException($"{ErrorCodes.InvalidArgument}: keys are required.");
        }

        var vks = keys.Select(ResolveVirtualKey).ToArray();
        var inputs = new List<NativeMethods.INPUT>();
        foreach (var vk in vks)
        {
            inputs.Add(KeyDown(vk));
        }

        for (var i = vks.Length - 1; i >= 0; i--)
        {
            inputs.Add(KeyUp(vks[i]));
        }

        Send(inputs.ToArray());
        return Result("input.hotkey", new { keys }, fallbackReason);
    }

    public object TypeText(string text, int delayMs = 0, string? fallbackReason = null)
    {
        if (text is null)
        {
            throw new ArgumentException($"{ErrorCodes.InvalidArgument}: text is required.");
        }

        var inputs = new List<NativeMethods.INPUT>();
        foreach (var ch in text)
        {
            if (ch is '\r')
            {
                continue;
            }

            if (ch is '\n')
            {
                inputs.Add(KeyDown(0x0D));
                inputs.Add(KeyUp(0x0D));
                continue;
            }

            inputs.Add(UnicodeDown(ch));
            inputs.Add(UnicodeUp(ch));
        }

        Send(inputs.ToArray());
        if (delayMs > 0)
        {
            Thread.Sleep(Math.Min(delayMs, 5000));
        }

        return Result("input.type", new { length = text.Length }, fallbackReason);
    }

    private static ScreenPoint ResolvePoint(int x, int y, double? dpiScale)
    {
        if (dpiScale is > 0 and not 1.0)
        {
            return ScreenCoordinates.FromLogical(x, y, dpiScale.Value);
        }

        return new ScreenPoint(x, y);
    }

    private static object Result(string operation, object data, string? fallbackReason) =>
        new
        {
            operation,
            data,
            provider = Provider,
            fallback_reason = string.IsNullOrWhiteSpace(fallbackReason) ? DefaultFallbackReason : fallbackReason
        };

    private static (uint Down, uint Up) ButtonFlags(string button) =>
        button.ToLowerInvariant() switch
        {
            "right" => (NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP),
            "middle" => (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP),
            _ => (NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP)
        };

    private static NativeMethods.INPUT MoveAbsolute(int x, int y)
    {
        var (nx, ny) = ScreenCoordinates.ToAbsoluteNormalized(x, y);
        return Mouse(
            NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
            0,
            nx,
            ny);
    }

    private static NativeMethods.INPUT Mouse(uint flags, uint data = 0, int dx = 0, int dy = 0) => new()
    {
        type = NativeMethods.INPUT_MOUSE,
        U = new NativeMethods.InputUnion
        {
            mi = new NativeMethods.MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                mouseData = data,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    private static NativeMethods.INPUT KeyDown(ushort vk) => Key(vk, 0);
    private static NativeMethods.INPUT KeyUp(ushort vk) => Key(vk, NativeMethods.KEYEVENTF_KEYUP);

    private static NativeMethods.INPUT Key(ushort vk, uint flags)
    {
        var scan = (ushort)NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC);
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static NativeMethods.INPUT UnicodeDown(char ch) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = 0,
                wScan = ch,
                dwFlags = NativeMethods.KEYEVENTF_UNICODE,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    private static NativeMethods.INPUT UnicodeUp(char ch) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = 0,
                wScan = ch,
                dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    private static void Send(params NativeMethods.INPUT[] inputs)
    {
        if (inputs.Length == 0)
        {
            return;
        }

        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException($"{ErrorCodes.Internal}: SendInput sent {sent}/{inputs.Length}.");
        }
    }

    private static ushort ResolveVirtualKey(string key)
    {
        var k = key.Trim();
        if (k.Length == 1)
        {
            var scan = NativeMethods.VkKeyScanW(k[0]);
            if (scan == -1)
            {
                throw new ArgumentException($"{ErrorCodes.InvalidArgument}: unsupported key '{key}'.");
            }

            return (ushort)(scan & 0xFF);
        }

        return k.ToLowerInvariant() switch
        {
            "ctrl" or "control" => 0x11,
            "alt" or "menu" => 0x12,
            "shift" => 0x10,
            "win" or "lwin" or "meta" => 0x5B,
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            "esc" or "escape" => 0x1B,
            "space" => 0x20,
            "backspace" => 0x08,
            "delete" or "del" => 0x2E,
            "insert" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "f1" => 0x70,
            "f2" => 0x71,
            "f3" => 0x72,
            "f4" => 0x73,
            "f5" => 0x74,
            "f6" => 0x75,
            "f7" => 0x76,
            "f8" => 0x77,
            "f9" => 0x78,
            "f10" => 0x79,
            "f11" => 0x7A,
            "f12" => 0x7B,
            _ when k.StartsWith("vk_", StringComparison.OrdinalIgnoreCase) &&
                   ushort.TryParse(k[3..], System.Globalization.NumberStyles.HexNumber, null, out var hex) => hex,
            _ when ushort.TryParse(k, out var num) => num,
            _ => throw new ArgumentException($"{ErrorCodes.InvalidArgument}: unknown key '{key}'.")
        };
    }
}

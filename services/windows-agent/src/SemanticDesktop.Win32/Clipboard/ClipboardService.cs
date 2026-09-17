using System.Runtime.InteropServices;
using System.Text;
using SemanticDesktop.Core.Errors;

namespace SemanticDesktop.Win32.Clipboard;

public static class ClipboardService
{
    private const uint CF_UNICODETEXT = 13;

    public static object Read()
    {
        string? text = null;
        Exception? error = null;
        RunSta(() =>
        {
            try
            {
                text = ReadUnicodeText() ?? string.Empty;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        if (error is not null)
        {
            throw new InvalidOperationException($"{ErrorCodes.Internal}: {error.Message}", error);
        }

        text ??= string.Empty;
        return new { text, length = text.Length };
    }

    public static object Write(string text)
    {
        text ??= string.Empty;
        Exception? error = null;
        RunSta(() =>
        {
            try
            {
                WriteUnicodeText(text);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        if (error is not null)
        {
            throw new InvalidOperationException($"{ErrorCodes.Internal}: {error.Message}", error);
        }

        return new { written = true, length = text.Length };
    }

    private static void RunSta(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            action();
            return;
        }

        Exception? threadError = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                threadError = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (threadError is not null)
        {
            throw threadError;
        }
    }

    private static string? ReadUnicodeText()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            throw new InvalidOperationException("OpenClipboard failed.");
        }

        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
            {
                return string.Empty;
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer) ?? string.Empty;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void WriteUnicodeText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            throw new InvalidOperationException("OpenClipboard failed.");
        }

        try
        {
            EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
            if (hGlobal == IntPtr.Zero)
            {
                throw new OutOfMemoryException("GlobalAlloc failed.");
            }

            var pointer = GlobalLock(hGlobal);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                throw new InvalidOperationException("GlobalLock failed.");
            }

            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                throw new InvalidOperationException("SetClipboardData failed.");
            }
            // Ownership transferred to the clipboard.
        }
        finally
        {
            CloseClipboard();
        }
    }

    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}

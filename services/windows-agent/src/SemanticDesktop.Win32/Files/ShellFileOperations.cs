using System.Diagnostics;
using System.Runtime.InteropServices;
using SemanticDesktop.Core.Errors;

namespace SemanticDesktop.Win32.Files;

/// <summary>Shell file operations (recycle bin) via SHFileOperationW.</summary>
public static class ShellFileOperations
{
    private const ushort FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOERRORUI = 0x0400;

    public static void Recycle(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("path is required.", nameof(path));
        }

        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            throw new InvalidOperationException($"{ErrorCodes.NotFound}: Path not found: {full}");
        }

        // Double-null-terminated path list required by SHFileOperation.
        var pathBuffer = full + "\0\0";
        var fileOp = new SHFILEOPSTRUCT
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            pFrom = pathBuffer,
            pTo = null,
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
            fAnyOperationsAborted = false,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null
        };

        var result = SHFileOperationW(ref fileOp);
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"{ErrorCodes.Internal}: SHFileOperation recycle failed (code {result}).");
        }
    }

    public static void OpenPath(string path, bool select)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("path is required.", nameof(path));
        }

        var full = Path.GetFullPath(path);
        try
        {
            if (select)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + full + "\"",
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = full,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{ErrorCodes.ProcessFailed}: {ex.Message}", ex);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT FileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public ushort wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }
}

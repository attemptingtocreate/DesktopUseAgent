using System.Runtime.InteropServices;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Win32.Native;

namespace SemanticDesktop.Win32.Media;

/// <summary>Media transport keys + volume (SendInput / Core Audio endpoint).</summary>
public static class MediaService
{
    public const string Provider = "media";

    private const ushort VkMediaNextTrack = 0xB0;
    private const ushort VkMediaPrevTrack = 0xB1;
    private const ushort VkMediaStop = 0xB2;
    private const ushort VkMediaPlayPause = 0xB3;
    private const ushort VkVolumeMute = 0xAD;
    private const ushort VkVolumeDown = 0xAE;
    private const ushort VkVolumeUp = 0xAF;

    public static object Transport(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException($"{ErrorCodes.InvalidArgument}: action is required.");
        }

        var normalized = NormalizeTransport(action);
        var vk = normalized switch
        {
            "play_pause" => VkMediaPlayPause,
            "next" => VkMediaNextTrack,
            "previous" => VkMediaPrevTrack,
            "stop" => VkMediaStop,
            _ => throw new ArgumentException(
                $"{ErrorCodes.InvalidArgument}: action must be play_pause|next|previous|stop.")
        };

        SendKey(vk);
        return new
        {
            action = normalized,
            vk,
            provider = Provider
        };
    }

    public static object Volume(string action, int? level)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException($"{ErrorCodes.InvalidArgument}: action is required.");
        }

        var act = action.Trim().ToLowerInvariant();
        return act switch
        {
            "up" => VolumeKey("up", VkVolumeUp),
            "down" => VolumeKey("down", VkVolumeDown),
            "mute" => VolumeKey("mute", VkVolumeMute),
            "set" => SetVolume(level),
            _ => throw new ArgumentException(
                $"{ErrorCodes.InvalidArgument}: action must be up|down|mute|set.")
        };
    }

    private static object VolumeKey(string action, ushort vk)
    {
        SendKey(vk);
        return new
        {
            action,
            vk,
            provider = Provider,
            method = "SendInput"
        };
    }

    private static object SetVolume(int? level)
    {
        if (level is null)
        {
            throw new ArgumentException($"{ErrorCodes.InvalidArgument}: level (0-100) is required for action=set.");
        }

        if (level is < 0 or > 100)
        {
            throw new ArgumentException($"{ErrorCodes.InvalidArgument}: level must be 0..100.");
        }

        if (!TrySetEndpointVolume(level.Value / 100.0, out var actual, out var error))
        {
            throw new InvalidOperationException(
                $"{ErrorCodes.Unsupported}: CoreAudio EndpointVolume unavailable ({error}). Use action up/down/mute.");
        }

        return new
        {
            action = "set",
            level = level.Value,
            actualLevel = (int)Math.Round(actual * 100.0),
            provider = Provider,
            method = "CoreAudio"
        };
    }

    private static string NormalizeTransport(string action) =>
        action.Trim().ToLowerInvariant() switch
        {
            "playpause" or "play-pause" => "play_pause",
            "prev" => "previous",
            var a => a
        };

    private static void SendKey(ushort vk)
    {
        var inputs = new[]
        {
            Key(vk, 0),
            Key(vk, NativeMethods.KEYEVENTF_KEYUP)
        };
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException($"{ErrorCodes.Internal}: SendInput sent {sent}/{inputs.Length}.");
        }
    }

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

    private static bool TrySetEndpointVolume(double scalar, out double actual, out string error)
    {
        actual = 0;
        error = "unknown";
        object? enumerator = null;
        object? device = null;
        object? endpoint = null;
        try
        {
            var mmDevEnumClsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
            var type = Type.GetTypeFromCLSID(mmDevEnumClsid, throwOnError: false);
            if (type is null)
            {
                error = "MMDeviceEnumerator CLSID unavailable";
                return false;
            }

            enumerator = Activator.CreateInstance(type);
            if (enumerator is not IMMDeviceEnumerator mm)
            {
                error = "IMMDeviceEnumerator cast failed";
                return false;
            }

            mm.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var immDevice);
            device = immDevice;
            if (immDevice is null)
            {
                error = "no default render endpoint";
                return false;
            }

            var iid = typeof(IAudioEndpointVolume).GUID;
            immDevice.Activate(ref iid, ClsCtx.ALL, IntPtr.Zero, out var obj);
            endpoint = obj;
            if (obj is not IAudioEndpointVolume volume)
            {
                error = "IAudioEndpointVolume activate failed";
                return false;
            }

            volume.SetMasterVolumeLevelScalar((float)Math.Clamp(scalar, 0.0, 1.0), Guid.Empty);
            volume.GetMasterVolumeLevelScalar(out var current);
            actual = current;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            ReleaseCom(endpoint);
            ReleaseCom(device);
            ReleaseCom(enumerator);
        }
    }

    private static void ReleaseCom(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            try { Marshal.ReleaseComObject(com); } catch { /* ignore */ }
        }
    }

    private enum EDataFlow
    {
        eRender = 0,
        eCapture = 1,
        eAll = 2
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    [Flags]
    private enum ClsCtx : uint
    {
        ALL = 0x17
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        // EnumAudioEndpoints
        void StubEnumAudioEndpoints();
        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, ClsCtx dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void StubRegisterControlChangeNotify();
        void StubUnregisterControlChangeNotify();
        void StubGetChannelCount();
        void StubSetMasterVolumeLevel();
        void SetMasterVolumeLevelScalar(float fLevel, Guid pguidEventContext);
        void StubGetMasterVolumeLevel();
        void GetMasterVolumeLevelScalar(out float pfLevel);
    }
}

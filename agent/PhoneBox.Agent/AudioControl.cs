using System.Runtime.InteropServices;

namespace PhoneBox.Agent;

/// <summary>Master volume of the default playback device (Windows Core Audio).</summary>
public sealed class AudioControl
{
    private static Guid _ctx = Guid.Empty;
    private readonly Lock _lock = new();
    private IAudioEndpointVolume? _ep;
    private long _acquiredAt;

    public AudioSnapshot? Snapshot() => Use(ep =>
    {
        ep.GetMasterVolumeLevelScalar(out var v);
        ep.GetMute(out var m);
        return new AudioSnapshot((int)Math.Round(v * 100), m);
    });

    public bool SetVolume(int level) => Use(ep =>
    {
        ep.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 100) / 100f, ref _ctx);
        if (level > 0) ep.SetMute(false, ref _ctx);
        return true;
    });

    public bool Step(int delta) => Snapshot() is { } s && SetVolume(s.Volume + delta);

    public bool ToggleMute() => Use(ep =>
    {
        ep.GetMute(out var m);
        ep.SetMute(!m, ref _ctx);
        return true;
    });

    private T? Use<T>(Func<IAudioEndpointVolume, T> f)
    {
        lock (_lock)
        {
            try
            {
                // The default device can change (headphones plugged in, ...): re-acquire every 10 s.
                if (_ep is null || Environment.TickCount64 - _acquiredAt > 10_000) Acquire();
                return f(_ep!);
            }
            catch
            {
                _ep = null;
                return default;
            }
        }
    }

    private void Acquire()
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var dev));
        var iid = typeof(IAudioEndpointVolume).GUID;
        Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out var obj));
        _ep = (IAudioEndpointVolume)obj;
        _acquiredAt = Environment.TickCount64;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

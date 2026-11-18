using System.Runtime.InteropServices;

namespace PanelHost.Services;

internal readonly record struct CoreAudioVolumeSnapshot(
    float VolumeScalar,
    bool IsMuted);

internal interface ICoreAudioDeviceProvider : IDisposable
{
    event EventHandler? DefaultRenderEndpointChanged;

    ICoreAudioEndpoint? OpenDefaultRenderEndpoint();
}

internal interface ICoreAudioEndpoint : IDisposable
{
    string Id { get; }

    event EventHandler? VolumeChanged;

    CoreAudioVolumeSnapshot ReadState();

    void SetVolumeScalar(float value, Guid eventContext);

    void SetMute(bool isMuted, Guid eventContext);
}

internal sealed class CoreAudioDeviceProvider : ICoreAudioDeviceProvider
{
    private IMMDeviceEnumerator? _enumerator;
    private CoreAudioNotificationClient? _notificationClient;
    private bool _registered;
    private bool _disposed;

    public CoreAudioDeviceProvider()
    {
        var enumerator = (IMMDeviceEnumerator)(object)
            new MMDeviceEnumeratorComObject();
        var notificationClient = new CoreAudioNotificationClient(
            OnDefaultRenderEndpointChanged);

        try
        {
            CoreAudioHResult.ThrowIfFailed(
                enumerator.RegisterEndpointNotificationCallback(notificationClient));
            _registered = true;
            _enumerator = enumerator;
            _notificationClient = notificationClient;
        }
        catch
        {
            notificationClient.Disable();
            notificationClient.WaitForCallbacksToDrain();
            CoreAudioCom.Release(enumerator);
            throw;
        }
    }

    public event EventHandler? DefaultRenderEndpointChanged;

    public ICoreAudioEndpoint? OpenDefaultRenderEndpoint()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var enumerator = _enumerator
            ?? throw new ObjectDisposedException(nameof(CoreAudioDeviceProvider));

        var result = enumerator.GetDefaultAudioEndpoint(
            CoreAudioDataFlow.Render,
            CoreAudioRole.Console,
            out var device);
        if (CoreAudioHResult.IsNotFound(result))
        {
            return null;
        }

        CoreAudioHResult.ThrowIfFailed(result);

        object? activatedInterface = null;
        try
        {
            CoreAudioHResult.ThrowIfFailed(device.GetId(out var deviceId));
            var endpointVolumeId = CoreAudioIds.AudioEndpointVolume;
            CoreAudioHResult.ThrowIfFailed(device.Activate(
                ref endpointVolumeId,
                CoreAudioClassContext.All,
                IntPtr.Zero,
                out activatedInterface));
            if (activatedInterface is not IAudioEndpointVolume endpointVolume)
            {
                throw new InvalidCastException(
                    "The default render endpoint did not expose IAudioEndpointVolume.");
            }

            return new CoreAudioEndpoint(deviceId, device, endpointVolume);
        }
        catch
        {
            CoreAudioCom.Release(activatedInterface);
            CoreAudioCom.Release(device);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var enumerator = _enumerator;
        var notificationClient = _notificationClient;
        _enumerator = null;
        _notificationClient = null;

        if (notificationClient is not null)
        {
            notificationClient.Disable();
        }

        if (_registered && enumerator is not null && notificationClient is not null)
        {
            _ = enumerator.UnregisterEndpointNotificationCallback(notificationClient);
            _registered = false;
        }

        notificationClient?.WaitForCallbacksToDrain();
        CoreAudioCom.Release(enumerator);
    }

    private void OnDefaultRenderEndpointChanged()
    {
        DefaultRenderEndpointChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class CoreAudioEndpoint : ICoreAudioEndpoint
{
    private IMMDevice? _device;
    private IAudioEndpointVolume? _endpointVolume;
    private CoreAudioEndpointVolumeCallback? _callback;
    private bool _registered;
    private bool _disposed;

    public CoreAudioEndpoint(
        string id,
        IMMDevice device,
        IAudioEndpointVolume endpointVolume)
    {
        Id = id;
        _device = device;
        _endpointVolume = endpointVolume;
        _callback = new CoreAudioEndpointVolumeCallback(OnVolumeChanged);
        CoreAudioHResult.ThrowIfFailed(
            endpointVolume.RegisterControlChangeNotify(_callback));
        _registered = true;
    }

    public string Id { get; }

    public event EventHandler? VolumeChanged;

    public CoreAudioVolumeSnapshot ReadState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var endpointVolume = _endpointVolume
            ?? throw new ObjectDisposedException(nameof(CoreAudioEndpoint));

        CoreAudioHResult.ThrowIfFailed(
            endpointVolume.GetMasterVolumeLevelScalar(out var volumeScalar));
        CoreAudioHResult.ThrowIfFailed(endpointVolume.GetMute(out var isMuted));
        if (!float.IsFinite(volumeScalar))
        {
            throw new InvalidOperationException(
                "Core Audio returned a non-finite master volume value.");
        }

        return new CoreAudioVolumeSnapshot(
            Math.Clamp(volumeScalar, 0.0f, 1.0f),
            isMuted);
    }

    public void SetVolumeScalar(float value, Guid eventContext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var endpointVolume = _endpointVolume
            ?? throw new ObjectDisposedException(nameof(CoreAudioEndpoint));
        var context = eventContext;
        CoreAudioHResult.ThrowIfFailed(endpointVolume.SetMasterVolumeLevelScalar(
            Math.Clamp(value, 0.0f, 1.0f),
            ref context));
    }

    public void SetMute(bool isMuted, Guid eventContext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var endpointVolume = _endpointVolume
            ?? throw new ObjectDisposedException(nameof(CoreAudioEndpoint));
        var context = eventContext;
        CoreAudioHResult.ThrowIfFailed(endpointVolume.SetMute(isMuted, ref context));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var endpointVolume = _endpointVolume;
        var device = _device;
        var callback = _callback;
        _endpointVolume = null;
        _device = null;
        _callback = null;

        if (callback is not null)
        {
            callback.Disable();
        }

        if (_registered && endpointVolume is not null && callback is not null)
        {
            _ = endpointVolume.UnregisterControlChangeNotify(callback);
            _registered = false;
        }

        callback?.WaitForCallbacksToDrain();
        CoreAudioCom.Release(endpointVolume);
        CoreAudioCom.Release(device);
    }

    private void OnVolumeChanged()
    {
        VolumeChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class CoreAudioCallbackLifetime
{
    private int _enabled = 1;
    private int _activeCallbacks;

    public int Invoke(Action action)
    {
        Interlocked.Increment(ref _activeCallbacks);
        try
        {
            if (Volatile.Read(ref _enabled) != 0)
            {
                try
                {
                    action();
                }
                catch
                {
                    // Exceptions must never cross a native Core Audio callback boundary.
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeCallbacks);
        }

        return CoreAudioHResult.Success;
    }

    public void Disable()
    {
        Volatile.Write(ref _enabled, 0);
    }

    public void WaitForCallbacksToDrain()
    {
        var spinWait = new SpinWait();
        while (Volatile.Read(ref _activeCallbacks) != 0)
        {
            spinWait.SpinOnce();
        }
    }
}

[ComVisible(true)]
internal sealed class CoreAudioEndpointVolumeCallback : IAudioEndpointVolumeCallback
{
    private readonly Action _onVolumeChanged;
    private readonly CoreAudioCallbackLifetime _lifetime = new();

    public CoreAudioEndpointVolumeCallback(Action onVolumeChanged)
    {
        _onVolumeChanged = onVolumeChanged;
    }

    public int OnNotify(IntPtr notificationData) =>
        _lifetime.Invoke(_onVolumeChanged);

    public void Disable() => _lifetime.Disable();

    public void WaitForCallbacksToDrain() => _lifetime.WaitForCallbacksToDrain();
}

[ComVisible(true)]
internal sealed class CoreAudioNotificationClient : IMMNotificationClient
{
    private static readonly Action NoOperation = () => { };

    private readonly Action _onDefaultRenderEndpointChanged;
    private readonly CoreAudioCallbackLifetime _lifetime = new();

    public CoreAudioNotificationClient(Action onDefaultRenderEndpointChanged)
    {
        _onDefaultRenderEndpointChanged = onDefaultRenderEndpointChanged;
    }

    public int OnDeviceStateChanged(string deviceId, uint newState) =>
        _lifetime.Invoke(_onDefaultRenderEndpointChanged);

    public int OnDeviceAdded(string deviceId) =>
        _lifetime.Invoke(_onDefaultRenderEndpointChanged);

    public int OnDeviceRemoved(string deviceId) =>
        _lifetime.Invoke(_onDefaultRenderEndpointChanged);

    public int OnDefaultDeviceChanged(
        CoreAudioDataFlow flow,
        CoreAudioRole role,
        string? defaultDeviceId) =>
        _lifetime.Invoke(() =>
        {
            if (flow == CoreAudioDataFlow.Render && role == CoreAudioRole.Console)
            {
                _onDefaultRenderEndpointChanged();
            }
        });

    public int OnPropertyValueChanged(string deviceId, PropertyKey key) =>
        _lifetime.Invoke(NoOperation);

    public void Disable() => _lifetime.Disable();

    public void WaitForCallbacksToDrain() => _lifetime.WaitForCallbacksToDrain();
}

internal static class CoreAudioHResult
{
    public const int Success = 0;
    private const int NotFound = unchecked((int)0x80070490);

    public static bool IsNotFound(int result) => result == NotFound;

    public static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }
}

internal static class CoreAudioCom
{
    public static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            _ = Marshal.FinalReleaseComObject(instance);
        }
    }
}

internal static class CoreAudioIds
{
    public static readonly Guid AudioEndpointVolume =
        new("5CDF2C82-841E-4546-9722-0CF74078229A");
}

internal enum CoreAudioDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

internal enum CoreAudioRole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

[Flags]
internal enum CoreAudioDeviceState : uint
{
    Active = 0x00000001,
    Disabled = 0x00000002,
    NotPresent = 0x00000004,
    Unplugged = 0x00000008,
    All = 0x0000000f,
}

[Flags]
internal enum CoreAudioClassContext : uint
{
    InProcessServer = 0x1,
    InProcessHandler = 0x2,
    LocalServer = 0x4,
    RemoteServer = 0x10,
    All = InProcessServer | InProcessHandler | LocalServer | RemoteServer,
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct PropertyKey
{
    public readonly Guid FormatId;
    public readonly uint PropertyId;
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class MMDeviceEnumeratorComObject;

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(
        CoreAudioDataFlow dataFlow,
        CoreAudioDeviceState stateMask,
        out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(
        CoreAudioDataFlow dataFlow,
        CoreAudioRole role,
        out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice(
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-C0A9A43184A3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(
        ref Guid interfaceId,
        CoreAudioClassContext classContext,
        IntPtr activationParameters,
        [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);

    [PreserveSig]
    int OpenPropertyStore(uint storageAccess, out IntPtr properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out CoreAudioDeviceState state);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);

    [PreserveSig]
    int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);

    [PreserveSig]
    int GetChannelCount(out uint channelCount);

    [PreserveSig]
    int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float levelDb);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float levelDb);

    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);

    [PreserveSig]
    int GetVolumeStepInfo(out uint step, out uint stepCount);

    [PreserveSig]
    int VolumeStepUp(ref Guid eventContext);

    [PreserveSig]
    int VolumeStepDown(ref Guid eventContext);

    [PreserveSig]
    int QueryHardwareSupport(out uint hardwareSupportMask);

    [PreserveSig]
    int GetVolumeRange(
        out float minimumDb,
        out float maximumDb,
        out float incrementDb);
}

[ComVisible(true)]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    [PreserveSig]
    int OnNotify(IntPtr notificationData);
}

[ComVisible(true)]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        uint newState);

    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDefaultDeviceChanged(
        CoreAudioDataFlow flow,
        CoreAudioRole role,
        [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        PropertyKey key);
}

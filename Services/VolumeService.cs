using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DynamicIsland.Services
{
    public class VolumeService : IDisposable
    {
        public event Action<float, bool>? VolumeChanged;

        private IMMDeviceEnumerator? _enumerator;
        private IMMDevice? _device;
        private IAudioEndpointVolume? _endpointVolume;
        private AudioEndpointVolumeCallback? _callback;

        public VolumeService()
        {
            try
            {
                _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                int hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out _device);
                if (hr == 0 && _device != null)
                {
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    hr = _device.Activate(ref iid, 1, IntPtr.Zero, out var epvObj);
                    if (hr == 0 && epvObj is IAudioEndpointVolume epv)
                    {
                        _endpointVolume = epv;
                        _callback = new AudioEndpointVolumeCallback(this);
                        _endpointVolume.RegisterControlChangeNotify(_callback);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VolumeService] Init error: {ex.Message}");
            }
        }

        public float GetVolume()
        {
            if (_endpointVolume == null) return 0.5f;
            try
            {
                _endpointVolume.GetMasterVolumeLevelScalar(out float level);
                return level;
            }
            catch { return 0.5f; }
        }

        public void SetVolume(float level)
        {
            if (_endpointVolume == null) return;
            try
            {
                var guid = Guid.Empty;
                _endpointVolume.SetMasterVolumeLevelScalar(level, ref guid);
            }
            catch { }
        }

        private void OnNotify(float volume, bool isMuted)
        {
            try
            {
                VolumeChanged?.Invoke(volume, isMuted);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VolumeService] Notify error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                if (_endpointVolume != null && _callback != null)
                {
                    _endpointVolume.UnregisterControlChangeNotify(_callback);
                }
            }
            catch { }

            _callback = null;

            if (_endpointVolume != null)
            {
                try { Marshal.ReleaseComObject(_endpointVolume); } catch { }
                _endpointVolume = null;
            }
            if (_device != null)
            {
                try { Marshal.ReleaseComObject(_device); } catch { }
                _device = null;
            }
            if (_enumerator != null)
            {
                try { Marshal.ReleaseComObject(_enumerator); } catch { }
                _enumerator = null;
            }
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        private enum EDataFlow { eRender, eCapture, eAll }
        private enum ERole { eConsole, eMultimedia, eCommunications }

        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr ppDevices);
            [PreserveSig]
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
        }

        [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig]
            int RegisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
            [PreserveSig]
            int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback pNotify);
            [PreserveSig]
            int GetChannelCount(out uint pnChannelCount);
            [PreserveSig]
            int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
            [PreserveSig]
            int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
            [PreserveSig]
            int GetMasterVolumeLevel(out float pfLevelDB);
            [PreserveSig]
            int GetMasterVolumeLevelScalar(out float pfLevel);
            [PreserveSig]
            int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
            [PreserveSig]
            int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
            [PreserveSig]
            int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
            [PreserveSig]
            int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
            [PreserveSig]
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
            [PreserveSig]
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
            [PreserveSig]
            int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
            [PreserveSig]
            int VolumeStepUp(ref Guid pguidEventContext);
            [PreserveSig]
            int VolumeStepDown(ref Guid pguidEventContext);
            [PreserveSig]
            int QueryHardwareSupport(out uint pdwHardwareSupportMask);
            [PreserveSig]
            int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
        }

        [Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolumeCallback
        {
            [PreserveSig]
            int OnNotify(IntPtr pNotify);
        }

        private class AudioEndpointVolumeCallback : IAudioEndpointVolumeCallback
        {
            private readonly VolumeService _parent;
            public AudioEndpointVolumeCallback(VolumeService parent) => _parent = parent;

            [PreserveSig]
            public int OnNotify(IntPtr pNotify)
            {
                try
                {
                    if (pNotify != IntPtr.Zero)
                    {
                        // AUDIO_VOLUME_NOTIFICATION_DATA layout:
                        // GUID guidEventContext (16 bytes)
                        // BOOL bMuted (4 bytes, offset 16)
                        // float fMasterVolume (4 bytes, offset 20)
                        byte[] buf = new byte[24];
                        Marshal.Copy(pNotify, buf, 0, 24);
                        bool isMuted = BitConverter.ToInt32(buf, 16) != 0;
                        float masterVol = BitConverter.ToSingle(buf, 20);
                        _parent.OnNotify(masterVol, isMuted);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VolumeService] OnNotify error: {ex.Message}");
                }
                return 0;
            }
        }
    }
}

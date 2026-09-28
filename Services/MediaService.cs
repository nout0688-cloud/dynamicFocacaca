using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Services
{
    public enum RepeatState
    {
        Off,
        List,
        Track
    }

    public class MediaTrackInfo
    {
        public string Title { get; set; } = "Нет трека";
        public string Artist { get; set; } = "Windows";
        public BitmapImage? Thumbnail { get; set; }
        public bool IsPlaying { get; set; }
        public bool HasMedia { get; set; }
        public RepeatState Repeat { get; set; } = RepeatState.Off;
        public bool IsShuffle { get; set; }
    }

    public class MediaTimelineInfo
    {
        public TimeSpan Position { get; set; } = TimeSpan.Zero;
        public TimeSpan Duration { get; set; } = TimeSpan.Zero;
        public bool CanSeek { get; set; }
    }

    public class MediaService : IDisposable
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private readonly DispatcherTimer _loopTimer = new();
        private RepeatState _userRepeatMode = RepeatState.Off;
        private string? _repeatingTrackTitle;
        private bool _isUserExplicitSkip = false;
        private DateTime _lastLoopTime = DateTime.MinValue;

        public event Action<MediaTrackInfo>? MediaChanged;
        public event Action<MediaTimelineInfo>? TimelineChanged;
        public MediaTrackInfo CurrentTrack { get; private set; } = new MediaTrackInfo();

        public MediaService()
        {
            _loopTimer.Interval = TimeSpan.FromMilliseconds(150);
            _loopTimer.Tick += LoopTimer_Tick;
        }

        public async Task InitializeAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_manager != null)
                {
                    _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
                    UpdateCurrentSession(_manager.GetCurrentSession());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MediaService Init Error: {ex.Message}");
            }
        }

        private void Manager_CurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            UpdateCurrentSession(sender.GetCurrentSession());
        }

        private void UpdateCurrentSession(GlobalSystemMediaTransportControlsSession? session)
        {
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
            }

            _currentSession = session;

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
                _ = RefreshMediaInfoAsync();
            }
            else
            {
                _userRepeatMode = RepeatState.Off;
                _repeatingTrackTitle = null;
                CurrentTrack = new MediaTrackInfo { HasMedia = false, IsPlaying = false };
                MediaChanged?.Invoke(CurrentTrack);
                TimelineChanged?.Invoke(new MediaTimelineInfo());
            }
        }

        private void Session_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            TimelineChanged?.Invoke(GetTimelineInfo());
        }

        private async void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            await RefreshMediaInfoAsync();

            // If track paused at the very end and Repeat Track is active, automatically loop it
            if (CurrentTrack.Repeat == RepeatState.Track && _currentSession != null)
            {
                try
                {
                    var playback = _currentSession.GetPlaybackInfo();
                    if (playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                    {
                        var timeline = _currentSession.GetTimelineProperties();
                        if (timeline != null && timeline.EndTime > TimeSpan.FromSeconds(2))
                        {
                            var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                            var currentPos = timeline.Position;
                            if (elapsed > TimeSpan.Zero)
                            {
                                currentPos += elapsed;
                            }

                            var remaining = timeline.EndTime - currentPos;
                            if (remaining <= TimeSpan.FromSeconds(2.5) || currentPos >= timeline.EndTime)
                            {
                                if ((DateTime.UtcNow - _lastLoopTime).TotalSeconds >= 1.5)
                                {
                                    _lastLoopTime = DateTime.UtcNow;
                                    await LoopCurrentTrackAsync(false);
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        private async void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            if (_userRepeatMode == RepeatState.Track && !string.IsNullOrEmpty(_repeatingTrackTitle) && !_isUserExplicitSkip && _currentSession != null)
            {
                try
                {
                    var props = await _currentSession.TryGetMediaPropertiesAsync();
                    if (props != null && !string.IsNullOrWhiteSpace(props.Title) && !props.Title.Equals(_repeatingTrackTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        // Browser or player auto-advanced to the next track!
                        // Immediately bounce back to the repeated track
                        if ((DateTime.UtcNow - _lastLoopTime).TotalSeconds >= 1.5)
                        {
                            _lastLoopTime = DateTime.UtcNow;
                            await LoopCurrentTrackAsync(true);
                            return;
                        }
                    }
                }
                catch { }
            }

            _isUserExplicitSkip = false;
            await RefreshMediaInfoAsync();
        }

        public async Task RefreshMediaInfoAsync()
        {
            if (_currentSession == null)
            {
                CurrentTrack = new MediaTrackInfo { HasMedia = false, IsPlaying = false };
                UpdateLoopTimer();
                MediaChanged?.Invoke(CurrentTrack);
                return;
            }

            try
            {
                var mediaProperties = await _currentSession.TryGetMediaPropertiesAsync();
                var playbackInfo = _currentSession.GetPlaybackInfo();

                if (mediaProperties == null || (string.IsNullOrWhiteSpace(mediaProperties.Title) && string.IsNullOrWhiteSpace(mediaProperties.Artist)))
                {
                    CurrentTrack = new MediaTrackInfo { HasMedia = false, IsPlaying = false };
                    UpdateLoopTimer();
                    MediaChanged?.Invoke(CurrentTrack);
                    return;
                }

                bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                BitmapImage? thumb = null;

                if (mediaProperties.Thumbnail != null)
                {
                    try
                    {
                        using IRandomAccessStreamWithContentType stream = await mediaProperties.Thumbnail.OpenReadAsync();
                        using Stream netStream = stream.AsStreamForRead();
                        using var memStream = new MemoryStream();
                        await netStream.CopyToAsync(memStream);
                        memStream.Position = 0;

                        thumb = new BitmapImage();
                        thumb.BeginInit();
                        thumb.CacheOption = BitmapCacheOption.OnLoad;
                        thumb.StreamSource = memStream;
                        thumb.EndInit();
                        thumb.Freeze();
                    }
                    catch
                    {
                        thumb = null;
                    }
                }

                var repeatMode = CurrentTrack.Repeat;
                if (playbackInfo?.Controls.IsRepeatEnabled == true && playbackInfo.AutoRepeatMode != null)
                {
                    var smtcRepeat = playbackInfo.AutoRepeatMode.Value;
                    if (smtcRepeat == Windows.Media.MediaPlaybackAutoRepeatMode.Track)
                    {
                        repeatMode = RepeatState.Track;
                        _userRepeatMode = RepeatState.Track;
                    }
                    else if (smtcRepeat == Windows.Media.MediaPlaybackAutoRepeatMode.List)
                    {
                        repeatMode = RepeatState.List;
                        _userRepeatMode = RepeatState.List;
                    }
                    else if (_userRepeatMode == RepeatState.Off)
                    {
                        repeatMode = RepeatState.Off;
                    }
                    else
                    {
                        repeatMode = _userRepeatMode;
                    }
                }
                else if (_userRepeatMode != RepeatState.Off)
                {
                    repeatMode = _userRepeatMode;
                }

                bool isShuffle = CurrentTrack.IsShuffle;
                if (playbackInfo?.Controls.IsShuffleEnabled == true && playbackInfo.IsShuffleActive != null)
                {
                    isShuffle = playbackInfo.IsShuffleActive.Value;
                }

                string newTitle = string.IsNullOrWhiteSpace(mediaProperties.Title) ? "Без названия" : mediaProperties.Title;
                string newArtist = string.IsNullOrWhiteSpace(mediaProperties.Artist) ? "Неизвестный исполнитель" : mediaProperties.Artist;

                if (repeatMode == RepeatState.Track && string.IsNullOrEmpty(_repeatingTrackTitle))
                {
                    _repeatingTrackTitle = newTitle;
                }

                CurrentTrack = new MediaTrackInfo
                {
                    Title = newTitle,
                    Artist = newArtist,
                    Thumbnail = thumb,
                    IsPlaying = isPlaying,
                    HasMedia = true,
                    Repeat = repeatMode,
                    IsShuffle = isShuffle
                };

                UpdateLoopTimer();
                MediaChanged?.Invoke(CurrentTrack);
                TimelineChanged?.Invoke(GetTimelineInfo());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RefreshMediaInfo Error: {ex.Message}");
            }
        }

        public MediaTimelineInfo GetTimelineInfo()
        {
            if (_currentSession == null)
                return new MediaTimelineInfo();

            try
            {
                var timeline = _currentSession.GetTimelineProperties();
                var playback = _currentSession.GetPlaybackInfo();
                if (timeline == null)
                    return new MediaTimelineInfo();

                var duration = timeline.EndTime;
                var position = timeline.Position;

                if (playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                    if (elapsed > TimeSpan.Zero)
                    {
                        position += elapsed;
                    }
                }

                if (duration > TimeSpan.Zero && position > duration)
                {
                    position = duration;
                }
                if (position < TimeSpan.Zero)
                {
                    position = TimeSpan.Zero;
                }

                bool canSeek = playback?.Controls.IsPlaybackPositionEnabled == true || duration > TimeSpan.Zero;

                return new MediaTimelineInfo
                {
                    Position = position,
                    Duration = duration,
                    CanSeek = canSeek
                };
            }
            catch
            {
                return new MediaTimelineInfo();
            }
        }

        public async Task<bool> SeekToAsync(TimeSpan target)
        {
            if (_currentSession == null) return false;
            try
            {
                var timeline = _currentSession.GetTimelineProperties();
                var duration = timeline?.EndTime ?? TimeSpan.Zero;

                long ticks = target.Ticks;
                if (duration > TimeSpan.Zero && ticks > duration.Ticks)
                {
                    ticks = duration.Ticks;
                }
                if (ticks < 0) ticks = 0;

                return await _currentSession.TryChangePlaybackPositionAsync(ticks);
            }
            catch
            {
                return false;
            }
        }

        private async Task LoopCurrentTrackAsync(bool isTrackChanged = false)
        {
            if (_currentSession == null) return;
            try
            {
                if (isTrackChanged)
                {
                    // Track actually switched to next song - jump back to previous track
                    await _currentSession.TrySkipPreviousAsync();
                    await Task.Delay(100);
                }

                // Rewind to start and resume playing
                bool seekSuccess = await _currentSession.TryChangePlaybackPositionAsync(0);
                await _currentSession.TryPlayAsync();

                if (!seekSuccess && !isTrackChanged)
                {
                    // Fallback if seek position command not supported: skip previous
                    await _currentSession.TrySkipPreviousAsync();
                    await _currentSession.TryPlayAsync();
                }
            }
            catch { }
        }

        private async void LoopTimer_Tick(object? sender, EventArgs e)
        {
            if (_currentSession == null || CurrentTrack.Repeat != RepeatState.Track || !CurrentTrack.IsPlaying)
            {
                return;
            }

            if ((DateTime.UtcNow - _lastLoopTime).TotalSeconds < 2.5)
            {
                return;
            }

            try
            {
                var timeline = _currentSession.GetTimelineProperties();
                if (timeline != null && timeline.EndTime > TimeSpan.FromSeconds(2))
                {
                    var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                    var currentPos = timeline.Position;
                    if (elapsed > TimeSpan.Zero)
                    {
                        currentPos += elapsed;
                    }

                    var remaining = timeline.EndTime - currentPos;

                    // When track is within 1.5 seconds of ending, or has reached EndTime
                    if (remaining <= TimeSpan.FromMilliseconds(1500) || currentPos >= timeline.EndTime)
                    {
                        _lastLoopTime = DateTime.UtcNow;
                        await LoopCurrentTrackAsync(false);
                    }
                }
            }
            catch { }
        }

        private void UpdateLoopTimer()
        {
            if (CurrentTrack.Repeat == RepeatState.Track && CurrentTrack.IsPlaying)
            {
                if (!_loopTimer.IsEnabled) _loopTimer.Start();
            }
            else
            {
                if (_loopTimer.IsEnabled) _loopTimer.Stop();
            }
        }

        public async Task TogglePlayPauseAsync()
        {
            if (_currentSession == null) return;
            try
            {
                var info = _currentSession.GetPlaybackInfo();
                if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    await _currentSession.TryPauseAsync();
                }
                else
                {
                    await _currentSession.TryPlayAsync();
                }
            }
            catch { }
        }

        public async Task ToggleRepeatAsync()
        {
            var next = CurrentTrack.Repeat switch
            {
                RepeatState.Off => RepeatState.List,
                RepeatState.List => RepeatState.Track,
                _ => RepeatState.Off
            };
            CurrentTrack.Repeat = next;
            _userRepeatMode = next;
            _repeatingTrackTitle = next == RepeatState.Track ? CurrentTrack.Title : null;

            if (_currentSession != null)
            {
                try
                {
                    var smtcMode = next switch
                    {
                        RepeatState.List => Windows.Media.MediaPlaybackAutoRepeatMode.List,
                        RepeatState.Track => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
                        _ => Windows.Media.MediaPlaybackAutoRepeatMode.None
                    };
                    await _currentSession.TryChangeAutoRepeatModeAsync(smtcMode);
                }
                catch { }

                SendSpotifyKey(0x52); // Ctrl+R for Spotify
            }

            UpdateLoopTimer();
            MediaChanged?.Invoke(CurrentTrack);
        }

        public async Task ToggleShuffleAsync()
        {
            CurrentTrack.IsShuffle = !CurrentTrack.IsShuffle;

            if (_currentSession != null)
            {
                try
                {
                    await _currentSession.TryChangeShuffleActiveAsync(CurrentTrack.IsShuffle);
                }
                catch { }

                SendSpotifyKey(0x53); // Ctrl+S for Spotify
            }

            MediaChanged?.Invoke(CurrentTrack);
        }

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private static void SendSpotifyKey(byte vkKey)
        {
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName("Spotify");
                if (processes.Length == 0) return;

                foreach (var p in processes)
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        const uint WM_KEYDOWN = 0x0100;
                        const uint WM_KEYUP = 0x0101;
                        const int VK_CONTROL = 0x11;

                        PostMessage(p.MainWindowHandle, WM_KEYDOWN, (IntPtr)VK_CONTROL, IntPtr.Zero);
                        PostMessage(p.MainWindowHandle, WM_KEYDOWN, (IntPtr)vkKey, IntPtr.Zero);
                        PostMessage(p.MainWindowHandle, WM_KEYUP, (IntPtr)vkKey, IntPtr.Zero);
                        PostMessage(p.MainWindowHandle, WM_KEYUP, (IntPtr)VK_CONTROL, IntPtr.Zero);
                        break;
                    }
                }
            }
            catch { }
        }

        public async Task SkipNextAsync()
        {
            _isUserExplicitSkip = true;
            _repeatingTrackTitle = null;
            if (_currentSession == null) return;
            try { await _currentSession.TrySkipNextAsync(); } catch { }
        }

        public async Task SkipPreviousAsync()
        {
            _isUserExplicitSkip = true;
            _repeatingTrackTitle = null;
            if (_currentSession == null) return;
            try { await _currentSession.TrySkipPreviousAsync(); } catch { }
        }

        public void Dispose()
        {
            _loopTimer.Stop();
            if (_manager != null)
            {
                try { _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged; } catch { }
            }
            if (_currentSession != null)
            {
                try
                {
                    _currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
                    _currentSession.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
                }
                catch { }
                _currentSession = null;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DynamicIsland.Services;
using Forms = System.Windows.Forms;

namespace DynamicIsland
{
    public partial class MainWindow : Window
    {
        private readonly MediaService _mediaService = new();
        private readonly FullscreenDetector _fullscreenDetector = new();
        private readonly VolumeService _volumeService = new();
        private readonly CallService _callService = new();
        private readonly NotificationService _notificationService = new();
        private readonly ToastBannerSuppressor _toastSuppressor = new();
        private readonly SettingsService _settingsService;

        private readonly DispatcherTimer _clockTimer = new();
        private readonly DispatcherTimer _volumeDismissTimer = new();
        private readonly DispatcherTimer _equalizerTimer = new();
        private readonly DispatcherTimer _collapseCleanupTimer = new();
        private readonly DispatcherTimer _timelineTimer = new();
        private readonly DispatcherTimer _notificationDismissTimer = new();
        private readonly Random _rand = new();

        private Forms.NotifyIcon? _trayIcon;

        // Pre-allocated equalizer animations (reused every tick)
        private readonly DoubleAnimation[] _barAnims;
        private readonly SineEase _barEase = new() { EasingMode = EasingMode.EaseInOut };

        private enum IslandMode
        {
            Idle,
            CompactMedia,
            CompactVolume,
            ExpandedMedia,
            IncomingCall,
            ActiveCall,
            Notification
        }

        private IslandMode _currentMode = IslandMode.Idle;
        private readonly Queue<NotificationItem> _notificationQueue = new();

        private bool _isExpanded = false;
        private bool _isShowingVolume = false;
        private bool _isHidingForGame = false;
        private bool _isRinging = false;
        private bool _isInActiveCall = false;
        private bool IsCallDisplayActive => _isRinging || _isInActiveCall;
        private bool _isNotificationActive = false;
        private NotificationItem? _currentNotification;
        private DateTime _callStartTime = DateTime.MinValue;
        private readonly DispatcherTimer _callDurationTimer = new();
        private bool _isDraggingScrubber = false;
        private IncomingCallInfo? _currentCall;

        private const string PLAY_ICON_DATA = "M8 5v14l11-7z";
        private const string PAUSE_ICON_DATA = "M6 5h4v14H6zm8 0h4v14h-4z";
        private const string REPEAT_ALL_DATA = "M 17,2 L 21,6 L 17,10 M 3,11 V 10 A 4,4 0 0 1 7,6 H 21 M 7,22 L 3,18 L 7,14 M 21,13 V 14 A 4,4 0 0 1 17,18 H 3";
        private const string REPEAT_ONE_DATA = "M 17,2 L 21,6 L 17,10 M 3,11 V 10 A 4,4 0 0 1 7,6 H 21 M 7,22 L 3,18 L 7,14 M 21,13 V 14 A 4,4 0 0 1 17,18 H 3 M 11,10 H 12 V 14";
        private const string SHUFFLE_DATA = "M 18,14 L 22,18 L 18,22 M 18,2 L 22,6 L 18,10 M 2,18 H 3.973 A 4,4 0 0 0 7.273,16.3 L 12.727,7.7 A 4,4 0 0 1 16.027,6 H 22 M 2,6 H 3.972 A 4,4 0 0 1 7.572,8.2 M 22,18 H 15.959 A 4,4 0 0 1 12.659,16.2 L 12.3,15.75";

        #region AnimationConstants
        // ─── Тривалості ────────────────────────────────────────────────────────────
        private static readonly TimeSpan DurExpand        = TimeSpan.FromMilliseconds(500);  // розкриття острова
        private static readonly TimeSpan DurCollapse      = TimeSpan.FromMilliseconds(320);  // закриття острова
        private static readonly TimeSpan DurSize          = TimeSpan.FromMilliseconds(440);  // resize pills
        private static readonly TimeSpan DurContentFadeIn = TimeSpan.FromMilliseconds(280);  // fade-in контенту
        private static readonly TimeSpan DurContentFadeOut= TimeSpan.FromMilliseconds(140);  // fade-out контенту
        private static readonly TimeSpan DurHover         = TimeSpan.FromMilliseconds(200);  // hover scale
        private static readonly TimeSpan DurDropDown      = TimeSpan.FromMilliseconds(500);  // drop після fullscreen
        private static readonly TimeSpan DurNotifBounce   = TimeSpan.FromMilliseconds(480);  // bounce нотифікації
        private static readonly TimeSpan DurCallSlide     = TimeSpan.FromMilliseconds(400);  // слайд call view
        private static readonly TimeSpan DurCallEnded     = TimeSpan.FromMilliseconds(200);  // fade out call
        private static readonly TimeSpan DurPulse         = TimeSpan.FromMilliseconds(900);  // пульс під час дзвінка
        private static readonly TimeSpan DurVolumeBar     = TimeSpan.FromMilliseconds(180);  // анімація volume bar

        // ─── Easing functions (ліниво ініціалізовані один раз) ─────────────────────
        private static readonly QuinticEase EaseSpringOut   = new() { EasingMode = EasingMode.EaseOut };
        private static readonly QuarticEase EaseQuartOut    = new() { EasingMode = EasingMode.EaseOut };
        private static readonly QuarticEase EaseQuartIn     = new() { EasingMode = EasingMode.EaseIn };
        private static readonly CubicEase   EaseCubicOut    = new() { EasingMode = EasingMode.EaseOut };
        private static readonly CubicEase   EaseCubicIn     = new() { EasingMode = EasingMode.EaseIn };
        private static readonly SineEase    EaseSineInOut   = new() { EasingMode = EasingMode.EaseInOut };
        private static readonly BackEase    EaseSpringSize  = new() { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 };
        private static readonly BackEase    EaseSpringDrop  = new() { EasingMode = EasingMode.EaseOut, Amplitude = 0.12 };
        private static readonly BackEase    EaseBadgePop    = new() { EasingMode = EasingMode.EaseOut, Amplitude = 0.50 };
        #endregion

        private static readonly SolidColorBrush ACTIVE_BRUSH = new(System.Windows.Media.Color.FromRgb(0x30, 0xD1, 0x58));
        private static readonly SolidColorBrush INACTIVE_BRUSH = new(System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93));

        private string _currentMiniText = "";
        private readonly LinearGradientBrush _tickerMask = new()
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 0),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(System.Windows.Media.Color.FromArgb(0, 0, 0, 0), 0.0),
                new GradientStop(System.Windows.Media.Color.FromArgb(255, 0, 0, 0), 0.05),
                new GradientStop(System.Windows.Media.Color.FromArgb(255, 0, 0, 0), 0.95),
                new GradientStop(System.Windows.Media.Color.FromArgb(0, 0, 0, 0), 1.0)
            }
        };

        public MainWindow()
        {
            try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow Constructor start\n"); } catch { }
            InitializeComponent();
            _settingsService = new SettingsService(Dispatcher);
            _settingsService.SettingsChanged += OnSettingsChanged;
            PositionWindow();
            ApplySettings(_settingsService.Current);
            _toastSuppressor.EnableSuppression();

            _barAnims = new DoubleAnimation[4];
            for (int i = 0; i < 4; i++)
            {
                _barAnims[i] = new DoubleAnimation
                {
                    Duration = TimeSpan.FromMilliseconds(150),
                    EasingFunction = _barEase
                };
            }

            IslandBorder.SizeChanged += (s, e) =>
            {
                IslandBorder.CornerRadius = new CornerRadius(Math.Min(32, IslandBorder.ActualHeight / 2));
            };

            MiniTickerContainer.SizeChanged += (s, e) =>
            {
                if (MiniTickerContainer.ActualWidth > 30 && !string.IsNullOrEmpty(_currentMiniText))
                {
                    UpdateMiniTicker(_currentMiniText);
                }
            };

            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += (s, e) =>
            {
                ClockText.Text = DateTime.Now.ToString("HH:mm");
                UpdateTimeOfDayIcon(DateTime.Now.Hour, DateTime.Now.Minute);
                if (!_isHidingForGame && !Topmost)
                {
                    Topmost = true;
                }
            };
            _clockTimer.Start();
            ClockText.Text = DateTime.Now.ToString("HH:mm");

            Deactivated += (s, e) =>
            {
                if (!_isHidingForGame)
                {
                    Topmost = true;
                }
            };

            _volumeDismissTimer.Interval = TimeSpan.FromMilliseconds(1800);
            _volumeDismissTimer.Tick += VolumeDismissTimer_Tick;

            _equalizerTimer.Interval = TimeSpan.FromMilliseconds(130);
            _equalizerTimer.Tick += EqualizerTimer_Tick;

            _collapseCleanupTimer.Interval = TimeSpan.FromMilliseconds(260);
            _collapseCleanupTimer.Tick += CollapseCleanupTimer_Tick;

            _callDurationTimer.Interval = TimeSpan.FromSeconds(1);
            _callDurationTimer.Tick += CallDurationTimer_Tick;

            _timelineTimer.Interval = TimeSpan.FromMilliseconds(200);
            _timelineTimer.Tick += (s, e) =>
            {
                if (_isExpanded && !_isDraggingScrubber)
                {
                    UpdateTimelineUI();
                }
            };

            _mediaService.TimelineChanged += (info) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_isExpanded && !_isDraggingScrubber)
                    {
                        UpdateTimelineUI();
                    }
                });
            };

            ScrubberTrack.SizeChanged += (s, e) =>
            {
                if (_isExpanded && !_isDraggingScrubber)
                {
                    UpdateTimelineUI();
                }
            };

            _notificationDismissTimer.Interval = TimeSpan.FromSeconds(4.5);
            _notificationDismissTimer.Tick += (s, e) => DismissNotification();

            _mediaService.MediaChanged += OnMediaChanged;
            _fullscreenDetector.FullscreenChanged += OnFullscreenChanged;
            _volumeService.VolumeChanged += OnVolumeChanged;
            _callService.IncomingCallReceived += OnIncomingCallReceived;
            _callService.ActiveCallStarted += () => Dispatcher.Invoke(SwitchToActiveCall);
            _callService.CallEnded += OnCallEnded;
            _notificationService.NotificationReceived += OnNotificationReceived;
            _notificationService.CallDetectedFromNotification += (callInfo) =>
            {
                Dispatcher.Invoke(() => _callService.TriggerCall(callInfo));
            };

            SetupTrayIcon();

            DpiChanged += (s, e) => PositionWindow();

            Loaded += MainWindow_Loaded;
            Loaded += (s, e) => UpdateTimeOfDayIcon(DateTime.Now.Hour, DateTime.Now.Minute);
            try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow Constructor end\n"); } catch { }
        }

        private void SetupTrayIcon()
        {
            _trayIcon = new Forms.NotifyIcon
            {
                Text = "Dynamic Island",
                Visible = true
            };

            using (var bmp = new System.Drawing.Bitmap(16, 16))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.FillEllipse(System.Drawing.Brushes.White, 1, 1, 14, 14);
                }
                _trayIcon.Icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
            }

            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Выход", null, (s, e) =>
            {
                Dispatcher.Invoke(Close);
            });
            _trayIcon.ContextMenuStrip = menu;
        }

        private void ExitMenu_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow_Loaded start\n"); } catch { }
            try
            {
                var fontRes = FindResource("IOSFont") as System.Windows.Media.FontFamily;
                var miniFont = MiniTrackTitle1.FontFamily;
                var tf = new System.Windows.Media.Typeface(fontRes ?? miniFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                System.Windows.Media.GlyphTypeface? gtf = null;
                bool ok = tf.TryGetGlyphTypeface(out gtf);
                var tfSemi = new System.Windows.Media.Typeface(fontRes ?? miniFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                System.Windows.Media.GlyphTypeface? gtfSemi = null;
                bool okSemi = tfSemi.TryGetGlyphTypeface(out gtfSemi);
                System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] FONT DIAG: IOSFont='{fontRes?.Source}' MiniFont='{miniFont?.Source}' BoldTry={ok} (Uri={gtf?.FontUri}, Weight={gtf?.Weight}) SemiTry={okSemi} (Uri={gtfSemi?.FontUri}, Weight={gtfSemi?.Weight})\n");
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] FONT DIAG ERROR: {ex}\n"); } catch { }
            }
            await _mediaService.InitializeAsync();
            try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow_Loaded end\n"); } catch { }
        }

        private void PositionWindow()
        {
            var settings = _settingsService?.Current ?? new IslandSettings();
            double targetLeft;
            if (string.Equals(settings.Alignment, "Left", StringComparison.OrdinalIgnoreCase))
            {
                targetLeft = 24;
            }
            else if (string.Equals(settings.Alignment, "Right", StringComparison.OrdinalIgnoreCase))
            {
                targetLeft = SystemParameters.PrimaryScreenWidth - Width - 24;
            }
            else
            {
                targetLeft = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            }

            Left = targetLeft;
            Top = Math.Clamp(settings.TopOffset, 0, 100);
        }

        private void OnSettingsChanged(IslandSettings settings)
        {
            Dispatcher.InvokeAsync(() => ApplySettings(settings));
        }

        private void ApplySettings(IslandSettings settings)
        {
            PositionWindow();

            // Scale transform on IslandBorder
            if (IslandBorder.RenderTransform is not TransformGroup tg)
            {
                tg = new TransformGroup();
                if (IslandBorder.RenderTransform != null) tg.Children.Add(IslandBorder.RenderTransform);
                IslandBorder.RenderTransform = tg;
            }

            ScaleTransform? st = null;
            foreach (var child in tg.Children)
            {
                if (child is ScaleTransform s) { st = s; break; }
            }
            if (st == null)
            {
                st = new ScaleTransform(settings.Scale, settings.Scale);
                tg.Children.Add(st);
            }
            else
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(settings.Scale, TimeSpan.FromMilliseconds(300)) { EasingFunction = EaseCubicOut });
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(settings.Scale, TimeSpan.FromMilliseconds(300)) { EasingFunction = EaseCubicOut });
            }

            // Theme styling
            if (string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase))
            {
                IslandBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(235, 245, 245, 247));
                IslandBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 0, 0, 0));
                MiniTrackTitle1.Foreground = System.Windows.Media.Brushes.Black;
                ClockText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 28, 30));
            }
            else
            {
                IslandBorder.Background = System.Windows.Media.Brushes.Black;
                IslandBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 255, 255, 255));
                MiniTrackTitle1.Foreground = System.Windows.Media.Brushes.White;
                ClockText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(229, 229, 234));
            }
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLongPtr32(hWnd, nIndex);
        }

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLongPtr32(hWnd, nIndex, dwNewLong);
        }

        private static void HideFromAltTab(IntPtr hwnd)
        {
            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            exStyle |= WS_EX_TOOLWINDOW;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            PositionWindow();
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            HideFromAltTab(hwnd);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTTRANSPARENT = -1;
            const int WM_CLOSE = 0x0010;
            const int WM_QUERYENDSESSION = 0x0011;
            const int WM_ENDSESSION = 0x0016;
            const int WM_DESTROY = 0x0002;

            if (msg == WM_CLOSE || msg == WM_QUERYENDSESSION || msg == WM_ENDSESSION || msg == WM_DESTROY)
            {
                try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] WndProc msg: 0x{msg:X4}, wParam: 0x{wParam:X}\n"); } catch { }
            }

            if (msg == WM_NCHITTEST)
            {
                try
                {
                    if (_isHidingForGame)
                    {
                        handled = true;
                        return new IntPtr(HTTRANSPARENT);
                    }

                    if (!IsLoaded || !IslandBorder.IsVisible || IslandBorder.ActualWidth <= 0)
                    {
                        return IntPtr.Zero;
                    }

                    int x = unchecked((short)(long)lParam);
                    int y = unchecked((short)((long)lParam >> 16));

                    System.Windows.Point screenPoint = new System.Windows.Point(x, y);
                    System.Windows.Point windowPoint = PointFromScreen(screenPoint);

                    GeneralTransform transform = IslandBorder.TransformToVisual(this);
                    Rect islandBounds = transform.TransformBounds(new Rect(0, 0, IslandBorder.ActualWidth, IslandBorder.ActualHeight));
                    islandBounds.Inflate(4, 4);

                    if (!islandBounds.Contains(windowPoint))
                    {
                        handled = true;
                        return new IntPtr(HTTRANSPARENT);
                    }
                }
                catch
                {
                    // Ignore layout transition exceptions during window creation
                }
            }
            return IntPtr.Zero;
        }

        private void OnFullscreenChanged(bool isFullscreen)
        {
            Dispatcher.Invoke(() =>
            {
                if (isFullscreen)
                {
                    _isHidingForGame = true;
                    IslandBorder.IsHitTestVisible = false;

                    // Контент зникає першим
                    IslandContentGrid.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(130))
                        { EasingFunction = EaseCubicOut });

                    // Острів стискається в крапку і їде вгору
                    var morphEase = new QuinticEase { EasingMode = EasingMode.EaseIn };
                    var slideUp = new DoubleAnimation(-60, TimeSpan.FromMilliseconds(420))
                    { EasingFunction = morphEase };

                    slideUp.Completed += (s, e) =>
                    {
                        if (_isHidingForGame) Visibility = Visibility.Collapsed;
                    };

                    IslandBorder.BeginAnimation(WidthProperty,
                        new DoubleAnimation(10, TimeSpan.FromMilliseconds(400))
                        { BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = morphEase });
                    IslandBorder.BeginAnimation(HeightProperty,
                        new DoubleAnimation(10, TimeSpan.FromMilliseconds(400))
                        { BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = morphEase });
                    IslandBorder.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(380))
                        { BeginTime = TimeSpan.FromMilliseconds(80), EasingFunction = EaseCubicIn });
                    IslandTransform.BeginAnimation(TranslateTransform.YProperty, slideUp);
                }
                else
                {
                    _isHidingForGame = false;
                    Visibility = Visibility.Visible;
                    Topmost = true;
                    IslandBorder.IsHitTestVisible = true;

                    // Скидаємо всі анімації
                    IslandTransform.BeginAnimation(TranslateTransform.YProperty, null);
                    IslandBorder.BeginAnimation(WidthProperty, null);
                    IslandBorder.BeginAnimation(HeightProperty, null);
                    IslandBorder.BeginAnimation(OpacityProperty, null);
                    IslandContentGrid.BeginAnimation(OpacityProperty, null);

                    _isExpanded = false;

                    IslandMode restoreMode = IslandMode.Idle;
                    if (_isInActiveCall) restoreMode = IslandMode.ActiveCall;
                    else if (_isRinging) restoreMode = IslandMode.IncomingCall;
                    else if (_isNotificationActive) restoreMode = IslandMode.Notification;
                    else if (_mediaService.CurrentTrack.HasMedia) restoreMode = IslandMode.CompactMedia;

                    ApplyIslandMode(restoreMode);

                    (double targetWidth, double targetHeight) = GetDimensionsForMode(restoreMode);

                    // Старт з крапки вище екрану → drop вниз з spring
                    IslandTransform.Y = -62;
                    IslandBorder.Width = 10;
                    IslandBorder.Height = 10;
                    IslandBorder.Opacity = 1.0;
                    IslandContentGrid.Opacity = 0.0;

                    // Drop: виражений BackEase bounce
                    IslandTransform.BeginAnimation(TranslateTransform.YProperty,
                        new DoubleAnimation(0, DurDropDown) { EasingFunction = EaseSpringDrop });

                    // Острів розширюється зі spring
                    IslandBorder.BeginAnimation(WidthProperty,
                        new DoubleAnimation(targetWidth, DurDropDown) { EasingFunction = EaseSpringOut });
                    IslandBorder.BeginAnimation(HeightProperty,
                        new DoubleAnimation(targetHeight, DurDropDown) { EasingFunction = EaseSpringOut });

                    // Контент з'являється після того як острів впав
                    IslandContentGrid.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(1.0, DurContentFadeIn)
                        {
                            BeginTime = TimeSpan.FromMilliseconds(160),
                            EasingFunction = EaseCubicOut
                        });
                }
            });
        }

        private void OnMediaChanged(MediaTrackInfo track)
        {
            Dispatcher.Invoke(() =>
            {
                if (track.HasMedia)
                {
                    // Mini view marquee ticker text
                    string fullText = string.IsNullOrWhiteSpace(track.Artist) ? track.Title : $"{track.Artist} - {track.Title}";
                    _currentMiniText = fullText;

                    if (_currentMode == IslandMode.CompactMedia && !_isExpanded && !_isShowingVolume && !_isHidingForGame && !IsCallDisplayActive && !_isNotificationActive)
                    {
                        AnimateIslandSize(GetCompactMediaWidth(), 36, useSpring: true);
                    }

                    UpdateMiniTicker(fullText);

                    // Expanded view text
                    ExpandedTrackTitle.Text = track.Title;
                    ExpandedArtist.Text = track.Artist;

                    if (track.Thumbnail != null)
                    {
                        MiniAlbumArt.Source = track.Thumbnail;
                        MiniAlbumArt.Visibility = Visibility.Visible;
                        MiniMusicNote.Visibility = Visibility.Collapsed;

                        LargeAlbumArt.Source = track.Thumbnail;
                        LargeAlbumArt.Visibility = Visibility.Visible;
                        LargeMusicNote.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        MiniAlbumArt.Visibility = Visibility.Collapsed;
                        MiniMusicNote.Visibility = Visibility.Visible;

                        LargeAlbumArt.Visibility = Visibility.Collapsed;
                        LargeMusicNote.Visibility = Visibility.Visible;
                    }

                    PlayPauseIcon.Data = Geometry.Parse(track.IsPlaying ? PAUSE_ICON_DATA : PLAY_ICON_DATA);

                    // Update Shuffle button state
                    ShuffleIcon.Data = Geometry.Parse(SHUFFLE_DATA);
                    ShuffleIcon.Stroke = track.IsShuffle ? ACTIVE_BRUSH : INACTIVE_BRUSH;
                    ShuffleDot.Visibility = track.IsShuffle ? Visibility.Visible : Visibility.Collapsed;

                    // Update Repeat button state (Off / Playlist / Track)
                    switch (track.Repeat)
                    {
                        case RepeatState.Track:
                            RepeatIcon.Data = Geometry.Parse(REPEAT_ONE_DATA);
                            RepeatIcon.Stroke = ACTIVE_BRUSH;
                            RepeatDot.Visibility = Visibility.Visible;
                            break;
                        case RepeatState.List:
                            RepeatIcon.Data = Geometry.Parse(REPEAT_ALL_DATA);
                            RepeatIcon.Stroke = ACTIVE_BRUSH;
                            RepeatDot.Visibility = Visibility.Visible;
                            break;
                        default:
                            RepeatIcon.Data = Geometry.Parse(REPEAT_ALL_DATA);
                            RepeatIcon.Stroke = INACTIVE_BRUSH;
                            RepeatDot.Visibility = Visibility.Collapsed;
                            break;
                    }

                    if (track.IsPlaying)
                    {
                        if (!_equalizerTimer.IsEnabled)
                        {
                            _equalizerTimer.Start();
                            EqualizerTimer_Tick(null, EventArgs.Empty);
                        }
                    }
                    else
                    {
                        if (_equalizerTimer.IsEnabled)
                        {
                            _equalizerTimer.Stop();
                        }
                        ResetEqualizerBars();
                    }

                    if (!_isExpanded && !_isShowingVolume && !_isHidingForGame && !IsCallDisplayActive && !_isNotificationActive)
                    {
                        SwitchToCompactMedia();
                    }
                }
                else
                {
                    _equalizerTimer.Stop();
                    ResetEqualizerBars();
                    if (!_isExpanded && !_isShowingVolume && !_isHidingForGame && !IsCallDisplayActive && !_isNotificationActive)
                    {
                        SwitchToCompactIdle();
                    }
                }
            });
        }

        /// <summary>
        /// Показує відповідну іконку часу доби і анімує її появу.
        /// </summary>
        private void UpdateTimeOfDayIcon(int hour, int minute)
        {
            double totalMinutes = hour * 60 + minute;

            // Визначаємо яку іконку показати
            string targetIcon =
                totalMinutes >= 300 && totalMinutes < 420  ? "Sunrise"   :  // 5:00–7:00
                totalMinutes >= 420 && totalMinutes < 660  ? "MorningSun":  // 7:00–11:00
                totalMinutes >= 660 && totalMinutes < 960  ? "DaySun"    :  // 11:00–16:00
                totalMinutes >= 960 && totalMinutes < 1170 ? "Sunset"    :  // 16:00–19:30
                totalMinutes >= 1170 && totalMinutes < 1290? "Dusk"      :  // 19:30–21:30
                totalMinutes >= 1290 && totalMinutes < 1380? "Moon"      :  // 21:30–23:00
                                                             "Night";       // 23:00–5:00

            var allIcons = new (string Name, UIElement Icon)[]
            {
                ("Sunrise",    IconSunrise),
                ("MorningSun", IconMorningSun),
                ("DaySun",     IconDaySun),
                ("Sunset",     IconSunset),
                ("Dusk",       IconDusk),
                ("Moon",       IconMoon),
                ("Night",      IconNight),
            };

            foreach (var (name, icon) in allIcons)
            {
                bool isActive = name == targetIcon;
                bool wasVisible = icon.Visibility == Visibility.Visible;

                if (isActive && !wasVisible)
                {
                    // Нова іконка — появляється з fade + tiny scale
                    icon.Visibility = Visibility.Visible;
                    icon.Opacity = 0;
                    icon.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(600))
                        { EasingFunction = EaseCubicOut });
                }
                else if (!isActive && wasVisible)
                {
                    // Стара іконка — зникає
                    var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(400))
                    { EasingFunction = EaseCubicIn };
                    fadeOut.Completed += (s, e) => icon.Visibility = Visibility.Collapsed;
                    icon.BeginAnimation(OpacityProperty, fadeOut);
                }
            }
        }

        private FrameworkElement? GetViewForMode(IslandMode mode) => mode switch
        {
            IslandMode.Idle => CompactIdleView,
            IslandMode.CompactMedia => CompactMediaView,
            IslandMode.CompactVolume => CompactVolumeView,
            IslandMode.ExpandedMedia => ExpandedMediaView,
            IslandMode.IncomingCall or IslandMode.ActiveCall => IncomingCallView,
            IslandMode.Notification => NotificationView,
            _ => null
        };

        private double GetCompactMediaWidth()
        {
            if (string.IsNullOrWhiteSpace(_currentMiniText) || _currentMiniText == "Музыка" || _currentMiniText == "Трек")
            {
                return 210;
            }

            try
            {
                var fontFam = (System.Windows.Media.FontFamily)FindResource("IOSFont");
                var formatted = new FormattedText(
                    _currentMiniText,
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    new Typeface(fontFam, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    12.0,
                    System.Windows.Media.Brushes.White,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);

                double textWidth = formatted.Width;
                double needed = textWidth + 76; // 22 album art + 15 outer margins + 16 inner margins + 22 equalizer bars + 1 padding
                return Math.Clamp(Math.Ceiling(needed), 210, 380);
            }
            catch
            {
                return 240;
            }
        }

        private (double width, double height) GetDimensionsForMode(IslandMode mode) => mode switch
        {
            IslandMode.Idle => (154, 37),
            IslandMode.CompactMedia => (GetCompactMediaWidth(), 37),
            IslandMode.CompactVolume => (195, 37),
            IslandMode.ExpandedMedia => (390, 180),
            IslandMode.IncomingCall or IslandMode.ActiveCall => (390, 60),
            IslandMode.Notification => (360, 48),
            _ => (154, 37)
        };

        private void HideAllViewsExcept(FrameworkElement? keep1, FrameworkElement? keep2 = null)
        {
            FrameworkElement[] allViews = [CompactIdleView, CompactMediaView, CompactVolumeView, ExpandedMediaView, IncomingCallView, NotificationView];
            foreach (var v in allViews)
            {
                if (v != keep1 && v != keep2)
                {
                    v.BeginAnimation(OpacityProperty, null);
                    v.Visibility = Visibility.Collapsed;
                    v.Opacity = 1.0;
                }
            }
        }

        private void UpdateModeSubElements(IslandMode mode)
        {
            if (mode == IslandMode.IncomingCall)
            {
                IncomingButtonsPanel.Visibility = Visibility.Visible;
                ActiveButtonsPanel.Visibility = Visibility.Collapsed;
                CallTickerContainer.Visibility = Visibility.Visible;
                CallDurationPanel.Visibility = Visibility.Collapsed;
            }
            else if (mode == IslandMode.ActiveCall)
            {
                IncomingButtonsPanel.Visibility = Visibility.Collapsed;
                ActiveButtonsPanel.Visibility = Visibility.Visible;
                CallTickerContainer.Visibility = Visibility.Collapsed;
                CallDurationPanel.Visibility = Visibility.Visible;
            }
            else if (mode == IslandMode.CompactMedia)
            {
                if (!string.IsNullOrEmpty(_currentMiniText))
                {
                    UpdateMiniTicker(_currentMiniText);
                }
            }
        }

        private void TransitionToMode(IslandMode newMode, bool useSpring = true)
        {
            var oldMode = _currentMode;
            _currentMode = newMode;

            var oldView = GetViewForMode(oldMode);
            var newView = GetViewForMode(newMode);

            (double targetWidth, double targetHeight) = GetDimensionsForMode(newMode);
            AnimateIslandSize(targetWidth, targetHeight, useSpring);

            // If switching between IncomingCall and ActiveCall, they share IncomingCallView
            if (oldView == newView && newView != null)
            {
                newView.Visibility = Visibility.Visible;
                newView.Opacity = 1.0;
                UpdateModeSubElements(newMode);
                return;
            }

            // 1. Smoothly fade out old view
            if (oldView != null && oldView.Visibility == Visibility.Visible)
            {
                var capturedOldView = oldView;
                var capturedOldMode = oldMode;
                var fadeOut = new DoubleAnimation(0.0, DurContentFadeOut)
                {
                    EasingFunction = EaseCubicIn
                };
                fadeOut.Completed += (s, e) =>
                {
                    if (_currentMode != capturedOldMode)
                    {
                        capturedOldView.Visibility = Visibility.Collapsed;
                        capturedOldView.Opacity = 1.0;
                        capturedOldView.BeginAnimation(OpacityProperty, null);
                    }
                };
                capturedOldView.BeginAnimation(OpacityProperty, fadeOut);
            }

            // 2. Hide other views to guarantee zero ghosting
            HideAllViewsExcept(oldView, newView);

            // 3. Smoothly fade in new view
            if (newView != null)
            {
                newView.Visibility = Visibility.Visible;
                newView.Opacity = 0.0;
                var fadeIn = new DoubleAnimation(1.0, DurContentFadeIn)
                {
                    BeginTime = TimeSpan.FromMilliseconds(45),
                    EasingFunction = EaseCubicOut
                };
                newView.BeginAnimation(OpacityProperty, fadeIn);
            }

            UpdateModeSubElements(newMode);
        }

        private void ApplyIslandMode(IslandMode mode)
        {
            TransitionToMode(mode, useSpring: true);
        }

        private void OnVolumeChanged(float volume, bool isMuted)
        {
            Dispatcher.Invoke(() =>
            {
                if (_isExpanded || _isHidingForGame || IsCallDisplayActive || _isNotificationActive) return;

                try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:HH:mm:ss.fff}] Volume={volume:F2} Muted={isMuted}\n"); } catch { }

                _isShowingVolume = true;
                _volumeDismissTimer.Stop();
                _volumeDismissTimer.Start();

                int percent = (int)Math.Round(volume * 100);
                VolumeText.Text = isMuted ? "Mute" : $"{percent}%";

                // ── Іконка: перемикаємо між звичайною і mute ──────────────────
                VolumeIcon.Visibility     = isMuted ? Visibility.Collapsed : Visibility.Visible;
                VolumeMuteIcon.Visibility = isMuted ? Visibility.Visible   : Visibility.Collapsed;

                // ── Колір полоски + тексту ─────────────────────────────────────
                if (isMuted)
                {
                    // Нові brushes кожен раз (не frozen static) — WPF вимагає
                    VolumeFillBar.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x3B, 0x30));
                    VolumeText.Foreground    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x3B, 0x30));

                    // Shake острова при mute
                    var shakeAnim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(300) };
                    shakeAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0,  KeyTime.FromTimeSpan(TimeSpan.Zero)));
                    shakeAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-3,   KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60)),  EaseSineInOut));
                    shakeAnim.KeyFrames.Add(new EasingDoubleKeyFrame( 3,   KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)), EaseSineInOut));
                    shakeAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-2,   KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), EaseSineInOut));
                    shakeAnim.KeyFrames.Add(new EasingDoubleKeyFrame( 0,   KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), EaseCubicOut));
                    IslandTransform.BeginAnimation(TranslateTransform.XProperty, shakeAnim);
                }
                else
                {
                    VolumeFillBar.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
                    VolumeText.Foreground    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA1, 0xA1, 0xAA));
                }

                // Анімація ширини полоски
                var barAnim = new DoubleAnimation(Math.Max(4, 85 * volume), TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = EaseCubicOut
                };
                VolumeFillBar.BeginAnimation(WidthProperty, barAnim);

                ApplyIslandMode(IslandMode.CompactVolume);
            });
        }

        private void VolumeDismissTimer_Tick(object? sender, EventArgs e)
        {
            _volumeDismissTimer.Stop();
            _isShowingVolume = false;

            // Скидаємо кольори volume view до дефолтних
            VolumeFillBar.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
            VolumeText.Foreground    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA1, 0xA1, 0xAA));
            VolumeIcon.Visibility     = Visibility.Visible;
            VolumeMuteIcon.Visibility = Visibility.Collapsed;

            if (_isExpanded || IsCallDisplayActive || _isNotificationActive) return;

            if (_mediaService.CurrentTrack.HasMedia)
            {
                SwitchToCompactMedia();
            }
            else
            {
                SwitchToCompactIdle();
            }
        }

        private void SwitchToCompactMedia()
        {
            if (IsCallDisplayActive || _isNotificationActive) return;

            ApplyIslandMode(IslandMode.CompactMedia);
            if (!string.IsNullOrEmpty(_currentMiniText))
            {
                UpdateMiniTicker(_currentMiniText);
            }
        }

        private void UpdateMiniTicker(string text)
        {
            _currentMiniText = text;
            if (string.IsNullOrWhiteSpace(text) || text == " - ")
            {
                text = "Музыка";
            }

            MiniTrackTitle1.Text = text;

            // Reset translation
            MiniTickerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            MiniTickerTranslate.X = 0;

            // Measure unconstrained text width
            MiniTrackTitle1.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double rawWidth = MiniTrackTitle1.DesiredSize.Width;

            // Target width calculation: always based on target island width so transitions don't cause false scrolling
            double targetIslandWidth = GetCompactMediaWidth();
            double availableWidth = Math.Max(130, targetIslandWidth - 76);

            double overflow = rawWidth - availableWidth;

            // Если название полностью влезает: ничего не скроллить, стоит статично и красиво
            if (overflow <= 6)
            {
                MiniTickerContainer.OpacityMask = null;
                return;
            }

            // Если сверхдлинное и действительно не влезает:
            // едет до конца -> ждет (2.0с) -> назад до начала -> ждет (2.5с)
            MiniTickerContainer.OpacityMask = _tickerMask;

            double scrollDistance = overflow + 10;
            double speed = 26.0; // скорость скролла (px/s)
            double scrollDuration = Math.Max(1.5, scrollDistance / speed);

            double holdStart = 2.5; // комфортная пауза на старте, чтобы прочитать начало
            double holdEnd = 2.0;   // пауза в конце, чтобы прочитать конец
            double holdCycle = 2.5; // пауза перед повтором

            double t0 = 0.0;
            double t1 = t0 + holdStart;
            double t2 = t1 + scrollDuration;
            double t3 = t2 + holdEnd;
            double t4 = t3 + scrollDuration;
            double t5 = t4 + holdCycle;

            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            var keyFramesAnim = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(t5),
                RepeatBehavior = RepeatBehavior.Forever
            };

            // 1. Короткая пауза в начале
            keyFramesAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            keyFramesAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t1))));

            // 2. Едет до конца названия
            keyFramesAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-scrollDistance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t2)), ease));

            // 3. "Подождать чуть-чуть прям чуть-чуть"
            keyFramesAnim.KeyFrames.Add(new LinearDoubleKeyFrame(-scrollDistance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t3))));

            // 4. Назад до начала
            keyFramesAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t4)), ease));

            // 5. "Ждет опять" перед следующим кругом
            keyFramesAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t5))));

            MiniTickerTranslate.BeginAnimation(TranslateTransform.XProperty, keyFramesAnim);
        }

        private void SwitchToCompactIdle()
        {
            if (IsCallDisplayActive || _isNotificationActive) return;
            ApplyIslandMode(IslandMode.Idle);
        }

        private void ExpandIsland()
        {
            if (IsCallDisplayActive || _isNotificationActive || !_mediaService.CurrentTrack.HasMedia) return;

            _isExpanded = true;
            _collapseCleanupTimer.Stop();
            _volumeDismissTimer.Stop();
            _isShowingVolume = false;

            // Reset hover scale
            IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            IslandScale.ScaleX = 1.0;
            IslandScale.ScaleY = 1.0;

            _currentMode = IslandMode.ExpandedMedia;
            AnimateIslandSize(390, 180, useSpring: true);

            // ── Fade out compact view smoothly ──────────────────────────────
            var compactFadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = EaseCubicIn
            };
            compactFadeOut.Completed += (s, e) =>
            {
                if (_isExpanded)
                {
                    CompactMediaView.Visibility = Visibility.Collapsed;
                    CompactIdleView.Visibility = Visibility.Collapsed;
                    CompactMediaView.Opacity = 1.0;
                    CompactMediaView.BeginAnimation(OpacityProperty, null);
                }
            };
            CompactMediaView.BeginAnimation(OpacityProperty, compactFadeOut);

            // Clear previous animations on expanded elements
            AlbumArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            AlbumArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            AlbumArtBorder.BeginAnimation(OpacityProperty, null);
            TitlesTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            TitlesPanel.BeginAnimation(OpacityProperty, null);
            TimelineTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            TimelineGrid.BeginAnimation(OpacityProperty, null);
            ControlsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            ControlsGrid.BeginAnimation(OpacityProperty, null);

            // ── Expanded Container: fade in with subtle delay ──────────────────
            ExpandedMediaView.Visibility = Visibility.Visible;
            ExpandedMediaView.Opacity = 0.0;
            ExpandedMediaView.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(260))
                {
                    BeginTime = TimeSpan.FromMilliseconds(30),
                    EasingFunction = EaseCubicOut
                });

            // ── 1. Album Art: spring scale + fade ──────────────────────────────────
            AlbumArtScale.ScaleX = 0.45;
            AlbumArtScale.ScaleY = 0.45;
            AlbumArtBorder.Opacity = 0.0;

            AlbumArtScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1.0, DurExpand) { BeginTime = TimeSpan.FromMilliseconds(30), EasingFunction = EaseSpringOut });
            AlbumArtScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.0, DurExpand) { BeginTime = TimeSpan.FromMilliseconds(30), EasingFunction = EaseSpringOut });
            AlbumArtBorder.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(280)) { BeginTime = TimeSpan.FromMilliseconds(30), EasingFunction = EaseCubicOut });

            // ── 2. Titles: slide in from left + fade (stagger +55ms) ─────────────
            TitlesTranslate.X = -18;
            TitlesPanel.Opacity = 0.0;
            TitlesTranslate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(420))
                {
                    BeginTime = TimeSpan.FromMilliseconds(55),
                    EasingFunction = EaseSpringOut
                });
            TitlesPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(280))
                {
                    BeginTime = TimeSpan.FromMilliseconds(55),
                    EasingFunction = EaseCubicOut
                });

            // ── 3. Timeline: slide up + fade (stagger +95ms) ─────────────────────
            TimelineTranslate.Y = 14;
            TimelineGrid.Opacity = 0.0;
            TimelineTranslate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(420))
                {
                    BeginTime = TimeSpan.FromMilliseconds(95),
                    EasingFunction = EaseSpringOut
                });
            TimelineGrid.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(280))
                {
                    BeginTime = TimeSpan.FromMilliseconds(95),
                    EasingFunction = EaseCubicOut
                });

            // ── 4. Controls: slide up + fade (stagger +135ms) ─────────────────────
            ControlsTranslate.Y = 18;
            ControlsGrid.Opacity = 0.0;
            ControlsTranslate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(420))
                {
                    BeginTime = TimeSpan.FromMilliseconds(135),
                    EasingFunction = EaseSpringOut
                });
            ControlsGrid.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(280))
                {
                    BeginTime = TimeSpan.FromMilliseconds(135),
                    EasingFunction = EaseCubicOut
                });

            UpdateTimelineUI();
            _timelineTimer.Start();
        }

        private void CollapseIsland()
        {
            if (IsCallDisplayActive || _isNotificationActive) return;

            _isExpanded = false;
            _isDraggingScrubber = false;
            _timelineTimer.Stop();

            var targetMode = _mediaService.CurrentTrack.HasMedia ? IslandMode.CompactMedia : IslandMode.Idle;
            var targetView = (targetMode == IslandMode.CompactMedia) ? (FrameworkElement)CompactMediaView : CompactIdleView;
            _currentMode = targetMode;

            (double targetWidth, double targetHeight) = GetDimensionsForMode(targetMode);
            AnimateIslandSize(targetWidth, targetHeight, useSpring: true);

            // ── Fade out expanded elements ──────────────────────────────────────
            ExpandedMediaView.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(130)) { EasingFunction = EaseQuartIn });

            AlbumArtScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.55, DurCollapse) { EasingFunction = EaseQuartIn });
            AlbumArtScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.55, DurCollapse) { EasingFunction = EaseQuartIn });

            ControlsTranslate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(12, DurCollapse) { EasingFunction = EaseQuartIn });
            ControlsGrid.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(110)) { EasingFunction = EaseQuartIn });

            TitlesTranslate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-10, DurCollapse) { EasingFunction = EaseQuartIn });
            TitlesPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(110)) { EasingFunction = EaseQuartIn });

            TimelineGrid.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(100)) { EasingFunction = EaseQuartIn });

            // ── Immediately cross-fade in target compact view ────────────────────
            targetView.Visibility = Visibility.Visible;
            targetView.Opacity = 0.0;
            targetView.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(220))
                {
                    BeginTime = TimeSpan.FromMilliseconds(85),
                    EasingFunction = EaseCubicOut
                });

            if (targetMode == IslandMode.CompactMedia && !string.IsNullOrEmpty(_currentMiniText))
            {
                UpdateMiniTicker(_currentMiniText);
            }

            _collapseCleanupTimer.Stop();
            _collapseCleanupTimer.Start();
        }

        private void CollapseCleanupTimer_Tick(object? sender, EventArgs e)
        {
            _collapseCleanupTimer.Stop();

            if (!_isExpanded && !IsCallDisplayActive && !_isNotificationActive)
            {
                ExpandedMediaView.Visibility = Visibility.Collapsed;
                ExpandedMediaView.Opacity = 1.0;
                ExpandedMediaView.BeginAnimation(OpacityProperty, null);

                // Reset all transform coordinates for fresh future expansion
                AlbumArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                AlbumArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                AlbumArtBorder.BeginAnimation(OpacityProperty, null);
                AlbumArtScale.ScaleX = 1.0;
                AlbumArtScale.ScaleY = 1.0;
                AlbumArtBorder.Opacity = 1.0;

                TitlesTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                TitlesPanel.BeginAnimation(OpacityProperty, null);
                TitlesTranslate.X = 0;
                TitlesPanel.Opacity = 1.0;

                TimelineTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                TimelineGrid.BeginAnimation(OpacityProperty, null);
                TimelineTranslate.Y = 0;
                TimelineGrid.Opacity = 1.0;

                ControlsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                ControlsGrid.BeginAnimation(OpacityProperty, null);
                ControlsTranslate.Y = 0;
                ControlsGrid.Opacity = 1.0;
            }
        }

        private void AnimateIslandSize(double targetWidth, double targetHeight, bool useSpring = true)
        {
            IEasingFunction ease = useSpring ? EaseSpringSize : EaseCubicOut;

            IslandBorder.BeginAnimation(WidthProperty, new DoubleAnimation
            {
                To = targetWidth,
                Duration = DurSize,
                EasingFunction = ease
            });
            IslandBorder.BeginAnimation(HeightProperty, new DoubleAnimation
            {
                To = targetHeight,
                Duration = DurSize,
                EasingFunction = ease
            });
        }

        private void AnimateHoverState(bool isHovered)
        {
            if (_isExpanded || IsCallDisplayActive || _isNotificationActive || _isHidingForGame) return;

            double targetScale = isHovered ? 1.038 : 1.0;
            // Hover-in: швидкий spring pop; hover-out: м'який повернення
            IEasingFunction ease = isHovered
                ? (IEasingFunction)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.30 }
                : EaseCubicOut;
            var dur = isHovered ? TimeSpan.FromMilliseconds(220) : DurHover;

            IslandScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(targetScale, dur) { EasingFunction = ease });
            IslandScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(targetScale, dur) { EasingFunction = ease });
        }

        private void EqualizerTimer_Tick(object? sender, EventArgs e)
        {
            if (_isExpanded) return;

            _barAnims[0].To = _rand.Next(4, 12);
            _barAnims[1].To = _rand.Next(6, 15);
            _barAnims[2].To = _rand.Next(4, 11);
            _barAnims[3].To = _rand.Next(5, 13);

            Bar1.BeginAnimation(HeightProperty, _barAnims[0]);
            Bar2.BeginAnimation(HeightProperty, _barAnims[1]);
            Bar3.BeginAnimation(HeightProperty, _barAnims[2]);
            Bar4.BeginAnimation(HeightProperty, _barAnims[3]);
        }

        private void ResetEqualizerBars()
        {
            var dotAnim = new DoubleAnimation(2.5, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Bar1.BeginAnimation(HeightProperty, dotAnim);
            Bar2.BeginAnimation(HeightProperty, dotAnim);
            Bar3.BeginAnimation(HeightProperty, dotAnim);
            Bar4.BeginAnimation(HeightProperty, dotAnim);
        }

        private void Island_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isHidingForGame || IsCallDisplayActive) return;

            if (_isNotificationActive)
            {
                _notificationDismissTimer.Stop();
                return;
            }

            if (_mediaService.CurrentTrack.HasMedia && !_isShowingVolume)
            {
                ExpandIsland();
            }
            else
            {
                AnimateHoverState(true);
            }
        }

        private void Island_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            AnimateHoverState(false);

            if (_isNotificationActive)
            {
                _notificationDismissTimer.Interval = TimeSpan.FromMilliseconds(1500);
                _notificationDismissTimer.Start();
                return;
            }

            if (IsCallDisplayActive || _isDraggingScrubber) return;

            if (_isExpanded)
            {
                CollapseIsland();
            }
        }

        private void Island_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isHidingForGame || IsCallDisplayActive) return;

            if (_isNotificationActive)
            {
                if (_currentNotification != null)
                {
                    OpenNotificationApp(_currentNotification);
                }
                DismissNotification();
                return;
            }

            if (_isExpanded)
            {
                CollapseIsland();
            }
            else if (_mediaService.CurrentTrack.HasMedia)
            {
                ExpandIsland();
            }
        }

        #region Scrubber / Timeline

        private void Scrubber_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var info = _mediaService.GetTimelineInfo();
            if (info.Duration <= TimeSpan.Zero) return;

            _isDraggingScrubber = true;
            ScrubberContainer.CaptureMouse();

            double x = e.GetPosition(ScrubberTrack).X;
            double trackWidth = ScrubberTrack.ActualWidth;
            if (trackWidth > 0)
            {
                double ratio = Math.Clamp(x / trackWidth, 0.0, 1.0);
                ApplyTimelineRatio(ratio);
            }
        }

        private void Scrubber_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_isDraggingScrubber) return;

            double x = e.GetPosition(ScrubberTrack).X;
            double trackWidth = ScrubberTrack.ActualWidth;
            if (trackWidth > 0)
            {
                double ratio = Math.Clamp(x / trackWidth, 0.0, 1.0);
                ApplyTimelineRatio(ratio);
            }
        }

        private void Scrubber_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingScrubber) return;
            _isDraggingScrubber = false;
            ScrubberContainer.ReleaseMouseCapture();

            double x = e.GetPosition(ScrubberTrack).X;
            double trackWidth = ScrubberTrack.ActualWidth;
            if (trackWidth > 0)
            {
                double ratio = Math.Clamp(x / trackWidth, 0.0, 1.0);
                ApplyTimelineRatio(ratio);

                var info = _mediaService.GetTimelineInfo();
                if (info.Duration > TimeSpan.Zero)
                {
                    var target = TimeSpan.FromTicks((long)(info.Duration.Ticks * ratio));
                    _ = _mediaService.SeekToAsync(target);
                }
            }

            if (!ScrubberContainer.IsMouseOver)
            {
                ResetScrubberHoverState();
                if (!IsMouseOver)
                {
                    CollapseIsland();
                }
            }
        }

        private void Scrubber_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isDraggingScrubber)
            {
                _isDraggingScrubber = false;
                if (!ScrubberContainer.IsMouseOver)
                {
                    ResetScrubberHoverState();
                }
            }
        }

        private void Scrubber_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var anim = new DoubleAnimation(5.5, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            ScrubberTrack.BeginAnimation(HeightProperty, anim);
            ScrubberFill.BeginAnimation(HeightProperty, anim);

            var scaleAnim = new DoubleAnimation(1.3, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            ScrubberThumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            ScrubberThumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        }

        private void Scrubber_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isDraggingScrubber) return;
            ResetScrubberHoverState();
        }

        private void Scrubber_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            e.Handled = true;
            var info = _mediaService.GetTimelineInfo();
            if (info.Duration > TimeSpan.Zero)
            {
                int deltaSeconds = e.Delta > 0 ? 5 : -5;
                var target = info.Position + TimeSpan.FromSeconds(deltaSeconds);
                if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                if (target > info.Duration) target = info.Duration;

                double ratio = target.TotalSeconds / info.Duration.TotalSeconds;
                ApplyTimelineRatio(ratio);
                _ = _mediaService.SeekToAsync(target);
            }
        }

        private void ResetScrubberHoverState()
        {
            var anim = new DoubleAnimation(4, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            ScrubberTrack.BeginAnimation(HeightProperty, anim);
            ScrubberFill.BeginAnimation(HeightProperty, anim);

            var scaleAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            ScrubberThumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
            ScrubberThumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        }

        private void ApplyTimelineRatio(double ratio)
        {
            double trackWidth = ScrubberTrack.ActualWidth;
            if (trackWidth <= 0) return;

            double fillWidth = Math.Clamp(trackWidth * ratio, 0, trackWidth);
            ScrubberFill.Width = fillWidth;
            ScrubberThumbTranslate.X = fillWidth;

            var info = _mediaService.GetTimelineInfo();
            if (info.Duration > TimeSpan.Zero)
            {
                var pos = TimeSpan.FromTicks((long)(info.Duration.Ticks * ratio));
                CurrentTimeText.Text = FormatTime(pos);
                var remaining = info.Duration - pos;
                RemainingTimeText.Text = "-" + FormatTime(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining);
            }
        }

        private void UpdateTimelineUI()
        {
            if (_isDraggingScrubber) return;

            var info = _mediaService.GetTimelineInfo();
            if (info.Duration > TimeSpan.Zero)
            {
                double ratio = Math.Clamp(info.Position.TotalSeconds / info.Duration.TotalSeconds, 0.0, 1.0);
                double trackWidth = ScrubberTrack.ActualWidth;
                if (trackWidth > 0)
                {
                    double fillWidth = trackWidth * ratio;
                    ScrubberFill.Width = fillWidth;
                    ScrubberThumbTranslate.X = fillWidth;
                }

                CurrentTimeText.Text = FormatTime(info.Position);
                var remaining = info.Duration - info.Position;
                RemainingTimeText.Text = "-" + FormatTime(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining);

                TimelineGrid.Opacity = 1.0;
                ScrubberContainer.IsEnabled = true;
            }
            else
            {
                ScrubberFill.Width = 0;
                ScrubberThumbTranslate.X = 0;
                CurrentTimeText.Text = "--:--";
                RemainingTimeText.Text = "--:--";
                TimelineGrid.Opacity = 0.4;
                ScrubberContainer.IsEnabled = false;
            }
        }

        private static string FormatTime(TimeSpan t)
        {
            if (t.TotalHours >= 1)
            {
                return t.ToString(@"h\:mm\:ss");
            }
            return t.ToString(@"m\:ss");
        }

        #endregion

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            await _mediaService.TogglePlayPauseAsync();
        }

        private async void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            await _mediaService.SkipPreviousAsync();
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            await _mediaService.SkipNextAsync();
        }

        private async void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            await _mediaService.ToggleShuffleAsync();
        }

        private async void RepeatButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            await _mediaService.ToggleRepeatAsync();
        }

        private void OnIncomingCallReceived(IncomingCallInfo info)
        {
            Dispatcher.Invoke(() =>
            {
                if (_isHidingForGame) return;

                // Dismiss any active notification immediately
                DismissNotification(immediate: true);

                // Cancel and collapse any expanded media
                if (_isExpanded)
                {
                    _isExpanded = false;
                    _timelineTimer.Stop();
                    _collapseCleanupTimer.Stop();
                    ExpandedMediaView.Visibility = Visibility.Collapsed;
                    ExpandedMediaView.BeginAnimation(OpacityProperty, null);
                }

                _isShowingVolume = false;
                _volumeDismissTimer.Stop();

                _isRinging = true;
                _isInActiveCall = false;
                _currentCall = info;
                _callDurationTimer.Stop();
                StopCallPulseAnimation();

                // Populate Call UI
                CallerNameText.Text = info.CallerName;
                CallerInitialsText.Text = string.IsNullOrWhiteSpace(info.CallerName) ? "П" : info.CallerName[0].ToString().ToUpper();

                // Switch UI to Incoming Mode
                IncomingButtonsPanel.Visibility = Visibility.Visible;
                ActiveButtonsPanel.Visibility = Visibility.Collapsed;
                CallTickerContainer.Visibility = Visibility.Visible;
                CallDurationPanel.Visibility = Visibility.Collapsed;

                string tickerText;
                SolidColorBrush tickerColor;

                if (info.App == CallApp.Telegram)
                {
                    AppBadgeGrid.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0xA1, 0xDE));
                    CallerAvatarFallback.Background = new LinearGradientBrush(
                        System.Windows.Media.Color.FromRgb(0x1E, 0x3A, 0x5F),
                        System.Windows.Media.Color.FromRgb(0x0F, 0x17, 0x2A),
                        90);
                    TelegramBadgeIcon.Visibility = Visibility.Visible;
                    DiscordBadgeIcon.Visibility = Visibility.Collapsed;
                    tickerText = "Входящий вызов • Telegram   •   ";
                    tickerColor = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7D, 0xD3, 0xFC));
                }
                else
                {
                    AppBadgeGrid.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x58, 0x65, 0xF2));
                    CallerAvatarFallback.Background = new LinearGradientBrush(
                        System.Windows.Media.Color.FromRgb(0x2C, 0x2F, 0x4D),
                        System.Windows.Media.Color.FromRgb(0x16, 0x14, 0x26),
                        90);
                    TelegramBadgeIcon.Visibility = Visibility.Collapsed;
                    DiscordBadgeIcon.Visibility = Visibility.Visible;
                    tickerText = "Входящий вызов • Discord   •   ";
                    tickerColor = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA5, 0xB4, 0xFC));
                }

                CallerTickerText1.Text = tickerText;
                CallerTickerText1.Foreground = tickerColor;
                CallerTickerText2.Text = tickerText;
                CallerTickerText2.Foreground = tickerColor;

                // Keep shadow deep, clean black
                IslandShadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                IslandShadow.Color = System.Windows.Media.Colors.Black;
                IslandShadow.Opacity = 0.65;

                ApplyIslandMode(IslandMode.IncomingCall);

                // ── Call view: fade + slide зверху ────────────────────────────────
                IncomingCallView.Opacity = 0.0;
                IncomingCallView.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(1.0, DurCallSlide)
                    {
                        BeginTime = TimeSpan.FromMilliseconds(40),
                        EasingFunction = EaseCubicOut
                    });

                // Avatar: spring pop
                if (CallerAvatarFallback.RenderTransform is ScaleTransform avatarScale)
                {
                    avatarScale.ScaleX = 0.5;
                    avatarScale.ScaleY = 0.5;
                    avatarScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                        new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(420))
                        { BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = EaseBadgePop });
                    avatarScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                        new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(420))
                        { BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = EaseBadgePop });
                }

                // Seamless infinite marquee ticker
                CallerTickerText1.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                double textWidth = Math.Max(120, CallerTickerText1.DesiredSize.Width);

                var tickerAnim = new DoubleAnimation
                {
                    From = 0,
                    To = -textWidth,
                    Duration = TimeSpan.FromSeconds(Math.Max(5, textWidth / 26.0)),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                TickerTranslate.BeginAnimation(TranslateTransform.XProperty, tickerAnim);
            });
        }

        private void CallDurationTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isInActiveCall) return;
            var elapsed = DateTime.UtcNow - _callStartTime;
            if (elapsed.TotalHours >= 1)
            {
                CallDurationText.Text = elapsed.ToString(@"h\:mm\:ss");
            }
            else
            {
                CallDurationText.Text = elapsed.ToString(@"mm\:ss");
            }
        }

        private void StartCallPulseAnimation()
        {
            // Пульс: плавне дихання від 0.3 → 1.0 з SineEase
            var pulseAnim = new DoubleAnimation
            {
                From = 0.28,
                To = 1.0,
                Duration = DurPulse,
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = EaseSineInOut
            };
            CallPulseDot.BeginAnimation(OpacityProperty, pulseAnim);

            // Додатково: micro-scale пульс на самій точці
            var scaleAnim = new DoubleAnimation
            {
                From = 0.85,
                To = 1.15,
                Duration = DurPulse,
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = EaseSineInOut
            };
            if (CallPulseDot.RenderTransform is ScaleTransform dotScale)
            {
                dotScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                dotScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
            }
        }

        private void StopCallPulseAnimation()
        {
            CallPulseDot.BeginAnimation(OpacityProperty, null);
            CallPulseDot.Opacity = 1.0;
            if (CallPulseDot.RenderTransform is ScaleTransform dotScale)
            {
                dotScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                dotScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                dotScale.ScaleX = 1.0;
                dotScale.ScaleY = 1.0;
            }
        }

        private void SwitchToActiveCall()
        {
            _isRinging = false;
            _isInActiveCall = true;
            _callStartTime = DateTime.UtcNow;

            CallDurationText.Text = "00:00";
            _callDurationTimer.Start();
            StartCallPulseAnimation();

            // Stop ticker animation and hide ticker
            TickerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            TickerTranslate.X = 0;

            ApplyIslandMode(IslandMode.ActiveCall);
            UpdateMuteButtonUi(false);
        }

        private void UpdateMuteButtonUi(bool isMuted)
        {
            if (isMuted)
            {
                MuteMicBtn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00));
                MicOnIcon.Visibility = Visibility.Collapsed;
                MicOffIcon.Visibility = Visibility.Visible;
                MuteMicBtn.ToolTip = "Включить микрофон";
            }
            else
            {
                MuteMicBtn.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2C, 0x2C, 0x2E));
                MicOnIcon.Visibility = Visibility.Visible;
                MicOffIcon.Visibility = Visibility.Collapsed;
                MuteMicBtn.ToolTip = "Выключить микрофон";
            }
        }

        private void OnCallEnded()
        {
            Dispatcher.Invoke(() =>
            {
                if (!_isRinging && !_isInActiveCall) return;

                _isRinging = false;
                _isInActiveCall = false;
                _currentCall = null;
                _callDurationTimer.Stop();
                StopCallPulseAnimation();

                // Stop ticker animation and reset
                TickerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                TickerTranslate.X = 0;

                IslandShadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                IslandShadow.Color = System.Windows.Media.Colors.Black;
                IslandShadow.Opacity = 0.45;

                // Smoothly fade out call view before switching back
                var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(160))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                fadeOut.Completed += (s, e) =>
                {
                    IncomingCallView.Visibility = Visibility.Collapsed;
                    IncomingCallView.BeginAnimation(OpacityProperty, null);

                    if (!IsCallDisplayActive && !_isNotificationActive)
                    {
                        if (_mediaService.CurrentTrack.HasMedia)
                        {
                            SwitchToCompactMedia();
                        }
                        else
                        {
                            SwitchToCompactIdle();
                        }
                    }
                };
                IncomingCallView.BeginAnimation(OpacityProperty, fadeOut);
            });
        }

        #region Notifications

        private void OnNotificationReceived(NotificationItem item)
        {
            Dispatcher.Invoke(() =>
            {
                if (IsCallDisplayActive || _isHidingForGame) return;

                if (_isNotificationActive)
                {
                    if (_notificationQueue.Count < 5)
                    {
                        _notificationQueue.Enqueue(item);
                    }
                    return;
                }

                ShowNotification(item);
            });
        }

        private void ShowNotification(NotificationItem item)
        {
            if (_isHidingForGame || IsCallDisplayActive) return;

            // If media player was expanded, collapse it first
            if (_isExpanded)
            {
                _isExpanded = false;
                _timelineTimer.Stop();
                _collapseCleanupTimer.Stop();
            }

            _currentNotification = item;
            _isNotificationActive = true;
            _volumeDismissTimer.Stop();
            _isShowingVolume = false;

            _notificationDismissTimer.Stop();
            _notificationDismissTimer.Interval = TimeSpan.FromSeconds(4.2);
            _notificationDismissTimer.Start();

            NotifSenderText.Text = item.Sender;
            NotifMessageText.Text = item.Message;
            NotifTimeText.Text = item.TimeText;
            ConfigureNotificationBadge(item);

            ApplyIslandMode(IslandMode.Notification);

            // ── Bounce острова: 3 хвилі ──────────────────────────────────────────
            var bounceAnim = new DoubleAnimationUsingKeyFrames { Duration = DurNotifBounce };
            bounceAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0,   KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bounceAnim.KeyFrames.Add(new EasingDoubleKeyFrame( 5.0,  KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), EaseSineInOut));
            bounceAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-1.5,  KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), EaseSineInOut));
            bounceAnim.KeyFrames.Add(new EasingDoubleKeyFrame( 2.5,  KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(310)), EaseSineInOut));
            bounceAnim.KeyFrames.Add(new EasingDoubleKeyFrame( 0,    KeyTime.FromTimeSpan(DurNotifBounce),
                new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.15 }));
            IslandTransform.BeginAnimation(TranslateTransform.YProperty, bounceAnim);

            // ── Notification view: fade in ────────────────────────────────────────
            NotificationView.Opacity = 0.0;
            NotificationView.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(240))
                {
                    BeginTime = TimeSpan.FromMilliseconds(30),
                    EasingFunction = EaseCubicOut
                });

            // ── Badge: spring pop з overshoot ────────────────────────────────────
            NotifBadgeScale.ScaleX = 0.3;
            NotifBadgeScale.ScaleY = 0.3;
            NotifBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(420))
                {
                    BeginTime = TimeSpan.FromMilliseconds(20),
                    EasingFunction = EaseBadgePop
                });
            NotifBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(420))
                {
                    BeginTime = TimeSpan.FromMilliseconds(20),
                    EasingFunction = EaseBadgePop
                });

            // ── Текст: slide зліва + fade (stagger після badge) ───────────────────
            NotifTextTranslate.X = 18;
            NotifTextPanel.Opacity = 0.0;
            NotifTextTranslate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(360))
                {
                    BeginTime = TimeSpan.FromMilliseconds(60),
                    EasingFunction = EaseSpringOut
                });
            NotifTextPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(280))
                {
                    BeginTime = TimeSpan.FromMilliseconds(60),
                    EasingFunction = EaseCubicOut
                });
        }

        private void DismissNotification(bool immediate = false)
        {
            _notificationDismissTimer.Stop();
            if (!_isNotificationActive) return;

            _isNotificationActive = false;
            _currentNotification = null;

            if (immediate || IsCallDisplayActive)
            {
                _notificationQueue.Clear();
                NotificationView.Visibility = Visibility.Collapsed;
                NotificationView.BeginAnimation(OpacityProperty, null);
                return;
            }

            // Smoothly fade out notification view
            var fadeOut = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (s, e) =>
            {
                if (!_isNotificationActive && !IsCallDisplayActive)
                {
                    NotificationView.Visibility = Visibility.Collapsed;
                    NotificationView.BeginAnimation(OpacityProperty, null);

                    if (_notificationQueue.Count > 0)
                    {
                        var next = _notificationQueue.Dequeue();
                        ShowNotification(next);
                    }
                    else if (_mediaService.CurrentTrack.HasMedia)
                    {
                        SwitchToCompactMedia();
                    }
                    else
                    {
                        SwitchToCompactIdle();
                    }
                }
            };
            NotificationView.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void NotificationView_MouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_currentNotification != null)
            {
                OpenNotificationApp(_currentNotification);
                DismissNotification();
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        private void OpenNotificationApp(NotificationItem notif)
        {
            try
            {
                string[] procNames = notif.App switch
                {
                    NotificationApp.Telegram => new[] { "Telegram" },
                    NotificationApp.Discord => new[] { "Discord", "DiscordPTB", "DiscordCanary" },
                    NotificationApp.WhatsApp => new[] { "WhatsApp" },
                    NotificationApp.VK => new[] { "VK", "VKontakte" },
                    NotificationApp.Mail => new[] { "olk", "Outlook", "HxOutlook", "Thunderbird" },
                    NotificationApp.Steam => new[] { "steam" },
                    NotificationApp.Browser => new[] { "chrome", "msedge", "firefox", "opera", "brave" },
                    _ => Array.Empty<string>()
                };

                IntPtr targetHwnd = IntPtr.Zero;

                foreach (var name in procNames)
                {
                    var procs = Process.GetProcessesByName(name);
                    foreach (var p in procs)
                    {
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            targetHwnd = p.MainWindowHandle;
                            break;
                        }
                    }
                    if (targetHwnd != IntPtr.Zero) break;
                }

                // If not found by known process name, search running processes by window title or AppName
                if (targetHwnd == IntPtr.Zero && !string.IsNullOrWhiteSpace(notif.AppName))
                {
                    foreach (var p in Process.GetProcesses())
                    {
                        try
                        {
                            if (p.MainWindowHandle != IntPtr.Zero &&
                                (p.ProcessName.Contains(notif.AppName, StringComparison.OrdinalIgnoreCase) ||
                                 p.MainWindowTitle.Contains(notif.AppName, StringComparison.OrdinalIgnoreCase)))
                            {
                                targetHwnd = p.MainWindowHandle;
                                break;
                            }
                        }
                        catch { }
                    }
                }

                if (targetHwnd != IntPtr.Zero)
                {
                    ShowWindowAsync(targetHwnd, SW_RESTORE);
                    SetForegroundWindow(targetHwnd);
                }
                else
                {
                    // Fallback to URI protocols if app window is in tray or not yet started
                    string? uri = notif.App switch
                    {
                        NotificationApp.Telegram => "tg://",
                        NotificationApp.Discord => "discord://",
                        NotificationApp.WhatsApp => "whatsapp://",
                        NotificationApp.VK => "https://vk.com/im",
                        NotificationApp.Steam => "steam://",
                        NotificationApp.Mail => "mailto:",
                        _ => null
                    };

                    if (!string.IsNullOrEmpty(uri))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = uri,
                            UseShellExecute = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpenNotificationApp] Error: {ex.Message}");
            }
        }

        private void ConfigureNotificationBadge(NotificationItem item)
        {
            NotifTelegramIcon.Visibility = Visibility.Collapsed;
            NotifDiscordIcon.Visibility = Visibility.Collapsed;
            NotifWhatsAppIcon.Visibility = Visibility.Collapsed;
            NotifMailIcon.Visibility = Visibility.Collapsed;
            NotifGenericIcon.Visibility = Visibility.Collapsed;
            NotifAppInitialText.Visibility = Visibility.Collapsed;

            switch (item.App)
            {
                case NotificationApp.Telegram:
                    NotifBadgeBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x24, 0xA1, 0xDE));
                    NotifTelegramIcon.Visibility = Visibility.Visible;
                    break;
                case NotificationApp.Discord:
                    NotifBadgeBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x58, 0x65, 0xF2));
                    NotifDiscordIcon.Visibility = Visibility.Visible;
                    break;
                case NotificationApp.WhatsApp:
                    NotifBadgeBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0xD3, 0x66));
                    NotifWhatsAppIcon.Visibility = Visibility.Visible;
                    break;
                case NotificationApp.VK:
                    NotifBadgeBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x77, 0xFF));
                    NotifAppInitialText.Text = "VK";
                    NotifAppInitialText.FontSize = 11;
                    NotifAppInitialText.Visibility = Visibility.Visible;
                    break;
                case NotificationApp.Mail:
                    NotifBadgeBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4));
                    NotifMailIcon.Visibility = Visibility.Visible;
                    break;
                case NotificationApp.Browser:
                    NotifBadgeBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x95, 0x00));
                    NotifAppInitialText.Text = "🌐";
                    NotifAppInitialText.FontSize = 13;
                    NotifAppInitialText.Visibility = Visibility.Visible;
                    break;
                case NotificationApp.Steam:
                    NotifBadgeBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x17, 0x1A, 0x21));
                    NotifAppInitialText.Text = "♨";
                    NotifAppInitialText.FontSize = 13;
                    NotifAppInitialText.Visibility = Visibility.Visible;
                    break;
                default:
                    NotifBadgeBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2C, 0x2C, 0x2E));
                    NotifGenericIcon.Visibility = Visibility.Visible;
                    break;
            }
        }

        #endregion

        private async void AnswerCallBtn_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var app = _currentCall?.App ?? CallApp.Telegram;
            SwitchToActiveCall();
            await _callService.AnswerCallAsync(app);
        }

        private async void DeclineCallBtn_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var app = _currentCall?.App ?? CallApp.Telegram;
            OnCallEnded();
            await _callService.DeclineCallAsync(app);
        }

        private async void MuteMicBtn_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            bool isMuted = await _callService.ToggleMuteAsync();
            UpdateMuteButtonUi(isMuted);
        }

        private async void HangUpCallBtn_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var app = _currentCall?.App ?? CallApp.Telegram;
            OnCallEnded();
            await _callService.HangUpCallAsync(app);
        }

        private void TestTelegramCall_Click(object sender, RoutedEventArgs e)
        {
            _callService.TriggerCall(new IncomingCallInfo
            {
                CallerName = "Павел Дуров",
                App = CallApp.Telegram
            }, isTest: true);
        }

        private void TestDiscordCall_Click(object sender, RoutedEventArgs e)
        {
            _callService.TriggerCall(new IncomingCallInfo
            {
                CallerName = "Wumpus",
                App = CallApp.Discord
            }, isTest: true);
        }

        private void TestTelegramMessage_Click(object sender, RoutedEventArgs e)
        {
            _notificationService.TriggerNotification(new NotificationItem
            {
                AppName = "Telegram",
                Sender = "Павел Дуров",
                Message = "Dynamic Island для Windows работает отлично! 🚀",
                App = NotificationApp.Telegram
            });
        }

        private void TestDiscordMessage_Click(object sender, RoutedEventArgs e)
        {
            _notificationService.TriggerNotification(new NotificationItem
            {
                AppName = "Discord",
                Sender = "Wumpus",
                Message = "Заходи в голосовой канал, катка начинается! 🎮",
                App = NotificationApp.Discord
            });
        }

        private void TestWhatsAppMessage_Click(object sender, RoutedEventArgs e)
        {
            _notificationService.TriggerNotification(new NotificationItem
            {
                AppName = "WhatsApp",
                Sender = "Анна",
                Message = "Привет! Отправила новые файлы по проекту 📄",
                App = NotificationApp.WhatsApp
            });
        }

        private void TestVkMessage_Click(object sender, RoutedEventArgs e)
        {
            _notificationService.TriggerNotification(new NotificationItem
            {
                AppName = "ВКонтакте",
                Sender = "Максим Смирнов",
                Message = "Скинул ссылку на трансляцию, подключайся! 🎬",
                App = NotificationApp.VK
            });
        }

        private void TestWindowsMessage_Click(object sender, RoutedEventArgs e)
        {
            _notificationService.TriggerNotification(new NotificationItem
            {
                AppName = "Параметры Windows",
                Sender = "Центр безопасности",
                Message = "Все службы системы функционируют штатно",
                App = NotificationApp.Generic
            });
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow OnClosing (Cancel={e.Cancel})\n"); } catch { }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow OnClosed started\n"); } catch { }
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            _notificationService.Dispose();
            _toastSuppressor.Dispose();
            _callService.Dispose();
            _mediaService.Dispose();
            _fullscreenDetector.Dispose();
            _volumeService.Dispose();
            _settingsService?.Dispose();
            base.OnClosed(e);
            try { System.IO.File.AppendAllText("app.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow OnClosed ended\n"); } catch { }
            System.Windows.Application.Current.Shutdown();
        }
    }
}
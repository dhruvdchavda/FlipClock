// FlipClock: a lightweight flip clock screensaver for Windows.
// Uses only WPF from .NET Framework 4.8, which ships with Windows 10 and 11.
// Must compile with the C# 5 compiler in the Windows folder, see build.cmd.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FlipClock
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // Skipping the GPU pipeline halves memory use, and one flip a minute needs little CPU
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var settings = Settings.Load();

            // Windows passes "/s", "/c", "/c:1234", "/p 1234" or "/p:1234"
            var mode = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "/c";
            var command = mode.Length >= 2 ? mode.Substring(0, 2) : mode;

            if (command == "/s")
            {
                ScreenSaver.ShowOnAllMonitors(settings);
            }
            else if (command == "/p")
            {
                var handle = mode.Length > 3 ? mode.Substring(3) : (args.Length > 1 ? args[1] : null);
                long parent;
                if (handle == null || !long.TryParse(handle, out parent))
                    return 1;
                ScreenSaver.ShowPreview(new IntPtr(parent), settings);
            }
            else
            {
                var window = new SettingsWindow(settings);
                window.Closed += delegate { app.Shutdown(); };
                window.Show();
            }
            return app.Run();
        }
    }

    public class Settings
    {
        public static string FilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlipClock", "settings.ini");

        public bool Use24Hour = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("H");
        public bool ShowSeconds = false;

        public static Settings Load()
        {
            var settings = new Settings();
            try
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    if (line.Trim() == "Use24Hour=1") settings.Use24Hour = true;
                    if (line.Trim() == "Use24Hour=0") settings.Use24Hour = false;
                    if (line.Trim() == "ShowSeconds=1") settings.ShowSeconds = true;
                    if (line.Trim() == "ShowSeconds=0") settings.ShowSeconds = false;
                }
            }
            catch (IOException) { } // No settings saved yet, use the defaults
            catch (UnauthorizedAccessException) { }
            return settings;
        }

        public void Save()
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
            File.WriteAllLines(FilePath, new[]
            {
                "Use24Hour=" + (Use24Hour ? "1" : "0"),
                "ShowSeconds=" + (ShowSeconds ? "1" : "0")
            });
        }
    }

    // One half of a card. Shows the whole card face, clipped to its top or bottom half.
    internal class CardHalf : Canvas
    {
        private readonly System.Windows.Shapes.Path _digits = new System.Windows.Shapes.Path { Fill = FlipCard.InkBrush };
        private readonly Border _shade = new Border
        {
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(FlipCard.Corner),
            Opacity = 0
        };

        public CardHalf(bool isTop)
        {
            Width = FlipCard.CardWidth;
            Height = FlipCard.CardHeight / 2;
            ClipToBounds = true;
            VerticalAlignment = isTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;

            var face = new Grid { Width = FlipCard.CardWidth, Height = FlipCard.CardHeight };
            face.Children.Add(new Border
            {
                Background = FlipCard.CardBrush,
                CornerRadius = new CornerRadius(FlipCard.Corner)
            });
            face.Children.Add(_digits);
            face.Children.Add(_shade);
            SetTop(face, isTop ? 0 : -FlipCard.CardHeight / 2);
            Children.Add(face);

            RenderTransform = new ScaleTransform(1, 1, 0, isTop ? FlipCard.CardHeight / 2 : 0);
        }

        public Geometry Digits { set { _digits.Data = value; } }
        public double Shade { set { _shade.Opacity = value; } }
        public double Fold { set { ((ScaleTransform)RenderTransform).ScaleY = value; } }
    }

    // A card showing two digits. A change folds the top half down over the bottom half.
    public class FlipCard : Grid
    {
        internal const double CardWidth = 360; // Just wider than the widest pair of digits
        internal const double CardHeight = 500;
        internal const double Corner = 34;
        private const double DigitSize = 430;
        private const double MaxShade = 0.55;
        private const double LabelGap = 22;
        internal static readonly Duration FlipTime = TimeSpan.FromMilliseconds(600);

        // Same as the screen background, so only the digits and the split across them show.
        // Each half still needs this fill, to hide the digits behind it during a flip.
        internal static readonly Brush CardBrush = Brushes.Black;
        internal static readonly Brush InkBrush = Frozen(Color.FromRgb(0xB8, 0xB8, 0xB8));

        private static readonly Typeface DigitFace = new Typeface(
            new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Condensed);
        private static readonly Rect ReferenceBounds = TextGeometry("0").Bounds;
        private static readonly Dictionary<string, Geometry> GeometryCache = new Dictionary<string, Geometry>();

        public static readonly DependencyProperty FlipProgressProperty = DependencyProperty.Register(
            "FlipProgress", typeof(double), typeof(FlipCard),
            new PropertyMetadata(1.0, (d, e) => ((FlipCard)d).ApplyProgress((double)e.NewValue)));

        private readonly CardHalf _top = new CardHalf(true);
        private readonly CardHalf _bottom = new CardHalf(false);
        private readonly CardHalf _topFlap = new CardHalf(true);
        private readonly CardHalf _bottomFlap = new CardHalf(false);
        private readonly TextBlock _label = new TextBlock
        {
            FontFamily = new FontFamily("Bahnschrift, Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            FontStretch = FontStretches.Condensed,
            FontSize = 46,
            Foreground = InkBrush
        };
        private readonly bool _alignRight;
        private string _value;

        // Hours align right and minutes align left, which keeps the gap between them the same
        // when the hour has only one digit
        public FlipCard(bool alignRight)
        {
            _alignRight = alignRight;
            Width = CardWidth;
            Height = CardHeight;
            Children.Add(_top);
            Children.Add(_bottom);
            Children.Add(_topFlap);
            Children.Add(_bottomFlap);
            Children.Add(new Rectangle
            {
                Height = 4,
                Fill = Brushes.Black,
                VerticalAlignment = VerticalAlignment.Center
            });
            // A Canvas lets the label sit outside the card, just left of the digits
            var labelLayer = new Canvas();
            Canvas.SetBottom(_label, 84);
            labelLayer.Children.Add(_label);
            Children.Add(labelLayer);
            ApplyProgress(1);
        }

        public double FlipProgress
        {
            get { return (double)GetValue(FlipProgressProperty); }
            set { SetValue(FlipProgressProperty, value); }
        }

        public string Label { set { _label.Text = value; } }

        // Left edge of the digits and label, measured from the left of the card
        public double VisibleLeft
        {
            get
            {
                var left = GetGeometry(_value).Bounds.Left;
                if (string.IsNullOrEmpty(_label.Text))
                    return left;
                _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return left - LabelGap - _label.DesiredSize.Width;
            }
        }

        public double VisibleRight { get { return GetGeometry(_value).Bounds.Right; } }

        public void ShowValue(string value, bool animate)
        {
            if (value == _value)
                return;

            var oldValue = _value;
            _value = value;
            var digits = GetGeometry(value);
            Canvas.SetRight(_label, CardWidth - digits.Bounds.Left + LabelGap);

            BeginAnimation(FlipProgressProperty, null);
            if (!animate || oldValue == null)
            {
                _top.Digits = _bottom.Digits = digits;
                FlipProgress = 1;
                return;
            }

            var oldDigits = GetGeometry(oldValue);
            _top.Digits = digits;
            _bottom.Digits = oldDigits;
            _topFlap.Digits = oldDigits;
            _bottomFlap.Digits = digits;

            var flip = new DoubleAnimation(0, 1, FlipTime) { FillBehavior = FillBehavior.Stop };
            flip.Completed += delegate
            {
                FlipProgress = 1;
            };
            FlipProgress = 0;
            BeginAnimation(FlipProgressProperty, flip);
        }

        private void ApplyProgress(double progress)
        {
            if (progress >= 1)
            {
                if (_value != null)
                    _bottom.Digits = GetGeometry(_value);
                _topFlap.Visibility = Visibility.Collapsed;
                _bottomFlap.Visibility = Visibility.Collapsed;
            }
            else if (progress < 0.5)
            {
                var t = progress * 2;
                t = t * t; // Speeds up as it falls
                _topFlap.Visibility = Visibility.Visible;
                _bottomFlap.Visibility = Visibility.Collapsed;
                _topFlap.Fold = 1 - t;
                _topFlap.Shade = MaxShade * t;
            }
            else
            {
                var t = (progress - 0.5) * 2;
                t = 1 - (1 - t) * (1 - t); // Slows down as it lands
                _topFlap.Visibility = Visibility.Collapsed;
                _bottomFlap.Visibility = Visibility.Visible;
                _bottomFlap.Fold = t;
                _bottomFlap.Shade = MaxShade * (1 - t);
            }
        }

        private Geometry GetGeometry(string text)
        {
            var key = (_alignRight ? "R" : "L") + text;
            Geometry geometry;
            if (GeometryCache.TryGetValue(key, out geometry))
                return geometry;

            // Centring on the shape of "0" keeps the hinge across the middle of every digit
            var outline = TextGeometry(text);
            outline.Transform = new TranslateTransform(
                _alignRight ? CardWidth - outline.Bounds.Right : -outline.Bounds.X,
                (CardHeight - ReferenceBounds.Height) / 2 - ReferenceBounds.Y);
            outline.Freeze();
            GeometryCache[key] = outline;
            return outline;
        }

        private static Geometry TextGeometry(string text)
        {
            return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                DigitFace, DigitSize, Brushes.White, 1.0).BuildGeometry(new Point(0, 0));
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    // The clock itself. Scales to fit whatever size it is given.
    public class ClockView : Viewbox
    {
        private const double Gap = 140;
        private const double Drift = 14;
        private const double TypicalSecondsSlack = 46;

        private readonly Settings _settings;
        private readonly FlipCard _hours = new FlipCard(true);
        private readonly FlipCard _minutes = new FlipCard(false);
        private readonly FlipCard _seconds = new FlipCard(false);
        private readonly TranslateTransform _offset = new TranslateTransform();
        private double _driftX;
        private double _driftY;
        private double _targetX;
        private double _targetY;
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private readonly Random _random = new Random();
        private int _lastMinute = -1;

        public ClockView(Settings settings)
        {
            _settings = settings;
            Stretch = Stretch.Uniform;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(240),
                RenderTransform = _offset
            };
            row.Children.Add(_hours);
            _minutes.Margin = new Thickness(Gap, 0, 0, 0);
            row.Children.Add(_minutes);
            if (settings.ShowSeconds)
            {
                _seconds.Margin = new Thickness(Gap, 0, 0, 0);
                row.Children.Add(_seconds);
            }
            Child = row;

            ShowTime(DateTime.Now, false);
            _timer.Tick += delegate
            {
                ShowTime(DateTime.Now, true);
                ScheduleNextTick();
            };
            ScheduleNextTick();
        }

        public void ShowTime(DateTime now, bool animate)
        {
            var hour = _settings.Use24Hour ? now.Hour.ToString("00") : (now.Hour % 12 == 0 ? 12 : now.Hour % 12).ToString();
            _hours.Label = _settings.Use24Hour ? "" : (now.Hour < 12 ? "AM" : "PM");
            _hours.ShowValue(hour, animate);
            _minutes.ShowValue(now.Minute.ToString("00"), animate);
            if (_settings.ShowSeconds)
                _seconds.ShowValue(now.Second.ToString("00"), animate);

            // Move slightly each minute so that the same pixels are not lit for hours
            if (_lastMinute != -1 && _lastMinute != now.Minute)
            {
                _driftX = (_random.NextDouble() * 2 - 1) * Drift;
                _driftY = (_random.NextDouble() * 2 - 1) * Drift;
            }
            _lastMinute = now.Minute;

            MoveTo(GetCentringOffset() + _driftX, _driftY, animate);
        }

        // Digits differ in width, so centre on what is actually lit rather than on the cards.
        // With seconds showing, assume a typical width or the clock would shift every second.
        private double GetCentringOffset()
        {
            var cardCount = _settings.ShowSeconds ? 3 : 2;
            var rowWidth = cardCount * FlipCard.CardWidth + (cardCount - 1) * Gap;
            var right = _settings.ShowSeconds ? rowWidth - TypicalSecondsSlack : FlipCard.CardWidth + Gap + _minutes.VisibleRight;
            return rowWidth / 2 - (_hours.VisibleLeft + right) / 2;
        }

        private void MoveTo(double x, double y, bool animate)
        {
            if (x == _targetX && y == _targetY)
                return;
            _targetX = x;
            _targetY = y;
            _offset.BeginAnimation(TranslateTransform.XProperty, animate ? new DoubleAnimation(x, FlipCard.FlipTime) : null);
            _offset.BeginAnimation(TranslateTransform.YProperty, animate ? new DoubleAnimation(y, FlipCard.FlipTime) : null);
            if (!animate)
            {
                _offset.X = x;
                _offset.Y = y;
            }
        }

        // Sleeps until the next change, so nothing runs between flips
        private void ScheduleNextTick()
        {
            var now = DateTime.Now;
            var wait = _settings.ShowSeconds
                ? 1000 - now.Millisecond
                : (60 - now.Second) * 1000 - now.Millisecond;
            _timer.Stop();
            _timer.Interval = TimeSpan.FromMilliseconds(wait + 20);
            _timer.Start();
        }
    }

    public static class ScreenSaver
    {
        private const double MouseSlack = 6;
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int Size;
            public RECT Monitor;
            public RECT Work;
            public uint Flags;
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr clip, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr window, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        public static void ShowOnAllMonitors(Settings settings)
        {
            var monitors = new List<RECT>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, dc, clip, data) =>
            {
                var info = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(monitor, ref info))
                    monitors.Add(info.Monitor);
                return true;
            }, IntPtr.Zero);

            foreach (var monitor in monitors)
                ShowOnMonitor(monitor, settings);
        }

        private static void ShowOnMonitor(RECT monitor, Settings settings)
        {
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Background = Brushes.Black,
                Cursor = Cursors.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = new ClockView(settings)
            };

            // Monitor positions are in physical pixels, so size the window through Win32
            EventHandler place = delegate
            {
                SetWindowPos(new WindowInteropHelper(window).Handle, HWND_TOPMOST, monitor.Left, monitor.Top,
                    monitor.Right - monitor.Left, monitor.Bottom - monitor.Top, SWP_NOACTIVATE);
            };
            window.SourceInitialized += place;
            window.Loaded += delegate { place(null, null); };

            Point? firstPosition = null;
            window.PreviewMouseMove += (sender, e) =>
            {
                // Windows sends a move event when the window first appears, and small
                // bumps of the desk should not end the screensaver
                var position = e.GetPosition(window);
                if (firstPosition == null)
                    firstPosition = position;
                else if ((position - firstPosition.Value).Length > MouseSlack)
                    Application.Current.Shutdown();
            };
            window.PreviewMouseDown += delegate { Application.Current.Shutdown(); };
            window.PreviewKeyDown += delegate { Application.Current.Shutdown(); };

            window.Show();
            window.Activate();
        }

        // Draws the clock inside the small monitor picture in Screen Saver Settings
        public static void ShowPreview(IntPtr parent, Settings settings)
        {
            RECT area;
            if (!IsWindow(parent) || !GetClientRect(parent, out area))
            {
                Application.Current.Shutdown();
                return;
            }

            var source = new HwndSource(new HwndSourceParameters("FlipClockPreview")
            {
                ParentWindow = parent,
                WindowStyle = WS_CHILD | WS_VISIBLE,
                PositionX = 0,
                PositionY = 0,
                Width = area.Right - area.Left,
                Height = area.Bottom - area.Top
            });
            source.CompositionTarget.BackgroundColor = Colors.Black;
            source.RootVisual = new ClockView(settings);

            // Windows just destroys the preview's parent when it is done with it
            var watcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            watcher.Tick += delegate
            {
                if (!IsWindow(parent))
                    Application.Current.Shutdown();
            };
            watcher.Start();
        }
    }

    public class SettingsWindow : Window
    {
        public SettingsWindow(Settings settings)
        {
            Title = "FlipClock Settings";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var use24Hour = new CheckBox
            {
                Content = "Use 24-hour time",
                IsChecked = settings.Use24Hour,
                Margin = new Thickness(0, 0, 0, 10)
            };
            var showSeconds = new CheckBox
            {
                Content = "Show seconds (flips every second, uses more CPU)",
                IsChecked = settings.ShowSeconds,
                Margin = new Thickness(0, 0, 0, 18)
            };
            var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };

            ok.Click += delegate
            {
                settings.Use24Hour = use24Hour.IsChecked == true;
                settings.ShowSeconds = showSeconds.IsChecked == true;
                try
                {
                    settings.Save();
                    Close();
                }
                catch (Exception error)
                {
                    if (!(error is IOException || error is UnauthorizedAccessException))
                        throw;
                    MessageBox.Show(this, "Could not save settings: " + error.Message, "FlipClock",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            cancel.Click += delegate { Close(); };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(use24Hour);
            panel.Children.Add(showSeconds);
            panel.Children.Add(buttons);
            Content = panel;
        }
    }
}

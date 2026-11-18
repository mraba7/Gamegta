// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using GameTranslator.Core;

namespace GameTranslator.App.Views;

/// <summary>
/// Bilgisayar ekranında oyunun tam üstünde şeffaf, tıklama geçiren (click-through)
/// ve odağı çalmayan canlı Türkçe altyazı katmanı (Desktop HUD Overlay).
/// MORT (Monkeyhead's OCR Realtime Translator) gibi çalışır:
/// OBS Elgato'dan temiz oyun görüntüsü kaydederken, oyuncu monitöründe canlı Türkçe altyazı okur.
/// 3 monitörlü yayıncı kurulumlarında istenen ekrana serbestçe sürüklenebilir.
/// </summary>
public sealed class DesktopSubtitleOverlay : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private static DesktopSubtitleOverlay? _instance;
    private static bool _enabled = false; // Varsayılan olarak kapalı (kullanıcı isterse açar)
    private static double? _customLeft;
    private static double? _customTop;
    private static Rect? _targetMonitorRect;
    private static bool _isPositioningMode;

    public static void SetTargetMonitor(int left, int top, int width, int height)
    {
        _targetMonitorRect = new Rect(left, top, width, height);
    }

    private readonly TextBlock _subtitleText;
    private readonly Border _container;
    private readonly DispatcherTimer _timer;

    public static bool IsOverlayEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) Clear();
        }
    }

    public static void Initialize()
    {
        if (Application.Current == null) return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_instance == null)
            {
                _instance = new DesktopSubtitleOverlay();
            }
        });
    }

    public static void Display(string text, int durationMs = 4500, SubtitleStyle? style = null, GameTranslator.Core.TextBox? box = null)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(text)) return;

        if (Application.Current == null) return;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_instance == null)
                {
                    _instance = new DesktopSubtitleOverlay();
                }
                _instance.ShowText(text, durationMs, style, box);
            }
            catch { }
        });
    }

    public static void StartDragMode()
    {
        if (Application.Current == null) return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_instance == null)
            {
                _instance = new DesktopSubtitleOverlay();
            }
            _instance.EnableDragMode();
        });
    }

    public static void ResetPosition()
    {
        _customLeft = null;
        _customTop = null;
        if (Application.Current == null) return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            _instance?.UpdatePosition();
        });
    }

    public static void Clear()
    {
        if (Application.Current == null) return;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _instance?.HideText();
            }
            catch { }
        });
    }

    private DesktopSubtitleOverlay()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        var work = SystemParameters.WorkArea;
        MaxWidth = Math.Max(1200, work.Width * 0.85);

        _subtitleText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 36,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                Direction = 315,
                ShadowDepth = 2.5,
                BlurRadius = 8,
                Opacity = 0.98
            }
        };

        _container = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDC, 0x0A, 0x0E, 0x17)),
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 0x38, 0x75, 0xE8)),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(24, 12, 24, 14),
            Child = _subtitleText
        };

        Content = _container;
        Visibility = Visibility.Collapsed;

        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) =>
        {
            if (!_isPositioningMode) HideText();
        };

        MouseDown += OnWindowMouseDown;

        SourceInitialized += (_, _) =>
        {
            ApplyClickThrough(true);
        };
    }

    private void ApplyClickThrough(bool clickThrough)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Ekran kaydından (OBS, BitBlt, Windows Graphics Capture) gizle - MORT gibi çalışır
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

            var style = GetWindowLong(hwnd, GWL_EXSTYLE);
            if (clickThrough)
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            }
            else
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, (style & ~WS_EX_TRANSPARENT) | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
            }
        }
        catch { }
    }

    private void EnableDragMode()
    {
        _isPositioningMode = true;
        _timer.Stop();
        ApplyClickThrough(false);

        Cursor = Cursors.SizeAll;
        _container.BorderBrush = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)); // Parlak Neon Mavi
        _container.BorderThickness = new Thickness(2.5);
        _subtitleText.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
        _subtitleText.Text = "📍 GameTranslator: اسحب الترجمة إلى الشاشة أو المكان الذي تريده\n(انقر هنا مرتين للتثبيت)";

        UpdatePosition();
        Visibility = Visibility.Visible;
    }

    private void DisableDragMode()
    {
        if (!_isPositioningMode) return;
        _isPositioningMode = false;

        _customLeft = Left;
        _customTop = Top;

        Cursor = Cursors.Arrow;
        _container.BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 0x38, 0x75, 0xE8));
        _container.BorderThickness = new Thickness(1.5);
        _subtitleText.Foreground = Brushes.White;

        ApplyClickThrough(true);
        HideText();
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isPositioningMode) return;

        if (e.ClickCount == 2)
        {
            DisableDragMode();
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            try
            {
                DragMove();
                _customLeft = Left;
                _customTop = Top;
            }
            catch { }
        }
    }

    private void ShowText(string text, int durationMs, SubtitleStyle? style, GameTranslator.Core.TextBox? box)
    {
        if (_isPositioningMode) return;

        ApplyStyle(style);
        _subtitleText.Text = text;

        UpdatePosition(style, box);

        if (Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
        }

        _timer.Stop();
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(2500, durationMs));
        _timer.Start();
    }

    private void ApplyStyle(SubtitleStyle? style)
    {
        if (style == null) return;

        // Yazı rengi
        var color = style.TextColor == SubtitleTextColor.Yellow ? Color.FromRgb(0xFF, 0xE6, 0x00) : Colors.White;
        _subtitleText.Foreground = new SolidColorBrush(color);

        // Yazı boyutu: 46px standart TV fontunu PC monitörü için ~28px'e oranlar
        double targetSize = Math.Clamp(style.FontSize * 0.62, 20, 52);
        _subtitleText.FontSize = targetSize;
        _subtitleText.LineHeight = targetSize * 1.3;
        _subtitleText.FontWeight = style.Bold ? FontWeights.Bold : FontWeights.SemiBold;

        // Arka plan opaklığı (Cover seçildiyse İngilizce altyazıyı tamamen örtmek için en az %90 opaklık)
        int minAlpha = style.Placement == SubtitlePlacement.Cover ? 230 : 40;
        byte alpha = (byte)Math.Clamp(Math.Max(minAlpha, (style.Background * 255) / 100), 40, 255);
        _container.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x0A, 0x0E, 0x17));
    }

    private void HideText()
    {
        _timer.Stop();
        Visibility = Visibility.Collapsed;
    }

    private void UpdatePosition(SubtitleStyle? style = null, GameTranslator.Core.TextBox? box = null)
    {
        Measure(new Size(MaxWidth, double.PositiveInfinity));
        var desiredWidth = Math.Max(ActualWidth, DesiredSize.Width);
        var desiredHeight = Math.Max(ActualHeight, DesiredSize.Height);

        if (_customLeft.HasValue && _customTop.HasValue)
        {
            Left = _customLeft.Value;
            Top = _customTop.Value;
            return;
        }

        Rect work = _targetMonitorRect ?? new Rect(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);

        if (box.HasValue)
        {
            var b = box.Value;
            // Kullanıcının seçtiği okuma alanının (Oyun Okuma Alanı) yatay merkezine tam otur
            double targetCenterX = work.Left + (((b.Left + b.Right) / 2.0) * work.Width);
            Left = Math.Clamp(targetCenterX - (desiredWidth / 2.0), work.Left, Math.Max(work.Left, work.Right - desiredWidth));

            if (style?.Placement == SubtitlePlacement.Cover)
            {
                // Orijinal altyazının tam üstüne oturup onu tamamen kapatır (MORT gibi)
                double originalCenterY = work.Top + (((b.Top + b.Bottom) / 2.0) * work.Height);
                Top = Math.Clamp(originalCenterY - (desiredHeight / 2.0), work.Top, Math.Max(work.Top, work.Bottom - desiredHeight));
                _container.MinWidth = Math.Max(420, (b.Right - b.Left) * work.Width + 40);
                return;
            }
            else if (style?.Placement == SubtitlePlacement.Above)
            {
                double originalTopY = work.Top + (b.Top * work.Height);
                Top = Math.Clamp(originalTopY - desiredHeight - 10, work.Top, Math.Max(work.Top, work.Bottom - desiredHeight));
                _container.MinWidth = 0;
                return;
            }
            else if (style?.Placement == SubtitlePlacement.Below)
            {
                double originalBottomY = work.Top + (b.Bottom * work.Height);
                Top = Math.Clamp(originalBottomY + 10, work.Top, Math.Max(work.Top, work.Bottom - desiredHeight));
                _container.MinWidth = 0;
                return;
            }
        }

        // Fixed modunda veya kutu yoksa standart alt ortalama
        Left = work.Left + ((work.Width - desiredWidth) / 2);
        _container.MinWidth = 0;
        int bottomPercent = style?.BottomPercent ?? 7;
        double marginFromBottom = Math.Max(40, work.Height * (bottomPercent / 100.0));
        Top = work.Bottom - desiredHeight - marginFromBottom;
    }
}

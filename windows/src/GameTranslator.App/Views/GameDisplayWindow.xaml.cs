// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameTranslator.Core;
using GameTranslator.Shell;

namespace GameTranslator.App.Views;

/// <summary>
/// Bilgisayarda TV deneyimi sunan bağımsız Canlı Oyun &amp; TV Ekranı penceresi.
/// PS5 Remote Play, Capture Card veya Masaüstü/OBS görüntüsünü tam ekranda veya pencerede
/// canlı Türkçe altyazı katmanıyla birlikte yansıtır.
/// </summary>
public sealed partial class GameDisplayWindow : Window
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private readonly AppController controller;
    private readonly DispatcherTimer subtitleTimer;
    private readonly DispatcherTimer hintTimer;
    private readonly DispatcherTimer headerAutoHideTimer;
    private bool isFullscreen;
    private WindowState previousState = WindowState.Normal;
    private WindowStyle previousStyle = WindowStyle.SingleBorderWindow;
    private int isRenderingFrame = 0;
    private WriteableBitmap? writeableBitmap;

    public GameDisplayWindow(AppController controller)
    {
        InitializeComponent();
        this.controller = controller;

        subtitleTimer = new DispatcherTimer();
        subtitleTimer.Tick += (_, _) => SubtitleContainer.Visibility = Visibility.Collapsed;

        hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        hintTimer.Tick += (_, _) =>
        {
            FullscreenHint.Visibility = Visibility.Collapsed;
            hintTimer.Stop();
        };

        headerAutoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        headerAutoHideTimer.Tick += (_, _) =>
        {
            if (isFullscreen && !HeaderBar.IsMouseOver)
            {
                HeaderBar.Visibility = Visibility.Collapsed;
            }
            headerAutoHideTimer.Stop();
        };

        controller.PropertyChanged += OnControllerPropertyChanged;
        controller.LiveSubtitleShown += OnLiveSubtitleShown;
        controller.SubtitlesCleared += OnSubtitlesCleared;

        UpdateSourceBadge();

        if (controller.LastFrame != null)
        {
            UpdateFrame(controller.LastFrame);
        }

        SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    // Masaüstü yakalamada bu pencerenin sonsuz döngüye girmesini (hall of mirrors) engelle
                    SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                }
            }
            catch { }
        };

        Closed += (_, _) =>
        {
            controller.PropertyChanged -= OnControllerPropertyChanged;
            controller.LiveSubtitleShown -= OnLiveSubtitleShown;
            controller.SubtitlesCleared -= OnSubtitlesCleared;
            subtitleTimer.Stop();
            hintTimer.Stop();
            headerAutoHideTimer.Stop();
        };
    }

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppController.LastFrame))
        {
            if (controller.LastFrame != null)
            {
                UpdateFrame(controller.LastFrame);
            }
            else
            {
                LiveGameImage.Source = null;
                PlaceholderPanel.Visibility = Visibility.Visible;
            }
        }
        else if (e.PropertyName == nameof(AppController.IsRunning))
        {
            UpdateSourceBadge();
            if (!controller.IsRunning)
            {
                LiveGameImage.Source = null;
                PlaceholderPanel.Visibility = Visibility.Visible;
            }
        }
    }

    private void UpdateFrame(Frame frame)
    {
        // Donmayı engellemek için önceki kare henüz işleniyorsa bu kareyi atla (Frame Throttling)
        if (Interlocked.CompareExchange(ref isRenderingFrame, 1, 0) != 0) return;

        try
        {
            if (writeableBitmap == null ||
                writeableBitmap.PixelWidth != frame.Width ||
                writeableBitmap.PixelHeight != frame.Height)
            {
                writeableBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                RenderOptions.SetBitmapScalingMode(LiveGameImage, BitmapScalingMode.LowQuality);
                LiveGameImage.Source = writeableBitmap;
            }

            writeableBitmap.Lock();
            unsafe
            {
                fixed (byte* pSrc = frame.Pixels)
                {
                    Buffer.MemoryCopy(pSrc, (void*)writeableBitmap.BackBuffer, frame.Pixels.Length, frame.Pixels.Length);
                }
            }
            writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, frame.Width, frame.Height));
            writeableBitmap.Unlock();

            if (PlaceholderPanel.Visibility != Visibility.Collapsed)
            {
                PlaceholderPanel.Visibility = Visibility.Collapsed;
            }
        }
        catch { }
        finally
        {
            Volatile.Write(ref isRenderingFrame, 0);
        }
    }

    private void OnLiveSubtitleShown(string text, int durationMs, TextBox? box)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                ApplyStyle(controller.Settings.Style, box);
                SubtitleTextBlock.Text = text;
                SubtitleContainer.Visibility = Visibility.Visible;

                subtitleTimer.Stop();
                subtitleTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(2500, durationMs));
                subtitleTimer.Start();
            }
            catch { }
        });
    }

    private void ApplyStyle(SubtitleStyle? style, TextBox? box = null)
    {
        if (style == null) return;

        var color = style.TextColor == SubtitleTextColor.Yellow ? Color.FromRgb(0xFF, 0xE6, 0x00) : Colors.White;
        SubtitleTextBlock.Foreground = new SolidColorBrush(color);

        double targetSize = Math.Clamp(style.FontSize * 0.72, 24, 60);
        SubtitleTextBlock.FontSize = targetSize;
        SubtitleTextBlock.LineHeight = targetSize * 1.35;
        SubtitleTextBlock.FontWeight = style.Bold ? FontWeights.Bold : FontWeights.SemiBold;

        // Orijinal altyazının üstünü tamamen örtmek için en az %96 opak siyah zemin
        int minAlpha = style.Placement == SubtitlePlacement.Cover ? 248 : 40;
        byte alpha = (byte)Math.Clamp(Math.Max(minAlpha, (style.Background * 255) / 100), 40, 255);
        SubtitleContainer.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x04, 0x06, 0x0B));

        var targetBox = box ?? (controller.Settings.Region is { } r
            ? new TextBox(r.Left, r.Top, r.Right, r.Bottom)
            : new TextBox(0.2, 0.78, 0.8, 0.92));

        double winW = ActualWidth;
        double winH = ActualHeight;

        if (winW > 100 && winH > 100 && LiveGameImage.Source != null)
        {
            double imgW = LiveGameImage.Source.Width;
            double imgH = LiveGameImage.Source.Height;
            if (imgW > 0 && imgH > 0)
            {
                double scale = Math.Min(winW / imgW, winH / imgH);
                double renderedW = imgW * scale;
                double renderedH = imgH * scale;
                double renderedX = (winW - renderedW) / 2.0;
                double renderedY = (winH - renderedH) / 2.0;

                // Genişlik ve yatay konum: orijinal altyazının merkezine tam oturacak şekilde
                double targetCenterX = renderedX + (((targetBox.Left + targetBox.Right) / 2.0) * renderedW);
                double boxWidth = Math.Max(560, (targetBox.Right - targetBox.Left) * renderedW + 60);
                boxWidth = Math.Min(winW - 20, boxWidth);
                SubtitleContainer.Width = boxWidth;
                SubtitleContainer.HorizontalAlignment = HorizontalAlignment.Left;
                double leftMargin = Math.Clamp(targetCenterX - (boxWidth / 2.0), renderedX, Math.Max(renderedX, renderedX + renderedW - boxWidth));

                if (style.Placement == SubtitlePlacement.Cover)
                {
                    // Orijinal altyazının TAM ÜSTÜNE yerleş ve TV CouchSubs gibi fiziksel olarak tamamen ört
                    SubtitleContainer.VerticalAlignment = VerticalAlignment.Top;
                    double originalCenterY = renderedY + (((targetBox.Top + targetBox.Bottom) / 2.0) * renderedH);
                    double origHeight = (targetBox.Bottom - targetBox.Top) * renderedH;
                    double boxHeight = Math.Max(Math.Max(70, origHeight + 20), targetSize * 2.2);
                    SubtitleContainer.Height = boxHeight;
                    double topMargin = Math.Clamp(originalCenterY - (boxHeight / 2.0), 10, winH - boxHeight - 10);
                    SubtitleContainer.Margin = new Thickness(leftMargin, topMargin, 0, 0);
                    return;
                }
                else if (style.Placement == SubtitlePlacement.Above)
                {
                    SubtitleContainer.VerticalAlignment = VerticalAlignment.Top;
                    SubtitleContainer.Height = double.NaN;
                    double originalTopY = renderedY + (targetBox.Top * renderedH);
                    double estHeight = targetSize * 2.2 + 28;
                    double topMargin = Math.Clamp(originalTopY - estHeight - 12, 10, winH - estHeight - 10);
                    SubtitleContainer.Margin = new Thickness(leftMargin, topMargin, 0, 0);
                    return;
                }
                else if (style.Placement == SubtitlePlacement.Below)
                {
                    SubtitleContainer.VerticalAlignment = VerticalAlignment.Top;
                    SubtitleContainer.Height = double.NaN;
                    double originalBottomY = renderedY + (targetBox.Bottom * renderedH);
                    double topMargin = Math.Clamp(originalBottomY + 12, 10, winH - 100);
                    SubtitleContainer.Margin = new Thickness(leftMargin, topMargin, 0, 0);
                    return;
                }
            }
        }

        // Sabit (Fixed) veya hesaplanamadıysa standart alt kenar boşluğu
        SubtitleContainer.HorizontalAlignment = HorizontalAlignment.Center;
        SubtitleContainer.VerticalAlignment = VerticalAlignment.Bottom;
        SubtitleContainer.Height = double.NaN;
        int bottomMargin = Math.Max(30, (int)(winH * (style.BottomPercent / 100.0)));
        SubtitleContainer.Margin = new Thickness(0, 0, 0, bottomMargin);
        SubtitleContainer.Width = double.NaN;
        SubtitleContainer.MaxWidth = Math.Max(600, winW * 0.85);
    }

    private void OnSubtitlesCleared()
    {
        Dispatcher.BeginInvoke(() =>
        {
            subtitleTimer.Stop();
            SubtitleContainer.Visibility = Visibility.Collapsed;
        });
    }

    private void UpdateSourceBadge()
    {
        var src = controller.Settings.VideoSource;
        SourceBadgeText.Text = src switch
        {
            VideoSourceType.DesktopScreen => "🖥️ سطح المكتب / OBS",
            VideoSourceType.CaptureCard => "📹 كرت الالتقاط",
            _ => "🎮 PS5 Remote Play"
        };

        bool running = controller.IsRunning;
        StatusDot.Fill = new SolidColorBrush(running ? Color.FromRgb(0x10, 0xB9, 0x81) : Color.FromRgb(0x94, 0xA3, 0xB8));
        FpsBadgeText.Text = running ? "1080p مباشر" : "BEKLEMEDE";
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !isFullscreen)
        {
            try { DragMove(); } catch { }
        }
    }

    private void OnImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleFullscreen();
        }
    }

    private void OnToggleFullscreen(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        if (!isFullscreen)
        {
            previousState = WindowState;
            previousStyle = WindowStyle;

            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            isFullscreen = true;

            // Tam ekranda üst kontrol çubuğunu tamamen gizle (kusursuz sinema TV modu)
            HeaderBar.Visibility = Visibility.Collapsed;

            FullscreenHint.Visibility = Visibility.Visible;
            hintTimer.Stop();
            hintTimer.Start();
        }
        else
        {
            WindowStyle = previousStyle;
            WindowState = previousState;
            isFullscreen = false;
            HeaderBar.Visibility = Visibility.Visible;
            FullscreenHint.Visibility = Visibility.Collapsed;
            headerAutoHideTimer.Stop();
        }
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!isFullscreen) return;

        var pos = e.GetPosition(this);
        // Fare ekranın en üstüne (0-45px) geldiğinde kontrol çubuğunu göster
        if (pos.Y < 45)
        {
            if (HeaderBar.Visibility != Visibility.Visible)
            {
                HeaderBar.Visibility = Visibility.Visible;
            }
            headerAutoHideTimer.Stop();
            headerAutoHideTimer.Start();
        }
        else if (pos.Y > 75 && !HeaderBar.IsMouseOver)
        {
            HeaderBar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 || (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt))
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && isFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

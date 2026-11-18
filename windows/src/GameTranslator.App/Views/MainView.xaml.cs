// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using GameTranslator.App.Controls;
using GameTranslator.App.Services;
using GameTranslator.Core;
using GameTranslator.Core.LgTv;
using GameTranslator.RemotePlay;
using GameTranslator.Shell;
using GameTranslator.Translation;
using Microsoft.Win32;

namespace GameTranslator.App.Views;

/// <summary>Altyazı geçmişi öğesi.</summary>
public sealed class SubtitleHistoryItem
{
    public required string Turkish { get; init; }
    public required string English { get; init; }
    public required string Time { get; init; }
    /// <summary>Satır oyun paketinden mi geldi (insan çevirisi) yoksa çeviri motorundan mı.</summary>
    public bool FromPack { get; init; }
    public string Summary => $"[{Time}] {(FromPack ? "📦" : "📺")} {Turkish}  |  🎮 {English}";
}

/// <summary>Ana ekran: Mac sürümüyle birebir aynı 2 panelli modern arayüz ve 5 sekme.</summary>
public partial class MainView : UserControl
{
    private readonly AppController controller;
    private readonly DispatcherTimer connectionWatch = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly ObservableCollection<SubtitleHistoryItem> history = new();
    private bool loading = true;
    private string selectedColor = "#FFFFFF";
    private bool isMutePc = true;
    private string selectedPreset = "BottomCenter";
    private int isRenderingLiveFrame = 0;
    private DateTime lastThumbnailUpdate = DateTime.MinValue;

    public MainView(AppController controller)
    {
        this.controller = controller;
        DataContext = controller;
        InitializeComponent();
        ArabicLayout.Apply(this);

        HistoryListBox.ItemsSource = history;
        controller.PropertyChanged += OnChanged;
        connectionWatch.Tick += async (_, _) => await controller.CheckConnectionsAsync();

        // Geçmiş denetleyicide tutulur ve history.json'a yazılır; önceki oturumların satırları da görünür.
        ReloadHistory();

        Loaded += (_, _) =>
        {
            connectionWatch.Start();
            LoadSettings();
            UpdateOcrBox();
            UpdateTvPreview();
        };

        Action patchUpdatedHandler = () =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(UpdatePatchUi);
        };
        CommunityPatchManager.Shared.PatchesUpdated += patchUpdatedHandler;

        Unloaded += (_, _) =>
        {
            controller.PropertyChanged -= OnChanged;
            connectionWatch.Stop();
            CommunityPatchManager.Shared.PatchesUpdated -= patchUpdatedHandler;
        };

        Refresh();
        FillGames();
        UpdateOverlayStatusUi(DesktopSubtitleOverlay.IsOverlayEnabled);
        _ = controller.CheckForUpdateAsync();
    }

    private void LoadSettings()
    {
        loading = true;
        var s = controller.Settings;

        // Sliders & Ayarlar
        TextSizeSlider.Value = Math.Clamp(s.Style.FontSize, TextSizeSlider.Minimum, TextSizeSlider.Maximum);
        TextSizeLabel.Text = $"{Math.Round(TextSizeSlider.Value)} px";

        BackgroundSlider.Value = Math.Clamp(s.Style.Background, BackgroundSlider.Minimum, BackgroundSlider.Maximum);
        BackgroundLabel.Text = $"%{Math.Round(BackgroundSlider.Value)}";

        BottomPercentSlider.Value = Math.Clamp(s.Style.BottomPercent, BottomPercentSlider.Minimum, BottomPercentSlider.Maximum);
        BottomPercentLabel.Text = $"%{Math.Round(BottomPercentSlider.Value)}";

        // Renk
        selectedColor = s.Style.TextColor == SubtitleTextColor.Yellow ? "#FFE600" : "#FFFFFF";
        UpdateColorSelection();

        // Kırpma Alanı (CropRegion)
        CropTopSlider.Value = s.Region.Top;
        CropBottomSlider.Value = s.Region.Bottom;
        CropLeftSlider.Value = s.Region.Left;
        CropRightSlider.Value = s.Region.Right;

        if (s.AutoRegion)
        {
            selectedPreset = "Auto";
            ShortcutPresetText.Text = "Otomatik";
        }

        CropTopLabel.Text = $"%{Math.Round(s.Region.Top * 100)}";
        CropBottomLabel.Text = $"%{Math.Round(s.Region.Bottom * 100)}";
        CropLeftLabel.Text = $"%{Math.Round(s.Region.Left * 100)}";
        CropRightLabel.Text = $"%{Math.Round(s.Region.Right * 100)}";

        // Checkboxlar
        BoldCheck.IsChecked = s.Style.Bold;
        SpeakerColorCheck.IsChecked = s.Style.SpeakerColor;
        SharePoolCheck.IsChecked = s.SharePool;
        DubbingCheck.IsChecked = s.DubbingEnabled;
        MutePcCheck.IsChecked = isMutePc;
        UpdatePcSesiButton();

        // Yerleşim Dropdown
        foreach (ComboBoxItem item in PlacementBox.Items)
        {
            if (string.Equals((string)item.Tag, s.Style.Placement.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                PlacementBox.SelectedItem = item;
                break;
            }
        }

        // Dil Dropdown
        foreach (ComboBoxItem item in LanguageBox.Items)
        {
            if (string.Equals((string)item.Tag, s.SourceLanguage, StringComparison.OrdinalIgnoreCase))
            {
                LanguageBox.SelectedItem = item;
                break;
            }
        }

        ObsText.Text = $"OBS overlay: http://localhost:{controller.ServerPort}/obs";

        // Video Giriş Kaynağı (Remote Play / Capture Card / Desktop)
        if (s.VideoSource == VideoSourceType.CaptureCard)
        {
            SourceRadioCaptureCard.IsChecked = true;
            SourceWindowPickerCard.Visibility = Visibility.Visible;
            if (QuickSourcePickerCard != null) QuickSourcePickerCard.Visibility = Visibility.Visible;
            PopulateSourceWindows();
            if (StreamThumbnailTitle != null) StreamThumbnailTitle.Text = "📹 بث كرت الالتقاط المباشر";
            if (ThumbnailStatusText != null) ThumbnailStatusText.Text = "بانتظار كرت الالتقاط";
        }
        else if (s.VideoSource == VideoSourceType.DesktopScreen)
        {
            SourceRadioDesktop.IsChecked = true;
            SourceWindowPickerCard.Visibility = Visibility.Visible;
            if (QuickSourcePickerCard != null) QuickSourcePickerCard.Visibility = Visibility.Visible;
            PopulateSourceWindows();
            if (StreamThumbnailTitle != null) StreamThumbnailTitle.Text = "🖥️ بث سطح المكتب / OBS المباشر";
            if (ThumbnailStatusText != null) ThumbnailStatusText.Text = "بانتظار بث سطح المكتب";
        }
        else
        {
            SourceRadioRemotePlay.IsChecked = true;
            SourceWindowPickerCard.Visibility = Visibility.Collapsed;
            if (QuickSourcePickerCard != null) QuickSourcePickerCard.Visibility = Visibility.Collapsed;
            if (StreamThumbnailTitle != null) StreamThumbnailTitle.Text = "صورة الـPS5";
            if (ThumbnailStatusText != null) ThumbnailStatusText.Text = "بانتظار الصورة";
        }

        // A saved key is never shown; dots tell the user one is stored.
        foreach (var (provider, input) in new[] { (AiProvider.Gemini, GeminiKeyInput), (AiProvider.OpenAi, OpenAiKeyInput), (AiProvider.Claude, ClaudeKeyInput) })
        {
            if (input != null && controller.HasAiKey(provider)) input.Password = SavedKeyMask;
        }

        UpdateModelSelectionUi();
        UpdatePatchUi();

        loading = false;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppController.IsRunning) 
            or nameof(AppController.TranslatorStatus) 
            or nameof(AppController.ModelInstalled)
            or nameof(AppController.Console)
            or nameof(AppController.Tv)
            or nameof(AppController.TvClients)
            or nameof(AppController.Ps5StatusText)
            or nameof(AppController.TvStatusText))
        {
            Refresh();
        }

        if (e.PropertyName == nameof(AppController.TranslationTrouble)) UpdateTroubleBanner();

        if (e.PropertyName == nameof(AppController.LastFrame))
        {
            if (controller.LastFrame != null)
            {
                UpdateLiveFrame(controller.LastFrame);
            }
            else
            {
                if (LiveStreamImage != null) LiveStreamImage.Source = null;
                if (ThumbnailStreamImage != null) ThumbnailStreamImage.Source = null;
                if (StreamPlaceholder != null) StreamPlaceholder.Visibility = Visibility.Visible;
                if (ThumbnailPlaceholder != null) ThumbnailPlaceholder.Visibility = Visibility.Visible;
            }
        }

        if (e.PropertyName == nameof(AppController.LastTranslation) && !string.IsNullOrWhiteSpace(controller.LastTranslation))
        {
            MonitorTurkishText.Text = controller.LastTranslation;
            MonitorEnglishText.Text = string.IsNullOrWhiteSpace(controller.LastSource) ? "Subtitles active" : controller.LastSource;
        }

        if (e.PropertyName == nameof(AppController.History)) ReloadHistory();
    }

    /// <summary>Listeyi denetleyicinin geçmişinden yeniden kurar (en çok 200 satır; satır başına bir kez çağrılır).</summary>
    private void ReloadHistory()
    {
        history.Clear();
        foreach (var entry in controller.History)
            history.Add(new SubtitleHistoryItem
            {
                Turkish = entry.Translation,
                English = entry.Source,
                Time = entry.Date.ToLocalTime().ToString(entry.Date.ToLocalTime().Date == DateTime.Today ? "HH:mm:ss" : "dd.MM HH:mm"),
                FromPack = entry.Origin == LineOrigin.Pack,
            });
    }

    private void OnClearHistory(object sender, RoutedEventArgs e) => controller.ClearHistory();

    private void Refresh()
    {
        var running = controller.IsRunning;
        Toggle.Background = new SolidColorBrush(
            running ? Color.FromRgb(220, 38, 38) : Color.FromRgb(10, 132, 255));
        ToggleGlyph.Text = running ? Glyphs.Stop : Glyphs.Play;
        ToggleText.Text = running ? "أوقف الترجمة" : "ابدأ الترجمة";

        bool hasFrame = LiveStreamImage?.Source != null;
        bool isStreaming = running && hasFrame;

        StreamThumbnailDot.Fill = new SolidColorBrush(isStreaming
            ? Color.FromRgb(16, 185, 129)
            : (running ? Color.FromRgb(245, 158, 11) : Color.FromRgb(156, 163, 175)));
        StreamThumbnailBadge.Text = isStreaming ? "CANLI" : (running ? "جارٍ الاتصال" : "KAPALI");
        StreamThumbnailBadge.Foreground = new SolidColorBrush(isStreaming ? Color.FromRgb(56, 189, 248) : Color.FromArgb(140, 255, 255, 255));
        ThumbnailStatusText.Text = isStreaming ? "بث الـPS5 المباشر يعمل" : (running ? "بانتظار بث الـPS5..." : "بانتظار الصورة");
        LiveBadgeText.Text = isStreaming ? "مباشر" : (running ? "Bekleniyor" : "Beklemede");

        // PS5 Durum Satırı
        if (Ps5StatusDot != null && Ps5StatusLabel != null)
        {
            var paired = controller.Console != null;
            Ps5StatusDot.Fill = new SolidColorBrush(paired ? Color.FromRgb(16, 185, 129) : Color.FromRgb(156, 163, 175));
            Ps5StatusLabel.Text = paired ? $"مقترن ({controller.Console!.Host})" : "غير مقترن";
        }

        // TV Durum Satırı (Bağlandı: Yeşil | Bekleniyor: Sarı)
        if (TvStatusDot != null && TvStatusLabel != null)
        {
            var tvConnected = controller.TvClients > 0;
            TvStatusDot.Fill = new SolidColorBrush(tvConnected ? Color.FromRgb(16, 185, 129) : (controller.Tv != null ? Color.FromRgb(245, 158, 11) : Color.FromRgb(148, 163, 184)));
            TvStatusLabel.Text = tvConnected
                ? (controller.TvClients == 1 ? $"{controller.Tv?.Name ?? "TV"} (متصل)" : $"{controller.TvClients} تلفزيون متصل")
                : (controller.Tv != null ? $"{controller.Tv.Name} (بانتظار الاتصال)" : "بانتظار تطبيق التلفزيون");

            if (TvActionButton != null)
            {
                TvActionButton.Content = "Ayarla";
            }
        }

        RefreshHome(running, isStreaming);

        if (!running)
        {
            if (LiveStreamImage != null) LiveStreamImage.Source = null;
            if (ThumbnailStreamImage != null) ThumbnailStreamImage.Source = null;
            if (StreamPlaceholder != null)
            {
                StreamPlaceholder.Visibility = Visibility.Visible;
                if (StreamPlaceholderTitle != null) StreamPlaceholderTitle.Text = "لا توجد صورة";
                if (StreamPlaceholderSubtitle != null) StreamPlaceholderSubtitle.Text = "ابدأ الترجمة لترى الصورة.";
            }
            if (ThumbnailPlaceholder != null) ThumbnailPlaceholder.Visibility = Visibility.Visible;
        }
        else
        {
            if (LiveStreamImage?.Source == null && StreamPlaceholder != null)
            {
                StreamPlaceholder.Visibility = Visibility.Visible;
                if (StreamPlaceholderTitle != null) StreamPlaceholderTitle.Text = "بانتظار بث الـPS5 المباشر...";
                if (StreamPlaceholderSubtitle != null) StreamPlaceholderSubtitle.Text = "جارٍ الاتصال بالـPS5، وستظهر الصورة هنا تلقائياً.";
            }
        }
    }

    /// <summary>
    /// Ana ekranın durumu (mobildeki gibi): tek cümlelik ipucu, PS5 → Bilgisayar → TV zinciri ve yalnız bir sorun
    /// varken görünen uyarı.
    /// </summary>
    private void RefreshHome(bool running, bool streaming)
    {
        if (HomeHint == null) return;
        var green = (Brush)FindResource("successBrush");
        var orange = (Brush)FindResource("warningBrush");
        var idle = (Brush)FindResource("idleBrush");
        var red = (Brush)FindResource("dangerBrush");
        var phase = controller.SessionState.Phase;
        var paired = controller.Console != null;
        var tvOn = controller.TvClients > 0;

        ToggleText.Text = running ? "الترجمة تعمل" : "الترجمة متوقفة";
        (HomePs5Text.Text, HomePs5Dot.Fill) = !paired ? ("غير مقترن", red)
            : streaming || phase == SessionPhase.Streaming ? ("متصل", green)
            : running ? ("جارٍ الاتصال", orange)
            : ("مقترن", idle);
        (HomePcText.Text, HomePcDot.Fill) = running ? ("يترجم", green) : ("جاهز", idle);
        (HomeTvText.Text, HomeTvDot.Fill) = tvOn ? ("متصل", green) : running ? ("Bekleniyor", orange) : ("غير متصل", idle);
        HomeTvText.ToolTip = controller.Tv?.Name;
        HomePs5Text.ToolTip = controller.Console?.Host;

        HomeHint.Text = !running ? "فعّل الترجمة داخل اللعبة ثم ابدأ."
            : !tvOn ? "افتح GameTranslator على التلفزيون؛ تظهر الترجمة فوق اللعبة."
            : "يمكنك اللعب؛ الترجمة تظهر على التلفزيون فوق اللعبة.";

        // Uyarı yalnız bir şey yolunda değilken.
        (string Title, string Text)? notice = !paired
            ? ("الـPS5 غير مقترن", "اقرن الـPS5 مرة واحدة من تبويب «الاتصال» في الإعدادات.")
            : phase == SessionPhase.Disconnected && controller.SessionState.Reason is { Kind: not DisconnectKind.Stopped }
                ? ("تعذّر الاتصال بالـPS5", controller.Ps5StatusText)
                : running && !tvOn && controller.Tv is null
                    ? ("لم يُختر تلفزيون", "أضف تلفزيونك من تبويب «الاتصال» في الإعدادات؛ الترجمة تظهر حالياً على الكمبيوتر.")
                    : null;
        HomeNotice.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
        if (notice is { } n)
        {
            HomeNoticeTitle.Text = n.Title;
            HomeNoticeText.Text = n.Text;
        }
        HomePresetText.Text = ShortcutPresetText.Text;
    }

    private void UpdateLiveFrame(GameTranslator.Core.Frame frame)
    {
        // Donmayı engellemek için önceki render bitmediyse bu kareyi atla (Frame Throttling)
        if (Interlocked.CompareExchange(ref isRenderingLiveFrame, 1, 0) != 0) return;

        try
        {
            bool ocrVisible = TabOcrArea?.IsChecked == true && PanelOcrArea?.Visibility == Visibility.Visible;
            bool updateThumb = (DateTime.UtcNow - lastThumbnailUpdate).TotalMilliseconds > 1200;

            if (ocrVisible || updateThumb)
            {
                if (ocrVisible && LiveStreamImage != null)
                {
                    var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Stride);
                    bitmap.Freeze();
                    LiveStreamImage.Source = bitmap;
                    if (StreamPlaceholder != null) StreamPlaceholder.Visibility = Visibility.Collapsed;
                }
                if (updateThumb)
                {
                    lastThumbnailUpdate = DateTime.UtcNow;
                    var thumbBmp = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Stride);
                    thumbBmp.Freeze();
                    if (ThumbnailStreamImage != null) ThumbnailStreamImage.Source = thumbBmp;
                    if (ThumbnailPlaceholder != null) ThumbnailPlaceholder.Visibility = Visibility.Collapsed;
                }
            }

            if (StreamThumbnailDot != null) StreamThumbnailDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            if (StreamThumbnailBadge != null)
            {
                StreamThumbnailBadge.Text = "1080p مباشر";
                StreamThumbnailBadge.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
            }
            if (ThumbnailStatusText != null)
            {
                ThumbnailStatusText.Text = controller.Settings.VideoSource switch
                {
                    VideoSourceType.DesktopScreen => "بث سطح المكتب يعمل",
                    VideoSourceType.CaptureCard => "كرت الالتقاط يعمل",
                    _ => "بث الـPS5 المباشر يعمل"
                };
            }
            if (LiveBadgeText != null) LiveBadgeText.Text = "مباشر";
        }
        catch { }
        finally
        {
            Volatile.Write(ref isRenderingLiveFrame, 0);
        }
    }

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (controller.IsRunning)
        {
            controller.Stop();
        }
        else
        {
            if (controller.Console == null || controller.Tv == null)
            {
                // Eğer konsol henüz eşleştirilmediyse kayıtlı yapılandırmayı kontrol et
                if (controller.Console == null)
                {
                    var savedHost = ChiakiLauncher.GetSavedHost();
                    if (!string.IsNullOrWhiteSpace(savedHost))
                    {
                        var nick = ChiakiLauncher.GetRegisteredNickname(savedHost) ?? "PS5";
                        controller.SetRegisteredConsole(new RegisteredConsole(savedHost, nick, controller.AccountId ?? new byte[16], new byte[16]));
                    }
                }

                // TV henüz seçilmediyse varsayılan Bilgisayar & OBS ekran modunu etkinleştir
                if (controller.Tv == null)
                {
                    try
                    {
                        await controller.PairAsync(new LgTv("الكمبيوتر و OBS", "127.0.0.1", TvBrand.AndroidTv));
                    }
                    catch { }
                }
            }
            await controller.StartAsync();
        }
    }

    // --- Sekme Yönetimi ---

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        if (PanelOcrArea == null || PanelDisplay == null || PanelModels == null || PanelSystem == null || PanelGuide == null) return;

        PanelOcrArea.Visibility = TabOcrArea.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelDisplay.Visibility = TabDisplay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelModels.Visibility = TabModels.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelSystem.Visibility = TabSystem.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelGuide.Visibility = TabGuide.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (TabOcrArea.IsChecked == true) UpdateOcrBox();
        if (TabDisplay.IsChecked == true) UpdateTvPreview();
    }

    // --- Ana ekran ve Ayarlar ekranı ---

    /// <summary>Ayarlar ekranını istenen sekmeyle açar (ana ekran mobildeki gibi sadedir; ayrıntılar burada).</summary>
    private void ShowSettings(RadioButton tab)
    {
        tab.IsChecked = true;
        HomeLayer.Visibility = Visibility.Collapsed;
        SettingsLayer.Visibility = Visibility.Visible;
        if (tab == TabOcrArea) UpdateOcrBox();
        if (tab == TabDisplay) UpdateTvPreview();
    }

    private void OnBackHome(object sender, RoutedEventArgs e)
    {
        SettingsLayer.Visibility = Visibility.Collapsed;
        HomeLayer.Visibility = Visibility.Visible;
        Refresh();
    }

    private void OnOcrShortcut(object sender, RoutedEventArgs e) => ShowSettings(TabOcrArea);

    private void OnDisplayShortcut(object sender, RoutedEventArgs e) => ShowSettings(TabDisplay);

    private void OnGuideShortcut(object sender, RoutedEventArgs e) => ShowSettings(TabGuide);

    /// <summary>Oyun seçme penceresi (paketler yüklenmeden açılmaz; yükleme sürerken liste eksik kalırdı).</summary>
    private void OnGameShortcut(object sender, RoutedEventArgs e)
    {
        if (!CommunityPatchManager.Shared.PacksReady) return;
        GamePickerWindow.Open(Window.GetWindow(this));
        FillGames();
    }

    /// <summary>Ana ekrandaki ve ayarlardaki seçili oyun yazısı.</summary>
    private void FillGames()
    {
        var pm = CommunityPatchManager.Shared;
        var selected = pm.SelectedGameId;
        // Paketler henüz yükleniyor: ad yükleme bitince gelen olayla yazılır, arayüz beklemez.
        // Otomatik'te PS5'in bildirdiği oyun da yazılır ("Otomatik · Mafia: The Old Country").
        var name = selected is null ? (pm.RunningGame?.Name is { } running ? $"تلقائي · {running}" : "Otomatik")
            : pm.PacksReady ? pm.Games.FirstOrDefault(g => g.Id == selected).Title ?? selected
            : "…";
        if (HomeGameText != null) HomeGameText.Text = name;
        if (GameSelectText != null) GameSelectText.Text = name;
    }

    private void OnSettingsButton(object sender, RoutedEventArgs e) => ShowSettings(TabOcrArea);

    // --- Tab 1: Oyun Okuma Alanı & Şablonlar ---

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        selectedPreset = tag;

        loading = true;
        switch (tag)
        {
            case "Auto":
                // Altyazının yeri kendiliğinden bulunur; aşağıdaki kutu yalnız elle seçimde kullanılır.
                ShortcutPresetText.Text = "Otomatik";
                controller.Settings = controller.Settings with { AutoRegion = true };
                loading = false;
                return;
            case "BottomCenter":
                CropTopSlider.Value = 0.72;
                CropBottomSlider.Value = 0.85;
                CropLeftSlider.Value = 0.16;
                CropRightSlider.Value = 0.84;
                ShortcutPresetText.Text = "أسفل الوسط (المعتاد)";
                break;
            case "BottomWide":
                CropTopSlider.Value = 0.65;
                CropBottomSlider.Value = 0.94;
                CropLeftSlider.Value = 0.08;
                CropRightSlider.Value = 0.92;
                ShortcutPresetText.Text = "شريط سفلي عريض";
                break;
            case "FullBottom":
                CropTopSlider.Value = 0.50;
                CropBottomSlider.Value = 0.98;
                CropLeftSlider.Value = 0.02;
                CropRightSlider.Value = 0.98;
                ShortcutPresetText.Text = "النصف السفلي كله";
                break;
            case "TopBanner":
                CropTopSlider.Value = 0.03;
                CropBottomSlider.Value = 0.22;
                CropLeftSlider.Value = 0.08;
                CropRightSlider.Value = 0.92;
                ShortcutPresetText.Text = "المنطقة العليا (المهام)";
                break;
            case "Custom":
                ShortcutPresetText.Text = "مربع مخصّص";
                break;
        }

        controller.Settings = controller.Settings with
        {
            AutoRegion = false,
            Region = new CropRegion(CropTopSlider.Value, CropBottomSlider.Value, CropLeftSlider.Value, CropRightSlider.Value)
        };
        UpdateOcrBox();
        loading = false;
    }

    private void OnCropSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        selectedPreset = "Custom";
        ShortcutPresetText.Text = "مربع مخصّص";

        controller.Settings = controller.Settings with
        {
            AutoRegion = false,
            Region = new CropRegion(CropTopSlider.Value, CropBottomSlider.Value, CropLeftSlider.Value, CropRightSlider.Value)
        };
        UpdateOcrBox();
    }

    private void OnOcrCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateOcrBox();
    }

    private void UpdateOcrBox()
    {
        if (OcrCanvas == null || OcrBoxBorder == null || CropTopSlider == null || CropBottomSlider == null || CropLeftSlider == null || CropRightSlider == null) return;
        var w = OcrCanvas.ActualWidth;
        var h = OcrCanvas.ActualHeight;
        if (w <= 10 || h <= 10)
        {
            w = 520;
            h = 230;
        }

        var top = CropTopSlider.Value;
        var bottom = CropBottomSlider.Value;
        var left = CropLeftSlider.Value;
        var right = CropRightSlider.Value;

        var boxX = left * w;
        var boxY = top * h;
        var boxW = Math.Max(40, (right - left) * w);
        var boxH = Math.Max(20, (bottom - top) * h);

        Canvas.SetLeft(OcrBoxBorder, boxX);
        Canvas.SetTop(OcrBoxBorder, boxY);
        OcrBoxBorder.Width = boxW;
        OcrBoxBorder.Height = boxH;

        if (CropTopLabel != null) CropTopLabel.Text = $"%{Math.Round(top * 100)}";
        if (CropBottomLabel != null) CropBottomLabel.Text = $"%{Math.Round(bottom * 100)}";
        if (CropLeftLabel != null) CropLeftLabel.Text = $"%{Math.Round(left * 100)}";
        if (CropRightLabel != null) CropRightLabel.Text = $"%{Math.Round(right * 100)}";
    }

    private void OnLaunchChiakiManual(object sender, RoutedEventArgs e)
    {
        try
        {
            var host = controller.Console?.Host;
            var name = controller.Console?.Name;
            if (!string.IsNullOrWhiteSpace(host))
            {
                ChiakiLauncher.SyncConfig(host);
            }
            if (!ChiakiLauncher.LaunchOrBringToFront(host, name))
            {
                MessageBox.Show(
                    "لم يُعثر على تطبيق Chiaki. تأكد من وجود الملف 'chiaki/chiaki.exe' في مجلد البرنامج.",
                    "Chiaki غير موجود",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"تعذّر تشغيل Chiaki: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRefreshCapture(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = controller.Settings;
            if (s.VideoSource is VideoSourceType.CaptureCard or VideoSourceType.DesktopScreen)
            {
                var target = s.DesktopWindowName;
                GameTranslator.Core.Frame? frame = null;

                if (!string.IsNullOrWhiteSpace(target) && target.StartsWith("window:", StringComparison.OrdinalIgnoreCase))
                {
                    var winTitle = target["window:".Length..];
                    var hWnd = WindowCaptureService.FindWindowBySubstring(winTitle);
                    if (hWnd != IntPtr.Zero) frame = WindowCaptureService.Capture(hWnd);
                }
                else if (!string.IsNullOrWhiteSpace(target) && target.StartsWith("monitor:", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(target["monitor:".Length..], out int mIndex))
                    {
                        var monitors = WindowCaptureService.GetDisplayMonitors();
                        var targetMon = monitors.FirstOrDefault(m => m.Index == mIndex) ?? monitors.FirstOrDefault(m => m.IsPrimary);
                        if (targetMon != null) frame = WindowCaptureService.CaptureScreen(targetMon.Left, targetMon.Top, targetMon.Width, targetMon.Height);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(target))
                {
                    var hWnd = WindowCaptureService.FindWindowBySubstring(target);
                    if (hWnd != IntPtr.Zero) frame = WindowCaptureService.Capture(hWnd);
                }
                else
                {
                    // Yalnızca hiçbir hedef seçilmemişse varsayılan ekranı yakala
                    var monitors = WindowCaptureService.GetDisplayMonitors();
                    var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
                    if (primary != null) frame = WindowCaptureService.CaptureScreen(primary.Left, primary.Top, primary.Width, primary.Height);
                }

                if (frame != null)
                {
                    controller.FeedFrame(frame);
                    UpdateLiveFrame(frame);
                    return;
                }
            }
            else
            {
                var hWnd = WindowCaptureService.FindTargetWindow(out var title);
                if (hWnd != IntPtr.Zero)
                {
                    var frame = WindowCaptureService.Capture(hWnd);
                    if (frame != null)
                    {
                        controller.FeedFrame(frame);
                        UpdateLiveFrame(frame);
                        return;
                    }
                }
            }

            MessageBox.Show(
                "لم يُعثر على النافذة المطلوبة أو مصدر الصورة المباشر.\n\n" +
                "تأكد أن نافذة اللعبة أو بث الـPS5 مفتوح.",
                "تعذّر الحصول على الصورة",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"خطأ في التقاط الصورة: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- Tab 2: TV Görünümü & Renkler ---

    private void OnColorWhite(object sender, RoutedEventArgs e) => SelectColor("#FFFFFF");
    private void OnColorYellow(object sender, RoutedEventArgs e) => SelectColor("#FFE600");
    private void OnColorCyan(object sender, RoutedEventArgs e) => SelectColor("#38BDF8");
    private void OnColorGreen(object sender, RoutedEventArgs e) => SelectColor("#10B981");
    private void OnColorOrange(object sender, RoutedEventArgs e) => SelectColor("#FF9F0A");

    private void SelectColor(string hex)
    {
        selectedColor = hex;
        UpdateColorSelection();

        var textColor = hex == "#FFE600" ? SubtitleTextColor.Yellow : SubtitleTextColor.White;
        controller.Settings = controller.Settings with
        {
            Style = controller.Settings.Style with { TextColor = textColor }
        };
        controller.SyncStyle();
        UpdateTvPreview();
    }

    private void UpdateColorSelection()
    {
        ColorWhiteBorder.BorderBrush = selectedColor == "#FFFFFF" ? new SolidColorBrush(Color.FromRgb(10, 132, 255)) : Brushes.Transparent;
        ColorYellowBorder.BorderBrush = selectedColor == "#FFE600" ? new SolidColorBrush(Color.FromRgb(10, 132, 255)) : Brushes.Transparent;
        ColorCyanBorder.BorderBrush = selectedColor == "#38BDF8" ? new SolidColorBrush(Color.FromRgb(10, 132, 255)) : Brushes.Transparent;
        ColorGreenBorder.BorderBrush = selectedColor == "#10B981" ? new SolidColorBrush(Color.FromRgb(10, 132, 255)) : Brushes.Transparent;
        ColorOrangeBorder.BorderBrush = selectedColor == "#FF9F0A" ? new SolidColorBrush(Color.FromRgb(10, 132, 255)) : Brushes.Transparent;
    }

    private void OnTextSize(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        var val = (int)Math.Round(e.NewValue);
        TextSizeLabel.Text = $"{val} px";
        controller.Settings = controller.Settings with { Style = controller.Settings.Style with { FontSize = val } };
        controller.SyncStyle();
        UpdateTvPreview();
    }

    private void OnBackground(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        var val = (int)Math.Round(e.NewValue);
        BackgroundLabel.Text = $"%{val}";
        controller.Settings = controller.Settings with { Style = controller.Settings.Style with { Background = val } };
        controller.SyncStyle();
        UpdateTvPreview();
    }

    private void OnBottomPercent(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        var val = (int)Math.Round(e.NewValue);
        BottomPercentLabel.Text = $"%{val}";
        controller.Settings = controller.Settings with { Style = controller.Settings.Style with { BottomPercent = val } };
        controller.SyncStyle();
        UpdateTvPreview();
    }

    private void OnPlacement(object sender, SelectionChangedEventArgs e)
    {
        if (loading || PlacementBox.SelectedItem is not ComboBoxItem item) return;
        if (Enum.TryParse<SubtitlePlacement>((string)item.Tag, true, out var placement))
        {
            controller.Settings = controller.Settings with { Style = controller.Settings.Style with { Placement = placement } };
            controller.SyncStyle();
            UpdateTvPreview();
        }
    }

    private void OnLanguage(object sender, SelectionChangedEventArgs e)
    {
        if (loading || LanguageBox.SelectedItem is not ComboBoxItem item) return;
        var lang = (string)item.Tag;
        controller.Settings = controller.Settings with { SourceLanguage = lang };
    }

    private void OnBold(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        controller.Settings = controller.Settings with { Style = controller.Settings.Style with { Bold = BoldCheck.IsChecked == true } };
        controller.SyncStyle();
        UpdateTvPreview();
    }

    private void OnSharePool(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        controller.Settings = controller.Settings with { SharePool = SharePoolCheck.IsChecked == true };
    }

    private void OnSpeakerColor(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        controller.Settings = controller.Settings with { Style = controller.Settings.Style with { SpeakerColor = SpeakerColorCheck.IsChecked == true } };
        controller.SyncStyle();
        UpdateTvPreview();
    }

    private void OnDubbing(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        controller.Settings = controller.Settings with { DubbingEnabled = DubbingCheck.IsChecked == true };
    }

    private void OnMutePc(object sender, RoutedEventArgs e)
    {
        isMutePc = MutePcCheck.IsChecked == true;
        UpdatePcSesiButton();
    }

    private void OnPcSesiToggle(object sender, RoutedEventArgs e)
    {
        isMutePc = !isMutePc;
        MutePcCheck.IsChecked = isMutePc;
        UpdatePcSesiButton();
    }

    private void UpdatePcSesiButton()
    {
        if (PcSesiButton == null) return;
        PcSesiButton.Content = isMutePc ? "Sessiz" : "مفعّل";
        var color = isMutePc ? Color.FromRgb(239, 68, 68) : Color.FromRgb(16, 185, 129);
        PcSesiButton.Foreground = new SolidColorBrush(color);
        PcSesiButton.Background = new SolidColorBrush(Color.FromArgb(31, color.R, color.G, color.B));
        PcSesiButton.BorderBrush = new SolidColorBrush(color);
    }

    private void UpdateTvPreview()
    {
        if (TvPreviewSubtitleText == null || TvPreviewSubtitleBox == null || TextSizeSlider == null || BoldCheck == null || BackgroundSlider == null || BottomPercentSlider == null) return;
        try
        {
            var converter = new BrushConverter();
            if (converter.ConvertFromString(selectedColor) is Brush brush)
                TvPreviewSubtitleText.Foreground = brush;
        }
        catch { }

        var size = TextSizeSlider.Value;
        TvPreviewSubtitleText.FontSize = Math.Max(11, size * 0.35);
        TvPreviewSubtitleText.FontWeight = BoldCheck.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;

        var opacity = BackgroundSlider.Value / 100.0;
        var byteAlpha = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);
        TvPreviewSubtitleBox.Background = new SolidColorBrush(Color.FromArgb(byteAlpha, 0, 0, 0));

        var bottomPx = BottomPercentSlider.Value * 1.5;
        TvPreviewSubtitleBox.Margin = new Thickness(0, 0, 0, bottomPx);
    }

    private void OnTestSubtitle(object sender, RoutedEventArgs e) => controller.SendTestSubtitle();

    private void OnDragOverlayPosition(object sender, RoutedEventArgs e)
    {
        DesktopSubtitleOverlay.StartDragMode();
    }

    private void OnDesktopOverlayToggle(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb)
        {
            var isEnabled = cb.IsChecked == true;
            DesktopSubtitleOverlay.IsOverlayEnabled = isEnabled;
            UpdateOverlayStatusUi(isEnabled);
        }
    }

    private void OnToggleOverlayButton(object sender, RoutedEventArgs e)
    {
        var newState = !DesktopSubtitleOverlay.IsOverlayEnabled;
        DesktopSubtitleOverlay.IsOverlayEnabled = newState;
        if (DesktopOverlayCheck != null) DesktopOverlayCheck.IsChecked = newState;
        UpdateOverlayStatusUi(newState);
    }

    private void UpdateOverlayStatusUi(bool enabled)
    {
        if (OverlayStatusDot != null)
        {
            OverlayStatusDot.Fill = new SolidColorBrush(enabled ? Color.FromRgb(0x10, 0xB9, 0x81) : Color.FromRgb(0x94, 0xA3, 0xB8));
        }
        if (OverlayToggleButton != null)
        {
            OverlayToggleButton.Content = enabled ? "مفعّل (على الشاشة)" : "مغلق";
        }
    }

    // --- Tab 3: Modeller ---

    private void OnModelSelected(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        TranslationEngine targetEngine = TranslationEngine.DirectML;

        if (CheckedAiProvider() is { } aiProvider && !(aiProvider == AiProvider.Gemini && controller.UsesTurkish))
        {
            if (!controller.HasAiKey(aiProvider))
            {
                MessageBox.Show(AiText.KeyNeeded(aiProvider), AiText.KeyNeededTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateModelSelectionUi();
                return;
            }
            controller.UseAiProvider(aiProvider);
            UpdateModelSelectionUi();
            return;
        }

        if (RadioModelGemini.IsChecked == true)
        {
            if (!controller.HasGeminiKey)
            {
                MessageBox.Show("لاستخدام Google Gemini أدخل مفتاح Gemini في الأسفل ثم اضغط «حفظ وتفعيل».", "مطلوب مفتاح API", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateModelSelectionUi();
                return;
            }
            targetEngine = TranslationEngine.Gemini;
        }
        else if (RadioModelDeepL.IsChecked == true)
        {
            if (!controller.HasDeepLKey)
            {
                MessageBox.Show("لاستخدام DeepL أدخل مفتاح DeepL في الأسفل ثم اضغط «حفظ وتفعيل».", "مطلوب مفتاح API", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateModelSelectionUi();
                return;
            }
            targetEngine = TranslationEngine.DeepL;
        }
        else
        {
            targetEngine = TranslationEngine.DirectML;
        }

        controller.Settings = controller.Settings with { Engine = targetEngine, OnlineTranslation = (targetEngine != TranslationEngine.DirectML) };
        UpdateModelSelectionUi();
    }

    private async void OnGeminiSave(object sender, RoutedEventArgs e)
    {
        if (!controller.UsesTurkish)
        {
            await SaveAiKeyAsync(AiProvider.Gemini, GeminiKeyInput);
            return;
        }
        var key = GeminiKeyInput.Password?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show("أدخل مفتاح Google Gemini صحيحاً.", "المفتاح ناقص", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var check = await controller.SetGeminiKeyAsync(key);
        if (check.State is OnlineKeyState.Valid or OnlineKeyState.QuotaExceeded)
        {
            controller.Settings = controller.Settings with { Engine = TranslationEngine.Gemini, OnlineTranslation = true };
            UpdateModelSelectionUi();
            controller.SendTestSubtitle();
            MessageBox.Show("تم قبول مفتاح Google Gemini وصار هو خدمة الترجمة الحالية!", "Gemini مفعّل", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("مفتاح Google Gemini غير صحيح أو تعذّر الاتصال بخادم Google. تأكد من المفتاح.", "خطأ في الاتصال", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnDeepLSave(object sender, RoutedEventArgs e)
    {
        var key = DeepLKeyInput.Password?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show("أدخل مفتاح DeepL صحيحاً.", "المفتاح ناقص", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var check = await controller.SetDeepLKeyAsync(key);
        if (check.State is OnlineKeyState.Valid or OnlineKeyState.QuotaExceeded)
        {
            controller.Settings = controller.Settings with { Engine = TranslationEngine.DeepL, OnlineTranslation = true };
            UpdateModelSelectionUi();
            controller.SendTestSubtitle();
            MessageBox.Show("تم قبول مفتاح DeepL وصار هو خدمة الترجمة الحالية!", "DeepL مفعّل", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("مفتاح DeepL غير صحيح أو تعذّر الاتصال بالخادم.", "خطأ في الاتصال", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateModelSelectionUi()
    {
        if (BorderDirectMl == null || BorderGemini == null || BorderDeepL == null) return;

        var prevLoading = loading;
        loading = true;
        try
        {
            var engine = controller.Settings.Engine;
            // Arabic edition: the three AI services share one engine value; Gemini saved by older versions counts as Gemini.
            var ai = engine == TranslationEngine.Ai ? controller.Settings.Provider : engine == TranslationEngine.Gemini ? AiProvider.Gemini : (AiProvider?)null;
            if (engine == TranslationEngine.Ai && ai == AiProvider.Gemini) engine = TranslationEngine.Gemini;
            UpdateAiCards(ai);

            // Radio Buttons
            if (RadioModelDirectMl != null) RadioModelDirectMl.IsChecked = (engine == TranslationEngine.DirectML);
            if (RadioModelGemini != null) RadioModelGemini.IsChecked = (engine == TranslationEngine.Gemini);
            if (RadioModelDeepL != null) RadioModelDeepL.IsChecked = (engine == TranslationEngine.DeepL);

            // Badges
            if (BadgeDirectMl != null) BadgeDirectMl.Visibility = (engine == TranslationEngine.DirectML) ? Visibility.Visible : Visibility.Collapsed;
            if (BadgeGemini != null) BadgeGemini.Visibility = (engine == TranslationEngine.Gemini) ? Visibility.Visible : Visibility.Collapsed;
            if (BadgeDeepL != null) BadgeDeepL.Visibility = (engine == TranslationEngine.DeepL) ? Visibility.Visible : Visibility.Collapsed;

            // Border Glow
            var activeBrush = new SolidColorBrush(Color.FromRgb(10, 132, 255));
            var inactiveBrush = new SolidColorBrush(Color.FromRgb(31, 41, 61));

            BorderDirectMl.BorderBrush = (engine == TranslationEngine.DirectML) ? activeBrush : inactiveBrush;
            BorderGemini.BorderBrush = (engine == TranslationEngine.Gemini) ? activeBrush : inactiveBrush;
            BorderDeepL.BorderBrush = (engine == TranslationEngine.DeepL) ? activeBrush : inactiveBrush;
        }
        finally
        {
            loading = prevLoading;
        }
    }

    private void OnCardDirectMlClick(object sender, MouseButtonEventArgs e)
    {
        controller.Settings = controller.Settings with { Engine = TranslationEngine.DirectML, OnlineTranslation = false };
        UpdateModelSelectionUi();
    }

    private void OnCardGeminiClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is PasswordBox || e.OriginalSource is Button) return;

        if (controller.HasGeminiKey)
        {
            if (controller.UsesTurkish) controller.Settings = controller.Settings with { Engine = TranslationEngine.Gemini, OnlineTranslation = true };
            else controller.UseAiProvider(AiProvider.Gemini);
            UpdateModelSelectionUi();
        }
        else
        {
            GeminiKeyInput?.Focus();
        }
    }

    // --- Arabic edition: OpenAI and Claude cards, quality mode ---

    private const string SavedKeyMask = "************";

    /// <summary>The AI service whose radio button is checked, if any.</summary>
    private AiProvider? CheckedAiProvider() =>
        RadioModelOpenAi?.IsChecked == true ? AiProvider.OpenAi :
        RadioModelClaude?.IsChecked == true ? AiProvider.Claude :
        RadioModelGemini?.IsChecked == true ? AiProvider.Gemini : null;

    private async Task SaveAiKeyAsync(AiProvider provider, PasswordBox input)
    {
        var key = input.Password?.Trim();
        if (string.IsNullOrWhiteSpace(key) || key == SavedKeyMask)
        {
            MessageBox.Show(AiText.KeyMissing(provider), AiText.KeyMissingTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var check = await controller.SetAiKeyAsync(provider, key);
        if (check.State is OnlineKeyState.Valid or OnlineKeyState.QuotaExceeded)
        {
            input.Password = SavedKeyMask;
            UpdateModelSelectionUi();
            controller.SendTestSubtitle();
            MessageBox.Show(AiText.KeyAccepted(provider), AiText.KeyAcceptedTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show(AiText.KeyRejected(provider), AiText.KeyRejectedTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SelectAiCard(AiProvider provider, PasswordBox? input, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is PasswordBox || e.OriginalSource is Button) return;
        if (controller.HasAiKey(provider))
        {
            controller.UseAiProvider(provider);
            UpdateModelSelectionUi();
        }
        else
        {
            input?.Focus();
        }
    }

    private async void OnOpenAiSave(object sender, RoutedEventArgs e) => await SaveAiKeyAsync(AiProvider.OpenAi, OpenAiKeyInput);
    private async void OnClaudeSave(object sender, RoutedEventArgs e) => await SaveAiKeyAsync(AiProvider.Claude, ClaudeKeyInput);
    private void OnCardOpenAiClick(object sender, MouseButtonEventArgs e) => SelectAiCard(AiProvider.OpenAi, OpenAiKeyInput, e);
    private void OnCardClaudeClick(object sender, MouseButtonEventArgs e) => SelectAiCard(AiProvider.Claude, ClaudeKeyInput, e);

    private void OnFastWhenSlow(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        controller.SetFastWhenSlow(CheckFastWhenSlow?.IsChecked == true);
        UpdateModelSelectionUi();
    }

    private DispatcherTimer? usageTimer;

    /// <summary>What this run has sent to the paid services so far; refreshed every few seconds while the screen is open.</summary>
    private void UpdateUsageText()
    {
        if (UsageText == null) return;
        UsageText.Text = AiText.Usage(AiUsage.Session.Total);
        if (usageTimer is not null) return;
        usageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        usageTimer.Tick += (_, _) => { if (UsageText != null) UsageText.Text = AiText.Usage(AiUsage.Session.Total); };
        usageTimer.Start();
        Unloaded += (_, _) => usageTimer?.Stop();
    }

    private void OnDialectSelected(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        controller.SetDialect(RadioDialectSaudi?.IsChecked == true ? ArabicStyle.Saudi
                              : RadioDialectColloquial?.IsChecked == true ? ArabicStyle.Colloquial
                              : ArabicStyle.Standard);
        UpdateModelSelectionUi();
    }

    /// <summary>Says why the AI service is not translating right now; hidden while it works.</summary>
    private void UpdateTroubleBanner()
    {
        if (TroubleBanner == null || TroubleText == null) return;
        var trouble = controller.TranslationTrouble;
        TroubleBanner.Visibility = trouble is null || controller.UsesTurkish ? Visibility.Collapsed : Visibility.Visible;
        if (trouble is { } state) TroubleText.Text = AiText.Trouble(state);
    }

    private void OnQualitySelected(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        controller.SetQuality(RadioQualityFastest?.IsChecked == true ? QualityMode.Fastest : QualityMode.Best);
        UpdateModelSelectionUi();
    }

    /// <summary>OpenAI and Claude cards, the quality switch and the Arabic wording of the shared cards.</summary>
    private void UpdateAiCards(AiProvider? active)
    {
        if (BorderOpenAi == null || BorderClaude == null || BorderQuality == null) return;
        var activeBrush = new SolidColorBrush(Color.FromRgb(10, 132, 255));
        var inactiveBrush = new SolidColorBrush(Color.FromRgb(31, 41, 61));
        foreach (var (provider, border, radio, badge) in new[]
                 {
                     (AiProvider.OpenAi, BorderOpenAi, RadioModelOpenAi, BadgeOpenAi),
                     (AiProvider.Claude, BorderClaude, RadioModelClaude, BadgeClaude),
                 })
        {
            var on = active == provider;
            radio.IsChecked = on;
            badge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            border.BorderBrush = on ? activeBrush : inactiveBrush;
        }

        var quality = controller.Settings.Quality;
        RadioQualityBest.IsChecked = quality == QualityMode.Best;
        RadioQualityFastest.IsChecked = quality == QualityMode.Fastest;
        QualityHint.Text = AiText.QualityHint(active, quality);

        CheckFastWhenSlow.IsChecked = controller.Settings.FastWhenSlow;
        // The safety net only means something in Best quality.
        CheckFastWhenSlow.IsEnabled = quality == QualityMode.Best;
        UpdateUsageText();

        var dialect = controller.Settings.Dialect;
        RadioDialectStandard.IsChecked = dialect == ArabicStyle.Standard;
        RadioDialectColloquial.IsChecked = dialect == ArabicStyle.Colloquial;
        RadioDialectSaudi.IsChecked = dialect == ArabicStyle.Saudi;
        DialectHint.Text = AiText.DialectHint(dialect);
        UpdateTroubleBanner();

        // What exists for Turkish only is hidden for any other target language, and the shared cards say what they do there.
        var turkish = controller.UsesTurkish;
        BorderOpenAi.Visibility = BorderClaude.Visibility = BorderQuality.Visibility = turkish ? Visibility.Collapsed : Visibility.Visible;
        if (BorderDeepL != null) BorderDeepL.Visibility = turkish ? Visibility.Visible : Visibility.Collapsed;
        // Turkish packs, Turkish dubbing and the line pool do nothing with another target: they are not shown.
        if (BorderPacks != null) BorderPacks.Visibility = turkish ? Visibility.Visible : Visibility.Collapsed;
        if (DubbingCheck != null) DubbingCheck.Visibility = turkish ? Visibility.Visible : Visibility.Collapsed;
        if (SharePoolCheck != null) SharePoolCheck.Visibility = turkish ? Visibility.Visible : Visibility.Collapsed;
        if (!turkish)
        {
            if (LocalModelDescription != null)
            {
                LocalModelDescription.Text = AiText.LocalDescription;
                LocalModelDescription.FlowDirection = FlowDirection.RightToLeft;
            }
            if (RadioModelDirectMl != null) RadioModelDirectMl.Content = AiText.LocalTitle;
            if (GeminiDescription != null)
            {
                GeminiDescription.Text = AiText.GeminiDescription;
                GeminiDescription.FlowDirection = FlowDirection.RightToLeft;
            }
        }
    }

    private void OnCardDeepLClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is PasswordBox || e.OriginalSource is Button) return;

        if (controller.HasDeepLKey)
        {
            controller.Settings = controller.Settings with { Engine = TranslationEngine.DeepL, OnlineTranslation = true };
            UpdateModelSelectionUi();
        }
        else
        {
            DeepLKeyInput?.Focus();
        }
    }

    // --- Geçmiş Dışa Aktarma ---

    private void OnExportTxt(object sender, RoutedEventArgs e)
    {
        if (history.Count == 0)
        {
            MessageBox.Show("لا يوجد سجل ترجمة للتصدير بعد.", "السجل فارغ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog { Filter = "ملف نصي (*.txt)|*.txt", FileName = "GameTranslator_Altyazi_Gecmisi.txt" };
        if (dlg.ShowDialog() == true)
        {
            var lines = history.Select(h => $"{h.Time}\tTV: {h.Turkish}\tPS5: {h.English}");
            File.WriteAllLines(dlg.FileName, lines);
            MessageBox.Show("تم حفظ سجل الترجمة بنجاح!", "تم التصدير", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnExportJson(object sender, RoutedEventArgs e)
    {
        if (history.Count == 0)
        {
            MessageBox.Show("لا يوجد سجل ترجمة للتصدير بعد.", "السجل فارغ", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog { Filter = "ملف JSON (*.json)|*.json", FileName = "GameTranslator_Altyazi_Gecmisi.json" };
        if (dlg.ShowDialog() == true)
        {
            var json = JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            MessageBox.Show("تم حفظ سجل الترجمة بصيغة JSON!", "تم التصدير", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // --- Genel Aksiyonlar & Footer ---

    private void OnCopyObs(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText($"http://localhost:{controller.ServerPort}/obs");
        MessageBox.Show("تم نسخ عنوان OBS:\nhttp://localhost:" + controller.ServerPort + "/obs", "تم النسخ", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("هل تريد إعادة ضبط إعدادات GameTranslator؟", "إعادة ضبط الإعدادات",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            controller.ResetSettings();
            LoadSettings();
            UpdateOcrBox();
            UpdateTvPreview();
        }
    }

    private void OnReinstall(object sender, RoutedEventArgs e)
    {
        controller.Step = OnboardingStep.Welcome;
    }

    private void OnTvActionButton(object sender, RoutedEventArgs e)
    {
        OnReinstall(sender, e);
    }

    private void OnDone(object sender, RoutedEventArgs e) => OnBackHome(sender, e);

    private void OnHyperlink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
    }

    // --- Video Giriş Kaynağı (Remote Play, Capture Card, Masaüstü/OBS) ---

    private void OnVideoSourceChanged(object sender, RoutedEventArgs e)
    {
        if (loading) return;

        // Eski donmuş kareyi ve görüntüleri anında sıfırla
        if (LiveStreamImage != null) LiveStreamImage.Source = null;
        if (ThumbnailStreamImage != null) ThumbnailStreamImage.Source = null;
        if (StreamPlaceholder != null) StreamPlaceholder.Visibility = Visibility.Visible;
        if (ThumbnailPlaceholder != null) ThumbnailPlaceholder.Visibility = Visibility.Visible;
        controller.ClearLastFrame();

        VideoSourceType newSource;
        if (SourceRadioCaptureCard.IsChecked == true)
        {
            newSource = VideoSourceType.CaptureCard;
            SourceWindowPickerCard.Visibility = Visibility.Visible;
            if (QuickSourcePickerCard != null) QuickSourcePickerCard.Visibility = Visibility.Visible;
            PopulateSourceWindows();
            if (StreamThumbnailTitle != null) StreamThumbnailTitle.Text = "📹 بث كرت الالتقاط المباشر";
            if (ThumbnailStatusText != null) ThumbnailStatusText.Text = "بانتظار كرت الالتقاط";
        }
        else if (SourceRadioDesktop.IsChecked == true)
        {
            newSource = VideoSourceType.DesktopScreen;
            SourceWindowPickerCard.Visibility = Visibility.Visible;
            if (QuickSourcePickerCard != null) QuickSourcePickerCard.Visibility = Visibility.Visible;
            PopulateSourceWindows();
            if (StreamThumbnailTitle != null) StreamThumbnailTitle.Text = "🖥️ بث سطح المكتب / OBS المباشر";
            if (ThumbnailStatusText != null) ThumbnailStatusText.Text = "بانتظار بث سطح المكتب";
        }
        else
        {
            newSource = VideoSourceType.RemotePlay;
            SourceWindowPickerCard.Visibility = Visibility.Collapsed;
            if (QuickSourcePickerCard != null) QuickSourcePickerCard.Visibility = Visibility.Collapsed;
            if (StreamThumbnailTitle != null) StreamThumbnailTitle.Text = "صورة الـPS5";
            if (ThumbnailStatusText != null) ThumbnailStatusText.Text = "بانتظار الصورة";
        }

        controller.Settings = controller.Settings with { VideoSource = newSource };
    }

    private void OnRefreshWindows(object sender, RoutedEventArgs e)
    {
        PopulateSourceWindows();
    }

    private bool populatingSourceWindows;
    private void PopulateSourceWindows()
    {
        if (populatingSourceWindows) return;
        populatingSourceWindows = true;
        try
        {
            if (SourceWindowComboBox != null) SourceWindowComboBox.Items.Clear();
            if (QuickSourceWindowComboBox != null) QuickSourceWindowComboBox.Items.Clear();

            ComboBoxItem? toSelectMain = null;
            ComboBoxItem? toSelectQuick = null;
            var currentTarget = controller.Settings.DesktopWindowName;

            // 1. Açık Pencereler & OBS Projektörleri (Öncelikli Seçim!)
            var titles = WindowCaptureService.GetTopLevelWindowTitles();
            if (titles.Count > 0)
            {
                var h1 = new ComboBoxItem { Content = "── 🪟 نوافذ الألعاب المفتوحة و OBS ──", IsEnabled = false, FontWeight = FontWeights.Bold };
                var h1q = new ComboBoxItem { Content = "── 🪟 نوافذ الألعاب المفتوحة و OBS ──", IsEnabled = false, FontWeight = FontWeights.Bold };
                SourceWindowComboBox?.Items.Add(h1);
                QuickSourceWindowComboBox?.Items.Add(h1q);

                foreach (var title in titles)
                {
                    var tag = $"window:{title}";
                    var item1 = new ComboBoxItem { Content = $"🪟 {title}", Tag = tag };
                    var item2 = new ComboBoxItem { Content = $"🪟 {title}", Tag = tag };
                    SourceWindowComboBox?.Items.Add(item1);
                    QuickSourceWindowComboBox?.Items.Add(item2);

                    if (!string.IsNullOrEmpty(currentTarget) && (currentTarget == tag || currentTarget == title || title.Contains(currentTarget, StringComparison.OrdinalIgnoreCase)))
                    {
                        toSelectMain = item1;
                        toSelectQuick = item2;
                    }
                }
            }

            // 2. Fiziksel Monitörler (Tüm Masaüstü)
            var monitors = WindowCaptureService.GetDisplayMonitors();
            var h2 = new ComboBoxItem { Content = "── 🖥️ الشاشة الكاملة / الشاشات ──", IsEnabled = false, FontWeight = FontWeights.Bold };
            var h2q = new ComboBoxItem { Content = "── 🖥️ الشاشة الكاملة / الشاشات ──", IsEnabled = false, FontWeight = FontWeights.Bold };
            SourceWindowComboBox?.Items.Add(h2);
            QuickSourceWindowComboBox?.Items.Add(h2q);

            foreach (var mon in monitors)
            {
                var tag = $"monitor:{mon.Index}";
                var item1 = new ComboBoxItem { Content = $"🖥️ {mon.Name} (الشاشة الكاملة)", Tag = tag };
                var item2 = new ComboBoxItem { Content = $"🖥️ {mon.Name} (الشاشة الكاملة)", Tag = tag };
                SourceWindowComboBox?.Items.Add(item1);
                QuickSourceWindowComboBox?.Items.Add(item2);

                if (currentTarget == tag)
                {
                    toSelectMain = item1;
                    toSelectQuick = item2;
                }
            }

            // Eğer daha önce bir hedef seçilmemişse: Açık bir pencere varsa İLK PENCEREYİ seç, yoksa monitörü seç!
            if (toSelectMain == null)
            {
                if (SourceWindowComboBox != null)
                {
                    foreach (ComboBoxItem it in SourceWindowComboBox.Items)
                    {
                        if (it.IsEnabled && it.Tag != null)
                        {
                            toSelectMain = it;
                            break;
                        }
                    }
                }
                if (QuickSourceWindowComboBox != null)
                {
                    foreach (ComboBoxItem it in QuickSourceWindowComboBox.Items)
                    {
                        if (it.IsEnabled && it.Tag != null)
                        {
                            toSelectQuick = it;
                            break;
                        }
                    }
                }
            }

            if (SourceWindowComboBox != null && toSelectMain != null)
            {
                SourceWindowComboBox.SelectedItem = toSelectMain;
            }
            if (QuickSourceWindowComboBox != null && toSelectQuick != null)
            {
                QuickSourceWindowComboBox.SelectedItem = toSelectQuick;
            }

            // Eğer ayarda henüz bir hedef kayıtlı değilse, seçilen öğeyi kaydet
            if (string.IsNullOrEmpty(currentTarget) && toSelectMain?.Tag is string defTarget)
            {
                controller.Settings = controller.Settings with { DesktopWindowName = defTarget };
            }
        }
        catch { }
        finally
        {
            populatingSourceWindows = false;
        }
    }

    private void OnSourceWindowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (loading || populatingSourceWindows) return;
        if (SourceWindowComboBox.SelectedItem is ComboBoxItem item && item.Tag is string target && !string.IsNullOrEmpty(target))
        {
            controller.Settings = controller.Settings with { DesktopWindowName = target };
            if (QuickSourceWindowComboBox != null)
            {
                foreach (ComboBoxItem q in QuickSourceWindowComboBox.Items)
                {
                    if ((q.Tag as string) == target)
                    {
                        QuickSourceWindowComboBox.SelectedItem = q;
                        break;
                    }
                }
            }

            if (target.StartsWith("monitor:", StringComparison.OrdinalIgnoreCase) && int.TryParse(target["monitor:".Length..], out int mIndex))
            {
                var monitors = WindowCaptureService.GetDisplayMonitors();
                if (monitors.FirstOrDefault(m => m.Index == mIndex) is { } mon)
                {
                    DesktopSubtitleOverlay.SetTargetMonitor(mon.Left, mon.Top, mon.Width, mon.Height);
                }
            }
        }
    }

    private void OnQuickSourceWindowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (loading || populatingSourceWindows) return;
        if (QuickSourceWindowComboBox?.SelectedItem is ComboBoxItem item && item.Tag is string target && !string.IsNullOrEmpty(target))
        {
            controller.Settings = controller.Settings with { DesktopWindowName = target };
            if (SourceWindowComboBox != null)
            {
                foreach (ComboBoxItem m in SourceWindowComboBox.Items)
                {
                    if ((m.Tag as string) == target)
                    {
                        SourceWindowComboBox.SelectedItem = m;
                        break;
                    }
                }
            }

            if (target.StartsWith("monitor:", StringComparison.OrdinalIgnoreCase) && int.TryParse(target["monitor:".Length..], out int mIndex))
            {
                var monitors = WindowCaptureService.GetDisplayMonitors();
                if (monitors.FirstOrDefault(m => m.Index == mIndex) is { } mon)
                {
                    DesktopSubtitleOverlay.SetTargetMonitor(mon.Left, mon.Top, mon.Width, mon.Height);
                }
            }
        }
    }

    private void UpdatePatchUi()
    {
        try
        {
            var pm = CommunityPatchManager.Shared;
            if (CheckCommunityPatches != null && CheckCommunityPatches.IsChecked != pm.IsEnabled)
            {
                CheckCommunityPatches.IsChecked = pm.IsEnabled;
            }
            if (PatchStatusText != null)
            {
                PatchStatusText.Text = pm.GetStatusSummary();
            }
            FillGames();
        }
        catch { }
    }

    private void OnPatchToggleChanged(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        var enabled = CheckCommunityPatches?.IsChecked ?? true;
        CommunityPatchManager.Shared.IsEnabled = enabled;
        if (PatchStatusText != null)
        {
            PatchStatusText.Text = CommunityPatchManager.Shared.GetStatusSummary();
        }
    }

    private async void OnScanPatchesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PatchStatusText != null) PatchStatusText.Text = "⏳ جارٍ فحص الحزم والتحقق من التحديثات...";
            var res = await CommunityPatchManager.Shared.ScanAndReloadAsync();
            UpdatePatchUi();
            MessageBox.Show(res.Message, "تم فحص حزم الألعاب", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"خطأ في فحص الحزم: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnImportPatchClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "حزم الألعاب (*.json;*.txt;*.srt)|*.json;*.txt;*.srt|كل الملفات (*.*)|*.*",
                Title = "اختر ملف حزمة اللعبة"
            };
            if (dialog.ShowDialog() == true)
            {
                var res = CommunityPatchManager.Shared.ImportFile(dialog.FileName);
                UpdatePatchUi();
                MessageBox.Show(res.Message, res.Success ? "تم تحميل الحزمة بنجاح" : "خطأ في التحميل", MessageBoxButton.OK, res.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"خطأ في تحميل الملف: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

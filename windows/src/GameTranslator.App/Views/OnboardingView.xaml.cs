// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameTranslator.Core;
using GameTranslator.Core.LgTv;
using GameTranslator.RemotePlay;
using GameTranslator.Shell;
using GameTranslator.App.Services;

namespace GameTranslator.App.Views;

/// <summary>Kurulum sihirbazı: Mac sürümüyle birebir aynı 5 adımlı modern kurulum arayüzü.</summary>
public partial class OnboardingView : UserControl
{
    private readonly AppController controller;
    private int currentStep = 1;
    private string selectedTvPlatform = "LG";

    public OnboardingView(AppController controller)
    {
        this.controller = controller;
        DataContext = controller;
        if (controller.Tv != null)
        {
            selectedTvPlatform = controller.Tv.Brand switch
            {
                TvBrand.Lg => "LG",
                TvBrand.Samsung => "Samsung",
                TvBrand.AndroidTv when controller.Tv.Name.Contains("PC") => "PC",
                TvBrand.AndroidTv => "Android",
                _ => "LG"
            };
        }
        else
        {
            selectedTvPlatform = "PC";
        }
        InitializeComponent();
        ArabicLayout.Apply(this);
        UpdateTvPlatformSelection();
        UpdateStepView();
    }

    /// <summary>Ekran görüntüsü aracı (--render-screens) her adımı ayrı çizebilsin diye.</summary>
    internal void ShowStep(int step) => SetStep(step);

    private void SetStep(int step)
    {
        currentStep = Math.Clamp(step, 1, 4);
        UpdateStepView();
    }

    private void UpdateStepView()
    {
        StepBadgeText.Text = $"الخطوة {currentStep} / 4";

        // Panel Görünürlükleri
        Step1Panel.Visibility = currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4Panel.Visibility = currentStep == 4 ? Visibility.Visible : Visibility.Collapsed;

        // Butonlar
        BackButton.Visibility = currentStep == 1 ? Visibility.Hidden : Visibility.Visible;
        NextButton.Content = currentStep == 4 ? "Bitir" : "Devam";
        SkipButton.Visibility = currentStep == 4 ? Visibility.Hidden : Visibility.Visible;

        // Kapsül Renkleri (Progress Pills)
        UpdatePill(PillBar1, PillText1, 1);
        UpdatePill(PillBar2, PillText2, 2);
        UpdatePill(PillBar3, PillText3, 3);
        UpdatePill(PillBar4, PillText4, 4);

        if (currentStep == 2)
        {
            if (controller.HasAccount)
            {
                UpdatePsnIdDisplay();
            }

            if (PairStatusText != null)
            {
                var savedHost = ChiakiLauncher.GetSavedHost();
                if (!string.IsNullOrWhiteSpace(savedHost) && Ps5HostInput != null && string.IsNullOrWhiteSpace(Ps5HostInput.Text))
                {
                    Ps5HostInput.Text = savedHost;
                }

                var host = controller.Console?.Host ?? Typed(Ps5HostInput);
                if (!string.IsNullOrWhiteSpace(host) && ChiakiLauncher.IsRegistered(host))
                {
                    PairStatusText.Text = $"✓ الـPS5 مقترن مسبقاً ({host}). يمكنك المتابعة.";
                    PairStatusText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                }
                else if (string.IsNullOrWhiteSpace(Ps5HostInput?.Text))
                {
                    // Otomatik ağda tara
                    OnScanPs5(this, new RoutedEventArgs());
                }
            }
        }
        else if (currentStep == 3)
        {
            if (AndroidHostInput != null)
            {
                if (!string.IsNullOrEmpty(controller.ConnectedTvIp))
                {
                    AndroidHostInput.Text = controller.ConnectedTvIp;
                }
                else if (controller.Tv != null && controller.Tv.Brand == TvBrand.AndroidTv && !string.IsNullOrWhiteSpace(controller.Tv.Host) && controller.Tv.Host != "192.168.1.50")
                {
                    AndroidHostInput.Text = controller.Tv.Host;
                }
            }
        }
    }

    private void UpdatePill(Border bar, TextBlock text, int step)
    {
        if (step < currentStep)
        {
            bar.Background = new SolidColorBrush(Color.FromRgb(10, 132, 255));
            text.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
            text.FontWeight = FontWeights.Normal;
        }
        else if (step == currentStep)
        {
            bar.Background = new SolidColorBrush(Color.FromRgb(10, 132, 255));
            text.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
            text.FontWeight = FontWeights.Bold;
        }
        else
        {
            bar.Background = new SolidColorBrush(Color.FromRgb(35, 43, 62));
            text.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
            text.FontWeight = FontWeights.Normal;
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (currentStep > 1) SetStep(currentStep - 1);
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (currentStep == 3)
        {
            SaveCurrentTvPlatform();
        }

        if (currentStep < 4)
        {
            SetStep(currentStep + 1);
        }
        else
        {
            controller.Step = OnboardingStep.Done;
        }
    }

    private void OnSkipToMain(object sender, RoutedEventArgs e)
    {
        SaveCurrentTvPlatform();
        controller.Step = OnboardingStep.Done;
    }

    private void SaveCurrentTvPlatform()
    {
        try
        {
            switch (selectedTvPlatform)
            {
                case "LG":
                    var lgHost = (Typed(LgHostInput) is { Length: > 0 } typedLgHostInput ? typedLgHostInput : controller.Tv?.Host);
                    if (!string.IsNullOrWhiteSpace(lgHost))
                        _ = controller.PairAsync(new LgTv("تلفزيون LG webOS", lgHost, TvBrand.Lg));
                    break;
                case "Samsung":
                    var samHost = (Typed(SamsungHostInput) is { Length: > 0 } typedSamsungHostInput ? typedSamsungHostInput : controller.Tv?.Host);
                    if (!string.IsNullOrWhiteSpace(samHost))
                        _ = controller.PairAsync(new LgTv("تلفزيون سامسونج الذكي", samHost, TvBrand.Samsung));
                    break;
                case "Android":
                    var andHost = (Typed(AndroidHostInput) is { Length: > 0 } typedAndroidHostInput ? typedAndroidHostInput : controller.Tv?.Host);
                    if (!string.IsNullOrWhiteSpace(andHost))
                        _ = controller.PairAsync(new LgTv("Android TV", andHost, TvBrand.AndroidTv));
                    break;
                case "PC":
                default:
                    _ = controller.PairAsync(new LgTv("الكمبيوتر و OBS", "127.0.0.1", TvBrand.AndroidTv));
                    break;
            }
        }
        catch { }
    }

    private void OnPill1(object sender, RoutedEventArgs e) => SetStep(1);
    private void OnPill2(object sender, RoutedEventArgs e) => SetStep(2);
    private void OnPill3(object sender, RoutedEventArgs e) => SetStep(3);
    private void OnPill4(object sender, RoutedEventArgs e) => SetStep(4);

    // ==========================================
    // Adım 2: PSN Girişi
    // ==========================================
    private void OnToggleManualPsnCode(object sender, RoutedEventArgs e)
    {
        if (ManualPsnCard != null)
        {
            ManualPsnCard.Visibility = ManualPsnCard.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void UpdatePsnIdDisplay()
    {
        var b64 = controller.AccountIdBase64;
        if (!string.IsNullOrWhiteSpace(b64))
        {
            if (PsnStatusText != null)
            {
                PsnStatusText.Text = "✓ تم ربط حساب PlayStation Network بنجاح!";
                PsnStatusText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            }
            if (PsnSignInButton != null)
            {
                PsnSignInButton.Content = "✓ حساب PSN مربوط (تغيير)";
                PsnSignInButton.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            }
        }
    }

    private async void OnOpenSonyLogin(object sender, RoutedEventArgs e)
    {
        try
        {
            var redirect = PsnLoginWindow.Show(Window.GetWindow(this));
            if (redirect != null)
            {
                await controller.CompletePsnLoginAsync(redirect);
                UpdatePsnIdDisplay();
            }
        }
        catch (Exception ex)
        {
            // WebView2 yoksa varsayılan tarayıcıda aç
            try
            {
                var url = PsnAccount.AuthorizeUrl.AbsoluteUri;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
            PsnStatusText.Text = $"معلومة الدخول: {ex.Message}";
            PsnStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        }
    }

    private void OnOpenExternalSonyBrowser(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(PsnAccount.AuthorizeUrl.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"تعذّر فتح المتصفح: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? ExtractCode(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();

        // 1. Doğrudan Uri olarak parse etmeyi dene
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            var code = PsnAccount.CodeFromRedirect(uri);
            if (!string.IsNullOrEmpty(code)) return code;

            if (!string.IsNullOrEmpty(uri.Query))
            {
                var queryMatch = System.Text.RegularExpressions.Regex.Match(uri.Query, @"[?&]code=([^&]+)");
                if (queryMatch.Success) return Uri.UnescapeDataString(queryMatch.Groups[1].Value);
            }
        }

        // 2. Metin içinde code= ara
        var match = System.Text.RegularExpressions.Regex.Match(input, @"(?:^|[?&])code=([^&\s]+)");
        if (match.Success)
        {
            return Uri.UnescapeDataString(match.Groups[1].Value);
        }

        // 3. Doğrudan ham kod verilmişse (v3.xxx veya tek parça alfanümerik)
        if (!input.Contains(' ') && !input.Contains('/') && input.Length >= 8)
        {
            return input;
        }

        return null;
    }

    private async void OnVerifyPsnCode(object sender, RoutedEventArgs e)
    {
        var raw = PsnCodeInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            MessageBox.Show("الصق العنوان الظاهر في المتصفح أو الرمز.", "الرمز مطلوب", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var code = ExtractCode(raw);
        if (string.IsNullOrWhiteSpace(code))
        {
            PsnStatusText.Text = "لا يوجد رمز في هذا العنوان. يجب أن يحتوي العنوان على 'code='.";
            PsnStatusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            return;
        }

        PsnVerifyButton.IsEnabled = false;
        PsnStatusText.Text = "⏳ جارٍ التحقق من الرمز مع Sony...";
        PsnStatusText.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));

        try
        {
            await controller.CompletePsnLoginWithCodeAsync(code);
            UpdatePsnIdDisplay();
            PsnCodeInput.Text = "";
        }
        catch (Exception ex)
        {
            PsnStatusText.Text = $"❌ خطأ في التحقق: {ex.Message}";
            PsnStatusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
        finally
        {
            PsnVerifyButton.IsEnabled = true;
        }
    }



    // ==========================================
    // Adım 3: PS5 Eşleştirme
    // ==========================================
    private async void OnScanPs5(object sender, RoutedEventArgs e)
    {
        PairStatusText.Text = "جارٍ البحث عن الـPS5 في الشبكة المحلية...";
        PairStatusText.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        try
        {
            var found = await controller.DiscoverConsolesAsync();
            var first = found.FirstOrDefault();
            if (first != null)
            {
                Ps5HostInput.Text = first.Host;
                PairStatusText.Text = $"✓ تم العثور على الـPS5: {first.Host} ({first.Name})";
                PairStatusText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            }
            else
            {
                PairStatusText.Text = "لم يُعثر على الـPS5. هل هو مشغّل وعلى نفس الشبكة؟ يمكنك كتابة عنوانه يدوياً.";
                PairStatusText.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            }
        }
        catch (Exception ex)
        {
            PairStatusText.Text = $"معلومة البحث: {ex.Message}";
        }
    }

    /// <summary>
    /// Elle yazılan PS5/TV adresi temizlenmiş haliyle ("192,168,1,5", ":3000", "http://" düzelir); adres değilse boş
    /// (boş adres zaten "adresi yaz" uyarısı veriyor). Bozuk adres bağlantıyı kurmadan bitiriyordu (CouchSubs 4c1f6ec).
    /// </summary>
    private static string Typed(System.Windows.Controls.TextBox? box) => ManualAddress.Normalize(box?.Text) ?? "";

    private async void OnPairPs5(object sender, RoutedEventArgs e)
    {
        var host = Typed(Ps5HostInput);
        if (string.IsNullOrWhiteSpace(host))
        {
            PairStatusText.Text = "اكتب عنوان الـPS5.";
            PairStatusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            return;
        }

        var pin = Ps5PinInput.Text.Trim();
        var digits = new string([.. pin.Where(char.IsDigit)]);
        if (digits.Length != 8)
        {
            PairStatusText.Text = "أدخل الرمز المكوّن من 8 أرقام الظاهر على الـPS5 (الإعدادات › النظام › اللعب عن بُعد › ربط جهاز).";
            PairStatusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            return;
        }

        PairButton.IsEnabled = false;
        PairButton.Content = "⏳ جارٍ الإقران مع الـPS5...";
        PairStatusText.Text = $"جارٍ إنشاء اتصال مشفّر مع الـPS5 على العنوان {host} والتحقق من الرمز...";
        PairStatusText.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));

        try
        {
            var accountId = controller.AccountId;
            ChiakiLauncher.SyncConfig(host, accountId);
            await controller.RegisterAsync(host, digits);

            PairStatusText.Text = $"✓ تم إقران الـPS5 بنجاح! ({host}) جارٍ الانتقال لإعداد التلفزيون...";
            PairStatusText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            PairButton.Content = "✓ تم الإقران!";

            await Task.Delay(800);
            SetStep(3);
        }
        catch (Exception ex)
        {
            PairButton.IsEnabled = true;
            PairButton.Content = "🎮 إقران مع الـPS5 ➔";
            PairStatusText.Text = $"فشل الإقران: {ex.Message}";
            PairStatusText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
    }

    private void OnSkipPs5ForCaptureCard(object sender, RoutedEventArgs e)
    {
        try
        {
            // Yakalama kartı veya OBS modu seçildiğinde ayarları güncelle
            controller.Settings = controller.Settings with { VideoSource = VideoSourceType.CaptureCard };

            // Eğer konsol henüz kayıtlı değilse sahte bir kayıt oluşturarak sonraki adımların bloklanmasını engelle
            if (controller.Console == null)
            {
                var mockKey = new byte[16];
                var mockConsole = new RegisteredConsole("127.0.0.1", "كرت التقاط / OBS", mockKey, mockKey);
                controller.SetRegisteredConsole(mockConsole);
            }
        }
        catch { }

        PairStatusText.Text = "✓ تم تفعيل وضع كرت الالتقاط / OBS. جارٍ الانتقال لخطوة التلفزيون...";
        PairStatusText.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        SetStep(3);
    }

    // ==========================================
    // Adım 4: Akıllı TV Platform Seçimi & Eşleştirme
    // ==========================================
    private async void OnSelectTvPc(object sender, RoutedEventArgs e)
    {
        selectedTvPlatform = "PC";
        UpdateTvPlatformSelection();
        try
        {
            await controller.PairAsync(new LgTv("الكمبيوتر و OBS", "127.0.0.1", TvBrand.AndroidTv));
        }
        catch { }
        TvStatusFeedback.Text = "✓ تم حفظ وضع الكمبيوتر و OBS.";
        TvStatusFeedback.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
    }

    private async void OnSelectTvSamsung(object sender, RoutedEventArgs e)
    {
        selectedTvPlatform = "Samsung";
        UpdateTvPlatformSelection();
        try
        {
            var host = (Typed(SamsungHostInput) is { Length: > 0 } typedSamsungHostInput ? typedSamsungHostInput : controller.Tv?.Host);
            if (!string.IsNullOrWhiteSpace(host))
                await controller.PairAsync(new LgTv("تلفزيون سامسونج الذكي", host, TvBrand.Samsung));
            TvStatusFeedback.Text = "✓ تم اختيار تلفزيون سامسونج.";
            TvStatusFeedback.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }
        catch { }
    }

    private async void OnSelectTvLg(object sender, RoutedEventArgs e)
    {
        selectedTvPlatform = "LG";
        UpdateTvPlatformSelection();
        try
        {
            var host = (Typed(LgHostInput) is { Length: > 0 } typedLgHostInput ? typedLgHostInput : controller.Tv?.Host);
            if (!string.IsNullOrWhiteSpace(host))
                await controller.PairAsync(new LgTv("تلفزيون LG webOS", host, TvBrand.Lg));
            TvStatusFeedback.Text = "✓ تم اختيار تلفزيون LG webOS.";
            TvStatusFeedback.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }
        catch { }
    }

    private async void OnSelectTvAndroid(object sender, RoutedEventArgs e)
    {
        selectedTvPlatform = "Android";
        UpdateTvPlatformSelection();
        try
        {
            var host = (Typed(AndroidHostInput) is { Length: > 0 } typedAndroidHostInput ? typedAndroidHostInput : controller.Tv?.Host);
            if (!string.IsNullOrWhiteSpace(host))
                await controller.PairAsync(new LgTv("Android TV", host, TvBrand.AndroidTv));
            TvStatusFeedback.Text = "✓ تم اختيار Android TV.";
            TvStatusFeedback.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }
        catch { }
    }

    private void UpdateTvPlatformSelection()
    {
        var blueBrush = new SolidColorBrush(Color.FromRgb(10, 132, 255));
        var defBrush = new SolidColorBrush(Color.FromRgb(35, 43, 62));

        // Kenarlıklar
        TvBorderPc.BorderBrush = selectedTvPlatform == "PC" ? blueBrush : defBrush;
        TvBorderSamsung.BorderBrush = selectedTvPlatform == "Samsung" ? blueBrush : defBrush;
        TvBorderLg.BorderBrush = selectedTvPlatform == "LG" ? blueBrush : defBrush;
        TvBorderAndroid.BorderBrush = selectedTvPlatform == "Android" ? blueBrush : defBrush;

        // Rozetler
        TvBadgePc.Visibility = selectedTvPlatform == "PC" ? Visibility.Visible : Visibility.Collapsed;
        TvBadgeSamsung.Visibility = selectedTvPlatform == "Samsung" ? Visibility.Visible : Visibility.Collapsed;
        TvBadgeLg.Visibility = selectedTvPlatform == "LG" ? Visibility.Visible : Visibility.Collapsed;
        TvBadgeAndroid.Visibility = selectedTvPlatform == "Android" ? Visibility.Visible : Visibility.Collapsed;

        // Detay Panelleri
        TvDetailPc.Visibility = selectedTvPlatform == "PC" ? Visibility.Visible : Visibility.Collapsed;
        TvDetailSamsung.Visibility = selectedTvPlatform == "Samsung" ? Visibility.Visible : Visibility.Collapsed;
        TvDetailLg.Visibility = selectedTvPlatform == "LG" ? Visibility.Visible : Visibility.Collapsed;
        TvDetailAndroid.Visibility = selectedTvPlatform == "Android" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCopyObsUrl(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText("http://localhost:8787/obs");
        MessageBox.Show("تم نسخ عنوان OBS:\nhttp://localhost:8787/obs", "تم النسخ", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnSaveSamsung(object sender, RoutedEventArgs e)
    {
        var host = Typed(SamsungHostInput);
        if (string.IsNullOrWhiteSpace(host)) return;
        TvStatusFeedback.Text = $"جارٍ حفظ تلفزيون سامسونج ({host})...";
        TvStatusFeedback.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        try
        {
            await controller.PairAsync(new LgTv("تلفزيون سامسونج الذكي", host, TvBrand.Samsung));
        }
        catch { }
        // Saving the address does not put anything on the TV. The TV app is installed by the step below: with
        // developer mode on, it is signed, installed and opened; with it off, the card shows how to turn it on and
        // continues by itself. What happened is shown by the card, not assumed here.
        TvStatusFeedback.Text = $"تم حفظ عنوان التلفزيون ({host}). تابع البطاقة بالأسفل: منها يُثبَّت تطبيق التلفزيون.";
        TvStatusFeedback.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        try
        {
            await controller.RefreshTvAppAsync();
        }
        catch (Exception error)
        {
            TvStatusFeedback.Text = $"تعذّر تثبيت تطبيق التلفزيون: {error.Message}";
            TvStatusFeedback.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
        }
    }

    private async void OnSaveLg(object sender, RoutedEventArgs e)
    {
        var host = Typed(LgHostInput);
        if (string.IsNullOrWhiteSpace(host)) return;
        TvStatusFeedback.Text = $"جارٍ حفظ تلفزيون LG ({host})...";
        TvStatusFeedback.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        try
        {
            await controller.PairAsync(new LgTv("تلفزيون LG webOS", host, TvBrand.Lg));
        }
        catch { }
        TvStatusFeedback.Text = $"✓ تم حفظ تلفزيون LG ({host}). افتح GameTranslator على التلفزيون.";
        TvStatusFeedback.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
    }

    private async void OnScanAndroidTv(object sender, RoutedEventArgs e)
    {
        ScanAndroidTvButton.IsEnabled = false;
        AndroidInstallStatus.Visibility = Visibility.Visible;
        AndroidInstallStatus.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        AndroidInstallStatus.Text = "🔍 جارٍ البحث عن Android TV في الشبكة...";
        try
        {
            string? first = controller.ConnectedTvIp;

            if (string.IsNullOrEmpty(first))
            {
                var found = await controller.Services.ScanAndroidTvsAsync();
                first = found.FirstOrDefault();
            }

            if (string.IsNullOrEmpty(first) && !string.IsNullOrWhiteSpace(controller.Tv?.Host) && controller.Tv.Host != "192.168.1.50")
            {
                first = controller.Tv.Host;
            }

            if (first != null)
            {
                AndroidHostInput.Text = first;
                AndroidInstallStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                AndroidInstallStatus.Text = $"✓ تم العثور على Android TV: {first}. جارٍ الاتصال...";
                await controller.PairAsync(new LgTv("Android TV", first, TvBrand.AndroidTv));
                TvStatusFeedback.Text = $"✓ تم العثور على Android TV ({first}) وتم الاتصال!";
                TvStatusFeedback.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            }
            else
            {
                AndroidInstallStatus.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                AndroidInstallStatus.Text = "لم يُعثر على التلفزيون. اكتب عنوانه في الأعلى أو افتح التطبيق على التلفزيون.";
            }
        }
        catch (Exception ex)
        {
            AndroidInstallStatus.Text = $"معلومة البحث: {ex.Message}";
        }
        finally
        {
            ScanAndroidTvButton.IsEnabled = true;
        }
    }

    private async void OnInstallAndroidApp(object sender, RoutedEventArgs e)
    {
        var host = Typed(AndroidHostInput);
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show("اكتب عنوان التلفزيون.", "العنوان مطلوب", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AndroidInstallButton.IsEnabled = false;
        AndroidInstallStatus.Visibility = Visibility.Visible;
        AndroidInstallStatus.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        AndroidInstallStatus.Text = $"⏳ جارٍ الاتصال بـAndroid TV على العنوان {host} عبر ADB...";

        try
        {
            var version = await controller.InstallAndroidTvAppAsync(host, ev =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (ev.Kind == TvInstallEventKind.ApproveOnTv)
                    {
                        AndroidInstallStatus.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                        AndroidInstallStatus.Text = "⚠️ انظر إلى التلفزيون: عند ظهور سؤال السماح بتصحيح أخطاء الشبكة / USB اختر بالريموت «السماح دائماً» ثم «موافق»!";
                    }
                    else
                    {
                        AndroidInstallStatus.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                        AndroidInstallStatus.Text = "⏳ جارٍ إرسال التطبيق للتلفزيون وتثبيته...";
                    }
                });
            });

            AndroidInstallStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            AndroidInstallStatus.Text = $"✓ تم! ثُبّت GameTranslator v{version} على التلفزيون وفُتح.";
            TvStatusFeedback.Text = $"✓ تم الاتصال بـAndroid TV ({host}) والتطبيق جاهز.";
            TvStatusFeedback.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
        }
        catch (Exception ex)
        {
            AndroidInstallStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            AndroidInstallStatus.Text = $"❌ خطأ في التثبيت: {ex.Message}\n(يجب تفعيل خيارات المطوّر > تصحيح أخطاء الشبكة/USB (ADB) في التلفزيون وأن تكون شاشته مشغّلة)";
        }
        finally
        {
            AndroidInstallButton.IsEnabled = true;
        }
    }

    private async void OnSaveAndroid(object sender, RoutedEventArgs e)
    {
        var host = Typed(AndroidHostInput);
        if (string.IsNullOrWhiteSpace(host)) return;
        TvStatusFeedback.Text = $"جارٍ الإقران مع Android TV ({host})...";
        TvStatusFeedback.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        try
        {
            await controller.PairAsync(new LgTv("Android TV", host, TvBrand.AndroidTv));
        }
        catch { }
        TvStatusFeedback.Text = $"✓ تم حفظ Android TV ({host}) وإرسال إشارة الاتصال.";
        TvStatusFeedback.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
    }

    private void OnOpenSamsungWgt(object sender, RoutedEventArgs e)
    {
        // The newest Samsung package shipped with the app (the file name carries its version).
        var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tv-app");
        var path = Directory.Exists(folder)
            ? GameTranslator.TvInstall.Versions.NewestBundled(Directory.EnumerateFiles(folder, "gametranslator-samsung_*.wgt")) ?? ""
            : "";
        if (File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            Process.Start(new ProcessStartInfo("https://github.com/Sefflex/GameTranslator/releases") { UseShellExecute = true });
    }

    private void OnOpenLgIpk(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tv-app", "com.gametranslator.tv_1.0.0_all.ipk");
        if (File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            Process.Start(new ProcessStartInfo("https://github.com/Sefflex/GameTranslator/releases") { UseShellExecute = true });
    }

    private void OnOpenAndroidApk(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tv-app", "gametranslator-androidtv_1.0.0.apk");
        if (File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            Process.Start(new ProcessStartInfo("https://github.com/Sefflex/GameTranslator/releases") { UseShellExecute = true });
    }

    private void OnOpenGuidePdf(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://github.com/Sefflex/GameTranslator/raw/main/docs/GameTranslator_TV_Kurulum_Rehberi.pdf") { UseShellExecute = true });
    }

    // ==========================================
    // Adım 5: Test
    // ==========================================
    private void OnTestDesktopOverlay(object sender, RoutedEventArgs e)
    {
        DesktopSubtitleOverlay.Display("GameTranslator: الترجمة على شاشة الكمبيوتر تعمل بنجاح! [اختبار]", 6000);
    }

    private void OnTestTv(object sender, RoutedEventArgs e)
    {
        controller.SendTestSubtitle();
        DesktopSubtitleOverlay.Display("GameTranslator: نجح اختبار الترجمة العربية المباشرة!", 6000);
        MessageBox.Show("أُرسلت جملة تجريبية. إذا ظهرت على التلفزيون فكل شيء جاهز.", "تم الإرسال", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

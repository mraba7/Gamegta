// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameTranslator.Core;
using GameTranslator.Core.Dubbing;
using GameTranslator.Core.LgTv;
using GameTranslator.RemotePlay;
using GameTranslator.Translation;

namespace GameTranslator.Shell;

/// <summary>
/// Uygulamanın durumu ve akışı (Mac'teki AppModel'in karşılığı): kurulum sihirbazı, çeviriyi
/// başlatma/durdurma, PS5 ve TV durumları. Arayüz bu nesneye bağlanır; olaylar <c>post</c> ile
/// arayüz iş parçacığına aktarılır.
/// </summary>
public sealed class AppController : INotifyPropertyChanged, IDisposable
{
    public const string TvAppId = "com.gametranslator.tv";
    public const int DefaultServerPort = 8787;

    private readonly IAppServices services;
    private readonly IAppStore store;
    private readonly ISettingsStore settingsStore;
    private readonly ISleepGuard sleep;
    private readonly Localizer text;
    private readonly Action<Action> post;

    private OnboardingStep step;
    private byte[]? accountId;
    private RegisteredConsole? console;
    private LgTv? tv;
    private TvAppState tvApp = TvAppState.Unknown;
    /// <summary>Son eşleşmede TV uygulaması açılamadı.</summary>
    private bool tvAppMissing;
    /// <summary>TV uygulaması denetimi sürüyor (bekleme ekranı ve çeviri başlangıcı aynı anda çağırabilir).</summary>
    private bool refreshingTvApp;
    private int? devModeHoursLeft;
    private UserSettings settings;
    private bool isRunning;
    private bool isStarting;
    private SessionState sessionState = SessionState.Disconnected(DisconnectKind.Stopped);
    private int tvClients;
    private string lastSource = "";
    private string lastTranslation = "";
    private TranslatorStatus translatorStatus = TranslatorStatus.Ready;
    private bool ocrMissing;
    private Frame? lastFrame;
    private bool modelDownloading;
    private double modelProgress;
    private string? modelError;
    private Task? modelDownload;

    private SubtitleServer server = new(DefaultServerPort);
    private bool serverStarted;
    private IStreamSession? session;
    private IKeeper? keeper;
    private SubtitlePipeline? pipeline;
    /// <summary>Hattın yazı okuyucusu; ek bölgelerin tek seferlik okuması da bunu kullanır.</summary>
    private ITextReader? activeReader;
    private int extraBusy;
    private bool subtitlesPaused;
    /// <summary>Oyundaki altyazının son görüldüğü yer; deneme altyazısı TV'de oraya yerleşir.</summary>
    private TextBox? lastSubtitleBox;
    /// <summary>Hatta aynı anda tek kare girer; durdurma ve yeni hat kapıyı sıfırlar, takılan okuma hattı yeniden kurdurur (CouchSubs 7175247).</summary>
    private readonly FrameGate frameGate = new();
    private Dubber? dubber;
    private string? updateVersion;
    private bool updating;
    private double updateProgress;
    private string? updateError;
    private bool dubbingDownloading;
    private double dubbingProgress;
    private string? dubbingError;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppController(IAppServices services, IAppStore store, ISettingsStore settingsStore, ISleepGuard sleep, Localizer text, Action<Action> post)
    {
        this.services = services;
        this.store = store;
        this.settingsStore = settingsStore;
        this.sleep = sleep;
        this.text = text;
        this.post = post;
        accountId = store.Load<byte[]>(StoreKeys.AccountId);
        console = store.Load<RegisteredConsole>(StoreKeys.Console);
        tv = store.Load<LgTv>(StoreKeys.Tv);
        settings = settingsStore.Load();
        // Settings left by the original app (same file, Turkish target) are moved to this edition once and written back.
        if (settings.ForArabicEdition() is var moved && !ReferenceEquals(moved, settings))
        {
            settings = moved;
            settingsStore.Save(moved);
        }
        history = services.HistoryFile is { } historyFile ? SubtitleHistory.Load(historyFile) : new SubtitleHistory();
        var ready = accountId is not null && console is not null && tv is not null && store.Load<string>(StoreKeys.TvKey) is not null;
        step = OnboardingStep.Done;
        server.ClientsChanged += OnClientsChanged;
        _ = Task.Run(async () =>
        {
            try { await StartServerAsync(); } catch { }
            try { await CommunityPatchManager.Shared.UpdateFromRemoteRepoAsync(); } catch { }
        });
        StartPool();
    }

    // --- Geçmiş ve canlı ölçümler (CouchSubs 93ffdba) ---

    /// <summary>Çevrilen satırların geçmişi (arayüz iş parçacığında değişir); oturumlar arasında history.json'da kalır.</summary>
    private readonly SubtitleHistory history;
    /// <summary>Geçmiş dosyaya sırayla yazılır; yazmalar üst üste binmez.</summary>
    private Task historySave = Task.CompletedTask;
    private string metricsText = "";

    /// <summary>TV'ye giden son satırın ölçümleri tek satırda ("OCR 40 ms · Çeviri 300 ms · Gecikme 520 ms"); satır gelene kadar boş.</summary>
    public string MetricsText { get => metricsText; private set => Set(ref metricsText, value); }
    /// <summary>Çevrilen satırlar, en yenisi başta (bu ve önceki oturumlar). Değişince bu özellik bildirilir.</summary>
    public IReadOnlyList<SubtitleHistory.Entry> History => [.. history.Entries];

    /// <summary>Gösterilen satır geçmişe eklenir (arayüz iş parçacığında) ve dosyaya yazılır.</summary>
    private void AddToHistory(string source, string translation, LineOrigin origin)
    {
        if (!history.Add(source, translation, origin)) return;
        Changed(nameof(History));
        SaveHistory();
    }

    /// <summary>Geçmişi siler (dosyadaki de boşalır).</summary>
    public void ClearHistory()
    {
        history.Clear();
        Changed(nameof(History));
        SaveHistory();
    }

    /// <summary>Bekleyen geçmiş yazmalarının bitmesini bekler (testler ve kapanış için).</summary>
    public Task WaitForHistoryAsync() => historySave;

    private void SaveHistory()
    {
        if (services.HistoryFile is not { } file) return;
        var copy = history.Copy();
        historySave = historySave.ContinueWith(_ =>
        {
            try { copy.Save(file); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    // --- Ortak havuz (CouchSubs) ---

    private Timer? poolTimer;

    private void StartPool()
    {
        if (services.Pool is not { } pool) return;
        pool.SetEnabled(PoolAllowed(settings));
        // Beş dakikada bir gönderilir; sunucuya ulaşılamazsa kuyruk diskte bekler, sonra yeniden denenir.
        poolTimer = new Timer(_ => _ = pool.FlushAsync(), null, LinePool.FlushInterval, LinePool.FlushInterval);
    }

    /// <summary>Pakette olmayan İngilizce satır havuza önerilir; herhangi bir pakette olan (başka oyunun bile) gönderilmez.</summary>
    /// <summary>
    /// Game packs, the short-phrase table, DeepL, dubbing and the shared line pool exist for Turkish only. With any other
    /// target language (this fork's default is Arabic) none of them runs: no Turkish text reaches the user and no game
    /// line is sent to the pool that feeds the Turkish packs.
    /// </summary>
    public static bool UsesTurkishFeatures(UserSettings settings) => TargetLanguages.IsTurkish(settings.TargetLanguage);

    /// <summary>Is the current target language Turkish (see <see cref="UsesTurkishFeatures"/>).</summary>
    public bool UsesTurkish => UsesTurkishFeatures(settings);

    /// <summary>The line "Send a test subtitle" shows on the TV when the target is Arabic: the user sees the Arabic font and direction.</summary>
    public const string ArabicTestSubtitle = "هذه ترجمة تجريبية. هل تراها فوق اللعبة؟";

    private static bool PoolAllowed(UserSettings settings) => settings.SharePool && UsesTurkishFeatures(settings);

    private void OfferToPool(string text)
    {
        if (!PoolAllowed(settings)) return;
        if (settings.SourceLanguage != "en" || services.Pool is not { } pool) return;
        if (CommunityPatchManager.Shared.Match(text) is not null) return;
        pool.Offer(text);
    }

    // --- Durum ---

    public OnboardingStep Step
    {
        get => step;
        set
        {
            // Eşleştirme adımındaki güvenlik duvarı önerisi başka adıma taşınmaz.
            if (Set(ref step, value, [nameof(CanAdvance), nameof(StepNumber)])) ClearFirewallPrompt();
        }
    }
    public static int StepCount => (int)OnboardingStep.Done;
    public int StepNumber => Math.Min((int)step + 1, StepCount);
    public IAppServices Services => services;
    public bool HasAccount => accountId is not null;
    public byte[]? AccountId => accountId;
    public string? AccountIdBase64 => accountId != null && accountId.Length > 0 ? Convert.ToBase64String(accountId) : null;
    public RegisteredConsole? Console => console;
    public SubtitleServer Server => server;
    public CommunityPatchManager PatchManager => CommunityPatchManager.Shared;

    public void SetRegisteredConsole(RegisteredConsole registered)
    {
        console = registered;
        store.Save(StoreKeys.Console, registered);
        Changed(nameof(Console), nameof(CanAdvance), nameof(Ps5StatusText), nameof(Ps5Tone));
    }
    public LgTv? Tv => tv;
    public bool IsRunning { get => isRunning; private set => Set(ref isRunning, value, [nameof(Ps5StatusText), nameof(Ps5Tone), nameof(TvTone)]); }
    public bool IsStarting { get => isStarting; private set => Set(ref isStarting, value); }
    public SessionState SessionState { get => sessionState; private set => Set(ref sessionState, value, [nameof(Ps5StatusText), nameof(Ps5Tone)]); }
    public string Ps5StatusText
    {
        get
        {
            if (isRunning) return StatusText(sessionState, isRunning, text);
            if (console is not null) return $"Eşleşti ({console.Host})";
            return "Eşleşmedi";
        }
    }
    public StatusTone Ps5Tone => isRunning ? Ps5ToneFor(sessionState, isRunning) : (console is not null ? StatusTone.Good : StatusTone.Idle);
    public int TvClients { get => tvClients; private set => Set(ref tvClients, value, [nameof(TvTone), nameof(TvStatusText), nameof(ConnectedTvIp)]); }
    public string? ConnectedTvIp => server.LastClientAddress;
    public StatusTone TvTone => tvClients > 0 ? StatusTone.Good : (tv is not null ? StatusTone.Warning : StatusTone.Idle);
    public string TvStatusText
    {
        get
        {
            if (tvClients > 0) return tvClients == 1 ? $"{tv?.Name ?? "TV"} (Bağlandı)" : $"{tvClients} TV Bağlandı";
            if (tv is not null) return $"{tv.Name} (Bağlantı Bekleniyor)";
            return "TV uygulaması bekleniyor";
        }
    }
    public string LastSource { get => lastSource; private set => Set(ref lastSource, value); }
    public string LastTranslation { get => lastTranslation; private set => Set(ref lastTranslation, value); }
    public TranslatorStatus TranslatorStatus { get => translatorStatus; private set => Set(ref translatorStatus, value); }
    /// <summary>Bilgisayarda yazı tanıma dili yok; ayarlardan eklenmesi gerekir.</summary>
    public bool OcrMissing { get => ocrMissing; private set => Set(ref ocrMissing, value); }
    /// <summary>Canlı altyazı çevirisi duraklatıldı (Num 0); ek bölge ve serbest alan çevirileri çalışmaya devam eder.</summary>
    public bool SubtitlesPaused { get => subtitlesPaused; private set => Set(ref subtitlesPaused, value); }
    public Frame? LastFrame { get => lastFrame; private set => Set(ref lastFrame, value); }
    public string? ActiveSourceLanguage { get; private set; }
    /// <summary>Sunucunun gerçekten dinlediği port (8787 doluysa rastgele bir port).</summary>
    public int ServerPort { get; private set; } = DefaultServerPort;

    /// <summary>Bu oyun dilinden hedef dile çeviri modeli var mı (ayarlarda listelenecek diller).</summary>
    public bool SupportsLanguage(string source) => services.Translation.IsSupported(source, settings.TargetLanguage);

    public bool ModelInstalled => true;
    public bool ModelDownloading { get => modelDownloading; private set => Set(ref modelDownloading, value); }
    public double ModelProgress { get => modelProgress; private set => Set(ref modelProgress, value); }
    public string? ModelError { get => modelError; private set => Set(ref modelError, value); }

    public UserSettings Settings
    {
        get => settings;
        set
        {
            var old = settings;
            if (!Set(ref settings, value)) return;
            settingsStore.Save(value);
            if (PoolAllowed(old) != PoolAllowed(value)) services.Pool?.SetEnabled(PoolAllowed(value));
            server.SendStyle(value.Style);
            if (old.Dubbing != value.Dubbing) DubbingSettingsChanged(old.Dubbing);
            if (isRunning && (old.SourceLanguage != value.SourceLanguage || old.TargetLanguage != value.TargetLanguage
                              || old.OnlineTranslation != value.OnlineTranslation || old.Engine != value.Engine
                              || old.Provider != value.Provider || old.Quality != value.Quality || old.Dialect != value.Dialect
                              || old.FastWhenSlow != value.FastWhenSlow))
            {
                RefreshTranslatorStatus();
                MakePipeline();
            }
            if (isRunning && (old.VideoSource != value.VideoSource || old.DesktopWindowName != value.DesktopWindowName || old.ScreenIndex != value.ScreenIndex))
            {
                RestartStream();
            }
        }
    }

    // --- Sihirbaz ---

    public bool CanAdvance => true;

    public void Advance()
    {
        if (!CanAdvance || step == OnboardingStep.Done) return;
        // Model büyük; kullanıcı diğer adımlarla uğraşırken arka planda iner.
        if (step == OnboardingStep.Welcome) StartModelDownload();
        Step = step + 1;
    }

    public void Back()
    {
        if (step > OnboardingStep.Welcome) Step = step - 1;
    }

    public async Task CompletePsnLoginAsync(Uri redirect)
    {
        var id = await services.AccountIdFromRedirectAsync(redirect);
        accountId = id;
        store.Save(StoreKeys.AccountId, id);
        Changed(nameof(HasAccount), nameof(CanAdvance));
    }

    public async Task CompletePsnLoginWithCodeAsync(string code)
    {
        var id = await PsnAccount.AccountIdAsync(code);
        accountId = id;
        store.Save(StoreKeys.AccountId, id);
        Changed(nameof(HasAccount), nameof(CanAdvance));
    }

    public async Task<string> InstallAndroidTvAppAsync(string host, Action<TvInstallEvent> onEvent)
    {
        var app = services.BundledApp(TvBrand.AndroidTv)
                  ?? throw new FileNotFoundException("Android TV APK paketi (tv-app\\gametranslator-androidtv_1.0.0.apk) bulunamadı.");
        var version = await services.InstallAndroidAppAsync(host, app, onEvent);
        var target = new LgTv("Android TV", host, TvBrand.AndroidTv);
        tv = target;
        store.Save(StoreKeys.Tv, target);
        store.Save(StoreKeys.TvAppVersion, version);
        Changed(nameof(Tv));
        TvApp = new TvAppState(TvAppKind.UpToDate, version);

        try
        {
            await StartServerAsync();
            var serverAddr = ServerAddress(target);
            await services.PairTvAsync(target, null, serverAddr);
        }
        catch { }

        return version;
    }

    public Task<IReadOnlyList<ConsoleInfo>> DiscoverConsolesAsync() => services.DiscoverConsolesAsync();

    public async Task RegisterAsync(string host, string pin)
    {
        var digits = new string([.. pin.Where(char.IsDigit)]);
        if (digits.Length != 8 || !uint.TryParse(digits, out var value)) throw Error(AppError.InvalidPin);
        if (accountId is null)
        {
            // Eğer PSN girişi yapılmadıysa varsayılan yerel eşleşme kimliği ata
            accountId = System.Text.Encoding.ASCII.GetBytes("GTPS5ACC");
            store.Save(StoreKeys.AccountId, accountId);
            Changed(nameof(HasAccount));
        }
        RegisteredConsole registered;
        try
        {
            registered = await services.RegisterAsync(host, accountId, value);
        }
        catch (RegistrationException error)
        {
            // PS5 kendi kodunu bildirdiyse yanıt bize ulaşmıştır, güvenlik duvarı sebep değildir.
            if (error.Reason is null && error.Failure != RegistrationFailure.WrongAccount) await OfferFirewallAsync();
            throw RegistrationError(error);
        }
        catch (Exception error) when (error is not AppException)
        {
            await OfferFirewallAsync();
            throw;
        }
        ClearFirewallPrompt();
        console = registered;
        store.Save(StoreKeys.Console, registered);
        Changed(nameof(Console), nameof(CanAdvance), nameof(Ps5StatusText), nameof(Ps5Tone));
    }

    /// <summary>Kullanıcıya gösterilen hata; destek bildirimleri teşhis edilebilsin diye PS5'in kodu da yazılır.</summary>
    private AppException RegistrationError(RegistrationException error)
    {
        var kind = error.Failure switch
        {
            RegistrationFailure.WrongAccount => AppError.WrongAccount,
            RegistrationFailure.NoAnswer => AppError.ConsoleNoAnswer,
            RegistrationFailure.InUse => AppError.ConsoleInUse,
            RegistrationFailure.ConsoleCrashed => AppError.ConsoleCrashed,
            RegistrationFailure.VersionMismatch => AppError.ConsoleVersion,
            _ => AppError.RegistrationFailed,
        };
        var message = AppException.Describe(kind, text);
        return new AppException(kind, string.IsNullOrEmpty(error.Detail) ? message : $"{message} ({error.Detail})");
    }

    public Task<IReadOnlyList<LgTv>> DiscoverTvsAsync() => services.DiscoverTvsAsync();

    public async Task PairAsync(LgTv target)
    {
        try { await StartServerAsync(); } catch { }
        TvPairing pairing;
        try
        {
            pairing = await services.PairTvAsync(target, store.Load<string>(StoreKeys.TvKey), ServerAddress(target));
        }
        catch
        {
            pairing = new TvPairing("", false);
        }
        if (tv?.Brand != target.Brand || (target.Brand != TvBrand.AndroidTv && tv?.Host != target.Host))
            store.Remove(StoreKeys.TvAppVersion);
        tv = target;
        store.Save(StoreKeys.Tv, target);
        if (!string.IsNullOrEmpty(pairing.Key))
            store.Save(StoreKeys.TvKey, pairing.Key);
        tvAppMissing = !pairing.Launched;
        Changed(nameof(Tv), nameof(CanAdvance), nameof(CanInstallTvApp), nameof(TvStatusText), nameof(TvTone));
        try
        {
            if (target.Brand == TvBrand.Lg) await RefreshTvAppAsync();
            else _ = RefreshTvAppAsync();
        }
        catch { }
    }

    // --- TV uygulamasını kurma ---

    public TvAppState TvApp
    {
        get => tvApp;
        private set
        {
            tvApp = value;
            Changed(nameof(TvApp));
        }
    }

    /// <summary>LG geliştirici modunun kalan süresi (saat); GameTranslator uzattıktan sonra bilinir.</summary>
    public int? DevModeHoursLeft
    {
        get => devModeHoursLeft;
        private set
        {
            devModeHoursLeft = value;
            Changed(nameof(DevModeHoursLeft));
        }
    }

    /// <summary>Seçili TV'ye uygulamayı GameTranslator kurabilir mi (paket uygulamayla geldi mi)?</summary>
    public bool CanInstallTvApp => tv is { } target &&
        (target.Brand == TvBrand.Lg ? services.BundledTvAppVersion is not null : services.BundledApp(target.Brand) is not null);

    /// <summary>Samsung'un sertifika sayfasını tarayıcıda açar (arayüz atar; testlerde boş kalır).</summary>
    public Action<Uri> OpenInBrowser { get; set; } = _ => { };

    /// <summary>Samsung'da geliştirici modunda "Host PC IP" olarak yazılacak bu bilgisayarın adresi.</summary>
    public string? ComputerAddress => LocalAddress(tv is { Host.Length: > 0 } target ? target.Host : null);

    /// <summary>
    /// Geliştirici modu açık TV'de uygulamayı eksikse kurar, eskiyse günceller.
    /// Anahtar yoksa ve uygulama çalışıyorsa kullanıcıdan bir şey istenmez.
    /// </summary>
    public async Task RefreshTvAppAsync()
    {
        if (tv is not { } target || refreshingTvApp) return;
        refreshingTvApp = true;
        try
        {
            switch (target.Brand)
            {
                case TvBrand.Lg: await RefreshLgAppAsync(target); break;
                case TvBrand.Samsung: await RefreshSamsungAppAsync(target); break;
                case TvBrand.AndroidTv: await RefreshAndroidAppAsync(target); break;
            }
        }
        finally
        {
            refreshingTvApp = false;
        }
    }

    private string? InstalledAppVersion => store.Load<string>(StoreKeys.TvAppVersion);

    private bool NeedsInstall(BundledTvApp bundled) =>
        InstalledAppVersion is not { } installed || DevModeInstaller.IsNewer(bundled.Version, installed);

    private async Task RefreshLgAppAsync(LgTv target)
    {
        if (services.BundledTvAppVersion is not { } bundled) return;
        if (!await services.IsDevModeOnAsync(target.Host))
        {
            TvApp = tvAppMissing ? new TvAppState(TvAppKind.DevModeOff) : TvAppState.Unknown;
            return;
        }
        if (store.Load<string>(StoreKeys.TvDevKey) is not { } devKey)
        {
            TvApp = tvAppMissing ? new TvAppState(TvAppKind.NeedsPassphrase) : TvAppState.Unknown;
            return;
        }
        await InstallTvAppAsync(target, devKey, bundled);
    }

    /// <summary>Kurulum hatasında TV'nin verdiği sebep de gösterilir; genel mesaj tek başına sorunu bulmaya yetmiyordu.</summary>
    private static TvAppState FailedState(DevModeException error) =>
        new(TvAppKind.Failed, Error: error.Error,
            Reason: error.Error is DevModeError.InstallFailed or DevModeError.NotEnoughSpace && !string.IsNullOrWhiteSpace(error.Detail)
                ? error.Detail.Trim() : null);

    /// <summary>Samsung: geliştirici modu açıksa uygulamayı imzalayıp kurar. Sertifika yoksa Samsung girişi istenir.</summary>
    private async Task RefreshSamsungAppAsync(LgTv target)
    {
        if (services.BundledApp(TvBrand.Samsung) is not { } bundled) return;
        if (!NeedsInstall(bundled))
        {
            TvApp = new TvAppState(TvAppKind.UpToDate, InstalledAppVersion ?? bundled.Version);
            return;
        }
        if (!await services.IsSamsungDevModeOnAsync(target.Host))
        {
            TvApp = new TvAppState(TvAppKind.DevModeOff);
            return;
        }
        TvApp = new TvAppState(TvAppKind.Working);
        try
        {
            await services.InstallSamsungAppAsync(target.Host, bundled, e => post(() => InstallEvent(e)));
            store.Save(StoreKeys.TvAppVersion, bundled.Version);
            TvApp = new TvAppState(TvAppKind.UpToDate, bundled.Version);
            await OpenTvAppAfterInstallAsync(target);
        }
        catch (DevModeException error)
        {
            TvApp = FailedState(error);
        }
    }

    /// <summary>Android TV: ağda ADB'si açık TV'yi bulur, uygulamayı kurar ve açar.</summary>
    private async Task RefreshAndroidAppAsync(LgTv target)
    {
        if (services.BundledApp(TvBrand.AndroidTv) is not { } bundled) return;
        var host = target.Host;
        if (host.Length == 0 || !await services.IsAndroidDebugOnAsync(host))
        {
            // Uygulama kuruluysa ve TV'de hata ayıklama kapalıysa yapılacak bir şey yok.
            if (!NeedsInstall(bundled))
            {
                TvApp = new TvAppState(TvAppKind.UpToDate, InstalledAppVersion ?? bundled.Version);
                return;
            }
            if ((await services.ScanAndroidTvsAsync()).FirstOrDefault() is not { } found)
            {
                TvApp = new TvAppState(TvAppKind.DevModeOff);
                return;
            }
            host = found;
        }
        TvApp = new TvAppState(TvAppKind.Working);
        try
        {
            var version = await services.InstallAndroidAppAsync(host, bundled, e => post(() => InstallEvent(e)));
            // TV'nin adresi bundan sonra sürüm denetimi için kullanılır.
            var located = target with { Host = host };
            tv = located;
            store.Save(StoreKeys.Tv, located);
            store.Save(StoreKeys.TvAppVersion, version);
            Changed(nameof(Tv));
            TvApp = new TvAppState(TvAppKind.UpToDate, version);
        }
        catch (DevModeException error)
        {
            TvApp = FailedState(error);
        }
    }

    private void InstallEvent(TvInstallEvent e)
    {
        // Kurulum bittikten sonra gelen geç olay sonucu ezmesin.
        if (TvApp.Kind is not (TvAppKind.Working or TvAppKind.SignIn or TvAppKind.ApproveOnTv)) return;
        switch (e.Kind)
        {
            case TvInstallEventKind.SignIn when e.Url is { } url:
                TvApp = new TvAppState(TvAppKind.SignIn, Url: url);
                OpenInBrowser(url);
                break;
            case TvInstallEventKind.ApproveOnTv:
                TvApp = new TvAppState(TvAppKind.ApproveOnTv);
                break;
            case TvInstallEventKind.Progress when TvApp.Kind == TvAppKind.SignIn:
                TvApp = new TvAppState(TvAppKind.Working);
                break;
        }
    }

    /// <summary>Key Server'dan anahtarı alır (TV ekranındaki parolayla) ve uygulamayı kurar.</summary>
    public async Task InstallTvAppAsync(string passphrase)
    {
        if (tv is not { } target || services.BundledTvAppVersion is not { } bundled) return;
        TvApp = new TvAppState(TvAppKind.Working);
        try
        {
            var devKey = await services.FetchDevKeyAsync(target.Host, passphrase.Trim());
            store.Save(StoreKeys.TvDevKey, devKey);
            await InstallTvAppAsync(target, devKey, bundled);
        }
        catch (DevModeException error)
        {
            TvApp = new TvAppState(TvAppKind.NeedsPassphrase, Error: error.Error);
        }
    }

    private async Task InstallTvAppAsync(LgTv target, string devKey, string bundled)
    {
        TvApp = new TvAppState(TvAppKind.Working);
        try
        {
            var installed = await services.InstalledTvAppVersionAsync(target.Host, devKey);
            if (installed is not null && !DevModeInstaller.IsNewer(bundled, installed))
            {
                TvApp = new TvAppState(TvAppKind.UpToDate, installed);
                await RenewDevSessionIfDueAsync(target, devKey);
                return;
            }
            await services.InstallTvAppAsync(target.Host, devKey);
            TvApp = new TvAppState(TvAppKind.UpToDate, bundled);
            tvAppMissing = false;
            await RenewDevSessionIfDueAsync(target, devKey);
            await OpenTvAppAfterInstallAsync(target);
        }
        catch (DevModeException error) when (error.Error == DevModeError.KeyRefused)
        {
            // Geliştirici modu yeniden açılınca TV yeni anahtar üretir; eskisi işe yaramaz.
            store.Remove(StoreKeys.TvDevKey);
            TvApp = new TvAppState(TvAppKind.NeedsPassphrase);
        }
        catch (DevModeException error)
        {
            TvApp = FailedState(error);
        }
    }

    /// <summary>
    /// LG geliştirici modunun 1000 saatlik süresini günde en çok bir kez baştan başlatır; kullanıcı TV'den uzatmak
    /// zorunda kalmaz.
    /// </summary>
    private async Task RenewDevSessionIfDueAsync(LgTv target, string devKey)
    {
        var last = store.Load<DateTimeOffset?>(StoreKeys.DevSessionRenewed) ?? DateTimeOffset.MinValue;
        if (DateTimeOffset.UtcNow - last < TimeSpan.FromHours(20)) return;
        if (await services.RenewDevSessionAsync(target.Host, devKey) is { } hours)
        {
            DevModeHoursLeft = hours;
            store.Save(StoreKeys.DevSessionRenewed, (DateTimeOffset?)DateTimeOffset.UtcNow);
        }
    }

    private async Task OpenTvAppAfterInstallAsync(LgTv target)
    {
        if (keeper is not null)
        {
            keeper.Reconnect();
            return;
        }
        try { await services.PairTvAsync(target, store.Load<string>(StoreKeys.TvKey), ServerAddress(target)); }
        catch (Exception) { /* TV uygulaması kuruldu; açılmadıysa kullanıcı açabilir */ }
    }

    /// <summary>TV ağda yeni bir adreste bulundu.</summary>
    public void TvMoved(LgTv moved)
    {
        tv = moved;
        store.Save(StoreKeys.Tv, moved);
        Changed(nameof(Tv));
    }

    public void StartModelDownload()
    {
        if (ModelInstalled || modelDownload is { IsCompleted: false }) return;
        ModelDownloading = true;
        ModelError = null;
        var download = services.Translation.DownloadAsync(settings.SourceLanguage, settings.TargetLanguage, new ImmediateProgress(value => post(() => ModelProgress = value)), CancellationToken.None);
        modelDownload = FinishDownload(download);
    }

    private async Task FinishDownload(Task download)
    {
        try
        {
            await download;
            post(() => ModelProgress = 1);
        }
        catch (Exception error)
        {
            post(() => ModelError = error.Message);
        }
        finally
        {
            post(() =>
            {
                ModelDownloading = false;
                Changed(nameof(ModelInstalled), nameof(CanAdvance));
                RefreshTranslatorStatus();
                // Çeviri sürerken model indiyse hat yeni çevirmenle kurulur.
                if (isRunning && TranslatorStatus == TranslatorStatus.Ready) MakePipeline();
            });
        }
    }

    public Task WaitForModelAsync() => modelDownload ?? Task.CompletedTask;

    public void SyncStyle()
    {
        server.SendStyle(settings.Style);
    }

    public void SendTestSubtitle()
    {
        server.SendStyle(settings.Style);
        // Oyunda henüz altyazı görülmediyse altyazı bölgesinin altına yakın tipik bir yer kullanılır.
        var region = settings.Region;
        var box = lastSubtitleBox ?? new TextBox(0.3, region.Bottom - 0.1, 0.7, region.Bottom - 0.03);
        var msg = TargetLanguages.Normalize(settings.TargetLanguage) == TargetLanguages.Arabic
            ? ArabicTestSubtitle
            : text["This is a test subtitle. Can you see it on top of the game?"];
        server.SendSubtitle(msg, 8000, box);
        post(() => LiveSubtitleShown?.Invoke(msg, 8000, box));
    }

    /// <summary>
    /// Num 0: canlı altyazı çevirisini duraklatır ya da sürdürür. Alan çevirirken sürekli çeviri de TV'de görünüp
    /// karışmasın diye kullanılır. TV'de kısa bir bildirim çıkar. Çeviri sürmüyorsa bir şey yapmaz.
    /// </summary>
    public void ToggleSubtitles()
    {
        if (!isRunning) return;
        var pause = !subtitlesPaused;
        SubtitlesPaused = pause;
        if (pause)
        {
            pipeline?.Reset();
            server.Clear();
            post(() => SubtitlesCleared?.Invoke());
        }
        server.SendStyle(settings.Style);
        server.SendSubtitle(text[pause ? "Subtitle translation paused" : "Subtitle translation resumed"], 1800);
    }

    /// <summary>Num 8: ayar ekranı, oyunun şu anki görüntüsünü yeniden alsın.</summary>
    public event Action? SnapshotRequested;

    public void RequestSnapshot() => SnapshotRequested?.Invoke();

    /// <summary>Canlı çeviri metni bilgisayar ekranında da (overlay) gösterilsin diye tetiklenir (metin, süre ms, kutu koordinatı).</summary>
    public event Action<string, int, TextBox?>? LiveSubtitleShown;

    /// <summary>Son altyazının algılandığı normalize ekran koordinatı kutusu.</summary>
    public TextBox? LastSubtitleBox => lastSubtitleBox;

    /// <summary>Altyazılar temizlendiğinde masaüstü overlay'i kapatılsın diye tetiklenir.</summary>
    public event Action? SubtitlesCleared;

    /// <summary>Çeviri bilgisayarda da gösterilecekse (metin, kalma süresi ms) ile çağrılır.</summary>
    public event Action<string, int>? PcResultReady;

    /// <summary>
    /// 1, 2 ya da 3 numaralı ek bölgeyi şimdi bir kez okuyup çevirir; sonuç ayardaki yere (TV, bilgisayar ya da ikisi) gider
    /// (görev paneli gibi yazılar için). Çeviri sürmüyorsa, bölge kapalıysa ya da önceki istek bitmediyse hiçbir şey yapılmaz.
    /// </summary>
    public async Task<ExtraRegionOutcome> TranslateExtraRegionAsync(int slot)
    {
        if (settings.ExtraRegion(slot) is not { } region) return ExtraRegionOutcome.NotConfigured;
        if (lastFrame is not { } frame) return ExtraRegionOutcome.NotRunning;
        var target = settings.ExtraTarget(slot);
        return (await RunRegionAsync(frame, region, target != ResultTarget.Pc, target != ResultTarget.Tv)).Outcome;
    }

    /// <summary>
    /// Serbest alan (Num 5): verilen karedeki alanı bir kez okuyup çevirir. Sonuç, ayardaki hedefe göre TV'ye de gönderilir;
    /// bilgisayarda gösterilmek üzere her durumda döner (serbest alan penceresi kendisi gösterir).
    /// </summary>
    public Task<(ExtraRegionOutcome Outcome, RegionTranslation.Result? Result)> TranslateAreaAsync(Frame frame, CropRegion region) =>
        RunRegionAsync(frame, region, settings.FreeAreaTarget != ResultTarget.Pc, false);

    private async Task<(ExtraRegionOutcome Outcome, RegionTranslation.Result? Result)> RunRegionAsync(Frame frame, CropRegion region, bool toTv, bool toPcPopup)
    {
        var reader = activeReader;
        if (!isRunning || reader is null) return (ExtraRegionOutcome.NotRunning, null);
        // Tuşa üst üste basılınca ikinci bir okuma başlamaz.
        if (Interlocked.Exchange(ref extraBusy, 1) == 1) return (ExtraRegionOutcome.Busy, null);
        try
        {
            RegionTranslation.Result? result;
            try
            {
                result = await RegionTranslation.TranslateOnceAsync(frame, region, reader, MakeTranslator());
            }
            catch (TranslatorException error)
            {
                post(() => TranslatorStatus = error.Status);
                return (ExtraRegionOutcome.TranslatorUnavailable, null);
            }
            catch
            {
                return (ExtraRegionOutcome.Failed, null);
            }
            if (result is null)
            {
                var message = text["No text found in this area."];
                if (toTv)
                {
                    server.SendStyle(settings.Style);
                    server.SendSubtitle(message, 2500, new TextBox(region.Left, region.Top, region.Right, region.Bottom));
                }
                if (toPcPopup) post(() => PcResultReady?.Invoke(message, 2500));
                return (ExtraRegionOutcome.NoText, null);
            }
            var milliseconds = RegionTranslation.DisplayMilliseconds(result.Translation);
            if (toTv)
            {
                server.SendStyle(settings.Style);
                server.SendSubtitle(result.Translation, milliseconds, result.Box, result.Structured);
            }
            post(() =>
            {
                LastSource = result.Source;
                LastTranslation = result.Translation;
                if (toPcPopup) PcResultReady?.Invoke(result.Translation, milliseconds);
            });
            return (ExtraRegionOutcome.Shown, result);
        }
        finally
        {
            Volatile.Write(ref extraBusy, 0);
        }
    }

    /// <summary>Süren "ekranı çevir" işi; yeniden basılınca iptal edilir.</summary>
    private CancellationTokenSource? screenWork;
    /// <summary>TV'deki ekran çevirilerinin kalkacağı an; bu andan önce yeniden basmak onları kaldırır.</summary>
    private DateTime screenShownUntil;

    /// <summary>
    /// Num 9: oyunun şu anki karesinin tamamını bir kez okur, her yazı grubunu ayrı çevirip TV'de kendi yerine koyar.
    /// Çeviriler hazırlandıkça TV'ye gider. Çeviri sürerken ya da çeviriler TV'deyken yeniden basmak onları kaldırır.
    /// </summary>
    public async Task<ScreenOutcome> TranslateScreenAsync()
    {
        var reader = activeReader;
        if (!isRunning || reader is null || lastFrame is not { } frame) return ScreenOutcome.NotRunning;
        if (screenWork is { } running)
        {
            running.Cancel();
            screenWork = null;
            HideScreen();
            return ScreenOutcome.Cleared;
        }
        if (DateTime.UtcNow < screenShownUntil)
        {
            HideScreen();
            return ScreenOutcome.Cleared;
        }

        using var work = new CancellationTokenSource();
        screenWork = work;
        server.SendStyle(settings.Style);
        server.SendSubtitle(text["Translating the screen…"], 2500);
        try
        {
            var translator = MakeTranslator();
            var items = await ScreenTranslation.TranslateAsync(frame, reader, translator, shown =>
            {
                if (work.IsCancellationRequested) return;
                server.SendScreen(shown, ScreenTranslation.DisplayMilliseconds(shown));
                screenShownUntil = DateTime.UtcNow.AddMilliseconds(ScreenTranslation.DisplayMilliseconds(shown));
            }, work.Token);
            server.Clear();
            if (items.Count == 0)
            {
                server.SendSubtitle(text["No text found on the screen."], 2500);
                return ScreenOutcome.NoText;
            }
            return ScreenOutcome.Shown;
        }
        catch (OperationCanceledException)
        {
            return ScreenOutcome.Cleared;
        }
        catch (TranslatorException error)
        {
            post(() => TranslatorStatus = error.Status);
            ShowScreenFailure(error);
            return ScreenOutcome.TranslatorUnavailable;
        }
        catch (Exception error)
        {
            SubtitleLog.Write($"ekran çevrilemedi: {error.GetType().Name}: {error.Message}");
            ShowScreenFailure(error);
            return ScreenOutcome.Failed;
        }
        finally
        {
            if (screenWork == work) screenWork = null;
        }
    }

    /// <summary>Ekran çevrilemedi: nedeni TV'de birkaç saniye görünür, sessizce kaybolmaz (CouchSubs 26edd77).</summary>
    private void ShowScreenFailure(Exception error)
    {
        server.Clear();
        var reason = (error as TranslatorException)?.Status switch
        {
            TranslatorStatus.NeedsDownload => "ücretsiz çeviri modeli henüz inmedi.",
            TranslatorStatus.EngineFailed => "ücretsiz çeviri başlatılamadı.",
            _ => "bir sorun oldu, yeniden dene.",
        };
        server.SendSubtitle($"Ekran çevrilemedi: {reason}", 6000);
    }

    private void HideScreen()
    {
        screenShownUntil = default;
        server.ClearScreen();
        server.Clear();
    }

    /// <summary>Kurulumu baştan başlatır (kayıtlı her şeyi siler).</summary>
    public void ResetSetup()
    {
        Stop();
        foreach (var key in new[] { StoreKeys.AccountId, StoreKeys.Console, StoreKeys.Tv, StoreKeys.TvKey, StoreKeys.TvDevKey, StoreKeys.TvAppVersion }) store.Remove(key);
        accountId = null;
        console = null;
        tv = null;
        tvAppMissing = false;
        TvApp = TvAppState.Unknown;
        Step = OnboardingStep.Welcome;
        Changed(nameof(HasAccount), nameof(Console), nameof(Tv));
    }

    /// <summary>Tüm kullanıcı ayarlarını varsayılana sıfırlar.</summary>
    public void ResetSettings()
    {
        Settings = new UserSettings();
    }

    // --- Çeviri ---

    public async Task StartAsync()
    {
        // Düğmeye iki kez basılması ya da beklerken tekrar çağrılması ikinci bir oturum açmaz.
        if (isRunning || isStarting) return;
        if (settings.VideoSource == VideoSourceType.RemotePlay && (console is null || accountId is null)) return;
        IsStarting = true;
        try
        {
            RefreshTranslatorStatus();
            try { await StartServerAsync(); } catch { /* port açılamadıysa TV durumu "bekleniyor" kalır */ }
            server.SendStyle(settings.Style);
            SubtitlesPaused = false;
            MakePipeline();
            if (settings.Dubbing.Enabled && DubbingInstalled) MakeDubber()?.Prepare();

            var stream = services.MakeStream(console, accountId, settings);
            stream.StateChanged += state => post(() => SessionStateChanged(state));
            stream.ConsoleMoved += moved => post(() => ConsoleMoved(moved));
            stream.FrameReceived += HandleFrame;
            stream.Start();
            session = stream;

            // TV uygulamasını başlat ve açık tut; TV kapalıysa açıldığında bağlanır.
            if (tv != null)
            {
                var running = server;
                var tvKeeper = services.MakeKeeper(tv, store.Load<string>(StoreKeys.TvKey), new Dictionary<string, string> { ["server"] = ServerAddress(tv) },
                    () => running.ConnectedClients > 0, moved => post(() => TvMoved(moved)), key => post(() => store.Save(StoreKeys.TvKey, key)));
                tvKeeper.Start(TimeSpan.FromSeconds(5));
                keeper = tvKeeper;
                _ = RefreshTvAppAsync();
            }

            sleep.Begin();
            IsRunning = true;
            _ = WatchRunningGameAsync();
            UpdateTroubleClocks();
        }
        finally
        {
            IsStarting = false;
        }
    }

    /// <summary>PS5'e çalışan oyunu sorma aralığı (soru PS5'i uyandırmaz).</summary>
    public static readonly TimeSpan RunningGameEvery = TimeSpan.FromSeconds(45);
    private int watchingGame;

    /// <summary>
    /// Çeviri açıkken PS5'e hangi oyunun çalıştığı sorulur: paketi varsa elle seçim yokken o paket ilk satırdan kullanılır,
    /// adı yapay zekâya bağlam olarak gider (CouchSubs d44bfe2'deki gibi, yerel kütüphane yerine doğrudan keşif sorusuyla).
    /// </summary>
    private async Task WatchRunningGameAsync()
    {
        if (Interlocked.Exchange(ref watchingGame, 1) == 1) return;
        try
        {
            while (isRunning)
            {
                if (console is { Host.Length: > 0 } target &&
                    await Ps5Status.QueryAsync(target.Host, TimeSpan.FromSeconds(2)).ConfigureAwait(false) is { } info)
                {
                    var pm = CommunityPatchManager.Shared;
                    // Havuz satırları oyunun kimliği ve adıyla gider; paket hazırlarken oyunlar ayrılır.
                    services.Pool?.SetGame(info.Game);
                    if (pm.SetRunningGame(info.Game))
                        SubtitleLog.Write(info.Game is { } game
                            ? $"PS5'te çalışan oyun: {game.Name} ({game.TitleId}){(pm.DetectedGameId is { } id ? $", paket: {id}" : ", paketi yok")}"
                            : "PS5'te oyun çalışmıyor");
                }
                await Task.Delay(RunningGameEvery).ConfigureAwait(false);
            }
        }
        catch { /* ağ hatası çeviriyi etkilemez */ }
        finally { Volatile.Write(ref watchingGame, 0); }
    }

    public void Stop()
    {
        session?.Stop();
        session?.Dispose();
        session = null;
        keeper?.Stop();
        keeper = null;
        pipeline?.Reset();
        pipeline = null;
        // Yeni "Başlat" eski oturumdan kalan (belki takılmış) okumayı beklemeden temiz başlar.
        frameGate.Reset();
        activeReader = null;
        screenWork?.Cancel();
        screenWork = null;
        if (DateTime.UtcNow < screenShownUntil) server.ClearScreen();
        screenShownUntil = default;
        SubtitlesPaused = false;
        post(() => SubtitlesCleared?.Invoke());
        ActiveSourceLanguage = null;
        StopDubber();
        if (isRunning) sleep.End();
        IsRunning = false;
        LastFrame = null;
        LastSource = "";
        MetricsText = "";
        LastTranslation = "";
        SessionState = SessionState.Disconnected(DisconnectKind.Stopped);
        UpdateTroubleClocks();
        firewallRuleSeen = false;
        ClearFirewallPrompt();
    }

    /// <summary>Görüntü giriş kaynağı (Remote Play, Yakalama Kartı, Masaüstü) değişince akışı anında yeniden başlatır.</summary>
    public void RestartStream()
    {
        if (!isRunning) return;
        session?.Stop();
        session?.Dispose();
        session = null;
        LastFrame = null;

        var stream = services.MakeStream(console, accountId, settings);
        stream.StateChanged += state => post(() => SessionStateChanged(state));
        stream.ConsoleMoved += moved => post(() => ConsoleMoved(moved));
        stream.FrameReceived += HandleFrame;
        stream.Start();
        session = stream;
    }

    /// <summary>Bilgisayar uykudan uyanınca bağlantıları hemen yenile.</summary>
    public void SystemResumed()
    {
        session?.ReconnectNow();
        keeper?.Reconnect();
    }

    /// <summary>Çeviri başlamamışken bağlı olmamak bir sorun değildir; nokta gri kalır.</summary>
    public static StatusTone Ps5ToneFor(SessionState state, bool running) =>
        state.Phase == SessionPhase.Streaming ? StatusTone.Good : running ? StatusTone.Warning : StatusTone.Idle;

    public static StatusTone TvToneFor(int clients, bool running) =>
        clients > 0 ? StatusTone.Good : running ? StatusTone.Warning : StatusTone.Idle;

    /// <summary>PS5 durumunun kullanıcıya gösterilecek metni.</summary>
    public static string StatusText(SessionState state, bool running, Localizer text)
    {
        switch (state.Phase)
        {
            case SessionPhase.Streaming: return text["Connected"];
            case SessionPhase.Connecting: return text["Connecting…"];
        }
        if (!running) return text["Not connected"];
        return text[state.Reason?.Kind switch
        {
            DisconnectKind.ConsoleAsleep => "Turn on your PS5",
            DisconnectKind.InUse => "Remote Play is in use on another device",
            DisconnectKind.Refused => "The PS5 refused the connection. Set up the PS5 again.",
            DisconnectKind.LoginPasscode => "The second user has a login passcode on the PS5. Turn it off in Settings › Users and Accounts.",
            // Eskiden "Yeniden bağlanılıyor…" diye sonsuza kadar deneniyordu (CouchSubs 1e02870).
            DisconnectKind.RegistrationInvalid => "The PS5 didn't accept this pairing. Pair the PS5 again with the second account in Settings.",
            _ => "Reconnecting…",
        }];
    }

    public void Dispose()
    {
        Stop();
        server.Dispose();
        poolTimer?.Dispose();
        // Kapanırken kuyruk bir kez daha gönderilir; en çok birkaç saniye beklenir.
        if (services.Pool is { } pool)
        {
            try { pool.FlushAsync().Wait(TimeSpan.FromSeconds(3)); }
            catch (AggregateException) { }
            pool.Save();
        }
    }

    // --- İç işleyiş ---

    /// <summary>Okuma hattını güncel dillerle (yeniden) kurar.</summary>
    private void MakePipeline()
    {
        pipeline?.Reset();
        pipeline = null;
        // Eski hatta süren okuma beklenmez; yeni hat ilk karesini hemen alır.
        frameGate.Reset();
        ActiveSourceLanguage = settings.SourceLanguage;
        var reader = services.MakeSubtitleReader(settings.SourceLanguage);
        activeReader = reader;
        OcrMissing = reader is null || !services.HasOcrLanguage(settings.SourceLanguage);
        if (reader is null) return;
        // Art arda altyazılara bölünen cümle bütün olarak çevrilir (hangi motor seçilirse seçilsin).
        // Kısa ünlemler ("Well?", "Hurry!") İngilizceden Türkçeye hazır karşılıkla çevrilir; çevirmenler bağlamsız kısa
        // satırı sözlük anlamıyla çeviriyordu ("Kuyu?", "Acele etmek!").
        var phrasebook = settings.SourceLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase) &&
                         settings.TargetLanguage.StartsWith("tr", StringComparison.OrdinalIgnoreCase)
            ? ShortPhrases.Translate
            : (Func<string, string?>?)null;
        // Konuşmacı adları: Windows'un okuyucusu bazı adları hep aynı biçimde bozuyor ("Aloy" → "Alou"); adı henüz
        // doğrulanmamış satırı güçlü okuyucu da okur, onun okuduğu ad doğru ad sayılır.
        var names = new SpeakerNames();
        if (reader is HybridReader hybrid)
        {
            hybrid.NeedsStrong = names.NeedsCheck;
            hybrid.StrongRead = names.Confirm;
        }
        // (Önce cihaz üstü çeviri gösterip Gemini'ninkini sonra yerine koyma denendi: ekrandaki yazının değişmesi
        // kullanıcıya göre mantıklı değildi; Gemini seçiliyse çeviri yine doğrudan Gemini'den gelir. Hattın Upgrader'ı kaldı.)
        var built = new SubtitlePipeline(reader, new ContinuationTranslator(MakeTranslator(), looksMisread: text => SubtitlePipeline.ExtraMisreadCheck?.Invoke(text) ?? false)
        {
            Phrasebook = phrasebook,
            Names = names,
        }, server, new ReadScheduler());
        // Paket yalnız oynanan oyunun paketiyse kullanılır (başka oyunun genel satırları yanlış çeviri basıyordu).
        var packGate = new PackGate();
        // Packs hold Turkish lines: they are looked up (and their misses offered to the pool) only for Turkish.
        // Without packs the pipeline must still hold and compare readings (a half-read or misread line waits for a
        // better reading, a re-read of the line just shown is not sent again): the original app got this as a side
        // effect of always having a pack lookup.
        built.HoldWithoutPacks = true;
        if (UsesTurkishFeatures(settings))
        {
            built.PackMissed += OfferToPool;
            // Oyun elle seçildiyse paket ilk satırdan kullanılır (oyunun tanınması beklenmez).
            built.PackLookup = text => CommunityPatchManager.Shared.GameKnown
                ? CommunityPatchManager.Shared.Match(text)
                : packGate.Filter(text, CommunityPatchManager.Shared.Match(text));
        }
        // Paket beklemesi kısa tutulur: 1.5.1'deki 300 / 800 ms her satıra gecikme ekliyordu. Tam cümle zaten
        // beklemez; yarım görünen satır ve bozuk görünen okuma yalnız daha iyisine fırsat verecek kadar bekler.
        // (Başka oyunların paketleri genel satırlarda ("Leave me alone!") isabet verdiği için isabete göre ayarlanmaz.)
        // Bozuk okuma (yazım denetimine göre gerçek olmayan sözcük içeren satır) biraz daha uzun bekler: hareketli
        // sahnede düzgün okuma çoğunlukla yarım saniye içinde geliyor; temiz satır bundan etkilenmez.
        built.CleanWait = 150;
        built.PackWait = 450;
        // Aynı okuma yinelenirse (yazı tanıma satırı hep böyle okuyor) beklemenin kalanı atlanır.
        built.SettledRelease = SubtitlePipeline.SettledAfterMs;
        built.TidySpeakersAndHeadings = true;
        // Otomatik algılamada şerit sert bir kırpma değildir: şeridin çevresi geniş okunur, şeritteki satırlar ve onlara
        // bitişik satırlar altyazıdır (üç dört satıra uzayan altyazının alt satırı kesiliyordu).
        // Şeride düşen arayüz yazısı (envanter çubuğu, savaş günlüğü) kısa sürede aynen yinelenir ve cümle gibi
        // bitmez; üç kez çevrildikten sonra bir süre okunmaz.
        var repeats = new RepeatedText();
        built.LineFilter = (read, area) => WithoutGarbageLines(repeats.Without(settings.AutoRegion
            ? autoBand is { } band ? SubtitleFinder.Grow(read, area, (band.Item1, band.Item2)) : WithoutOffCenterLines(read, area)
            : read));
        built.PairShown += (source, _) =>
        {
            if (repeats.Note(source) is { } blocked) SubtitleLog.Write($"arayüz yazısı: {SubtitleLog.Excerpt(blocked)} yineleniyor, artık çevrilmeyecek");
        };
        // Cümlenin başı okunamamış parça ("bad at surrendering.") bozuk okumadır: tamamı gelsin diye bekletilir;
        // çok kısaysa ya da sözcüklerinin çoğu gerçek değilse hiç çevrilmez.
        var misread = built.LooksMisread;
        // Oyun her satırda konuşmacı adı gösteriyorsa (son üç satır adlı geldiyse) adsız okunan satır büyük olasılıkla
        // adı kaçırılmış okumadır: adlı okuması gelsin diye kısa süre bekletilir (aynı satır önce adsız sonra adlı
        // iki kez gösteriliyordu).
        var speakerStreak = 0;
        built.LooksMisread = text => misread(text) || Misspelling.IsOrphanFragment(text, built.LastPair?.Source) || LooksLikeFragment(text) ||
            (Volatile.Read(ref speakerStreak) >= 3 && !text.Contains('\n') && !HasSpeakerLabel(text));
        built.PairShown += (source, _) =>
        {
            if (HasSpeakerLabel(source)) Interlocked.Increment(ref speakerStreak);
            else Volatile.Write(ref speakerStreak, 0);
        };
        built.IsGarbage = text => (SubtitlePipeline.ExtraGarbageCheck?.Invoke(text) ?? false) ||
            (Misspelling.IsOrphanFragment(text, built.LastPair?.Source) && text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 3);
        // Altyazı günlüğü (altyazi.log): okunan satırlar ve akıbetleri; gösterilen çevirinin kendisi de yazılır.
        built.Log = SubtitleLog.Write;
        built.MetricsReady += metrics => post(() => MetricsText = metrics.Summary(text));
        built.PairShown += (source, translation) =>
        {
            SubtitleLog.Write($"çeviri: {SubtitleLog.Excerpt(source)} ⇒ {SubtitleLog.Excerpt(translation)}");
            var box = built.LastBox;
            var origin = built.LastOrigin == "paket" ? LineOrigin.Pack : LineOrigin.Engine;
            var ms = RegionTranslation.DisplayMilliseconds(translation);
            post(() =>
            {
                LastSource = source;
                LastTranslation = translation;
                AddToHistory(source, translation, origin);
                if (box is not null) lastSubtitleBox = box;
                var targetBox = box ?? lastSubtitleBox ?? (ActiveRegion is { } r
                    ? new TextBox(r.Left, r.Top, r.Right, r.Bottom)
                    : null);
                LiveSubtitleShown?.Invoke(translation, ms, targetBox);
            });
        };
        // Model bu arada inmiş olabilir; her çeviri sonucu durumu günceller.
        built.TranslationResult += error => post(() => TranslatorStatus = error ?? TranslatorStatus.Ready);
        built.SpeechReady += lines => post(() => Speak(lines));
        pipeline = built;
    }

    /// <summary>
    /// Altyazı değişirken bir anlığına okunan parça gibi mi: konuşmacı adsız tek satır ve ya tek sözcük ("Yeah?",
    /// "Sit?") ya da en çok üç sözcük olup cümlenin ortasından başlıyor (",to me.", "pin Ito me."). Böyle okuma bozuk
    /// sayılır: kısa süre bekletilir, ardından tam satır gelirse atılır, gelmezse her zamanki gibi çevrilir.
    /// </summary>
    public static bool LooksLikeFragment(string text)
    {
        var line = text.Trim();
        if (line.Length == 0 || line.Contains('\n') || HasSpeakerLabel(line)) return false;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(word => word.Any(char.IsLetter));
        if (words == 1) return true;
        if (words > 3) return false;
        var first = line.FirstOrDefault(char.IsLetter);
        return char.IsLower(first) || ",;.:-'".Contains(line[0]);
    }

    /// <summary>
    /// Birden çok satırlı okumadan okunamayan satırları çıkarır (yazı belirirken ikinci satır "wee eats etic lid entia"
    /// okunuyor ve okunan her bozuk hâli ayrı çeviri olarak gösteriliyordu). Hiç düzgün satır yoksa okuma olduğu gibi
    /// kalır; bütünüyle bozuk okumayı okuma hattının kendi denetimi ele alır.
    /// </summary>
    /// <summary>
    /// Altyazının yeri henüz öğrenilmeden geniş alan okunur; orada ekranın ortasından belirgin biçimde uzaktaki satırlar
    /// (sol üstteki görev yazısı "Activate the statues", köşe göstergeleri) altyazı sayılmaz.
    /// </summary>
    public static Reading WithoutOffCenterLines(Reading reading, TextBox area)
    {
        if (reading.Lines.Count == 0) return reading;
        var kept = reading.Lines.Where(line => line.Box is not { } box ||
            Math.Abs((box.PlacedIn(area).Left + box.PlacedIn(area).Right) / 2 - 0.5) <= 0.2).ToList();
        return kept.Count == reading.Lines.Count ? reading : new Reading(kept);
    }

    /// <summary>
    /// Zemindeki dokudan ya da filigrandan okunan, tek "sözcüklük", tümü büyük harfli ve gerçek sözcük olmayan satır
    /// ("IETTET", "HIITIIE"). Büyük harfli gerçek sözcük ("NO!", "RUN") ve kısaltmalar (en çok 3 harf) kalır.
    /// </summary>
    public static bool IsCapsJunk(string text, Func<string, bool>? isWord)
    {
        var token = text.Trim().Trim('.', ',', '!', '?', '-', '•', '\'', '"');
        if (token.Length < 4 || token.Contains(' ') || !token.All(char.IsUpper)) return false;
        return isWord is null ? !"AEIOU".Any(token.Contains) || token.Distinct().Count() * 2 <= token.Length : !isWord(token.ToLowerInvariant());
    }

    private static Reading WithoutGarbageLines(Reading reading)
    {
        if (reading.Lines.Count < 2 || SubtitlePipeline.ExtraGarbageCheck is not { } garbage) return reading;
        var word = SubtitlePipeline.ExtraWordCheck;
        var kept = reading.Lines.Where(line => !garbage(line.Text) && !IsCapsJunk(line.Text, word)).ToList();
        return kept.Count == reading.Lines.Count || kept.Count == 0 ? reading : new Reading(kept);
    }

    /// <summary>Satır "Ad: söz" biçiminde mi (ad büyük harfle başlar, en çok üç sözcüktür).</summary>
    private static bool HasSpeakerLabel(string text)
    {
        var line = text.TrimStart();
        var colon = line.IndexOf(": ", StringComparison.Ordinal);
        return colon is >= 2 and <= 30 && char.IsUpper(line[0]) && line[..colon].Split(' ').Length <= 3 &&
               line[..colon].All(c => char.IsLetter(c) || c is ' ' or '-' or '.' or '\'' or '’');
    }

    private IOnlineTranslator? watchedTranslator;
    private OnlineKeyState? translationTrouble;

    /// <summary>
    /// Why the AI service is not translating right now (unreachable, key refused, quota used up…); null while it works
    /// or when no AI service is in use. While it is set, lines are translated by the free fallback.
    /// </summary>
    public OnlineKeyState? TranslationTrouble { get => translationTrouble; private set => Set(ref translationTrouble, value); }

    /// <summary>
    /// Follows the failure state of the translator now in use. The one it replaces is let go first: a late report from
    /// a translator the user already switched away from must not show as trouble.
    /// </summary>
    private ITextTranslator Watched(ITextTranslator translator)
    {
        Unwatch();
        if (translator is IOnlineTranslator online)
        {
            watchedTranslator = online;
            online.FailureChanged += OnTranslatorFailure;
            TranslationTrouble = online.LastError;
        }
        return translator;
    }

    private void Unwatch()
    {
        if (watchedTranslator is not null) watchedTranslator.FailureChanged -= OnTranslatorFailure;
        watchedTranslator = null;
        TranslationTrouble = null;
    }

    private void OnTranslatorFailure(OnlineKeyState? state) => post(() => TranslationTrouble = state);

    private ITextTranslator MakeTranslator()
    {
        Unwatch();
        var local = MakeLocalTranslator();

        if (ActiveAiProvider is { } provider && HasAiKey(provider))
        {
            if (StoreKeys.ForAi(provider) is { } slot && store.Load<string>(slot) is { Length: > 0 } key)
            {
                var target = TargetLanguages.Normalize(settings.TargetLanguage);
                var fallback = local is UnavailableTranslator ? null : local;
                var modelId = ModelIdFor(provider);
                var fastId = provider.ModelFor(QualityMode.Fastest).Id;
                // The safety net for slow lines: prepared first, so the translator in use is the last one made.
                var quick = settings.FastWhenSlow && !UsesTurkishFeatures(settings) && modelId != fastId
                    ? services.MakeAiTranslator(provider, key, fastId, target, fallback, settings.Dialect)
                    : null;
                if (services.MakeAiTranslator(provider, key, modelId, target, fallback, settings.Dialect) is { } ai)
                {
                    Watched(ai);
                    return quick is null ? ai : new RacingTranslator(ai, quick, UserSettings.SlowLinePatience);
                }
            }
        }
        // DeepL is wired for English→Turkish only (its translator and settings screen say so).
        else if (settings.Engine == TranslationEngine.DeepL && HasDeepLKey && UsesTurkishFeatures(settings))
        {
            if (store.Load<string>(StoreKeys.DeepLKey) is { Length: > 0 } key &&
                services.MakeOnlineTranslator(key, local is UnavailableTranslator ? null : local) is { } online)
            {
                return online;
            }
        }

        return local;
    }

    private ITextTranslator MakeLocalTranslator()
    {
        var translation = services.Translation;
        var (source, target) = (settings.SourceLanguage, settings.TargetLanguage);
        return translation.Create(source, target);
    }

    /// <summary>
    /// The AI service that translates now: the one in settings when the engine is <see cref="TranslationEngine.Ai"/>;
    /// Gemini for settings saved by older versions (engine "Gemini"); null for the local model and DeepL.
    /// </summary>
    private AiProvider? ActiveAiProvider => settings.Engine switch
    {
        TranslationEngine.Ai => settings.Provider,
        TranslationEngine.Gemini => AiProvider.Gemini,
        _ => null,
    };

    /// <summary>
    /// The model that translates with this service. The quality switch belongs to the Arabic edition's screens; a
    /// Turkish target keeps the fast model the original app used (free tier, with its per-minute budget).
    /// </summary>
    private string ModelIdFor(AiProvider provider) =>
        provider.ModelFor(UsesTurkishFeatures(settings) ? QualityMode.Fastest : settings.Quality).Id;

    /// <summary>Is a key saved for this AI service (the key itself never goes back to the UI).</summary>
    public bool HasAiKey(AiProvider provider) =>
        StoreKeys.ForAi(provider) is { } slot && store.Load<string>(slot) is { Length: > 0 };

    /// <summary>
    /// Tries the key with a sample line in the target language; if the service accepts it (even with its quota used
    /// up) the key is saved and the service becomes the active translator.
    /// </summary>
    public async Task<OnlineKeyCheck> SetAiKeyAsync(AiProvider provider, string key)
    {
        key = key.Trim();
        if (StoreKeys.ForAi(provider) is not { } slot) return new OnlineKeyCheck(OnlineKeyState.Invalid);
        var check = await services.CheckAiKeyAsync(provider, key, ModelIdFor(provider), TargetLanguages.Normalize(settings.TargetLanguage));
        if (check.State is OnlineKeyState.Valid or OnlineKeyState.QuotaExceeded)
        {
            store.Save(slot, key);
            Changed(nameof(HasGeminiKey));
            Settings = settings with { Engine = TranslationEngine.Ai, Provider = provider, OnlineTranslation = true };
            if (isRunning) MakePipeline();
            RefreshTranslatorStatus();
        }
        return check;
    }

    /// <summary>Forgets the service's key; if it was the active translator, translation goes back to the local one.</summary>
    public void RemoveAiKey(AiProvider provider)
    {
        if (StoreKeys.ForAi(provider) is not { } slot) return;
        var wasActive = ActiveAiProvider == provider;
        store.Remove(slot);
        Changed(nameof(HasGeminiKey));
        if (wasActive) Settings = settings with { Engine = TranslationEngine.DirectML };
        if (isRunning) MakePipeline();
        RefreshTranslatorStatus();
    }

    /// <summary>Uses the service saved for an AI key that is already stored (the user picks between saved services).</summary>
    public void UseAiProvider(AiProvider provider)
    {
        if (!HasAiKey(provider)) return;
        Settings = settings with { Engine = TranslationEngine.Ai, Provider = provider, OnlineTranslation = true };
    }

    /// <summary>Turns the fast-model safety net for slow lines on or off (the setter rebuilds the pipeline).</summary>
    public void SetFastWhenSlow(bool on) => Settings = settings with { FastWhenSlow = on };

    /// <summary>The Arabic style of the next lines (the setter rebuilds the pipeline).</summary>
    public void SetDialect(ArabicStyle style) => Settings = settings with { Dialect = style };

    /// <summary>"Best quality" or "Fastest": the next line is translated with that model (the setter rebuilds the pipeline).</summary>
    public void SetQuality(QualityMode mode) => Settings = settings with { Quality = mode };

    /// <summary>Kayıtlı bir Google Gemini anahtarı var mı.</summary>
    public bool HasGeminiKey => HasAiKey(AiProvider.Gemini);

    /// <summary>Gemini anahtarını dener; geçerliyse kaydeder ve Gemini'yi aktif model yapar.</summary>
    public Task<OnlineKeyCheck> SetGeminiKeyAsync(string key) => SetAiKeyAsync(AiProvider.Gemini, key);

    public void RemoveGeminiKey() => RemoveAiKey(AiProvider.Gemini);

    /// <summary>DeepL yalnız İngilizce→Türkçe için açılır (ayar ekranındaki dil seçenekleri de bunlar).</summary>
    private bool OnlineTranslationActive =>
        settings.OnlineTranslation && settings.SourceLanguage == "en" && settings.TargetLanguage == "tr" && HasDeepLKey;

    /// <summary>Kayıtlı bir DeepL anahtarı var mı (anahtarın kendisi arayüze hiç dönmez).</summary>
    public bool HasDeepLKey => store.Load<string>(StoreKeys.DeepLKey) is { Length: > 0 };

    /// <summary>Anahtar denenir; geçerliyse (kotası bitmiş olsa da) şifreli depoya yazılır ve çevrimiçi çeviri açılır.</summary>
    public async Task<OnlineKeyCheck> SetDeepLKeyAsync(string key)
    {
        key = key.Trim();
        var check = await services.CheckOnlineKeyAsync(key);
        if (check.State is OnlineKeyState.Valid or OnlineKeyState.QuotaExceeded)
        {
            store.Save(StoreKeys.DeepLKey, key);
            Changed(nameof(HasDeepLKey));
            Settings = settings with { Engine = TranslationEngine.DeepL, OnlineTranslation = true };
            if (isRunning) MakePipeline();
            RefreshTranslatorStatus();
        }
        return check;
    }

    /// <summary>Kayıtlı anahtarın bu ayki kullanımı.</summary>
    public Task<OnlineKeyCheck> CheckDeepLKeyAsync() =>
        store.Load<string>(StoreKeys.DeepLKey) is { Length: > 0 } key ? services.CheckOnlineKeyAsync(key)
            : Task.FromResult(new OnlineKeyCheck(OnlineKeyState.Invalid));

    public void RemoveDeepLKey()
    {
        store.Remove(StoreKeys.DeepLKey);
        Changed(nameof(HasDeepLKey));
        Settings = settings with { Engine = TranslationEngine.DirectML, OnlineTranslation = false };
        if (isRunning) MakePipeline();
        RefreshTranslatorStatus();
    }

    private void RefreshTranslatorStatus()
    {
        TranslatorStatus = TranslatorStatus.Ready;
    }

    /// <summary>PS5'in adresi değişti: sonraki açılışlar yeni adresle başlasın.</summary>
    private void ConsoleMoved(RegisteredConsole moved)
    {
        if (console is null) return;
        console = moved;
        store.Save(StoreKeys.Console, moved);
        Changed(nameof(Console));
    }

    private void SessionStateChanged(SessionState state)
    {
        SessionState = state;
        UpdateTroubleClocks();
        // Bağlantı koptuysa TV'de eski altyazı kalmasın.
        if (state.Phase == SessionPhase.Disconnected)
        {
            pipeline?.Reset();
            server.Clear();
            dubber?.Stop();
        }
    }

    public void FeedFrame(Frame frame) => HandleFrame(frame);
    public void ClearLastFrame() => post(() => LastFrame = null);

    private async void HandleFrame(Frame frame)
    {
        post(() => LastFrame = frame);
        // Duraklatıldıysa kare yalnız ek bölgeler ve serbest alan için saklanır; altyazı okunmaz.
        if (subtitlesPaused) return;
        TryFindSubtitles(frame);
        // Önceki kare hâlâ işleniyorsa bu kare atlanır; okuma kareleri biriktirmez.
        var current = pipeline;
        if (current is null) return;
        var entry = frameGate.TryEnter(Environment.TickCount64);
        if (entry.Kind == FrameGate.EntryKind.Stuck)
        {
            // Okuma bitmedi: önizleme canlıyken altyazı hiç gelmiyordu ve ancak uygulamayı kapatıp açmak düzeltiyordu.
            SubtitleLog.Write($"okuma {entry.Ms} ms'dir bitmedi; okuma hattı yeniden kuruldu");
            post(MakePipeline);
            return;
        }
        if (entry.Kind != FrameGate.EntryKind.Enter) return;
        try
        {
            await current.ProcessAsync(frame, ActiveRegion);
        }
        catch
        {
            // Tek bir karenin okunamaması akışı durdurmaz.
        }
        finally
        {
            frameGate.Leave(entry.Token);
        }
    }

    // Otomatik altyazı algılama (SubtitleFinder): karenin tamamı ayrı bir okuyucuyla aralıklı okunur (şerit öğrenilene
    // kadar saniyede bir, sonra iki saniyede bir). Öğrenilen şerit, ayarlarda "otomatik" açıkken okunan alan olur;
    // hızlı okuma yalnız o şeritte yapılır. Bulgular altyazı günlüğüne "otomatik:" diye yazılır.
    private const int FindEveryMs = 2000;
    private const int LearnEveryMs = 1000;
    /// <summary>Şerit öğrenilene kadar okunan alan: ekranın alt yarısına yakın, kenarlar hariç.</summary>
    private static readonly CropRegion AutoStart = new(Top: 0.55, Bottom: 0.98, Left: 0.08, Right: 0.92);
    private readonly SubtitleFinder finder = new();
    private readonly HashSet<string> findLogged = [];
    private ITextReader? findReader;
    private string? findLanguage;
    private (double Top, double Bottom)? findBand;
    private volatile CropRegion? autoRegion;
    /// <summary>Öğrenilen şerit (üst, alt); okunan geniş alandaki satırlar buna göre süzülür.</summary>
    private volatile Tuple<double, double>? autoBand;
    /// <summary>Okunan alan şeridin bu kadar üstünden ve altından başlar: uzayan altyazının satırları kesilmesin.</summary>
    private const double BandMarginTop = 0.08, BandMarginBottom = 0.10;
    private long lastFind;
    private int finding;

    /// <summary>Altyazının okunduğu alan: otomatikse öğrenilen şerit (henüz öğrenilmediyse geniş alt alan), değilse elle çizilen.</summary>
    public CropRegion ActiveRegion => settings.AutoRegion ? autoRegion ?? AutoStart : settings.Region;

    private void TryFindSubtitles(Frame frame)
    {
        var at = Environment.TickCount64;
        if (!settings.AutoRegion && !SubtitleLog.IsEnabled) return;
        var every = autoRegion is null ? LearnEveryMs : FindEveryMs;
        if (at - lastFind < every || Interlocked.Exchange(ref finding, 1) == 1) return;
        lastFind = at;
        var language = settings.SourceLanguage;
        _ = Task.Run(() =>
        {
            try
            {
                if (findReader is null || findLanguage != language)
                {
                    findReader = services.MakeReader(language);
                    findLanguage = language;
                }
                if (findReader is not { } reader) return;
                var reading = reader.ReadLines(frame, deep: false).WithoutOcrNoise();
                var took = Environment.TickCount64 - at;
                foreach (var found in finder.Classify(reading, at))
                {
                    if (!findLogged.Add(found.Verdict + ":" + TextMatch.Normalize(found.Line.Text))) continue;
                    if (findLogged.Count > 600) findLogged.Clear();
                    var box = found.Line.Box!.Value;
                    SubtitleLog.Write($"otomatik: {FindName(found.Verdict)} y=%{(box.Top + box.Bottom) * 50:0} x=%{(box.Left + box.Right) * 50:0} " +
                                      $"({took} ms) {SubtitleLog.Excerpt(found.Line.Text)}");
                }
                // Şerit her yeni altyazıyla azıcık oynar; okunan alan yalnız belirgin değişince güncellenir (alan
                // değişince okuma baştan kararlı hale gelmek zorunda kalır).
                if (finder.Band is { } band &&
                    (findBand is not { } old || Math.Abs(old.Top - band.Top) > 0.035 || Math.Abs(old.Bottom - band.Bottom) > 0.035))
                {
                    findBand = band;
                    autoBand = Tuple.Create(band.Top, band.Bottom);
                    autoRegion = new CropRegion(Math.Max(0, band.Top - BandMarginTop), Math.Min(0.99, band.Bottom + BandMarginBottom), AutoStart.Left, AutoStart.Right);
                    SubtitleLog.Write($"otomatik: altyazı şeridi %{band.Top * 100:0}–%{band.Bottom * 100:0}" +
                                      (settings.AutoRegion ? " (okunan alan güncellendi)" : " (elle seçilen alan kullanılıyor)"));
                }
            }
            catch
            {
                // Deneme okuması başarısız olursa yalnız o tarama atlanır.
            }
            finally
            {
                Volatile.Write(ref finding, 0);
            }
        });
    }

    private static string FindName(SubtitleFinder.Verdict verdict) => verdict switch
    {
        SubtitleFinder.Verdict.Subtitle => "ALTYAZI",
        SubtitleFinder.Verdict.Prompt => "elendi(tuş ipucu)",
        SubtitleFinder.Verdict.Menu => "elendi(menü)",
        SubtitleFinder.Verdict.Static => "elendi(sabit yazı)",
        SubtitleFinder.Verdict.OffCenter => "elendi(ortada değil)",
        SubtitleFinder.Verdict.NotSentence => "elendi(cümle değil)",
        _ => "elendi(şerit dışı)",
    };

    private async Task StartServerAsync()
    {
        if (serverStarted) return;
        try
        {
            ServerPort = await server.StartAsync();
        }
        catch (SocketException)
        {
            // 8787 başka bir uygulamada; TV'ye adres her seferinde bildirildiği için herhangi bir boş port olur.
            server.ClientsChanged -= OnClientsChanged;
            server = new SubtitleServer(0);
            server.ClientsChanged += OnClientsChanged;
            ServerPort = await server.StartAsync();
        }
        serverStarted = true;
    }

    private void OnClientsChanged(int count) => post(() =>
    {
        TvClients = count;
        UpdateTroubleClocks();
    });

    /// <summary>TV ile aynı ağdaki bilgisayar adresi.</summary>
    private string ServerAddress(LgTv target) => $"http://{LocalAddress(target.Host) ?? "127.0.0.1"}:{ServerPort}";

    /// <summary>
    /// <paramref name="near"/> adresine (TV) ulaşan bu bilgisayar adresi; TV adresi bilinmiyorsa (null ya da geçersiz)
    /// Wi-Fi/Ethernet adresi. VPN ve sanal bağdaştırıcılar (Hyper-V, WSL, VirtualBox…) ancak başka adres yoksa seçilir:
    /// onlar da 192.168.x.x taşıyor ama TV onlara ulaşamıyor (CouchSubs 8f73eb9, <see cref="LocalNetwork.ChooseComputerAddress"/>).
    /// </summary>
    public static string? LocalAddress(string? near)
    {
        IPAddress? target = IPAddress.TryParse(near, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork ? parsed : null;
        return LocalNetwork.ChooseComputerAddress(LocalNetwork.Ipv4Adapters(), target);
    }

    // --- Windows Güvenlik Duvarı ---

    /// <summary>PS5 bu kadar süre "Yeniden bağlanıyor…" kalırsa güvenlik duvarı kuralına bakılır.</summary>
    public static readonly TimeSpan Ps5TroubleDelay = TimeSpan.FromSeconds(20);
    /// <summary>TV uygulaması bu kadar süre bağlanmazsa güvenlik duvarı kuralına bakılır.</summary>
    public static readonly TimeSpan TvWaitDelay = TimeSpan.FromSeconds(60);

    private FirewallPrompt firewallPrompt;
    /// <summary>Bu çeviri oturumunda kural bulundu; her denetimde kurallar yeniden taranmaz.</summary>
    private bool firewallRuleSeen;
    private bool checkingFirewall;
    private DateTime? ps5TroubleSince;
    private DateTime? tvWaitingSince;

    /// <summary>Güvenlik duvarı kartı ya da eşleştirme adımındaki düğme; yalnız kural eksikken önerilir.</summary>
    public FirewallPrompt FirewallPrompt { get => firewallPrompt; private set => Set(ref firewallPrompt, value); }

    /// <summary>Zaman (testlerde ileri sarılır).</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// Ana ekran birkaç saniyede bir çağırır: PS5 20 saniyedir yeniden bağlanıyorsa ya da TV 60 saniyedir bağlanmadıysa ve
    /// güvenlik duvarında GameTranslator'a izin yoksa kart önerilir. İkisi de bağlanınca kart kalkar.
    /// </summary>
    public async Task CheckConnectionsAsync()
    {
        UpdateTroubleClocks();
        if (!isRunning || services.Firewall is not { } firewall) return;
        if (ps5TroubleSince is null && tvWaitingSince is null)
        {
            ClearFirewallPrompt();
            return;
        }
        var now = Clock();
        var stuck = now - ps5TroubleSince >= Ps5TroubleDelay || now - tvWaitingSince >= TvWaitDelay;
        if (!stuck || firewallPrompt != FirewallPrompt.None || firewallRuleSeen || checkingFirewall) return;
        checkingFirewall = true;
        try
        {
            var hasRule = await HasFirewallRuleAsync(firewall);
            if (!isRunning) return;
            if (hasRule) firewallRuleSeen = true;
            else FirewallPrompt = FirewallPrompt.Offer;
        }
        finally
        {
            checkingFirewall = false;
        }
    }

    /// <summary>
    /// Kuralı yönetici izniyle ekler. Çeviri sürüyorsa PS5 ve TV hemen yeniden denenir; eşleştirmede kullanıcı yeni
    /// kodla yeniden bağlanır.
    /// </summary>
    public async Task<bool> AllowThroughFirewallAsync()
    {
        if (services.Firewall is not { } firewall || firewallPrompt == FirewallPrompt.Working) return false;
        FirewallPrompt = FirewallPrompt.Working;
        bool added;
        try { added = await firewall.AddRuleAsync(); }
        catch (Exception) { added = false; }
        FirewallPrompt = added ? FirewallPrompt.Allowed : FirewallPrompt.Failed;
        if (!added) return false;
        firewallRuleSeen = true;
        if (isRunning)
        {
            session?.ReconnectNow();
            keeper?.Reconnect();
        }
        return true;
    }

    /// <summary>Eşleştirme başarısız oldu: kural eksikse düğme önerilir.</summary>
    private async Task OfferFirewallAsync()
    {
        if (services.Firewall is not { } firewall || firewallPrompt == FirewallPrompt.Working) return;
        FirewallPrompt = await HasFirewallRuleAsync(firewall) ? FirewallPrompt.None : FirewallPrompt.Offer;
    }

    /// <summary>Kurallar arka planda taranır; okunamazsa kural var sayılır (yersiz öneri yapılmaz).</summary>
    private static async Task<bool> HasFirewallRuleAsync(IFirewall firewall)
    {
        try { return await Task.Run(firewall.HasRule); }
        catch (Exception) { return true; }
    }

    private void ClearFirewallPrompt()
    {
        if (firewallPrompt != FirewallPrompt.Working) FirewallPrompt = FirewallPrompt.None;
    }

    /// <summary>
    /// PS5'in ve TV'nin ne zamandan beri bağlanamadığı. PS5 uykudaysa, başka cihazdaysa ya da bağlantıyı reddettiyse
    /// sorun güvenlik duvarı değildir; sayılmaz.
    /// </summary>
    private void UpdateTroubleClocks()
    {
        var now = Clock();
        var ps5Trouble = isRunning && sessionState.Phase != SessionPhase.Streaming && sessionState.Reason?.Kind is not
            (DisconnectKind.ConsoleAsleep or DisconnectKind.InUse or DisconnectKind.Refused or DisconnectKind.LoginPasscode
             or DisconnectKind.RegistrationInvalid);
        ps5TroubleSince = ps5Trouble ? ps5TroubleSince ?? now : null;
        tvWaitingSince = isRunning && tvClients == 0 ? tvWaitingSince ?? now : null;
    }

    // --- Uygulama güncellemesi ---

    /// <summary>GitHub'da daha yeni sürüm varsa numarası.</summary>
    public string? UpdateVersion { get => updateVersion; private set => Set(ref updateVersion, value); }
    public bool Updating { get => updating; private set => Set(ref updating, value); }
    public double UpdateProgress { get => updateProgress; private set => Set(ref updateProgress, value); }
    public string? UpdateError { get => updateError; private set => Set(ref updateError, value); }
    /// <summary>Kurulum dosyasıyla kurulduysa tek tıkla güncellenir; değilse sürüm sayfası açılır.</summary>
    public bool CanUpdateItself => services.Updater?.CanUpdateItself == true;

    /// <summary>Açılışta bir kez; ağ yoksa sessizce geçer.</summary>
    public async Task CheckForUpdateAsync()
    {
        if (services.Updater is not { } updater) return;
        try { UpdateVersion = await updater.CheckAsync(); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException) { }
    }

    public async Task InstallUpdateAsync()
    {
        if (services.Updater is not { } updater || updating) return;
        if (!updater.CanUpdateItself)
        {
            OpenInBrowser(updater.DownloadPage);
            return;
        }
        Updating = true;
        UpdateError = null;
        try
        {
            // Çeviri sürüyorsa önce durur; uygulama güncellenip yeniden açılır.
            Stop();
            await updater.InstallAsync(new ImmediateProgress(value => post(() => UpdateProgress = value)));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
                                          or UnauthorizedAccessException)
        {
            UpdateError = error.Message;
        }
        finally
        {
            Updating = false;
        }
    }

    // --- Türkçe dublaj ---

    /// <summary>Bu derlemede dublaj var mı (ses motoru uygulamayla geliyor mu).</summary>
    public bool DubbingAvailable => services.Dubbing is not null || services.FastDubbing is not null;
    public bool FastDubbingAvailable => services.FastDubbing is not null;
    /// <summary>Ayarlarda seçili motor; hızlı ses yoksa doğal sese düşer.</summary>
    private IDubbingModel? ActiveDubbing =>
        // The voices are Turkish; another target language has no dubbing.
        !UsesTurkishFeatures(settings) ? null :
        settings.Dubbing.Engine == DubbingEngine.Fast && services.FastDubbing is { } fast ? fast : services.Dubbing;
    public bool DubbingInstalled => ActiveDubbing?.IsInstalled == true;
    public long DubbingDownloadBytes => services.Dubbing?.DownloadBytes ?? 0;
    public bool DubbingDownloading { get => dubbingDownloading; private set => Set(ref dubbingDownloading, value); }
    public double DubbingProgress { get => dubbingProgress; private set => Set(ref dubbingProgress, value); }
    public string? DubbingError { get => dubbingError; private set => Set(ref dubbingError, value); }

    /// <summary>Ses modelini indirir (ilk kullanımda bir kez, ~440 MB).</summary>
    public async Task DownloadDubbingAsync()
    {
        if (services.Dubbing is not { } model || dubbingDownloading || model.IsInstalled) return;
        DubbingDownloading = true;
        DubbingError = null;
        try
        {
            await Task.Run(() => model.DownloadAsync(new ImmediateProgress(value => post(() => DubbingProgress = value)), CancellationToken.None));
            if (settings.Dubbing.Enabled && isRunning) MakeDubber()?.Prepare();
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or TaskCanceledException
                                          or UnauthorizedAccessException)
        {
            DubbingError = error.Message;
        }
        finally
        {
            DubbingDownloading = false;
            Changed(nameof(DubbingInstalled));
        }
    }

    /// <summary>Ayarlardaki "Dinle": sesi bilgisayardan çalar.</summary>
    public async Task PreviewVoiceAsync(DubbingVoice voice)
    {
        if (!DubbingInstalled) return;
        DubbingError = null;
        try { await StartServerAsync(); } catch (SocketException) { return; }
        MakeDubber()?.Preview(voice);
    }

    public void AssignVoice(DubbingVoice voice, string name)
    {
        var cast = settings.Dubbing.Cast.Copy();
        cast.Assign(voice, name);
        Settings = settings with { Dubbing = settings.Dubbing with { Cast = cast } };
    }

    private Dubber? MakeDubber()
    {
        if (dubber is not null) return dubber;
        if (ActiveDubbing is not { } model || services.Audio is not { } audio) return null;
        var made = new Dubber(model, audio, () => server, () => ServerPort, post)
        {
            Cast = settings.Dubbing.Cast.Copy(),
            Output = settings.Dubbing.Output,
        };
        made.CastChanged += cast => Settings = settings with { Dubbing = settings.Dubbing with { Cast = cast } };
        made.Failed += message => DubbingError = message;
        dubber = made;
        return made;
    }

    private void Speak(IReadOnlyList<SpokenLine> lines)
    {
        if (!settings.Dubbing.Enabled || !DubbingInstalled) return;
        MakeDubber()?.Speak(lines);
    }

    private void DubbingSettingsChanged(DubbingSettings old)
    {
        var now = settings.Dubbing;
        if (now.Engine != old.Engine)
        {
            // Motor değişti: eski dublaj kapanır, yenisi hazırlanır.
            StopDubber();
            Changed(nameof(DubbingInstalled));
            if (now.Enabled && isRunning && DubbingInstalled) MakeDubber()?.Prepare();
            return;
        }
        if (dubber is null)
        {
            if (now.Enabled && !old.Enabled && isRunning && DubbingInstalled) MakeDubber()?.Prepare();
            return;
        }
        // Dublajın kendi atadığı ses (CastChanged) zaten dublajda; kullanıcının seçimi kopyalanır.
        if (!ReferenceEquals(now.Cast, old.Cast)) dubber.Cast = now.Cast.Copy();
        if (now.Output != old.Output)
        {
            dubber.Stop();
            dubber.Output = now.Output;
        }
        if (!now.Enabled) StopDubber();
    }

    private void StopDubber()
    {
        if (dubber is not { } stopping) return;
        dubber = null;
        _ = stopping.DisposeAsync().AsTask();
    }

    private AppException Error(AppError error) => new(error, AppException.Describe(error, text));

    /// <summary>Değer değiştiyse yazar ve özelliği (ve ona bağlı olanları) bildirir.</summary>
    private bool Set<T>(ref T field, T value, string[]? also = null, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed([name, .. also ?? []]);
        return true;
    }

    private void Changed(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Model eksikken ya da dil desteklenmezken kullanılan çevirmen: nedeni bildirir.</summary>
    private sealed class UnavailableTranslator(TranslatorStatus status) : ITextTranslator
    {
        public Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default) => throw new TranslatorException(status);
    }

    /// <summary>İlerlemeyi bağlama yakalamadan doğrudan iletir (post zaten arayüze aktarır).</summary>
    private sealed class ImmediateProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

/// <summary>Ek bölgenin tek seferlik çevirisinin sonucu.</summary>
public enum ExtraRegionOutcome { Shown, NoText, NotConfigured, NotRunning, Busy, TranslatorUnavailable, Failed }

/// <summary>"Ekranı çevir"in sonucu; <see cref="Cleared"/>: TV'deki çeviriler kaldırıldı ya da süren iş iptal edildi.</summary>
public enum ScreenOutcome { Shown, NoText, Cleared, NotRunning, TranslatorUnavailable, Failed }

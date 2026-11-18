// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json.Serialization;
using GameTranslator.Core;
using GameTranslator.Core.LgTv;
using GameTranslator.RemotePlay;

namespace GameTranslator.Shell;

public static class StoreKeys
{
    public const string AccountId = "accountId";
    public const string Console = "console";
    public const string Tv = "tv";
    public const string TvKey = "tvKey";
    public const string TvDevKey = "tvDevKey";
    /// <summary>Samsung ve Android TV'ye GameTranslator'ın kurduğu sürüm (LG'de sürüm TV'den okunur).</summary>
    public const string TvAppVersion = "tvAppVersion";
    /// <summary>LG geliştirici modu süresinin son uzatıldığı an.</summary>
    public const string DevSessionRenewed = "devSessionRenewed";
    /// <summary>Kullanıcının DeepL anahtarı (isteğe bağlı çevrimiçi çeviri).</summary>
    public const string DeepLKey = "deepLKey";
    /// <summary>Kullanıcının Google Gemini API anahtarı.</summary>
    public const string GeminiKey = "geminiKey";
    /// <summary>The user's OpenAI API key.</summary>
    public const string OpenAiKey = "openAiKey";
    /// <summary>The user's Claude API key.</summary>
    public const string ClaudeKey = "claudeKey";

    /// <summary>Where a cloud AI service's key is kept; the local model has no stored key.</summary>
    public static string? ForAi(AiProvider provider) => provider switch
    {
        AiProvider.Gemini => GeminiKey,
        AiProvider.OpenAi => OpenAiKey,
        AiProvider.Claude => ClaudeKey,
        _ => null,
    };
}

/// <summary>Kullanıcının seçtiği çeviri motoru/modeli.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TranslationEngine>))]
public enum TranslationEngine
{
    /// <summary>Cihaz üstü donanım hızlandırmalı model (DirectML / NPU / CPU).</summary>
    DirectML,
    /// <summary>Google Gemini 2.5 / 2.0 Flash (Bulut Yapay Zeka).</summary>
    Gemini,
    /// <summary>DeepL REST API servisi.</summary>
    DeepL,
    /// <summary>
    /// The AI service in <see cref="UserSettings.Provider"/> (Gemini, OpenAI or Claude) with the model of
    /// <see cref="UserSettings.Quality"/>. Appended last so settings saved by older versions keep their meaning;
    /// <see cref="Gemini"/> is read as this with the Gemini provider.
    /// </summary>
    Ai
}

/// <summary>Kalıcı ayarlar (şifreli depo değil, sıradan ayar dosyası).</summary>
public sealed record UserSettings
{
    public CropRegion Region { get; init; } = new();
    /// <summary>
    /// Altyazının yeri kendiliğinden bulunur (<see cref="SubtitleFinder"/>); <see cref="Region"/> yalnız kapalıyken
    /// kullanılır. Altyazıyı alışılmadık yerde gösteren oyunlar için kapatılıp alan elle çizilebilir.
    /// </summary>
    public bool AutoRegion { get; init; } = true;
    /// <summary>Aktif çeviri motoru (DirectML, Gemini veya DeepL).</summary>
    public TranslationEngine Engine { get; init; } = TranslationEngine.DirectML;
    /// <summary>Ek bölge 1 (ör. sağ üstteki görev paneli); null ise kapalı. Num 1 tuşuyla bir kez okunup çevrilir.</summary>
    public CropRegion? ExtraRegion1 { get; init; }
    /// <summary>Ek bölge 2 (ör. sağ alttaki bilgi kutusu); null ise kapalı. Num 2 tuşuyla bir kez okunup çevrilir.</summary>
    public CropRegion? ExtraRegion2 { get; init; }
    /// <summary>Ek bölge 3 (ör. ekranın ortası); null ise kapalı. Num 3 tuşuyla bir kez okunup çevrilir.</summary>
    public CropRegion? ExtraRegion3 { get; init; }
    /// <summary>Ek bölgelerin çevirisi nerede gösterilir (TV'de, bilgisayarda ya da ikisinde de).</summary>
    public ResultTarget ExtraTarget1 { get; init; } = ResultTarget.Tv;
    public ResultTarget ExtraTarget2 { get; init; } = ResultTarget.Tv;
    public ResultTarget ExtraTarget3 { get; init; } = ResultTarget.Tv;
    /// <summary>Serbest alan çevirisinin (Num 5) sonucu nerede gösterilir.</summary>
    public ResultTarget FreeAreaTarget { get; init; } = ResultTarget.Tv;
    public SubtitleStyle Style { get; init; } = new();
    public string SourceLanguage { get; init; } = "en";
    /// <summary>The mark this edition writes into the settings file (see <see cref="Edition"/>).</summary>
    public const string ArabicEdition = "ar";
    /// <summary>
    /// Which edition last saved these settings. The original app keeps its settings in the same file and always writes
    /// a Turkish target into it; settings without this edition's mark are moved to Arabic once
    /// (<see cref="ForArabicEdition"/>). null: saved by the original app, or never saved.
    /// </summary>
    public string? Edition { get; init; }

    /// <summary>
    /// These settings as this edition uses them: unmarked settings (the original app's, or a fresh install) get the
    /// Arabic target and the mark; settings already marked are returned unchanged, whatever their target.
    /// </summary>
    public UserSettings ForArabicEdition() =>
        Edition == ArabicEdition ? this : this with { TargetLanguage = TargetLanguages.Arabic, Edition = ArabicEdition };

    /// <summary>The language subtitles are translated into; this fork's default is Arabic.</summary>
    public string TargetLanguage { get; init; } = TargetLanguages.Default;
    /// <summary>The AI service used when <see cref="Engine"/> is <see cref="TranslationEngine.Ai"/> (or the older Gemini value).</summary>
    public AiProvider Provider { get; init; } = AiProvider.Gemini;
    /// <summary>Strongest model of the service, or its fast one.</summary>
    public QualityMode Quality { get; init; } = QualityMode.Best;
    public DubbingSettings Dubbing { get; init; } = new();
    public bool DubbingEnabled
    {
        get => Dubbing.Enabled;
        init => Dubbing = (Dubbing ?? new DubbingSettings()) with { Enabled = value };
    }
    /// <summary>Anahtar girildiyse çeviri DeepL'le yapılır; kapalıysa ya da DeepL'e ulaşılamazsa cihaz üstü model.</summary>
    public bool OnlineTranslation { get; init; }
    /// <summary>
    /// Hiçbir pakette bulunmayan İngilizce oyun satırları ortak havuza gönderilir (anonim, yalnız satır metni ve oyun adı;
    /// bkz. <see cref="LinePool"/>). Yeni oyun paketleri bunlardan hazırlanır. Ayarlardan kapatılabilir.
    /// </summary>
    public bool SharePool { get; init; } = LinePool.EnabledByDefault;
    /// <summary>Görüntü kaynağı: Remote Play (Chiaki arkada gizli), Yakalama Kartı (Capture Card), Masaüstü / Ekran / OBS.</summary>
    public VideoSourceType VideoSource { get; init; } = VideoSourceType.RemotePlay;
    public string? CaptureCardDeviceName { get; init; }
    public string? DesktopWindowName { get; init; }
    public int ScreenIndex { get; init; } = 0;

    /// <summary>Ek bölgenin sayısı; <see cref="ExtraRegion"/> 1..ExtraRegionCount arasında çalışır.</summary>
    public const int ExtraRegionCount = 3;

    /// <summary>1, 2 ya da 3 numaralı ek bölge; kapalıysa null.</summary>
    public CropRegion? ExtraRegion(int slot) => slot switch { 1 => ExtraRegion1, 2 => ExtraRegion2, 3 => ExtraRegion3, _ => null };

    public UserSettings WithExtraRegion(int slot, CropRegion? region) => slot switch
    {
        1 => this with { ExtraRegion1 = region },
        2 => this with { ExtraRegion2 = region },
        3 => this with { ExtraRegion3 = region },
        _ => this,
    };

    /// <summary>Ek bölgenin çevirisinin gösterileceği yer.</summary>
    public ResultTarget ExtraTarget(int slot) => slot switch { 1 => ExtraTarget1, 2 => ExtraTarget2, 3 => ExtraTarget3, _ => ResultTarget.Tv };

    public UserSettings WithExtraTarget(int slot, ResultTarget target) => slot switch
    {
        1 => this with { ExtraTarget1 = target },
        2 => this with { ExtraTarget2 = target },
        3 => this with { ExtraTarget3 = target },
        _ => this,
    };
}

/// <summary>Tek seferlik çevirinin sonucunun gösterileceği yer.</summary>
public enum ResultTarget { Tv, Pc, Both }

/// <summary>Görüntünün alınacağı video giriş kaynağı.</summary>
public enum VideoSourceType
{
    /// <summary>PlayStation 5 Remote Play (Arka planda gizli oturum).</summary>
    RemotePlay = 0,
    /// <summary>HDMI Video Yakalama Kartı (Elgato, Cam Link, USB Video Capture).</summary>
    CaptureCard = 1,
    /// <summary>Masaüstü / OBS Tam Ekran / Pencere Yakalama.</summary>
    DesktopScreen = 2
}

/// <summary>Hesap kimliği, PS5 ve TV anahtarları (gerçekte DPAPI ile şifreli dosya).</summary>
public interface IAppStore
{
    void Save<T>(string key, T value);
    T? Load<T>(string key);
    void Remove(string key);
}

public interface ISettingsStore
{
    UserSettings Load();
    void Save(UserSettings settings);
}

/// <summary>Çeviri sürerken bilgisayarın boşta uykuya geçmesini engeller.</summary>
public interface ISleepGuard
{
    void Begin();
    void End();
}

/// <summary>TV uygulamasını açık tutan görevli (gerçekte TvKeeper).</summary>
public interface IKeeper
{
    void Start(TimeSpan interval);
    void Stop();
    void Reconnect();
}

/// <summary>Cihaz üstü çeviri modeli: kurulu mu, indir, çevirmen oluştur.</summary>
public interface ITranslationModel
{
    bool IsSupported(string source, string target);
    bool IsInstalled(string source, string target);
    Task DownloadAsync(string source, string target, IProgress<double> progress, CancellationToken cancellationToken);
    ITextTranslator Create(string source, string target);
}

/// <summary>Dış dünyayla (PS5, PSN, TV, OCR, çeviri) konuşan her şey; testlerde sahtesi kullanılır.</summary>
public interface IAppServices
{
    Task<IReadOnlyList<ConsoleInfo>> DiscoverConsolesAsync();
    Task<byte[]> AccountIdFromRedirectAsync(Uri redirect);
    Task<RegisteredConsole> RegisterAsync(string host, byte[] accountId, uint pin);
    Task<IReadOnlyList<LgTv>> DiscoverTvsAsync();
    /// <summary>TV ile eşleşir ve TV uygulamasını sunucu adresiyle başlatmayı dener.</summary>
    Task<TvPairing> PairTvAsync(LgTv tv, string? key, string server);
    IStreamSession MakeStream(RegisteredConsole? console, byte[]? accountId, UserSettings? settings = null);
    /// <param name="appConnected">TV uygulaması bilgisayara bağlı mı (Samsung'da bağlıyken TV'ye dokunulmaz).</param>
    /// <param name="onMoved">TV ağda yeni bir adreste bulununca çağrılır.</param>
    /// <param name="onKey">TV bağlantıda yeni jeton verdi (Samsung); saklanır.</param>
    IKeeper MakeKeeper(LgTv tv, string? key, IReadOnlyDictionary<string, string> parameters, Func<bool> appConnected, Action<LgTv> onMoved,
                       Action<string> onKey);
    /// <summary>Uygulamayla gelen TV paketinin sürümü (geliştirici derlemesinde paket olmayabilir).</summary>
    string? BundledTvAppVersion { get; }
    Task<bool> IsDevModeOnAsync(string host);
    Task<string> FetchDevKeyAsync(string host, string passphrase);
    Task<string?> InstalledTvAppVersionAsync(string host, string devKey);
    Task InstallTvAppAsync(string host, string devKey);
    /// <summary>LG geliştirici modunun süresini baştan başlatır; kalan saati döner (bilinmiyorsa null).</summary>
    Task<int?> RenewDevSessionAsync(string host, string devKey);
    /// <summary>Samsung ve Android TV için uygulamayla gelen paket (geliştirici derlemesinde olmayabilir).</summary>
    BundledTvApp? BundledApp(TvBrand brand);
    Task<bool> IsSamsungDevModeOnAsync(string host);
    Task InstallSamsungAppAsync(string host, BundledTvApp app, Action<TvInstallEvent> onEvent);
    Task<bool> IsAndroidDebugOnAsync(string host);
    /// <summary>Ağda ADB'si açık cihazlar.</summary>
    Task<IReadOnlyList<string>> ScanAndroidTvsAsync();
    /// <summary>Eksikse ya da eskiyse kurar; TV'deki sürümü döner.</summary>
    Task<string> InstallAndroidAppAsync(string host, BundledTvApp app, Action<TvInstallEvent> onEvent);
    /// <summary>Oyun dili için yazı tanıyıcı; bilgisayarda hiç OCR dili yoksa null.</summary>
    ITextReader? MakeReader(string language);
    /// <summary>
    /// Altyazı şeridini okuyan tanıyıcı. Varsayılanı <see cref="MakeReader"/>; uygulama, güçlü okuyucunun modeli
    /// kuruluysa hızlı okuyucuyu onunla birleştirir (<see cref="HybridReader"/>). Karenin tamamını tarayan işler
    /// (altyazı yerini bulma, bölge çevirisi) hızlı okuyucuda kalır.
    /// </summary>
    ITextReader? MakeSubtitleReader(string language) => MakeReader(language);
    /// <summary>
    /// Oyun dilinin kendi yazı tanıma paketi yüklü mü. Değilse okuyucu sistemin diliyle (ör. Türkçe) okur ve İngilizce
    /// altyazı "thıs.messs" gibi bozulur; kullanıcıya paketi yüklemesi söylenir.
    /// </summary>
    bool HasOcrLanguage(string language) => true;
    ITranslationModel Translation { get; }
    /// <summary>DeepL çevirmeni; <paramref name="fallback"/> DeepL'e ulaşılamayan satırları çevirir. Bu derlemede yoksa null.</summary>
    ITextTranslator? MakeOnlineTranslator(string key, ITextTranslator? fallback) => null;
    Task<OnlineKeyCheck> CheckOnlineKeyAsync(string key) => Task.FromResult(new OnlineKeyCheck(OnlineKeyState.Unreachable));
    /// <summary>Google Gemini çevirmeni; <paramref name="fallback"/> Gemini'ye ulaşılamayan satırları çevirir.</summary>
    ITextTranslator? MakeGeminiTranslator(string key, ITextTranslator? fallback) => null;
    /// <summary>
    /// AI translator for a service, model and target language; <paramref name="fallback"/> translates the lines the
    /// service could not. Null when this build has none.
    /// </summary>
    ITextTranslator? MakeAiTranslator(AiProvider provider, string key, string modelId, string target, ITextTranslator? fallback) => null;
    /// <summary>Tries the key with one sample line in the target language, on the model that will translate.</summary>
    Task<OnlineKeyCheck> CheckAiKeyAsync(AiProvider provider, string key, string modelId, string target) =>
        Task.FromResult(new OnlineKeyCheck(OnlineKeyState.Unreachable));
    Task<OnlineKeyCheck> CheckGeminiKeyAsync(string key) => Task.FromResult(new OnlineKeyCheck(OnlineKeyState.Unreachable));
    /// <summary>Türkçe dublaj modeli; bu derlemede yoksa null.</summary>
    IDubbingModel? Dubbing => null;
    /// <summary>Hızlı dublaj: sistemin kendi Türkçe sesi (Windows'ta Microsoft Tolga); yoksa null.</summary>
    IDubbingModel? FastDubbing => null;
    /// <summary>Bilgisayardan ses çalma (dublaj).</summary>
    IAudioOutput? Audio => null;
    /// <summary>Uygulama güncellemesi; geliştirici derlemesinde null.</summary>
    IUpdater? Updater => null;
    /// <summary>Windows Güvenlik Duvarı izni; Windows dışında ve testlerde null (hiçbir şey önerilmez).</summary>
    IFirewall? Firewall => null;
    /// <summary>Ortak havuz (CouchSubs'ın havuz sunucusu); testlerde null (hiçbir şey gönderilmez).</summary>
    LinePool? Pool => null;
    /// <summary>Altyazı geçmişinin dosyası (%LOCALAPPDATA%\GameTranslator\history.json); testlerde null (diske yazılmaz).</summary>
    string? HistoryFile => null;
}

/// <param name="Launched">false ise TV uygulaması açılamadı; büyük olasılıkla TV'de kurulu değil.</param>
public sealed record TvPairing(string Key, bool Launched);

/// <param name="Path">Paket dosyası.</param>
public sealed record BundledTvApp(string Path, string Version);

public enum TvInstallEventKind { SignIn, ApproveOnTv, Progress }

/// <summary>Samsung ve Android TV kurulumu sırasında kullanıcıdan bir şey isteyen olay.</summary>
public sealed record TvInstallEvent(TvInstallEventKind Kind, Uri? Url = null);

/// <summary>
/// SignIn: Samsung sertifikası için tarayıcıda Samsung hesabıyla giriş bekleniyor. ApproveOnTv: Android TV ekranında
/// hata ayıklama izni bekleniyor.
/// </summary>
public enum TvAppKind { Unknown, Working, UpToDate, DevModeOff, NeedsPassphrase, Failed, SignIn, ApproveOnTv }

/// <summary>TV uygulamasının kurulum durumu.</summary>
public sealed record TvAppState(TvAppKind Kind, string? Version = null, DevModeError? Error = null, Uri? Url = null, string? Reason = null)
{
    public static readonly TvAppState Unknown = new(TvAppKind.Unknown);
}

/// <summary>Durum noktasının rengi: bağlı (yeşil), sorun var (turuncu), boşta (gri).</summary>
public enum StatusTone { Idle, Warning, Good }

public enum OnboardingStep { Welcome, SecondAccount, PsnLogin, Console, Tv, Translation, Test, Done }

public enum AppError { InvalidPin, InvalidLogin, MissingAccount, WrongAccount, RegistrationFailed, ConsoleNoAnswer, ConsoleInUse, ConsoleCrashed, ConsoleVersion }

/// <summary>Kullanıcıya gösterilecek, ne yapması gerektiğini söyleyen hata.</summary>
public sealed class AppException(AppError error, string message) : Exception(message)
{
    public AppError Error { get; } = error;

    public static string Describe(AppError error, Localizer text) => text[error switch
    {
        AppError.InvalidPin => "The code must be 8 digits.",
        AppError.InvalidLogin => "Sign-in did not complete. Please try again.",
        AppError.MissingAccount => "Sign in to PlayStation Network first.",
        AppError.WrongAccount => "The PS5 did not recognise this account. Get the code from the PS5 user signed in with the account you used in GameTranslator, or sign in again with that user's account.",
        AppError.ConsoleNoAnswer => "The PS5 did not answer. Make sure it is on (not in rest mode), on the same network as this computer, and that Remote Play is turned on, then try again.",
        AppError.ConsoleInUse => "Remote Play is in use on another device. Close it there and enter a new code.",
        AppError.ConsoleCrashed => "The PS5 Remote Play service stopped unexpectedly. Restart the PS5 and enter a new code.",
        AppError.ConsoleVersion => "This PS5 system version is not supported yet. Update GameTranslator and try again.",
        _ => "The PS5 refused the code. Open Pair Device again on the PS5 and enter the new code.",
    }];
}

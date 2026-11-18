// SPDX-License-Identifier: AGPL-3.0-only

using System.IO;
using System.Net.Http;
using System.Text.Json;
using GameTranslator.Core;
using GameTranslator.Core.LgTv;
using GameTranslator.RemotePlay;
using GameTranslator.Shell;
using GameTranslator.Translation;
using GameTranslator.TvInstall.Android;
using GameTranslator.TvInstall.Samsung;

namespace GameTranslator.App.Services;

/// <summary>Uygulama denetleyicisinin gerçek dünya bağlantıları (PS5, PSN, TV, OCR, çeviri).</summary>
public sealed class LiveServices : IAppServices
{
    public ITranslationModel Translation { get; } = new MarianModel();

    public ITextTranslator? MakeOnlineTranslator(string key, ITextTranslator? fallback) => new DeepLTranslator(key, fallback);

    public Task<OnlineKeyCheck> CheckOnlineKeyAsync(string key) => DeepLTranslator.CheckAsync(key);

    public ITextTranslator? MakeGeminiTranslator(string key, ITextTranslator? fallback) =>
        new AiTranslator(AiProvider.Gemini, key, model: null, fallback, game: () => CommunityPatchManager.Shared.PlayedGameTitle);

    public async Task<OnlineKeyCheck> CheckGeminiKeyAsync(string key)
    {
        var result = await AiTranslator.TestAsync(AiProvider.Gemini, key, model: null);
        return new OnlineKeyCheck(result.State);
    }
    public ITextTranslator? MakeAiTranslator(AiProvider provider, string key, string modelId, string target, ITextTranslator? fallback) =>
        new AiTranslator(provider, key, modelId, fallback,
                         cache: TranslationCache.Shared(Path.Combine(AppPaths.Data, "cache"), provider.EngineId(modelId), "en", TargetLanguages.Normalize(target)),
                         game: () => CommunityPatchManager.Shared.PlayedGameTitle, target: target);

    public async Task<OnlineKeyCheck> CheckAiKeyAsync(AiProvider provider, string key, string target)
    {
        // The key is tried with the fast model: a key that works there works for the service's other models.
        var result = await AiTranslator.TestAsync(provider, key, model: null, target: target);
        return new OnlineKeyCheck(result.State);
    }

    public IDubbingModel? Dubbing { get; } = PocketDubbingModel.EngineBundled ? new PocketDubbingModel() : null;
    public IDubbingModel? FastDubbing { get; } = WindowsVoiceModel.Available ? new WindowsVoiceModel() : null;
    public IAudioOutput? Audio { get; } = new WindowsAudioOutput();
    public IUpdater? Updater { get; } = new Updater();
    public IFirewall? Firewall { get; } = new WindowsFirewall();
    public string? HistoryFile { get; } = Path.Combine(AppPaths.Data, "history.json");
    public LinePool? Pool { get; } = new(Path.Combine(AppPaths.Data, "pool.json"),
        "GameTranslator " + AppVersion.Current(System.Reflection.Assembly.GetEntryAssembly() ?? typeof(LiveServices).Assembly));

    public Task<IReadOnlyList<ConsoleInfo>> DiscoverConsolesAsync() => ConsoleDiscovery.DiscoverAsync();

    public async Task<byte[]> AccountIdFromRedirectAsync(Uri redirect)
    {
        var code = PsnAccount.CodeFromRedirect(redirect)
                   ?? throw new AppException(AppError.InvalidLogin, AppException.Describe(AppError.InvalidLogin, Localizer.Current));
        return await PsnAccount.AccountIdAsync(code);
    }

    public Task<RegisteredConsole> RegisterAsync(string host, byte[] accountId, uint pin) => Registration.RegisterAsync(host, accountId, pin);

    public async Task<IReadOnlyList<LgTv>> DiscoverTvsAsync()
    {
        var lg = LgDiscovery.SearchAsync();
        var samsung = SamsungDiscovery.SearchAsync();
        return [.. await lg, .. await samsung];
    }

    public async Task<TvPairing> PairTvAsync(LgTv tv, string? key, string server)
    {
        // Android TV: Doğrudan ADB üzerinden TV uygulamasını PC sunucu adresi parametresiyle başlat
        if (tv.Brand == TvBrand.AndroidTv)
        {
            var host = tv.Host;
            try
            {
                if (string.IsNullOrEmpty(host) || !await AndroidTvInstaller.IsReachableAsync(host, timeout: TimeSpan.FromMilliseconds(800)))
                {
                    var found = await AndroidTvInstaller.ScanAsync();
                    if (found.Count > 0) host = found[0];
                }
                if (!string.IsNullOrEmpty(host))
                {
                    var installer = new AndroidTvInstaller(host, DataDirectory("adb"));
                    await installer.StartAppWithServerAsync(server);
                }
            }
            catch { }
            return new TvPairing("", true);
        }
        if (tv.Brand == TvBrand.Samsung)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var samsung = new SamsungClient(tv, key);
                var token = await samsung.ConnectAsync(cts.Token);
                try { await samsung.LaunchAsync(SamsungClient.AppId, new Dictionary<string, string> { ["server"] = server }); }
                catch (Exception) { /* TV uygulaması bilgisayarı kendisi de bulabilir */ }
                return new TvPairing(token, true);
            }
            catch (Exception)
            {
                // TV kapalı, IP farklı veya kumanda izni henüz verilmemiş olsa bile
                // TV uygulamasının (veya tarayıcının) bilgisayara bağlanabilmesi için eşleşmeyi tamamla
                return new TvPairing(key ?? "", false);
            }
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new SsapClient(tv, key);
            var newKey = await client.ConnectAsync(cts.Token);
            try
            {
                await client.LaunchAsync(AppController.TvAppId, new Dictionary<string, string> { ["server"] = server });
                return new TvPairing(newKey, true);
            }
            catch (Exception)
            {
                return new TvPairing(newKey, false);
            }
        }
        catch (Exception)
        {
            return new TvPairing(key ?? "", false);
        }
    }

    /// <summary>Geliştirme aracı (<see cref="Replay"/>): doluysa görüntü PS5 yerine buradan gelir.</summary>
    public static Func<IStreamSession>? ReplayStream { get; set; }

    public IStreamSession MakeStream(RegisteredConsole? console, byte[]? accountId, UserSettings? settings = null)
    {
        if (ReplayStream is { } replay) return replay();

        if (settings?.VideoSource == VideoSourceType.DesktopScreen || settings?.VideoSource == VideoSourceType.CaptureCard)
        {
            return new DesktopCaptureStreamSession(settings?.DesktopWindowName, settings?.ScreenIndex ?? 0);
        }

        if (StreamSession.IsNativeAvailable && console != null && accountId != null)
        {
            return new StreamSession(console, accountId);
        }

        var cons = console ?? new RegisteredConsole("127.0.0.1", "PS5", accountId ?? new byte[16], new byte[16]);
        return new WindowCaptureStreamSession(cons, accountId);
    }

    public IKeeper MakeKeeper(LgTv tv, string? key, IReadOnlyDictionary<string, string> parameters, Func<bool> appConnected, Action<LgTv> onMoved,
                       Action<string> onKey)
    {
        if (tv.Brand == TvBrand.AndroidTv)
        {
            var serverUrl = parameters.TryGetValue("server", out var s) ? s : "http://192.168.1.100:8787";
            return new AndroidTvKeeper(tv, serverUrl, DataDirectory("adb"), appConnected, onMoved);
        }
        if (tv.Brand == TvBrand.Samsung)
        {
            // TV bağlantıda yeni jeton verirse sonraki bağlantılar onu kullanır ve saklanır (TV her seferinde izin sormasın).
            var token = new SamsungTokenHolder(key, onKey);
            var samsungLocator = new TvLocator(tv, async target =>
            {
                var client = new SamsungClient(target, token.Current);
                try
                {
                    token.Received(await client.ConnectAsync());
                    return client;
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }, () => SamsungDiscovery.SearchAsync(), onMoved);
            return new Keeper(new TvKeeper(samsungLocator.ConnectAsync, SamsungClient.AppId, parameters, appConnected));
        }
        var locator = new TvLocator(tv, async target =>
        {
            var client = new SsapClient(target, key);
            try
            {
                await client.ConnectAsync();
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }, () => LgDiscovery.SearchAsync(), onMoved);
        return new Keeper(new TvKeeper(locator.ConnectAsync, AppController.TvAppId, parameters));
    }

    // --- TV uygulamasını kurma ---

    private static string? BundledTvPackage
    {
        get
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "tv-app");
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.ipk").FirstOrDefault() : null;
        }
    }

    public string? BundledTvAppVersion =>
        BundledTvPackage is { } package ? DevModeInstaller.VersionOfPackage(Path.GetFileName(package)) : null;

    public Task<bool> IsDevModeOnAsync(string host) => DevModeInstaller.IsDevModeOnAsync(host);

    public Task<string> FetchDevKeyAsync(string host, string passphrase) =>
        DevModeInstaller.FetchKeyAsync(host, passphrase, new ProcessRunner(), DevModeTools.ForThisSystem());

    public Task<string?> InstalledTvAppVersionAsync(string host, string devKey) =>
        WithKeyFileAsync(devKey, file => Installer(host, file).InstalledVersionAsync());

    public async Task InstallTvAppAsync(string host, string devKey)
    {
        try
        {
            await WithKeyFileAsync<object?>(devKey, async file =>
            {
                if (BundledTvPackage is not { } package || BundledTvAppVersion is not { } version)
                    throw new DevModeException(DevModeError.InstallFailed, "uygulamanın yanında LG paketi (tv-app/*.ipk) yok");
                await Installer(host, file).InstallAsync(package, version);
                return null;
            });
        }
        catch (DevModeException error)
        {
            StartupGuard.Log("LG TV kurulumu", error);
            throw;
        }
    }

    public async Task<int?> RenewDevSessionAsync(string host, string devKey)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            return await WithKeyFileAsync(devKey, file => Installer(host, file).RenewSessionAsync(http));
        }
        catch (DevModeException)
        {
            return null;
        }
    }

    // --- Samsung ve Android TV (GameTranslator.TvInstall) ---

    /// <summary>Kalıcı dosyalar: ADB anahtarı ve Samsung sertifikaları.</summary>
    private static string DataDirectory(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameTranslator", name);

    public BundledTvApp? BundledApp(TvBrand brand)
    {
        var pattern = brand switch { TvBrand.Samsung => "*.wgt", TvBrand.AndroidTv => "*.apk", _ => null };
        if (pattern is null) return null;
        var folder = Path.Combine(AppContext.BaseDirectory, "tv-app");
        string? file = null;
        if (Directory.Exists(folder))
        {
            file = Directory.EnumerateFiles(folder, pattern).FirstOrDefault();
        }
        if (file is null)
        {
            var fallback = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tv", "packages");
            if (Directory.Exists(fallback))
            {
                file = Directory.EnumerateFiles(fallback, pattern).FirstOrDefault();
            }
        }
        return file is not null && (TvInstall.Versions.OfBundledFile(file) ?? "1.0.0") is { } version ? new BundledTvApp(file, version) : null;
    }

    public Task<bool> IsSamsungDevModeOnAsync(string host) => SamsungTvInstaller.IsDevModeOnAsync(host);

    public async Task InstallSamsungAppAsync(string host, BundledTvApp app, Action<TvInstallEvent> onEvent)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
        var installer = new SamsungTvInstaller(host, DataDirectory("samsung"), new SamsungCertificates(http))
        {
            Progress = _ => onEvent(new TvInstallEvent(TvInstallEventKind.Progress)),
            SignIn = token => SamsungLogin.SignInAsync(url => onEvent(new TvInstallEvent(TvInstallEventKind.SignIn, new Uri(url))),
                TimeSpan.FromMinutes(10), token),
        };
        try
        {
            await Task.Run(() => installer.InstallAsync(app.Path));
        }
        catch (SamsungInstallException error)
        {
            StartupGuard.Log("Samsung TV kurulumu", error);
            throw new DevModeException(error.Error switch
            {
                SamsungInstallError.Unreachable => DevModeError.Unreachable,
                SamsungInstallError.LoginFailed => DevModeError.SignInFailed,
                SamsungInstallError.CertificateFailed => DevModeError.CertificateFailed,
                SamsungInstallError.TooOld => DevModeError.TooOld,
                _ => DevModeError.InstallFailed,
            }, error.Message);
        }
        catch (Exception error) when (error is not DevModeException)
        {
            StartupGuard.Log("TV kurulumu", error);
            throw new DevModeException(DevModeError.InstallFailed, error.Message);
        }
    }

    public Task<bool> IsAndroidDebugOnAsync(string host) => AndroidTvInstaller.IsReachableAsync(host);

    public Task<IReadOnlyList<string>> ScanAndroidTvsAsync() => AndroidTvInstaller.ScanAsync();

    public async Task<string> InstallAndroidAppAsync(string host, BundledTvApp app, Action<TvInstallEvent> onEvent)
    {
        var installer = new AndroidTvInstaller(host, DataDirectory("adb"))
        {
            OnApprovalNeeded = () => onEvent(new TvInstallEvent(TvInstallEventKind.ApproveOnTv)),
        };
        try
        {
            var (version, _) = await Task.Run(() => installer.EnsureInstalledAsync(app.Path, app.Version));
            return version;
        }
        catch (AndroidInstallException error)
        {
            StartupGuard.Log("Android TV kurulumu", error);
            throw new DevModeException(error.Error switch
            {
                AndroidInstallError.Unreachable => DevModeError.Unreachable,
                AndroidInstallError.NotApproved => DevModeError.NotApproved,
                _ => DevModeError.InstallFailed,
            }, error.Message);
        }
        catch (Exception error) when (error is not DevModeException)
        {
            StartupGuard.Log("TV kurulumu", error);
            throw new DevModeException(DevModeError.InstallFailed, error.Message);
        }
    }

    private static DevModeInstaller Installer(string host, string keyFile) => DevModeInstaller.ForTv(host, keyFile);

    /// <summary>ssh anahtarı dosyadan okur; dosya yalnız komut süresince durur.</summary>
    private static async Task<T> WithKeyFileAsync<T>(string key, Func<string, Task<T>> body)
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(file, key);
        try
        {
            return await body(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public ITextReader? MakeReader(string language) => WindowsOcrReader.Create(language);

    /// <summary>PP-OCR modelinin klasörü (%LOCALAPPDATA%\GameTranslator\models\ppocr).</summary>
    private static readonly string PpOcrDirectory = Path.Combine(AppPaths.Models, "ppocr");
    private static readonly object PpOcrGate = new();
    private static PpOcrReader? ppOcr;
    private static bool ppOcrEnglish;
    private static bool ppOcrFailed;

    /// <summary>
    /// Altyazı okuyucusu: Windows'un yazı tanıması, PP-OCR modeli kuruluysa onunla birlikte (bozuk okunan satır güçlü
    /// okuyucuyla yeniden okunur). Model yoksa ya da açılamazsa yalnız Windows okuyucu. Model oturumları uygulama
    /// boyunca tek örnektir ve okuma hatları arasında paylaşılır (eşzamanlı çağrıya dayanıklıdır).
    /// </summary>
    public ITextReader? MakeSubtitleReader(string language)
    {
        var fast = WindowsOcrReader.Create(language);
        if (fast is null || PpOcrShared(language) is not { } strong) return fast;
        return new HybridReader(fast, strong)
        {
            LooksMisread = text => SubtitlePipeline.ExtraMisreadCheck?.Invoke(text) ?? false,
            Score = text => (SubtitlePipeline.ExtraReadingScore ?? OcrNoise.Score)(text),
            Repair = text => OcrConfusions.Repair(text, SubtitlePipeline.ExtraWordCheck),
            Log = SubtitleLog.Write,
        };
    }

    /// <summary>
    /// Güçlü okuyucunun modelleri (PP-OCR, ~24 MB) yoksa uygulama açılışında arka planda indirilir; dosyalar boyut ve
    /// SHA-256 ile doğrulanır (<see cref="ModelStore"/>). İndirme bitince sonraki "Çeviriyi Başlat"ta kullanılır. İnternet
    /// yoksa ya da indirme başarısız olursa uygulama yalnız Windows'un okuyucusuyla çalışır; sonraki açılışta yeniden denenir.
    /// </summary>
    /// <summary>
    /// Oyun paketlerinin yeni ve düzeltilmiş sürümleri paket sunucusundan (CouchSubs'ınki, izinle) iner: haftada bir
    /// bakılır, SHA-256 ile doğrulanır, kullanıcının paket klasörüne konur ve paketler yeniden okunur. Adres yoksa kapalı.
    /// </summary>
    public static void RefreshPacksInBackground()
    {
        if (PackUpdater.ConfiguredBaseUrl() is not { } url) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var bundled = GamePackLibrary.EmbeddedPacks().ToDictionary(pack => GamePackLibrary.BaseName(pack.Name),
                    pack => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pack.Data)));
                using var updater = new PackUpdater(url, GamePackLibrary.UserDirectory, Path.Combine(AppPaths.Data, "packs-remote.json"))
                {
                    BundledSha256 = id => bundled.GetValueOrDefault(id),
                };
                var result = await updater.RefreshAsync().ConfigureAwait(false);
                if (!result.Changed) return;
                CommunityPatchManager.Shared.Reload();
                SubtitleLog.Write("paketler güncellendi: " + string.Join(", ", result.Downloaded.Concat(result.Removed)));
            }
            catch (Exception error)
            {
                SubtitleLog.Write($"paketler güncellenemedi ({error.GetType().Name}); sonraki açılışta yeniden denenir");
            }
        });
    }

    public static void DownloadReaderModelsInBackground()
    {
        var store = new ModelStore(PpOcrDirectory, [.. PpOcrReader.Files, PpOcrReader.EnglishFile]);
        if (store.IsInstalled) return;
        _ = Task.Run(async () =>
        {
            try
            {
                SubtitleLog.Write("güçlü okuyucu: modeller indiriliyor");
                await store.DownloadAsync(null, CancellationToken.None);
                SubtitleLog.Write("güçlü okuyucu: modeller indirildi");
            }
            catch (Exception error)
            {
                SubtitleLog.Write($"güçlü okuyucu: modeller indirilemedi ({error.GetType().Name})");
            }
        });
    }

    private static PpOcrReader? PpOcrShared(string language)
    {
        lock (PpOcrGate)
        {
            // Oyun dili İngilizceye (ya da İngilizceden) değiştiyse okuyucu ek modelle (ya da onsuz) yeniden kurulur.
            // Ölçüm için (replay): güçlü okuyucu olmadan çalıştırma.
            if (Environment.GetEnvironmentVariable("GAMETRANSLATOR_NO_PPOCR") == "1") return null;
            var english = PpOcrReader.HasEnglish(PpOcrDirectory, language);
            if (ppOcr is not null && ppOcrEnglish == english) return ppOcr;
            if (ppOcrFailed || !PpOcrReader.IsInstalled(PpOcrDirectory)) return ppOcr;
            try
            {
                // Eski okuyucu kapatılmaz: çalışan bir okuma hattı onu hâlâ kullanıyor olabilir.
                ppOcr = new PpOcrReader(PpOcrDirectory, language)
                {
                    Score = text => (SubtitlePipeline.ExtraReadingScore ?? OcrNoise.Score)(text),
                };
                ppOcrEnglish = english;
                SubtitleLog.Write("güçlü okuyucu: PP-OCR modeli yüklendi" + (ppOcr.UsesEnglish ? " (İngilizce tanıma modeliyle)" : ""));
            }
            catch (Exception error)
            {
                // Model açılamadıysa (bozuk dosya, yerel kitaplık) her seferinde yeniden denenmez.
                ppOcrFailed = true;
                SubtitleLog.Write($"güçlü okuyucu: PP-OCR modeli açılamadı ({error.GetType().Name})");
            }
            return ppOcr;
        }
    }
    public bool HasOcrLanguage(string language) => WindowsOcrReader.HasLanguage(language);

    /// <summary>Android TV: TV açıkken ADB üzerinden uygulamanın açık ve PC'ye bağlı kalmasını sağlar. IP değişirse dinamik olarak ağda bulur.</summary>
    private sealed class AndroidTvKeeper(LgTv tv, string serverUrl, string keyDir, Func<bool> appConnected, Action<LgTv>? onMoved = null) : IKeeper
    {
        private CancellationTokenSource? cts;
        private string host = tv.Host;

        public void Start(TimeSpan interval)
        {
            cts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                var token = cts.Token;
                while (!token.IsCancellationRequested)
                {
                    if (!appConnected())
                    {
                        var connected = false;
                        if (host.Length > 0 && await AndroidTvInstaller.IsReachableAsync(host, timeout: TimeSpan.FromMilliseconds(800)))
                        {
                            try
                            {
                                var installer = new AndroidTvInstaller(host, keyDir);
                                await installer.StartAppWithServerAsync(serverUrl, token);
                                connected = true;
                            }
                            catch { }
                        }

                        // Kayıtlı statik IP'ye ulaşılamadıysa ağda dinamik tarama yapıp TV'nin yeni IP'sini bul!
                        if (!connected && !token.IsCancellationRequested)
                        {
                            try
                            {
                                var found = await AndroidTvInstaller.ScanAsync(cancellationToken: token);
                                if (found.Count > 0)
                                {
                                    var newHost = found[0];
                                    if (newHost != host)
                                    {
                                        host = newHost;
                                        onMoved?.Invoke(tv with { Host = newHost });
                                    }
                                    var installer = new AndroidTvInstaller(newHost, keyDir);
                                    await installer.StartAppWithServerAsync(serverUrl, token);
                                }
                            }
                            catch { }
                        }
                    }
                    try { await Task.Delay(interval, token); } catch { break; }
                }
            });
        }

        public void Stop()
        {
            cts?.Cancel();
            cts = null;
        }

        public void Reconnect()
        {
            if (!appConnected())
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (host.Length > 0 && await AndroidTvInstaller.IsReachableAsync(host))
                        {
                            var installer = new AndroidTvInstaller(host, keyDir);
                            await installer.StartAppWithServerAsync(serverUrl);
                            return;
                        }

                        var found = await AndroidTvInstaller.ScanAsync();
                        if (found.Count > 0)
                        {
                            var newHost = found[0];
                            if (newHost != host)
                            {
                                host = newHost;
                                onMoved?.Invoke(tv with { Host = newHost });
                            }
                            var installer = new AndroidTvInstaller(newHost, keyDir);
                            await installer.StartAppWithServerAsync(serverUrl);
                        }
                    }
                    catch { }
                });
            }
        }
    }

    /// <summary>Denetlenemeyen TV'ler için: TV uygulaması kendisi bağlanır.</summary>
    private sealed class IdleKeeper : IKeeper
    {
        public void Start(TimeSpan interval) { }
        public void Stop() { }
        public void Reconnect() { }
    }

    private sealed class Keeper(TvKeeper keeper) : IKeeper
    {
        public void Start(TimeSpan interval) => keeper.Start(interval);
        public void Stop() => keeper.Stop();
        public void Reconnect() => keeper.Reconnect();
    }

    /// <summary>OPUS-MT modelleri ve Google GTX çeviri motoru hibriti.</summary>
    private sealed class MarianModel : ITranslationModel
    {
        private readonly ModelStore englishTurkish = new(Path.Combine(AppPaths.Models, "en-tr"));
        private MarianTranslator? translator;

        public bool IsSupported(string source, string target) => true;

        // Yerel Marian modeli indirilmemiş olsa bile dahili Google GTX motoru sıfır kurulumla hazırdır.
        public bool IsInstalled(string source, string target) => true;

        public Task DownloadAsync(string source, string target, IProgress<double> progress, CancellationToken cancellationToken) =>
            englishTurkish.DownloadAsync(progress, cancellationToken);

        // Yerel model indirilmişse Marian motorunu kullan; indirilmemişse anında Google GTX motoruna dön.
        public ITextTranslator Create(string source, string target)
        {
            // The on-device model is English→Turkish; any other target is translated by the built-in Google GTX engine.
            if (!TargetLanguages.IsTurkish(target)) return new GoogleGtxTranslator(source, TargetLanguages.Normalize(target));
            if (englishTurkish.IsInstalled)
            {
                if (translator is not null) return translator;
                translator = new MarianTranslator(englishTurkish.InstallDirectory);
                _ = translator.WarmUpAsync();
                return translator;
            }
            return new GoogleGtxTranslator(source, target);
        }
    }
}

/// <summary>Şifreli depo (hesap, PS5, TV anahtarları).</summary>
public sealed class DpapiAppStore : IAppStore
{
    public void Save<T>(string key, T value) => SecureStore.Default.Save(key, value);
    public T? Load<T>(string key) => SecureStore.Default.Load<T>(key);
    public void Remove(string key) => SecureStore.Default.Remove(key);
}

/// <summary>Sıradan ayarlar: %LOCALAPPDATA%\GameTranslator\settings.json.</summary>
public sealed class FileSettingsStore : ISettingsStore
{
    private static readonly string FilePath = Path.Combine(AppPaths.Data, "settings.json");

    public UserSettings Load()
    {
        try { return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new UserSettings(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return new UserSettings(); }
    }

    public void Save(UserSettings settings)
    {
        Directory.CreateDirectory(AppPaths.Data);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings));
    }
}

public sealed class WindowsSleepGuard : ISleepGuard
{
    public void Begin() => SleepGuard.Begin();
    public void End() => SleepGuard.End();
}

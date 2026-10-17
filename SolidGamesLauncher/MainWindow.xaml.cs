using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.IO.Compression;
using System.Net.Http;
using DiscordRPC;

namespace Arma3Launcher
{
    public partial class MainWindow : Window
    {
        // ================= ССЫЛКИ И НАСТРОЙКИ SOLIDGAMES =================
        private const string SgZoneSiteUrl = "https://sg.zone/";
        private const string SolidStatsSiteUrl = "https://solid-stats.web.app/";
        private const string AuthorProfileUrl = "https://sg.zone/profile/Natrii";
        private const string ResilioReadOnlyKey = "BAORERSOCOOW764LYFEVHPMOHUV5FW34M";
        private const string ResilioOfficialDownloadUrl = "https://download-cdn.resilio.com/stable/windows/x64/0/Resilio-Sync_x64.exe";
        private const string TeamSpeakDownloadUrl = "https://teamspeak.com/en/thanks-for-downloading-teamspeak?file=https%3A%2F%2Ffiles.teamspeak-services.com%2Freleases%2Fclient%2F3.6.2%2FTeamSpeak3-Client-win64-3.6.2.exe#";
        private const string TeamSpeakSgZoneZipUrl = "https://sg.zone/files/teamspeak-3.6.1.zip";
        private const string TeamSpeakPluginDirectUrl = "https://sg.zone/files/task_force_radio.ts3_plugin";
        private const string SteamWorkshopCollectionUrl = "https://steamcommunity.com/sharedfiles/filedetails/?id=2381167992";
        private const string DefaultSystemMallocText = "Системный (по умолчанию)";

        private const double BaseWindowWidth = 1060.0;
        private const double BaseWindowHeight = 680.0;

        private string _arma3Path = string.Empty;
        private string _resilioExePath = string.Empty;
        private string _resilioModsPath = string.Empty;
        private bool _isLoadingSettings = true;
        private bool _isSgZoneWebViewInit = false;
        private bool _isStatsWebViewInit = false;

        private readonly DispatcherTimer _resilioCheckTimer;
        private readonly DispatcherTimer _serverStatusTimer;

        // Настройки автообновления (укажите ваш логин GitHub и название репозитория)
        private static string CurrentLauncherVersion =>
    System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        private const string GitHubRepoUrl = "https://api.github.com/repos/Attache23/SolidGamesLauncher/releases/latest";
        private string _latestReleaseUrl = "https://github.com/Attache23/SolidGamesLauncher/releases";

        // Discord Rich Presence (вставьте Application ID вашего приложения из Discord Developer Portal)
        private const string DiscordAppId = "1560996176751362128";
        private DiscordRpcClient? _discordClient;
        // Все служебные файлы и кэш WebView2 хранятся в AppData\Local\SolidGamesLauncher,
        private static readonly string AppDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SolidGamesLauncher");

        private readonly string _settingsFilePath = Path.Combine(AppDataDirectory, "launcher_settings.json");
        private readonly string _webViewCacheDir = Path.Combine(AppDataDirectory, "WebView2Cache");

        // СТРОГИЙ ПОРЯДОК МОДОВ ИЗ STEAM WORKSHOP
        private readonly List<(string FolderName, string WorkshopId)> _steamWorkshopMods = new()
        {
            ("@RHSAFRF", "843425103"),
            ("@RHSUSAF", "843577117"),
            ("@RHSGREF", "843593391"),
            ("@RHSSAF", "843632231"),
            ("@CUP Terrains - Core", "583496184")
        };

        // СТРОГИЙ ПОРЯДОК МОДОВ ИЗ ПАПКИ RESILIO SYNC
        private readonly List<string> _resilioMods = new()
        {
            "@sg_core",
            "@sg_islands",
            "@sg_mods"
        };

        private record ServerConfig(string GameHost, int GamePort, int QueryPort, string GamePassword, string TsHost, int TsPort);

        private readonly ServerConfig _server1 = new("s1.sg.zone", 2302, 2303, "2020", "t1.sg.zone", 9987);
        private readonly ServerConfig _server2 = new("s2.sg.zone", 2302, 2303, "2020", "t2.sg.zone", 9987);

        private class UserLauncherSettings
        {
            public string TsNickname { get; set; } = "[SG]Игрок";
            public string ArmaProfile { get; set; } = string.Empty;
            public int SelectedServer { get; set; } = 1;
            public int Server1TsIndex { get; set; } = 0;
            public int Server2TsIndex { get; set; } = 0;
            public string ResilioModsPath { get; set; } = string.Empty;
            public string MemoryAllocator { get; set; } = DefaultSystemMallocText;
            public bool NoSplash { get; set; } = true;
            public bool EmptyWorld { get; set; } = true;
            public bool Windowed { get; set; } = false;
            public bool CloseLauncherOnStart { get; set; } = false;
            public bool ShowStatsTab { get; set; } = false;
            public bool RememberWindowPosition { get; set; } = false;
            public double? WindowLeft { get; set; }
            public double? WindowTop { get; set; }
            public double? WindowWidth { get; set; }
            public double? WindowHeight { get; set; }
            public bool WindowMaximized { get; set; } = false;
        }

        public MainWindow()

        {

            Directory.CreateDirectory(AppDataDirectory);

            InitializeComponent();
            LoadVisualAssets();
            FindArma3Directory();
            LoadMemoryAllocators();
            LoadArma3Profiles();
            LoadUserSettings();
            TryAutoDetectResilioModsFolder();
            UpdateResilioModsFolderUi();
            CheckResilioInstallation();

            _ = UpdateBothServersPlayerCountAsync();

            _resilioCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _resilioCheckTimer.Tick += async (_, _) =>
            {
                CheckResilioInstallation();
                if (string.IsNullOrEmpty(_resilioModsPath) || !HasAllResilioMods(_resilioModsPath))
                {
                    TryAutoDetectResilioModsFolder();
                    UpdateResilioModsFolderUi();

                }

                RefreshDiscordPresence();

                // Обновляет синюю полосу скачивания, скорость и статус готовности модов:
                await UpdateResilioDownloadProgressUiAsync();
            };
            _resilioCheckTimer.Start();

            _serverStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
            _serverStatusTimer.Tick += async (_, _) => await UpdateBothServersPlayerCountAsync();
            _serverStatusTimer.Start();

            // Запуск новых функций:
            CheckTfarPluginInstallation();
            InitializeDiscordRpc();
            _ = CheckForUpdatesAsync();

            // При возвращении фокуса в окно лаунчера проверяем статус TS3 и плагина
            this.Activated += (_, _) =>
            {
                CheckTeamSpeakInstallation();
                CheckTfarPluginInstallation();
            };
        }

        /// <summary>
        /// Выполняет автоматический поиск папки с модами Resilio на всех дисках системы
        /// </summary>
        private void TryAutoDetectResilioModsFolder()
        {
            // Список вероятных путей к сборке модов
            var candidatePaths = new List<string>
            {
                @"C:\SolidGames",
                @"D:\SolidGames",
                @"E:\SolidGames",
                @"C:\SolidGames_Mods",
                @"D:\SolidGames_Mods",
                @"E:\SolidGames_Mods",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SolidGames")
            };

            // Проверяем все доступные локальные диски
            try
            {
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                {
                    candidatePaths.Add(Path.Combine(drive.RootDirectory.FullName, "SolidGames"));
                    candidatePaths.Add(Path.Combine(drive.RootDirectory.FullName, "Games", "SolidGames"));
                }
            }
            catch { }

            foreach (string path in candidatePaths.Distinct())
            {
                if (!Directory.Exists(path)) continue;

                // Проверяем наличие ключевых папок сборки
                bool hasCore = Directory.Exists(Path.Combine(path, "@sg_core"));
                bool hasIslands = Directory.Exists(Path.Combine(path, "@sg_islands"));
                bool hasMods = Directory.Exists(Path.Combine(path, "@sg_mods"));

                if (hasCore || hasIslands || hasMods)
                {
                    _resilioModsPath = path;
                    SaveUserSettings();
                    _cachedModsIntegrity = CheckModsFolderIntegrity(path);
                    break;
                }
            }
        }
        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;

            double scaleX = ActualWidth / BaseWindowWidth;
            double scaleY = ActualHeight / BaseWindowHeight;
            double scale = Math.Min(scaleX, scaleY);

            if (FindScaleResource() is ScaleTransform transform)
            {
                transform.ScaleX = scale;
                transform.ScaleY = scale;
            }

            if (_isSgZoneWebViewInit && SgZoneWebView != null)
            {
                SgZoneWebView.ZoomFactor = Math.Clamp(0.75 * scale, 0.5, 2.0);
            }
        }

        private object? FindScaleResource()
        {
            if (Content is System.Windows.Controls.Border outerBorder &&
                outerBorder.Child is System.Windows.Controls.Grid rootGrid &&
                rootGrid.Resources.Contains("AdaptiveScale"))
            {
                return rootGrid.Resources["AdaptiveScale"];
            }
            return null;
        }

        // Универсальная загрузка картинок: работает и с Embedded Resource, и с Resource, и с файлами на диске
        private void LoadVisualAssets()
        {
            // 1. Загрузка логотипа
            BitmapImage? logoBitmap = LoadImageUniversal("logo.png");
            if (logoBitmap != null)
            {
                LogoImage.Source = logoBitmap;
                LogoFallbackText.Visibility = Visibility.Collapsed;
            }
            else
            {
                LogoFallbackText.Visibility = Visibility.Visible;
            }

            // 2. Загрузка иконки окна
            BitmapImage? iconBitmap = LoadImageUniversal("icon.ico", "icon.png", "logo.png");
            if (iconBitmap != null)
            {
                Icon = iconBitmap;
            }

            // 3. Загрузка фоновой картинки
            BitmapImage? bgBitmap = LoadImageUniversal("bg1920w.jpg", "bg.jpg");
            if (bgBitmap != null)
            {
                BgImage.Source = bgBitmap;
            }
        }

        private static BitmapImage? LoadImageUniversal(params string[] fileNames)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string[] manifestResources = assembly.GetManifestResourceNames();

            foreach (string fileName in fileNames)
            {
                // Способ 1: Чтение из Embedded Resource (Внедренный ресурс внутри .exe)
                string? matchedManifest = manifestResources.FirstOrDefault(r =>
                    r.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

                if (matchedManifest != null)
                {
                    try
                    {
                        using Stream? stream = assembly.GetManifestResourceStream(matchedManifest);
                        if (stream != null)
                        {
                            return CreateBitmapFromStream(stream);
                        }
                    }
                    catch { }
                }

                // Способ 2: Чтение из WPF Resource (Build Action = Resource)
                try
                {
                    var packUri = new Uri($"pack://application:,,,/{fileName}", UriKind.Absolute);
                    var streamInfo = Application.GetResourceStream(packUri);
                    if (streamInfo != null)
                    {
                        using Stream stream = streamInfo.Stream;
                        return CreateBitmapFromStream(stream);
                    }
                }
                catch { }

                // Способ 3: Чтение с диска (если файл лежит в папке проекта или рядом с .exe)
                string[] diskCandidates =
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName),
                    Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\", fileName))
                };

                string? existingFile = diskCandidates.FirstOrDefault(File.Exists);
                if (existingFile != null)
                {
                    try
                    {
                        using var fs = new FileStream(existingFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                        return CreateBitmapFromStream(fs);
                    }
                    catch { }
                }
            }

            return null;
        }

        private static BitmapImage CreateBitmapFromStream(Stream stream)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private void LoadMemoryAllocators()
        {
            MallocComboBox.Items.Clear();
            MallocComboBox.Items.Add(DefaultSystemMallocText);

            string dllFolderPath = Path.Combine(_arma3Path, "Dll");
            if (Directory.Exists(dllFolderPath))
            {
                foreach (string dllFile in Directory.GetFiles(dllFolderPath, "*.dll"))
                {
                    MallocComboBox.Items.Add(Path.GetFileName(dllFile));
                }
            }

            MallocComboBox.SelectedIndex = 0;
        }

        // ================= ОТСЛЕЖИВАНИЕ И ВЫБОР ПАПКИ RESILIO MODS =================
        // Обработчик клика по красному баннеру во вкладке СЕРВЕРЫ
        private void ResilioWarningBanner_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SelectResilioModsFolder_Click(sender, e);
        }

        private async void SelectResilioModsFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Выберите папку с модами Resilio (@sg_core, @sg_islands, @sg_mods)"
            };

            if (dialog.ShowDialog() != true) return;

            string selectedPath = dialog.FolderName;

            // Если пользователь выбрал внутреннюю подпапку (например, сам @sg_core), берем родительскую
            string folderName = Path.GetFileName(selectedPath.TrimEnd(Path.DirectorySeparatorChar));
            if (folderName.StartsWith("@sg_", StringComparison.OrdinalIgnoreCase))
            {
                string? parent = Path.GetDirectoryName(selectedPath);
                if (!string.IsNullOrEmpty(parent))
                    selectedPath = parent;
            }

            // 1. Останавливаем текущую автозагрузку в старую папку, если она шла
            StopEmbeddedResilioProcess();
            _isAutoSyncActive = false;
            _isResilioPaused = false;

            // 2. Проверяем, нет ли внутри чужой папки .sync от другого клиента Resilio
            CheckAndHandleForeignSyncFolder(selectedPath);

            // 3. Сохраняем новую папку и проверяем целостность файлов
            _resilioModsPath = selectedPath;
            SaveUserSettings();

            var integrity = await Task.Run(() => CheckModsFolderIntegrity(selectedPath));
            _cachedModsIntegrity = integrity;

            UpdateResilioModsFolderUi();
            await UpdateResilioDownloadProgressUiAsync();

            // 4. Если папки модов есть, но они недокачаны — предлагаем докачать
            if (integrity.HasDirectories && !integrity.IsFullyReady)
            {
                var result = MessageBox.Show(
                    $"В выбранной папке моды скачаны не полностью:\n" +
                    $"• Скачано: {integrity.CompletedGb:F2} ГБ из {ExpectedModpackSizeGb:F1} ГБ\n" +
                    $"• Недокачанных файлов (.!sync / .rsls): {integrity.IncompleteFilesCount}\n\n" +
                    $"Запустить автоматическую проверку и докачку недостающих файлов?",
                    "Моды скачаны не полностью",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    _isAutoSyncActive = true;
                    _isResilioPaused = false;
                    _resilioResumeGraceUntil = DateTime.UtcNow.AddSeconds(10);
                    StartResilioAutoSync(_resilioModsPath);
                    await UpdateResilioDownloadProgressUiAsync();
                }
            }
        }

        /// <summary>
        /// Проверяет, есть ли в папке каталог .sync от другого клиента Resilio Sync
        /// </summary>
        private void CheckAndHandleForeignSyncFolder(string folderPath)
        {
            string syncFolderPath = Path.Combine(folderPath, ".sync");
            if (!Directory.Exists(syncFolderPath)) return;

            string ourMarkerFile = Path.Combine(syncFolderPath, "sg_launcher.marker");

            // Если нет нашего маркера — значит папка .sync создана сторонним клиентом Resilio
            if (!File.Exists(ourMarkerFile))
            {
                var answer = MessageBox.Show(
                    "В выбранной папке обнаружена служебная папка «.sync» от другого клиента Resilio Sync!\n\n" +
                    "Если её оставить, возникнет конфликт баз данных (встроенный клиент может начать перекачивать файлы заново).\n\n" +
                    "Удалить служебную папку «.sync» автоматически? (Сами моды удалены НЕ будут).",
                    "Обнаружена сторонняя папка .sync",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (answer == MessageBoxResult.Yes)
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(syncFolderPath) { Attributes = FileAttributes.Normal };
                        foreach (var info in dirInfo.GetFileSystemInfos("*", SearchOption.AllDirectories))
                        {
                            info.Attributes = FileAttributes.Normal;
                        }
                        Directory.Delete(syncFolderPath, true);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Не удалось удалить папку .sync (возможно, открыт сторонний клиент Resilio):\n{ex.Message}",
                                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        /// <summary>
        /// Проверяет не только наличие папок, но и отсутствие недокачанных файлов и полный объем (79.6 ГБ)
        /// </summary>
        private bool HasAllResilioMods(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return false;

            // Быстрая проверка наличия 3 базовых директорий
            bool dirsExist = Directory.Exists(Path.Combine(path, "@sg_core")) &&
                             Directory.Exists(Path.Combine(path, "@sg_islands")) &&
                             Directory.Exists(Path.Combine(path, "@sg_mods"));

            if (!dirsExist) return false;

            // Если путь совпадает с последним проверенным — используем точный результат проверки целостности
            if (_cachedModsIntegrity.CheckedPath == path)
                return _cachedModsIntegrity.IsFullyReady;

            return _cachedModsIntegrity.IsFullyReady;
        }

        private void UpdateResilioModsFolderUi()
        {
            if (ResilioModsFolderText == null || ResilioModsCheckText == null) return;

            if (string.IsNullOrEmpty(_resilioModsPath) || !Directory.Exists(_resilioModsPath))
            {
                ResilioModsFolderText.Text = "Папка модов Resilio: не выбрана";
                ResilioModsCheckText.Text = "Укажите папку, куда скачались @sg_core, @sg_islands и @sg_mods";
                ResilioModsCheckText.Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 107));
                if (ResilioWarningBanner != null) ResilioWarningBanner.Visibility = Visibility.Visible;
                return;
            }

            ResilioModsFolderText.Text = $"Папка модов Resilio: {_resilioModsPath}";

            var status = _cachedModsIntegrity.CheckedPath == _resilioModsPath
                ? _cachedModsIntegrity
                : CheckModsFolderIntegrity(_resilioModsPath);

            _cachedModsIntegrity = status;

            if (status.IsFullyReady)
            {
                ResilioModsCheckText.Text = $"✔ Все моды проверены и готовы к игре ({status.CompletedGb:F1} ГБ)";
                ResilioModsCheckText.Foreground = new SolidColorBrush(Color.FromRgb(104, 199, 107));
                if (ResilioWarningBanner != null) ResilioWarningBanner.Visibility = Visibility.Collapsed;
            }
            else if (status.HasDirectories)
            {
                ResilioModsCheckText.Text = $"⚠ Моды скачаны не полностью: {status.CompletedGb:F2} ГБ из {ExpectedModpackSizeGb:F1} ГБ (недокачано файлов: {status.IncompleteFilesCount})";
                ResilioModsCheckText.Foreground = new SolidColorBrush(Color.FromRgb(255, 183, 77));
                if (ResilioWarningBanner != null) ResilioWarningBanner.Visibility = Visibility.Visible;
            }
            else
            {
                ResilioModsCheckText.Text = "В выбранной папке не найдены @sg_core, @sg_islands и @sg_mods";
                ResilioModsCheckText.Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 107));
                if (ResilioWarningBanner != null) ResilioWarningBanner.Visibility = Visibility.Visible;
            }
        }

        // ================= ОНЛАЙН СЕРВЕРОВ ЧЕРЕЗ STEAM A2S_INFO (UDP) =================
        private async Task UpdateBothServersPlayerCountAsync()
        {
            Task<string> s1Task = QueryServerPlayersA2SAsync(_server1);
            Task<string> s2Task = QueryServerPlayersA2SAsync(_server2);

            await Task.WhenAll(s1Task, s2Task);

            Server1PlayersText.Text = s1Task.Result;
            Server2PlayersText.Text = s2Task.Result;

            _server1Players = s1Task.Result;
            _server2Players = s2Task.Result;

            RefreshDiscordPresence();
        }

        private async Task<string> QueryServerPlayersA2SAsync(ServerConfig server)
        {
            int[] candidatePorts = { server.QueryPort, server.GamePort, server.GamePort + 2 };

            foreach (int port in candidatePorts)
            {
                string? result = await TryQuerySinglePortA2SAsync(server.GameHost, port);
                if (result != null)
                {
                    return result;
                }
            }

            return "Оффлайн / Н/Д";
        }

        private static async Task<string?> TryQuerySinglePortA2SAsync(string host, int port)
        {
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host);
                IPAddress? ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                if (ip == null) return null;

                using var udp = new UdpClient();
                var endpoint = new IPEndPoint(ip, port);
                udp.Connect(endpoint);

                byte[] requestPacket =
                {
                    0xFF, 0xFF, 0xFF, 0xFF, 0x54,
                    0x53, 0x6F, 0x75, 0x72, 0x63, 0x65, 0x20,
                    0x45, 0x6E, 0x67, 0x69, 0x6E, 0x65, 0x20,
                    0x51, 0x75, 0x65, 0x72, 0x79, 0x00
                };

                await udp.SendAsync(requestPacket, requestPacket.Length);

                var receiveTask = udp.ReceiveAsync();
                if (await Task.WhenAny(receiveTask, Task.Delay(2200)) != receiveTask)
                {
                    return null;
                }

                byte[] response = receiveTask.Result.Buffer;

                if (response.Length >= 9 && response[4] == 0x41)
                {
                    byte[] challengeRequest = new byte[requestPacket.Length + 4];
                    Buffer.BlockCopy(requestPacket, 0, challengeRequest, 0, requestPacket.Length);
                    Buffer.BlockCopy(response, 5, challengeRequest, requestPacket.Length, 4);

                    await udp.SendAsync(challengeRequest, challengeRequest.Length);

                    var secondReceiveTask = udp.ReceiveAsync();
                    if (await Task.WhenAny(secondReceiveTask, Task.Delay(2200)) != secondReceiveTask)
                    {
                        return null;
                    }

                    response = secondReceiveTask.Result.Buffer;
                }

                if (response.Length > 10 && response[4] == 0x49)
                {
                    int index = 6;
                    SkipNullTerminatedString(response, ref index);
                    SkipNullTerminatedString(response, ref index);
                    SkipNullTerminatedString(response, ref index);
                    SkipNullTerminatedString(response, ref index);
                    index += 2;

                    if (index + 1 < response.Length)
                    {
                        int players = response[index];
                        int maxPlayers = response[index + 1];
                        return $"Игроков: {players} / {maxPlayers}";
                    }
                }
            }
            catch { }

            return null;
        }

        private static void SkipNullTerminatedString(byte[] data, ref int index)
        {
            while (index < data.Length && data[index] != 0x00)
            {
                index++;
            }
            index++;
        }

        private async void RefreshServerStatus_Click(object sender, RoutedEventArgs e)
        {
            Server1PlayersText.Text = "Обновление...";
            Server2PlayersText.Text = "Обновление...";
            await UpdateBothServersPlayerCountAsync();
        }

        // ================= НАВИГАЦИЯ И ВКЛАДКИ (КЭШ WEBVIEW2 В APPDATA) =================
        private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void MaximizeRestoreWindow_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

        private void LogoButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo(SgZoneSiteUrl) { UseShellExecute = true });
        }

        private void AuthorProfileLink_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo(AuthorProfileUrl) { UseShellExecute = true });
        }

        private async void NavTab_Checked(object sender, RoutedEventArgs e)
        {
            if (PageServers == null || PageMods == null || PageTs == null || PageSgZone == null || PageParams == null || PageStats == null)
                return;

            PageServers.Visibility = NavServersBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageMods.Visibility = NavModsBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageTs.Visibility = NavTsBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageParams.Visibility = NavParamsBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageSgZone.Visibility = NavSgZoneBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PageStats.Visibility = NavStatsBtn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            if (NavServersBtn.IsChecked == true) ActiveSectionHeaderText.Text = "СЕРВЕРЫ И ПРОФИЛЬ";
            else if (NavModsBtn.IsChecked == true) ActiveSectionHeaderText.Text = "МОДЫ (RESILIO / WORKSHOP)";
            else if (NavTsBtn.IsChecked == true) ActiveSectionHeaderText.Text = "TEAMSPEAK 3 И РАЦИЯ";
            else if (NavParamsBtn.IsChecked == true) ActiveSectionHeaderText.Text = "ПАРАМЕТРЫ";
            else if (NavSgZoneBtn.IsChecked == true) ActiveSectionHeaderText.Text = "АНОНС ИГР SG.ZONE";
            else if (NavStatsBtn.IsChecked == true) ActiveSectionHeaderText.Text = "СТАТИСТИКА ПРОЕКТА";

            if (NavSgZoneBtn.IsChecked == true && !_isSgZoneWebViewInit)
            {
                await InitializeSgZoneNewsTabAsync();
            }

            if (NavStatsBtn.IsChecked == true && !_isStatsWebViewInit)
            {
                await InitializeStatsTabAsync();
            }
        }

        private async Task InitializeSgZoneNewsTabAsync()
        {
            try
            {
                _isSgZoneWebViewInit = true;
                Directory.CreateDirectory(_webViewCacheDir);
                var env = await CoreWebView2Environment.CreateAsync(null, _webViewCacheDir);
                await SgZoneWebView.EnsureCoreWebView2Async(env);

                SgZoneWebView.ZoomFactor = 0.67;
                SgZoneWebView.CoreWebView2.NavigationCompleted += SgZoneWebView_NavigationCompleted;
                SgZoneWebView.CoreWebView2.Navigate(SgZoneSiteUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось загрузить WebView2:\n{ex.Message}", "Ошибка WebView2", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void SgZoneWebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess) return;

            string cleanLayoutScript = @"
                (function() {
                    document.querySelectorAll('header, nav, footer').forEach(el => el.style.display = 'none');
                    document.body.style.paddingTop = '0px';
                    document.body.style.marginTop = '0px';
                    window.scrollTo(0, 0);
                })();";
            await SgZoneWebView.CoreWebView2.ExecuteScriptAsync(cleanLayoutScript);
        }

        private void ReloadSgZone_Click(object sender, RoutedEventArgs e) => SgZoneWebView.CoreWebView2?.Reload();

        private async Task InitializeStatsTabAsync()
        {
            try
            {
                _isStatsWebViewInit = true;
                Directory.CreateDirectory(_webViewCacheDir);
                var env = await CoreWebView2Environment.CreateAsync(null, _webViewCacheDir);
                await StatsWebView.EnsureCoreWebView2Async(env);

                StatsWebView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;

                string resetToBrowserDefaultScript = @"
                    try {
                        localStorage.removeItem('theme');
                        localStorage.removeItem('color-theme');
                        localStorage.removeItem('vueuse-color-scheme');
                        localStorage.removeItem('darkMode');
                        localStorage.removeItem('isDark');
                        document.documentElement.classList.remove('dark');
                        document.documentElement.removeAttribute('data-theme');
                        document.documentElement.style.backgroundColor = '#ffffff';
                    } catch(e) {}";
                await StatsWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(resetToBrowserDefaultScript);

                StatsWebView.CoreWebView2.NavigationCompleted += async (_, args) =>
                {
                    if (!args.IsSuccess) return;
                    await StatsWebView.CoreWebView2.ExecuteScriptAsync(
                        "document.documentElement.style.backgroundColor = '#ffffff'; if(document.body) document.body.style.backgroundColor = '#ffffff';");
                };

                StatsWebView.CoreWebView2.Navigate(SolidStatsSiteUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось загрузить статистику:\n{ex.Message}", "Ошибка WebView2", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ReloadStats_Click(object sender, RoutedEventArgs e) => StatsWebView.CoreWebView2?.Reload();

        private void OpenStatsInBrowser_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo(SolidStatsSiteUrl) { UseShellExecute = true });
        }

        private void StatsOption_Changed(object sender, RoutedEventArgs e)
        {
            if (NavStatsBtn != null)
            {
                NavStatsBtn.Visibility = ParamShowStatsTabCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            }
            SaveUserSettings();
        }

        private void ServerSelection_Changed(object sender, RoutedEventArgs e)
        {
            if (LaunchServerBtnText != null)
            {
                LaunchServerBtnText.Text = Server2Radio?.IsChecked == true
                    ? "СЕРВЕР 2"
                    : "СЕРВЕР 1";
            }
            SaveUserSettings();
        }

        // ================= СОХРАНЕНИЕ И ЗАГРУЗКА НАСТРОЕК =================
        private void LoadUserSettings()
        {
            _isLoadingSettings = true;
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize<UserLauncherSettings>(json);
                    if (settings != null)
                    {
                        TsNicknameTextBox.Text = settings.TsNickname;
                        Server1Radio.IsChecked = settings.SelectedServer != 2;
                        Server2Radio.IsChecked = settings.SelectedServer == 2;
                        LaunchServerBtnText.Text = settings.SelectedServer == 2 ? "СЕРВЕР 2" : "СЕРВЕР 1";

                        if (Server1TsComboBox != null) Server1TsComboBox.SelectedIndex = settings.Server1TsIndex;
                        if (Server2TsComboBox != null) Server2TsComboBox.SelectedIndex = settings.Server2TsIndex;

                        if (!string.IsNullOrEmpty(settings.ResilioModsPath) && Directory.Exists(settings.ResilioModsPath))
                        {
                            _resilioModsPath = settings.ResilioModsPath;
                        }

                        if (!string.IsNullOrEmpty(settings.ArmaProfile))
                        {
                            ProfileComboBox.Text = settings.ArmaProfile;
                        }

                        if (!string.IsNullOrEmpty(settings.MemoryAllocator) && MallocComboBox.Items.Contains(settings.MemoryAllocator))
                        {
                            MallocComboBox.SelectedItem = settings.MemoryAllocator;
                        }

                        ParamNoSplashCheck.IsChecked = settings.NoSplash;
                        ParamEmptyWorldCheck.IsChecked = settings.EmptyWorld;
                        ParamWindowedCheck.IsChecked = settings.Windowed;
                        ParamCloseOnLaunchCheck.IsChecked = settings.CloseLauncherOnStart;
                        ParamShowStatsTabCheck.IsChecked = settings.ShowStatsTab;
                        ParamRememberWindowCheck.IsChecked = settings.RememberWindowPosition;
                        NavStatsBtn.Visibility = settings.ShowStatsTab ? Visibility.Visible : Visibility.Collapsed;

                        if (settings.RememberWindowPosition)
                        {
                            if (settings.WindowWidth is >= 850 && settings.WindowHeight is >= 540)
                            {
                                Width = settings.WindowWidth.Value;
                                Height = settings.WindowHeight.Value;
                            }

                            if (settings.WindowLeft.HasValue && settings.WindowTop.HasValue)
                            {
                                WindowStartupLocation = WindowStartupLocation.Manual;
                                Left = settings.WindowLeft.Value;
                                Top = settings.WindowTop.Value;
                            }

                            if (settings.WindowMaximized)
                            {
                                WindowState = WindowState.Maximized;
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                _isLoadingSettings = false;
            }
        }

        private void SaveUserSettings()
        {
            if (_isLoadingSettings) return;
            try
            {
                Directory.CreateDirectory(AppDataDirectory);
                bool rememberPos = ParamRememberWindowCheck?.IsChecked == true;
                Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

                var settings = new UserLauncherSettings
                {
                    TsNickname = TsNicknameTextBox.Text,
                    ArmaProfile = ProfileComboBox.Text,
                    SelectedServer = Server2Radio.IsChecked == true ? 2 : 1,
                    Server1TsIndex = Server1TsComboBox?.SelectedIndex ?? 0,
                    Server2TsIndex = Server2TsComboBox?.SelectedIndex ?? 0,
                    ResilioModsPath = _resilioModsPath,
                    MemoryAllocator = MallocComboBox.SelectedItem?.ToString() ?? DefaultSystemMallocText,
                    NoSplash = ParamNoSplashCheck.IsChecked == true,
                    EmptyWorld = ParamEmptyWorldCheck.IsChecked == true,
                    Windowed = ParamWindowedCheck.IsChecked == true,
                    CloseLauncherOnStart = ParamCloseOnLaunchCheck.IsChecked == true,
                    ShowStatsTab = ParamShowStatsTabCheck.IsChecked == true,
                    RememberWindowPosition = rememberPos,
                    WindowLeft = rememberPos ? bounds.Left : null,
                    WindowTop = rememberPos ? bounds.Top : null,
                    WindowWidth = rememberPos ? bounds.Width : null,
                    WindowHeight = rememberPos ? bounds.Height : null,
                    WindowMaximized = rememberPos && WindowState == WindowState.Maximized
                };
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch { }
        }

        private void UserSettings_Changed(object sender, EventArgs e) => SaveUserSettings();

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e) => SaveUserSettings();

        private void FindArma3Directory()
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\bohemia interactive\arma 3");
            _arma3Path = key?.GetValue("main")?.ToString() ?? @"C:\Program Files (x86)\Steam\steamapps\common\Arma 3";

            ArmaPathStatusText.Text = Directory.Exists(_arma3Path)
                ? $"ARMA 3: {_arma3Path.ToUpperInvariant()}"
                : "ПАПКА ARMA 3 НЕ НАЙДЕНА";
        }

        private void CheckResilioInstallation()
        {
            _resilioExePath = FindResilioExePath() ?? string.Empty;

            if (!string.IsNullOrEmpty(_resilioExePath))
            {
                ResilioStatusText.Text = "Установлен";
                ResilioStatusText.Foreground = new SolidColorBrush(Color.FromRgb(104, 199, 107));
                SyncResilioModsBtn.IsEnabled = true;
            }
            else
            {
                ResilioStatusText.Text = "Не установлен";
                ResilioStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 68, 68));
                SyncResilioModsBtn.IsEnabled = false;
            }

            // Одновременно проверяем установку TeamSpeak 3
            CheckTeamSpeakInstallation();

        }

            // ================= 1. ПРОВЕРКА И АВТОУСТАНОВКА ПЛАГИНА TFAR =================
        private void CheckTfarPluginInstallation()
        {
            if (TfarPluginStatusText == null) return;

            string appDataPlugins = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TS3Client", "plugins", "TFAR_win64.dll");

            string? tsExe = FindTeamSpeakExePath();
            string? installDirPlugins = !string.IsNullOrEmpty(tsExe)
                ? Path.Combine(Path.GetDirectoryName(tsExe)!, "plugins", "TFAR_win64.dll")
                : null;

            bool isPluginInstalled = File.Exists(appDataPlugins) ||
                                     (!string.IsNullOrEmpty(installDirPlugins) && File.Exists(installDirPlugins));

            if (isPluginInstalled)
            {
                TfarPluginStatusText.Text = "Установлен (TFAR_win64.dll найден)";
                TfarPluginStatusText.Foreground = new SolidColorBrush(Color.FromRgb(104, 199, 107));
                if (AutoInstallTfarBtn != null) AutoInstallTfarBtn.Content = "ПЕРЕУСТАНОВИТЬ ПЛАГИН";
            }
            else
            {
                TfarPluginStatusText.Text = "Не установлен";
                TfarPluginStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 68, 68));
                if (AutoInstallTfarBtn != null) AutoInstallTfarBtn.Content = "УСТАНОВИТЬ ПЛАГИН";
            }
        }

        private async void AutoInstallTfarPlugin_Click(object sender, RoutedEventArgs e)
        {
            string pluginArchivePath = Path.Combine(_resilioModsPath, "@sg_core", "teamspeak", "task_force_radio.ts3_plugin");
            if (!File.Exists(pluginArchivePath))
            {
                MessageBox.Show(
                    "Не найден файл task_force_radio.ts3_plugin в папке @sg_core\\teamspeak.\n" +
                    "Убедитесь, что моды Resilio полностью скачаны и выбрана верная папка!",
                    "Файл не найден", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string? tsExePath = FindTeamSpeakExePath();
                string? tsDir = !string.IsNullOrEmpty(tsExePath) ? Path.GetDirectoryName(tsExePath) : null;
                string? installerPath = tsDir != null ? Path.Combine(tsDir, "package_installer.exe") : null;

                Process? process = null;

                if (!string.IsNullOrEmpty(installerPath) && File.Exists(installerPath))
                {
                    process = Process.Start(new ProcessStartInfo
                    {
                        FileName = installerPath,
                        Arguments = $"\"{pluginArchivePath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    process = Process.Start(new ProcessStartInfo(pluginArchivePath)
                    {
                        UseShellExecute = true
                    });
                }

                if (process != null)
                {
                    // Временно меняем текст кнопки, показывая что ждем установку
                    if (AutoInstallTfarBtn != null) AutoInstallTfarBtn.IsEnabled = false;

                    // Ждем, пока пользователь нажмет Install в окне TeamSpeak и закроет его
                    await process.WaitForExitAsync();

                    // Небольшая задержка на полсекунды для завершения записи файлов на диск
                    await Task.Delay(500);

                    // Моментально обновляем статус
                    CheckTfarPluginInstallation();

                    if (AutoInstallTfarBtn != null) AutoInstallTfarBtn.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                if (AutoInstallTfarBtn != null) AutoInstallTfarBtn.IsEnabled = true;

                MessageBox.Show(
                    $"Не удалось запустить установщик плагина:\n{ex.Message}\n\n" +
                    $"Вы можете установить его вручную двойным кликом по файлу:\n{pluginArchivePath}",
                    "Ошибка установки", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Переменные для хранения информации о новом релизе
        private string? _downloadExeUrl;
        private string? _newVersionTag;

        // ================= АВТОМАТИЧЕСКОЕ ОБНОВЛЕНИЕ ЛАУНЧЕРА =================
        private async Task CheckForUpdatesAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SolidGamesLauncher");

                string json = await http.GetStringAsync(GitHubRepoUrl);
                using JsonDocument doc = JsonDocument.Parse(json);

                string? latestTag = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
                _newVersionTag = latestTag;

                // Ищем прямую ссылку на скачивание файла .exe из релиза на GitHub
                if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string? name = asset.GetProperty("name").GetString();
                        if (name != null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            _downloadExeUrl = asset.GetProperty("browser_download_url").GetString();
                            break;
                        }
                    }
                }

                // Сравниваем версии
                if (!string.IsNullOrEmpty(latestTag) &&
                    Version.TryParse(latestTag, out Version? remoteVer) &&
                    Version.TryParse(CurrentLauncherVersion, out Version? localVer) &&
                    remoteVer > localVer)
                {
                    // Спрашиваем игрока в главном потоке интерфейса
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        var result = MessageBox.Show(
                            $"Вышло обновление лаунчера {latestTag}!\n\nХотите обновиться сейчас?",
                            "Обновление лаунчера",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (result == MessageBoxResult.Yes)
                        {
                            await PerformAutoUpdateAsync();
                        }
                        else
                        {
                            // Если игрок отказался — показываем небольшую зеленую кнопку во всех вкладках
                            if (UpdateAvailableBtn != null)
                            {
                                UpdateAvailableBtn.Content = $"⬆ Доступно обновление лаунчера {latestTag}";
                                UpdateAvailableBtn.Visibility = Visibility.Visible;
                            }
                        }
                    });
                }
            }
            catch
            {
                /* Игнорируем сетевые сбои, если нет подключения */
            }
        }

        private async void UpdateAvailableBtn_Click(object sender, RoutedEventArgs e)
        {
            await PerformAutoUpdateAsync();
        }

        private async Task PerformAutoUpdateAsync()
        {
            if (string.IsNullOrEmpty(_downloadExeUrl))
            {
                // Запасной вариант: если к релизу забыли прикрепить .exe файл
                Process.Start(new ProcessStartInfo(_latestReleaseUrl) { UseShellExecute = true });
                return;
            }

            try
            {
                string currentExePath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? AppContext.BaseDirectory;

                string tempUpdateExe = Path.Combine(Path.GetTempPath(), $"SG_Launcher_Update_{Guid.NewGuid():N}.exe");

                if (UpdateAvailableBtn != null)
                {
                    UpdateAvailableBtn.IsEnabled = false;
                    UpdateAvailableBtn.Content = "Загрузка обновления...";
                    UpdateAvailableBtn.Visibility = Visibility.Visible;
                }

                // Скачиваем новый .exe файл во временную директорию
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SolidGamesLauncher");

                byte[] newExeBytes = await http.GetByteArrayAsync(_downloadExeUrl);
                await File.WriteAllBytesAsync(tempUpdateExe, newExeBytes);

                // Запускаем скрытую системную команду:
                // 1. Ждет 1 сек закрытия текущего процесса
                // 2. Заменяет старый .exe на скачанный
                // 3. Запускает обновленный лаунчер
                string cmdScript = $"/c timeout /t 1 /nobreak > NUL & move /y \"{tempUpdateExe}\" \"{currentExePath}\" & start \"\" \"{currentExePath}\"";

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = cmdScript,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });

                // Мгновенно закрываем старый лаунчер, чтобы освободить файл
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                if (UpdateAvailableBtn != null)
                {
                    UpdateAvailableBtn.IsEnabled = true;
                    UpdateAvailableBtn.Content = $"⬆ Доступно обновление {_newVersionTag}";
                }

                MessageBox.Show(
                    $"Не удалось выполнить автообновление:\n{ex.Message}\n\nВы можете обновиться вручную с сайта.",
                    "Ошибка обновления", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }



                  
        // ================= 3. DISCORD RICH PRESENCE (RPC) =================
        // Поля для хранения онлайна и состояния Discord RPC
        private string _server1Players = "0/0";
        private string _server2Players = "0/0";
        private string _lastDiscordDetails = string.Empty;
        private string _lastDiscordState = string.Empty;
        private bool _wasArmaRunning = false;
        private Timestamps _discordTimestamp = Timestamps.Now;

        // ================= 3. DISCORD RICH PRESENCE (RPC) =================
        private void InitializeDiscordRpc()
        {
            try
            {
                _discordClient = new DiscordRpcClient(DiscordAppId);
                _discordClient.Initialize();
                RefreshDiscordPresence();
            }
            catch { }
        }

        private static bool IsArma3Running()
        {
            return Process.GetProcessesByName("arma3_x64").Length > 0 ||
                   Process.GetProcessesByName("arma3").Length > 0 ||
                   Process.GetProcessesByName("arma3battleye").Length > 0;
        }

        private void RefreshDiscordPresence()
        {
            if (_discordClient == null) return;

            bool isArmaRunning = IsArma3Running();

            // Если игра только что запустилась или закрылась — сбрасываем таймер проведенного времени
            if (isArmaRunning != _wasArmaRunning)
            {
                _wasArmaRunning = isArmaRunning;
                _discordTimestamp = Timestamps.Now;
            }

            string details;
            string state;

            if (isArmaRunning)
            {
                int serverNum = Server2Radio?.IsChecked == true ? 2 : 1;
                string playersInfo = serverNum == 2 ? _server2Players : _server1Players;

                // Верхняя строка: коротко статус
                details = "В игре";

                // Нижняя строка: конкретный сервер и счетчик игроков
                state = $"Сервер {serverNum} • {playersInfo}";
            }
            else
            {
                details = "В меню лаунчера";
                state = "Готовится к запуску Arma 3";
            }

            // Отправляем обновление в Discord только если текст действительно изменился
            if (details == _lastDiscordDetails && state == _lastDiscordState)
                return;

            _lastDiscordDetails = details;
            _lastDiscordState = state;

            UpdateDiscordPresence(details, state);
        }

        private void UpdateDiscordPresence(string details, string state)
        {
            try
            {
                _discordClient?.SetPresence(new RichPresence
                {
                    Details = details,
                    State = state,
                    Timestamps = _discordTimestamp,
                    Assets = new Assets
                    {
                        LargeImageKey = "logo",
                        LargeImageText = "SolidGames Arma 3"
                    },
                    Buttons = new DiscordRPC.Button[]
                    {
                        new DiscordRPC.Button { Label = "Сайт проекта SG.ZONE", Url = SgZoneSiteUrl }
                    }
                });
            }
            catch { }
        }



        private static string? FindResilioExePath()
        {
            // 1. Ищем точный путь к .exe в ветке протокола rslsync (работает для любой папки установки)
            string? command = Registry.ClassesRoot.OpenSubKey(@"rslsync\shell\open\command")?.GetValue("")?.ToString()
                           ?? Registry.CurrentUser.OpenSubKey(@"Software\Classes\rslsync\shell\open\command")?.GetValue("")?.ToString();

            if (!string.IsNullOrEmpty(command))
            {
                Match match = Regex.Match(command, @"""([^""]+\.exe)""", RegexOptions.IgnoreCase);
                if (match.Success && File.Exists(match.Groups[1].Value))
                {
                    return match.Groups[1].Value;
                }
            }

            // 2. Проверяем ветки реестра самого клиента Resilio Sync
            string[] regKeys =
            {
                @"Software\Resilio, Inc.\Resilio Sync",
                @"SOFTWARE\Resilio, Inc.\Resilio Sync",
                @"SOFTWARE\WOW6432Node\Resilio, Inc.\Resilio Sync"
            };

            foreach (string keyPath in regKeys)
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath) ?? Registry.LocalMachine.OpenSubKey(keyPath);
                string? installDir = key?.GetValue("InstallDir")?.ToString() ?? key?.GetValue("")?.ToString();

                if (!string.IsNullOrEmpty(installDir))
                {
                    string candidate = Path.Combine(installDir, "Resilio Sync.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }

            // 3. Запасная проверка стандартных путей
            string[] defaultPaths =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Resilio Sync", "Resilio Sync.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Resilio Sync", "Resilio Sync.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Resilio Sync", "Resilio Sync.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Resilio Sync", "Resilio Sync.exe")
            };

            return defaultPaths.FirstOrDefault(File.Exists);
        }

        private void CheckTeamSpeakInstallation()
        {
            if (TsStatusText == null) return;

            string? tsExePath = FindTeamSpeakExePath();

            if (!string.IsNullOrEmpty(tsExePath))
            {
                TsStatusText.Text = $"Установлено ({tsExePath})";
                TsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(104, 199, 107));
            }
            else
            {
                TsStatusText.Text = "Не установлено";
                TsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 68, 68));
            }
        }

        private static string? FindTeamSpeakExePath()
        {
            // 1. Ищем точный путь к .exe в ветке протокола ts3server (работает для любой папки установки)
            string? command = Registry.ClassesRoot.OpenSubKey(@"ts3server\shell\open\command")?.GetValue("")?.ToString()
                           ?? Registry.CurrentUser.OpenSubKey(@"Software\Classes\ts3server\shell\open\command")?.GetValue("")?.ToString();

            if (!string.IsNullOrEmpty(command))
            {
                // Строка в реестре имеет вид: "D:\CustomPath\ts3client_win64.exe" "%1"
                Match match = Regex.Match(command, @"""([^""]+\.exe)""", RegexOptions.IgnoreCase);
                if (match.Success && File.Exists(match.Groups[1].Value))
                {
                    return match.Groups[1].Value;
                }
            }

            // 2. Проверяем ветки реестра самого клиента TeamSpeak 3 (CurrentUser и LocalMachine)
            string[] regKeys =
            {
                @"Software\TeamSpeak 3 Client",
                @"SOFTWARE\TeamSpeak 3 Client",
                @"SOFTWARE\WOW6432Node\TeamSpeak 3 Client"
            };

            foreach (string keyPath in regKeys)
            {
                string? installDir = Registry.CurrentUser.OpenSubKey(keyPath)?.GetValue("")?.ToString()
                                  ?? Registry.LocalMachine.OpenSubKey(keyPath)?.GetValue("")?.ToString();

                if (!string.IsNullOrEmpty(installDir))
                {
                    string candidate = Path.Combine(installDir, "ts3client_win64.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }

            // 3. Запасная проверка стандартных путей
            string[] defaultPaths =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeamSpeak 3 Client", "ts3client_win64.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TeamSpeak 3 Client", "ts3client_win64.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "TeamSpeak 3 Client", "ts3client_win64.exe")
            };

            return defaultPaths.FirstOrDefault(File.Exists);
        }

        private void InstallResilio_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo(ResilioOfficialDownloadUrl) { UseShellExecute = true });
        }

        private void DownloadResilioMods_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(ResilioReadOnlyKey);

                if (!string.IsNullOrEmpty(_resilioExePath) && File.Exists(_resilioExePath))
                {
                    Process.Start(new ProcessStartInfo(_resilioExePath) { UseShellExecute = true });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("rslsync://") { UseShellExecute = true });
                }

                MessageBox.Show(
                    $"Ключ скопирован в буфер обмена:\n{ResilioReadOnlyKey}\n\n" +
                    "1. В Resilio Sync нажмите «+» → «Ввести ключ или ссылку» и вставьте ключ (Ctrl + V).\n" +
                    "2. Нажмите ПКМ по раздаче → Настройки:\n" +
                    "   • УБЕРИТЕ галочку с «Сохранять удаленные файлы в архив»\n" +
                    "   • ПОСТАВЬТЕ галочку «Перезаписывать измененные файлы»\n\n" +
                    "3. После выбора папки в Resilio нажмите кнопку «ВЫБРАТЬ ПАПКУ С МОДАМИ» в лаунчере.\n\n" +
                    "Состояния раздачи:\n" +
                    "🔵 Синий круг — идет скачивание\n" +
                    "🟢 Зеленый круг — моды загружены, идет раздача",
                    "Инструкция Resilio Sync", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка запуска Resilio Sync:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SubscribeSteamWorkshop_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo(SteamWorkshopCollectionUrl) { UseShellExecute = true });
        }

        private void InstallTS3_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo(TeamSpeakDownloadUrl) { UseShellExecute = true });
        }

        private void DownloadTS3FromSgZone_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo(TeamSpeakSgZoneZipUrl) { UseShellExecute = true });
        }

        private void DownloadTS3PluginFromWeb_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo(TeamSpeakPluginDirectUrl) { UseShellExecute = true });
        }

        private void OpenModsFolderForPlugin_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_resilioModsPath) || !Directory.Exists(_resilioModsPath))
            {
                MessageBox.Show("Сначала выберите папку с модами Resilio во вкладке «МОДЫ»!", "Папка модов не выбрана", MessageBoxButton.OK, MessageBoxImage.Warning);
                NavModsBtn.IsChecked = true;
                return;
            }

            string pluginFilePath = Path.Combine(_resilioModsPath, "@sg_core", "teamspeak", "task_force_radio.ts3_plugin");
            string pluginFolderPath = Path.Combine(_resilioModsPath, "@sg_core", "teamspeak");

            if (File.Exists(pluginFilePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{pluginFilePath}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(pluginFolderPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", pluginFolderPath) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", _resilioModsPath) { UseShellExecute = true });
            }
        }

        private void LoadArma3Profiles()
        {
            ProfileComboBox.Items.Clear();
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string otherProfilesDir = Path.Combine(docs, "Arma 3 - Other Profiles");

            if (Directory.Exists(otherProfilesDir))
            {
                foreach (string dir in Directory.GetDirectories(otherProfilesDir))
                {
                    string folderName = Path.GetFileName(dir);
                    ProfileComboBox.Items.Add(Uri.UnescapeDataString(folderName));
                }
            }

            if (ProfileComboBox.Items.Count == 0)
            {
                ProfileComboBox.Items.Add(Environment.UserName);
            }

            ProfileComboBox.SelectedIndex = 0;
        }

        private void RefreshProfiles_Click(object sender, RoutedEventArgs e) => LoadArma3Profiles();

        // ================= СБОРКА МОДОВ В СТРОГОМ ПОРЯДКЕ И ЗАПУСК ARMA 3 =================
        private string? ResolveSteamWorkshopModPath(string folderName, string workshopId)
        {
            string inWorkshopSymlink = Path.Combine(_arma3Path, "!Workshop", folderName);
            if (Directory.Exists(inWorkshopSymlink)) return inWorkshopSymlink;

            string inArmaRoot = Path.Combine(_arma3Path, folderName);
            if (Directory.Exists(inArmaRoot)) return inArmaRoot;

            try
            {
                string workshopContent = Path.GetFullPath(Path.Combine(_arma3Path, @"..\..\workshop\content\107410", workshopId));
                if (Directory.Exists(workshopContent)) return workshopContent;
            }
            catch { }

            return null;
        }

        private List<string> BuildStrictModList(out List<string> missingMods)
        {
            var resolvedMods = new List<string>();
            missingMods = new List<string>();

            foreach (var (folderName, workshopId) in _steamWorkshopMods)
            {
                string? path = ResolveSteamWorkshopModPath(folderName, workshopId);
                if (path != null)
                {
                    resolvedMods.Add(path);
                }
                else
                {
                    missingMods.Add($"{folderName} (Steam Workshop)");
                }
            }

            string baseResilioDir = !string.IsNullOrWhiteSpace(_resilioModsPath) ? _resilioModsPath : _arma3Path;
            foreach (string modName in _resilioMods)
            {
                string candidate = Path.Combine(baseResilioDir, modName);
                if (Directory.Exists(candidate))
                {
                    resolvedMods.Add(candidate);
                }
                else
                {
                    missingMods.Add($"{modName} (Resilio Sync)");
                }
            }

            return resolvedMods;
        }

        private void LaunchGameOnly_Click(object sender, RoutedEventArgs e)
        {
            SaveUserSettings();
            LaunchArma3(connectToServer: false);
        }

        private void ConnectAndPlay_Click(object sender, RoutedEventArgs e)
        {
            SaveUserSettings();

            bool isServer2 = Server2Radio.IsChecked == true;
            ServerConfig selectedServer = isServer2 ? _server2 : _server1;

            // Берем выбранный адрес TS3 из выпадающего списка
            var activeCombo = isServer2 ? Server2TsComboBox : Server1TsComboBox;
            string tsAddressTag = (activeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString()
                                  ?? $"{selectedServer.TsHost}:{selectedServer.TsPort}";

            string tsNickname = TsNicknameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(tsNickname))
            {
                tsNickname = ProfileComboBox.Text.Trim();
            }

            ConnectToTeamSpeak(tsAddressTag, tsNickname);
            LaunchArma3(connectToServer: true, selectedServer);
        }

        private void ConnectToTeamSpeak(string hostAndPort, string nickname)
        {
            string[] parts = hostAndPort.Split(':');
            string host = parts[0];
            string port = parts.Length > 1 ? parts[1] : "9987";

            string encodedNick = Uri.EscapeDataString(string.IsNullOrEmpty(nickname) ? "Player" : nickname);
            string tsUri = $"ts3server://{host}?port={port}&nickname={encodedNick}";
            Process.Start(new ProcessStartInfo(tsUri) { UseShellExecute = true });
        }

        private void LaunchArma3(bool connectToServer, ServerConfig? server = null)
        {
            string battlEyeExe = Path.Combine(_arma3Path, "arma3battleye.exe");
            if (!File.Exists(battlEyeExe))
            {
                MessageBox.Show($"Не найден файл arma3battleye.exe по пути:\n{battlEyeExe}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            List<string> orderedModPaths = BuildStrictModList(out List<string> missingMods);

            if (missingMods.Count > 0)
            {
                MessageBoxResult res = MessageBox.Show(
                    "Не найдены следующие моды:\n• " + string.Join("\n• ", missingMods) +
                    "\n\nПроверьте подписку в Steam Workshop и выбранную папку Resilio во вкладке «МОДЫ».\nВсе равно запустить игру?",
                    "Не все моды найдены", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (res != MessageBoxResult.Yes) return;
            }

            string armaProfileName = ProfileComboBox.Text.Trim();
            string modArgument = string.Join(";", orderedModPaths);

            Directory.CreateDirectory(AppDataDirectory);
            string parFilePath = Path.Combine(AppDataDirectory, "startup_params.txt");
            var lines = new List<string> { "-skipIntro" };

            if (ParamNoSplashCheck?.IsChecked == true) lines.Add("-noSplash");
            if (ParamEmptyWorldCheck?.IsChecked == true) lines.Add("-world=empty");
            if (ParamWindowedCheck?.IsChecked == true) lines.Add("-window");

            string? selectedMalloc = MallocComboBox?.SelectedItem?.ToString();
            if (!string.IsNullOrEmpty(selectedMalloc) && selectedMalloc != DefaultSystemMallocText)
            {
                string mallocName = Path.GetFileNameWithoutExtension(selectedMalloc);
                lines.Add($"-malloc={mallocName}");
            }

            lines.Add($"-name=\"{armaProfileName}\"");
            lines.Add($"-mod=\"{modArgument}\"");

            if (connectToServer && server != null)
            {
                lines.Add($"-connect={server.GameHost}");
                lines.Add($"-port={server.GamePort}");
                if (!string.IsNullOrEmpty(server.GamePassword))
                {
                    lines.Add($"-password=\"{server.GamePassword}\"");
                }
            }

            File.WriteAllLines(parFilePath, lines, Encoding.UTF8);

            var startInfo = new ProcessStartInfo
            {
                FileName = battlEyeExe,
                WorkingDirectory = _arma3Path,
                Arguments = $"2 1 0 -exe arma3_x64.exe -par=\"{parFilePath}\"",
                UseShellExecute = true
            };

            Process.Start(startInfo);

            if (ParamCloseOnLaunchCheck?.IsChecked == true)
            {
                Application.Current.Shutdown();
            }
        }
        // ================= RESILIO SYNC: ВСТРОЕННЫЙ ДВИЖОК И UI ЗАГРУЗКИ =================

        // Реальный полный размер сборки модов SolidGames в ГБ
        private const double ExpectedModpackSizeGb = 79.6;

        // Минимальный порог полностью скачанной сборки (97% от 79.6 ГБ = ~77.2 ГБ), чтобы не считать неполную папку готовой
        private const double MinCompleteModpackSizeGb = 77.2;

        private readonly Dictionary<string, string> _projectResilioKeys = new()
        {
            { "", "BAORERSOCOOW764LYFEVHPMOHUV5FW34M" }
        };

        private struct ModsIntegrityResult
        {
            public string CheckedPath;
            public bool HasDirectories;
            public bool HasPboFiles;
            public int IncompleteFilesCount;
            public long CompletedBytes;
            public long TempBytes;
            public double CompletedGb => CompletedBytes / (1024.0 * 1024.0 * 1024.0);
            public bool IsFullyReady => HasDirectories && HasPboFiles && IncompleteFilesCount == 0 && CompletedGb >= MinCompleteModpackSizeGb;
        }

        private ModsIntegrityResult _cachedModsIntegrity;
        private bool _isAutoSyncActive = false;
        private bool _isResilioPaused = false;
        private bool _isScanningProgress = false;
        private DateTime _resilioResumeGraceUntil = DateTime.MinValue;
        private long _lastProcessWriteBytes = -1;
        private DateTime _lastSpeedCheckTime = DateTime.MinValue;
        private double _smoothedSpeedMbPerSec = 0;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

        /// <summary>
        /// Глубокая проверка папки с модами: проверяет .pbo, отсутствие .!sync/.rsls и общий размер
        /// </summary>
        private static ModsIntegrityResult CheckModsFolderIntegrity(string folderPath)
        {
            var result = new ModsIntegrityResult { CheckedPath = folderPath };

            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return result;

            string coreDir = Path.Combine(folderPath, "@sg_core");
            string islandsDir = Path.Combine(folderPath, "@sg_islands");
            string modsDir = Path.Combine(folderPath, "@sg_mods");

            result.HasDirectories = Directory.Exists(coreDir) &&
                                    Directory.Exists(islandsDir) &&
                                    Directory.Exists(modsDir);

            int pboCount = 0;

            try
            {
                var dirInfo = new DirectoryInfo(folderPath);
                foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (file.FullName.Contains("\\.sync\\", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string ext = file.Extension;
                    if (ext.Equals(".!sync", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".rsls", StringComparison.OrdinalIgnoreCase))
                    {
                        result.IncompleteFilesCount++;
                        result.TempBytes += file.Length;
                    }
                    else
                    {
                        result.CompletedBytes += file.Length;
                        if (ext.Equals(".pbo", StringComparison.OrdinalIgnoreCase))
                            pboCount++;
                    }
                }
            }
            catch { }

            result.HasPboFiles = pboCount > 20;
            return result;
        }

        /// <summary>
        /// Полностью отключает проверку обновлений Resilio Sync в реестре Windows
        /// </summary>
        private static void DisableResilioUpdatePromptsInRegistry()
        {
            string[] registryPaths =
            {
                @"Software\Resilio, Inc.\Resilio Sync",
                @"Software\BitTorrent\Sync"
            };

            foreach (string regPath in registryPaths)
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(regPath);
                    key?.SetValue("CheckForUpdates", 0, Microsoft.Win32.RegistryValueKind.DWord);
                    key?.SetValue("check_for_updates", 0, Microsoft.Win32.RegistryValueKind.DWord);
                }
                catch { }
            }
        }

        private string EnsureResilioExtracted()
        {
            string resilioDir = Path.Combine(AppDataDirectory, "ResilioEngine");
            Directory.CreateDirectory(resilioDir);
            string targetExePath = Path.Combine(resilioDir, "Resilio Sync.exe");

            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("Resilio Sync.exe", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                throw new FileNotFoundException(
                    "Файл 'Resilio Sync.exe' не найден внутри ресурсов лаунчера. " +
                    "Убедитесь, что Build Action установлен в Embedded Resource.");
            }

            using (Stream resourceStream = assembly.GetManifestResourceStream(resourceName)!)
            {
                if (File.Exists(targetExePath) && new FileInfo(targetExePath).Length == resourceStream.Length)
                    return targetExePath;

                StopEmbeddedResilioProcess();

                using (FileStream fileStream = new FileStream(targetExePath, FileMode.Create, FileAccess.Write))
                {
                    resourceStream.CopyTo(fileStream);
                }
            }

            return targetExePath;
        }

        private static void StopEmbeddedResilioProcess()
        {
            foreach (var proc in Process.GetProcessesByName("Resilio Sync"))
            {
                try { proc.Kill(); proc.WaitForExit(1500); } catch { }
            }
        }

        /// <summary>
        /// Кнопка "УДАЛИТЬ МОДЫ"
        /// </summary>
        private async void DeleteResilioMods_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_resilioModsPath) || !Directory.Exists(_resilioModsPath))
            {
                MessageBox.Show("Папка с модами еще не выбрана.", "Удаление модов", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"Вы действительно хотите полностью удалить моды (@sg_core, @sg_islands, @sg_mods) из папки:\n{_resilioModsPath}?",
                "Подтверждение удаления модов",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                if (DeleteResilioModsBtn != null)
                {
                    DeleteResilioModsBtn.IsEnabled = false;
                    DeleteResilioModsBtn.Content = "УДАЛЕНИЕ...";
                }

                // Останавливаем процесс Resilio, чтобы он не держал файлы
                _isAutoSyncActive = false;
                _isResilioPaused = false;
                StopEmbeddedResilioProcess();

                string targetPath = _resilioModsPath;

                await Task.Run(() =>
                {
                    string[] subDirsToDelete = { "@sg_core", "@sg_islands", "@sg_mods", ".sync" };
                    foreach (string sub in subDirsToDelete)
                    {
                        string fullSubPath = Path.Combine(targetPath, sub);
                        if (Directory.Exists(fullSubPath))
                        {
                            try
                            {
                                var di = new DirectoryInfo(fullSubPath) { Attributes = FileAttributes.Normal };
                                foreach (var info in di.GetFileSystemInfos("*", SearchOption.AllDirectories))
                                    info.Attributes = FileAttributes.Normal;

                                Directory.Delete(fullSubPath, true);
                            }
                            catch { }
                        }
                    }
                });

                _cachedModsIntegrity = CheckModsFolderIntegrity(_resilioModsPath);
                UpdateResilioModsFolderUi();

                if (ResilioDownloadActivePanel != null) ResilioDownloadActivePanel.Visibility = Visibility.Collapsed;
                if (ResilioDownloadCompletePanel != null) ResilioDownloadCompletePanel.Visibility = Visibility.Collapsed;
                if (OneClickDownloadModsBtn != null) OneClickDownloadModsBtn.Content = "СКАЧАТЬ МОДЫ";

                MessageBox.Show("Моды проекта успешно удалены с диска.", "Удаление завершено", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при удалении модов:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (DeleteResilioModsBtn != null)
                {
                    DeleteResilioModsBtn.IsEnabled = true;
                    DeleteResilioModsBtn.Content = "УДАЛИТЬ МОДЫ";
                }
            }
        }

        /// <summary>
        /// Обработчик кнопки: "СКАЧАТЬ МОДЫ В 1 КЛИК" <-> "⏸ ПАУЗА" <-> "▶ ПРОДОЛЖИТЬ"
        /// </summary>
        private void OneClickDownloadMods_Click(object sender, RoutedEventArgs e)
        {
            // 1. Если загрузка идет -> ставим на ПАУЗУ
            if (_isAutoSyncActive && !_isResilioPaused)
            {
                _isResilioPaused = true;
                StopEmbeddedResilioProcess();

                if (OneClickDownloadModsBtn != null)
                    OneClickDownloadModsBtn.Content = "▶ ПРОДОЛЖИТЬ";

                if (ResilioSyncStatusTitle != null)
                {
                    ResilioSyncStatusTitle.Text = "⏸ Загрузка на паузе";
                    ResilioSyncStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(255, 183, 77));
                }

                if (ResilioSpeedText != null)
                    ResilioSpeedText.Text = "Скорость: Пауза";

                return;
            }

            // 2. Если стоит на паузе -> ПРОДОЛЖИТЬ (без мигания плашки завершения)
            if (_isAutoSyncActive && _isResilioPaused)
            {
                if (!string.IsNullOrEmpty(_resilioModsPath) && Directory.Exists(_resilioModsPath))
                {
                    _isResilioPaused = false;
                    // Защитный интервал 10 секунд на запуск процесса, чтобы статус не прыгал
                    _resilioResumeGraceUntil = DateTime.UtcNow.AddSeconds(10);

                    if (ResilioDownloadCompletePanel != null)
                        ResilioDownloadCompletePanel.Visibility = Visibility.Collapsed;
                    if (ResilioDownloadActivePanel != null)
                        ResilioDownloadActivePanel.Visibility = Visibility.Visible;

                    if (OneClickDownloadModsBtn != null)
                        OneClickDownloadModsBtn.Content = "⏸ ПАУЗА";

                    if (ResilioSyncStatusTitle != null)
                    {
                        ResilioSyncStatusTitle.Text = "⏳ Возобновление загрузки...";
                        ResilioSyncStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(41, 182, 246));
                    }

                    StartResilioAutoSync(_resilioModsPath);
                    return;
                }
            }

            // 3. Первый запуск ("СКАЧАТЬ МОДЫ В 1 КЛИК") — выбираем папку
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Выберите папку, куда будут скачиваться моды SolidGames"
            };

            if (dialog.ShowDialog() != true) return;

            string selectedFolder = dialog.FolderName;

            CheckAndHandleForeignSyncFolder(selectedFolder);

            _resilioModsPath = selectedFolder;
            SaveUserSettings();

            _isAutoSyncActive = true;
            _isResilioPaused = false;
            _resilioResumeGraceUntil = DateTime.UtcNow.AddSeconds(10);

            if (OneClickDownloadModsBtn != null)
                OneClickDownloadModsBtn.Content = "⏸ ПАУЗА";

            if (ResilioDownloadCompletePanel != null)
                ResilioDownloadCompletePanel.Visibility = Visibility.Collapsed;
            if (ResilioDownloadActivePanel != null)
                ResilioDownloadActivePanel.Visibility = Visibility.Visible;

            StartResilioAutoSync(selectedFolder);
            _ = UpdateResilioDownloadProgressUiAsync();
        }

        private void StartResilioAutoSync(string targetModsDirectory)
        {
            try
            {
                DisableResilioUpdatePromptsInRegistry();
                string resilioExe = EnsureResilioExtracted();

                string storagePath = Path.Combine(AppDataDirectory, "ResilioStorage");
                Directory.CreateDirectory(storagePath);

                var sharedFoldersJson = new List<string>();

                foreach (var kvp in _projectResilioKeys)
                {
                    string subFolder = string.IsNullOrEmpty(kvp.Key)
                        ? targetModsDirectory
                        : Path.Combine(targetModsDirectory, kvp.Key);

                    Directory.CreateDirectory(subFolder);

                    // Создаем маркер нашего лаунчера в папке .sync
                    string syncSubDir = Path.Combine(subFolder, ".sync");
                    Directory.CreateDirectory(syncSubDir);
                    File.WriteAllText(Path.Combine(syncSubDir, "sg_launcher.marker"), "SolidGamesLauncher");

                    string escapedDir = subFolder.Replace("\\", "\\\\");

                    sharedFoldersJson.Add($@"    {{
      ""secret"" : ""{kvp.Value}"",
      ""dir"" : ""{escapedDir}"",
      ""use_relay_server"" : true,
      ""use_tracker"" : true,
      ""search_lan"" : true,
      ""use_sync_trash"" : false,
      ""overwrite_changes"" : true
    }}");
                }

                string escapedStorage = storagePath.Replace("\\", "\\\\");
                string deviceName = $"SG_{Environment.UserName}";

                // Полное отключение GUI и любых уведомлений об обновлении Resilio
                string configContent = $@"{{
  ""device_name"": ""{deviceName}"",
  ""storage_path"": ""{escapedStorage}"",
  ""use_gui"": false,
  ""check_for_updates"": false,
  ""display_new_version"": false,
  ""rate_limit_local_peers"": false,
  ""use_upnp"": true,
  ""download_limit"": 0,
  ""upload_limit"": 0,
  ""shared_folders"": [
{string.Join(",\n", sharedFoldersJson)}
  ]
}}";

                string configFilePath = Path.Combine(AppDataDirectory, "sync.conf");
                File.WriteAllText(configFilePath, configContent);

                StopEmbeddedResilioProcess();

                Process.Start(new ProcessStartInfo
                {
                    FileName = resilioExe,
                    Arguments = $"/noinstall /minimized /S /config \"{configFilePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось запустить фоновую синхронизацию:\n{ex.Message}",
                                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private double GetResilioDownloadSpeedMbPerSec()
        {
            var procs = Process.GetProcessesByName("Resilio Sync");
            if (procs.Length == 0)
            {
                _lastProcessWriteBytes = -1;
                _smoothedSpeedMbPerSec = 0;
                return 0;
            }

            long totalWriteBytes = 0;
            foreach (var p in procs)
            {
                try
                {
                    if (GetProcessIoCounters(p.Handle, out IO_COUNTERS counters))
                        totalWriteBytes += (long)counters.WriteTransferCount;
                }
                catch { }
            }

            DateTime now = DateTime.UtcNow;
            if (_lastProcessWriteBytes < 0 || _lastSpeedCheckTime == DateTime.MinValue)
            {
                _lastProcessWriteBytes = totalWriteBytes;
                _lastSpeedCheckTime = now;
                return 0;
            }

            double seconds = (now - _lastSpeedCheckTime).TotalSeconds;
            if (seconds <= 0.2) return _smoothedSpeedMbPerSec;

            long deltaBytes = Math.Max(0, totalWriteBytes - _lastProcessWriteBytes);
            double currentSpeedMb = (deltaBytes / (1024.0 * 1024.0)) / seconds;

            _lastProcessWriteBytes = totalWriteBytes;
            _lastSpeedCheckTime = now;

            _smoothedSpeedMbPerSec = currentSpeedMb > 0.05
                ? (_smoothedSpeedMbPerSec * 0.4) + (currentSpeedMb * 0.6)
                : 0;

            return _smoothedSpeedMbPerSec;
        }

        private async Task UpdateResilioDownloadProgressUiAsync()
        {
            if (_isScanningProgress || ResilioDownloadActivePanel == null || ResilioDownloadCompletePanel == null)
                return;

            if (string.IsNullOrEmpty(_resilioModsPath) || !Directory.Exists(_resilioModsPath))
            {
                ResilioDownloadActivePanel.Visibility = Visibility.Collapsed;
                ResilioDownloadCompletePanel.Visibility = Visibility.Collapsed;
                if (OneClickDownloadModsBtn != null)
                    OneClickDownloadModsBtn.Content = "СКАЧАТЬ МОДЫ";
                return;
            }

            if (_isResilioPaused)
            {
                ResilioDownloadActivePanel.Visibility = Visibility.Visible;
                ResilioDownloadCompletePanel.Visibility = Visibility.Collapsed;
                if (OneClickDownloadModsBtn != null)
                    OneClickDownloadModsBtn.Content = "▶ ПРОДОЛЖИТЬ";
                return;
            }

            _isScanningProgress = true;

            try
            {
                double speedMb = GetResilioDownloadSpeedMbPerSec();
                var integrity = await Task.Run(() => CheckModsFolderIntegrity(_resilioModsPath));
                _cachedModsIntegrity = integrity;

                UpdateResilioModsFolderUi();

                bool isInGracePeriod = DateTime.UtcNow < _resilioResumeGraceUntil;
                bool isResilioRunning = Process.GetProcessesByName("Resilio Sync").Length > 0;

                // Считаем загрузку завершенной ТОЛЬКО если прошёл стартовый интервал,
                // нет файлов .!sync/.rsls, все .pbo на месте и размер >= 77.2 ГБ
                if (!isInGracePeriod && integrity.IsFullyReady && speedMb < 0.1)
                {
                    _isAutoSyncActive = false;
                    ResilioDownloadActivePanel.Visibility = Visibility.Collapsed;
                    ResilioDownloadCompletePanel.Visibility = Visibility.Visible;

                    if (OneClickDownloadModsBtn != null)
                        OneClickDownloadModsBtn.Content = "СКАЧАТЬ МОДЫ";

                    return;
                }

                // Если идет активное скачивание или сверка
                if (_isAutoSyncActive || isResilioRunning || integrity.IncompleteFilesCount > 0)
                {
                    _isAutoSyncActive = true;
                    ResilioDownloadCompletePanel.Visibility = Visibility.Collapsed;
                    ResilioDownloadActivePanel.Visibility = Visibility.Visible;

                    if (OneClickDownloadModsBtn != null)
                        OneClickDownloadModsBtn.Content = "⏸ ПАУЗА";

                    long expectedTotalBytes = (long)(ExpectedModpackSizeGb * 1024.0 * 1024.0 * 1024.0);
                    long knownTotalBytes = Math.Max(expectedTotalBytes, integrity.CompletedBytes + integrity.TempBytes);

                    long downloadedBytes = integrity.CompletedBytes;
                    long remainingBytes = Math.Max(0, knownTotalBytes - downloadedBytes);

                    double progressPercent = knownTotalBytes > 0
                        ? Math.Clamp((double)downloadedBytes / knownTotalBytes * 100.0, 0.0, 99.9)
                        : 0.0;

                    double downloadedGb = downloadedBytes / (1024.0 * 1024.0 * 1024.0);
                    double remainingGb = remainingBytes / (1024.0 * 1024.0 * 1024.0);

                    ResilioSyncStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(41, 182, 246));
                    ResilioSyncStatusTitle.Text = speedMb > 0.05
                        ? "⬇ Скачивание модов..."
                        : "⏳ Подключение к раздаче и сверка файлов...";

                    ResilioSpeedText.Text = $"Скорость: {speedMb:F1} МБ/с";
                    ResilioProgressBar.Value = progressPercent;
                    ResilioDownloadedText.Text = $"Скачано: {downloadedGb:F2} ГБ";
                    ResilioRemainingText.Text = $"Осталось: {remainingGb:F2} ГБ ({progressPercent:F0}%)";
                }
                else
                {
                    ResilioDownloadActivePanel.Visibility = Visibility.Collapsed;
                    ResilioDownloadCompletePanel.Visibility = Visibility.Collapsed;
                    if (OneClickDownloadModsBtn != null)
                        OneClickDownloadModsBtn.Content = "СКАЧАТЬ МОДЫ";
                }
            }
            finally
            {
                _isScanningProgress = false;
            }
        }


    }
}
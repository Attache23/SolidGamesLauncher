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
            _resilioCheckTimer.Tick += (_, _) =>
            {
                CheckResilioInstallation();
                if (string.IsNullOrEmpty(_resilioModsPath) || !HasAllResilioMods(_resilioModsPath))
                {
                    TryAutoDetectResilioModsFolder();
                    UpdateResilioModsFolderUi();
                }
            };
            _resilioCheckTimer.Start();

            _serverStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
            _serverStatusTimer.Tick += async (_, _) => await UpdateBothServersPlayerCountAsync();
            _serverStatusTimer.Start();
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
        private bool HasAllResilioMods(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return false;

            return Directory.Exists(Path.Combine(folderPath, "@sg_core"));
        }

        private void TryAutoDetectResilioModsFolder()
        {
            if (HasAllResilioMods(_resilioModsPath))
                return;

            if (HasAllResilioMods(_arma3Path))
            {
                _resilioModsPath = _arma3Path;
                SaveUserSettings();
                return;
            }

            string[] syncDatLocations =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Resilio Sync", "sync.dat"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Resilio Sync", "sync.dat")
            };

            foreach (string syncDat in syncDatLocations.Where(File.Exists))
            {
                try
                {
                    using var fs = new FileStream(syncDat, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    string content = sr.ReadToEnd();

                    MatchCollection matches = Regex.Matches(content, @"[A-Za-z]:\\[^""\0\r\n<>|?*]+");
                    foreach (Match match in matches)
                    {
                        string candidate = match.Value.TrimEnd('\\');
                        if (HasAllResilioMods(candidate))
                        {
                            _resilioModsPath = candidate;
                            SaveUserSettings();
                            return;
                        }
                    }
                }
                catch { }
            }
        }

        private void UpdateResilioModsFolderUi()
        {
            if (ResilioModsFolderText == null || ResilioModsCheckText == null) return;

            if (!string.IsNullOrWhiteSpace(_resilioModsPath) && Directory.Exists(_resilioModsPath))
            {
                ResilioModsFolderText.Text = $"Папка модов Resilio: {_resilioModsPath}";

                var found = _resilioMods.Where(m => Directory.Exists(Path.Combine(_resilioModsPath, m))).ToList();
                if (found.Count == _resilioMods.Count)
                {
                    ResilioModsCheckText.Text = "✓ Найдены все моды: @sg_core, @sg_islands, @sg_mods";
                    ResilioModsCheckText.Foreground = new SolidColorBrush(Color.FromRgb(104, 199, 107));
                }
                else if (found.Count > 0)
                {
                    ResilioModsCheckText.Text = $"Найдено ({found.Count} из 3): {string.Join(", ", found)} (докачайте остальные в Resilio)";
                    ResilioModsCheckText.Foreground = new SolidColorBrush(Color.FromRgb(255, 180, 68));
                }
                else
                {
                    ResilioModsCheckText.Text = "В выбранной папке пока нет @sg_core, @sg_islands, @sg_mods";
                    ResilioModsCheckText.Foreground = new SolidColorBrush(Color.FromRgb(255, 90, 90));
                }
            }
            else
            {
                ResilioModsFolderText.Text = "Папка модов Resilio: не выбрана";
                ResilioModsCheckText.Text = "Укажите папку, куда скачались моды Resilio";
                ResilioModsCheckText.Foreground = new SolidColorBrush(Color.FromRgb(255, 90, 90));
            }
            // Проверяем, выбрана ли папка с модами Resilio
            bool isResilioFolderReady = HasAllResilioMods(_resilioModsPath);

            // Показываем большую надпись во вкладке СЕРВЕРЫ, если папка не выбрана
            if (ResilioWarningBanner != null)
            {
                ResilioWarningBanner.Visibility = isResilioFolderReady ? Visibility.Collapsed : Visibility.Visible;
            }

            // Делаем кнопку ЗАПУСК серой (неактивной), пока папка не выбрана
            if (LaunchServerBtn != null) LaunchServerBtn.IsEnabled = isResilioFolderReady;
            if (LaunchNoServerBtn != null) LaunchNoServerBtn.IsEnabled = isResilioFolderReady;
        }

        private void SelectResilioModsFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Выберите папку, в которой находятся @sg_core, @sg_islands и @sg_mods"
            };

            if (dialog.ShowDialog() == true)
            {
                string selected = dialog.FolderName;

                string folderName = Path.GetFileName(selected.TrimEnd(Path.DirectorySeparatorChar));
                if (folderName.Equals("@sg_core", StringComparison.OrdinalIgnoreCase) ||
                    folderName.Equals("@sg_islands", StringComparison.OrdinalIgnoreCase) ||
                    folderName.Equals("@sg_mods", StringComparison.OrdinalIgnoreCase))
                {
                    selected = Path.GetDirectoryName(selected) ?? selected;
                }

                _resilioModsPath = selected;
                UpdateResilioModsFolderUi();
                SaveUserSettings();
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
                ResilioStatusText.Text = "Установлено (Готов к скачиванию модов)";
                ResilioStatusText.Foreground = new SolidColorBrush(Color.FromRgb(104, 199, 107));
                SyncResilioModsBtn.IsEnabled = true;
            }
            else
            {
                ResilioStatusText.Text = "Не установлено";
                ResilioStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 68, 68));
                SyncResilioModsBtn.IsEnabled = false;
            }

            // Одновременно проверяем установку TeamSpeak 3
            CheckTeamSpeakInstallation();
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
                    "3. После выбора папки в Resilio нажмите кнопку «ВЫБРАТЬ ПАПКУ С МОДАМИ (RESILIO)» в лаунчере.\n\n" +
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
    }
}
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.Graphics.Imaging;
using Windows.UI;
using MyMusic.Models;
using Track = MyMusic.Models.Track;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Globalization;
using Windows.Media.Core;
using Windows.UI.Popups;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.UI.Text;
using WinRT.Interop;
using Sharpcaster;
using Sharpcaster.Models;
using Sharpcaster.Models.Media;
using MyMusic.Services;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace MyMusic;

public sealed partial class MainWindow : Window
{
    private readonly List<Track> _libraryTracks = [];
    private readonly ObservableCollection<Track> _queue = [];
    private ObservableCollection<Track> _customPlaylistOrder = [];
    private readonly MediaPlayer _player = new();
    private readonly MediaPlayer _castingPlayer = new();
    private ChromecastClient? _castClient;
    private readonly CastMediaServer _castServer = new();
    private string? _castingDeviceName;
    private string? _selectedAudioDeviceId;
    private bool _isCasting;
    private bool _isStoppingCasting;
    private bool _castRequestInProgress;
    private bool _castStatusUpdateInProgress;
    private double _volumeBeforeCasting = 1.0;
    private MediaPlayer ActivePlayer => _isCasting ? _castingPlayer : _player;
    private DispatcherQueueTimer? _positionTimer;
    private int _queueIndex = -1;
    private long _currentTrackId = -1;
    private bool _shuffle;
    private bool _repeat;
    private bool _isSeeking;
    private bool _isVolumeSeeking;
    private bool _isMuted;
    private double _volumeBeforeMute = 1.0;
    private string _page = "Home";
    private string? _accent = "#1DB954";
    private bool _darkTheme = true;
    private string? _activePlaylist;
    private string? _queueSourceName;
    private string? _activeCollection;
    private bool _repeatPlaylist;
    private string _collectionSort = "Custom";
    private string _trackSortKey = "Title";
    private bool _trackSortAscending = true;
    private string _collectionCardSortKey = "Name";
    private bool _collectionCardSortAscending = true;
    private List<Track> _collectionTracks = [];
    private string _collectionViewMode = "Tiles";
    private readonly Random _random = new();
    private DispatcherQueueTimer? _toastTimer;
    private DispatcherQueueTimer? _fullscreenHideTimer;
    private AppWindow? _appWindow;
    private bool _isFullscreen;
    private Track? _editingTrack;
    private string? _editArtworkSourcePath;
    private string? _editArtworkWorkingPath;
    private string _editReturnPage = "Songs";
    private string? _editReturnCollection;
    private string? _editReturnPlaylist;
    private bool _editReturnWasDetail;
    private string _editReturnSearch = "";
    private double _editReturnScrollOffset;
    private bool _allowWindowClose;
    private bool _trayIconAdded;
    private IntPtr _trayIconHandle;
    private IntPtr _oldWndProc;
    private WndProcDelegate? _wndProcDelegate;
    private const uint TrayIconId = 1;
    private const uint TrayCallbackMessage = 0x8001;
    private static string RecentEditsPath => Path.Combine(App.DataRoot, "recent-edits.json");


    private sealed class CollectionCard
    {
        public string Name { get; init; } = "";
        public string? ArtworkUri { get; init; }
        public List<string> ArtworkUris { get; init; } = [];
        public bool UseSingleCover { get; init; } = true;
        public int Count { get; init; }
        public long DurationSeconds { get; init; }
        public DateTime? CreatedAtUtc { get; init; }
        public string CountText => $"{Count} {(Count == 1 ? "song" : "songs")}";
        public string DurationText => FormatTime(TimeSpan.FromSeconds(DurationSeconds));
        public string CreatedText => CreatedAtUtc is null ? "" : $"Created {RelativeAge(CreatedAtUtc.Value)}";
    }

    private static string RelativeAge(DateTime utc)
    {
        var age = DateTime.UtcNow - DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (age.TotalDays < 1) return "today";
        if (age.TotalDays < 2) return "1 day ago";
        if (age.TotalDays < 30) return $"{(int)age.TotalDays} days ago";
        if (age.TotalDays < 365) return $"{(int)(age.TotalDays / 30)} months ago";
        return $"{(int)(age.TotalDays / 365)} years ago";
    }

    private sealed class HomeCard
    {
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string? ArtworkUri { get; init; }
        public bool IsPlaylist { get; init; }
        public bool UseSingleCover { get; init; } = true;
        public List<Track> Tracks { get; init; } = [];
        public List<string> ArtworkUris { get; init; } = [];
    }

    public MainWindow()
    {
        InitializeComponent();
        var hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "MyMusic.ico");
        if (File.Exists(iconPath))
        {
            try { _appWindow.SetIcon(iconPath); } catch { }
        }
        InitializeTrayIcon(hwnd, iconPath);
        _appWindow.Closing += OnAppWindowClosing;
        _fullscreenHideTimer = DispatcherQueue.CreateTimer();
        _fullscreenHideTimer.Interval = TimeSpan.FromSeconds(3);
        _fullscreenHideTimer.Tick += (_, _) => HideFullscreenPlayerBar();
        LoadSettings();
        UpdateTrackHeaderLabels();
        QueueList.ItemsSource = _queue;
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
        _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        _castingPlayer.MediaEnded += OnMediaEnded;
        _castingPlayer.MediaFailed += OnMediaFailed;
        _castingPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        _castingPlayer.Volume = 0;
        _player.Volume = 1.0;
        _volumeBeforeMute = 1.0;
        SetVolumeVisual(1.0);
        _positionTimer = DispatcherQueue.CreateTimer();
        _positionTimer.Interval = TimeSpan.FromMilliseconds(50);
        _positionTimer.Tick += (_, _) => UpdateProgress();
        _positionTimer.Start();
        Closed += (_, _) => DisposePlayer();
        SizeChanged += (_, _) => UpdateProgressVisuals(ActivePlayer.PlaybackSession.Position.TotalSeconds, ActivePlayer.PlaybackSession.NaturalDuration.TotalSeconds);
    }

    public async Task OnDatabaseReadyAsync() => await LoadLibraryAsync();

    public void ShowStartupError(Exception ex) => ShowToast($"Could not initialize the music database: {ex.Message}");

    private async Task LoadLibraryAsync(bool refreshMissing = false)
    {
        if (refreshMissing)
        {
            var removed = await App.Database.RemoveMissingTracksAsync();
            ShowToast(removed == 0 ? "Library is up to date." : $"Removed {removed} missing track{(removed == 1 ? "" : "s")}");
        }

        _libraryTracks.Clear();
        _libraryTracks.AddRange(await App.Database.GetTracksAsync());
        // Recover covers for tracks imported before embedded-art extraction was fixed.
        await App.Library.RepairMissingArtworkAsync(_libraryTracks);
        await ShowPageAsync(_page);
    }

    private async Task ShowPageAsync(string page)
    {
        _page = page;
        DetailPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        PageTitle.Visibility = page == "Search" ? Visibility.Collapsed : Visibility.Visible;
        SearchShell.Visibility = page == "Search" ? Visibility.Visible : Visibility.Collapsed;
        HomePanel.Visibility = page == "Home" ? Visibility.Visible : Visibility.Collapsed;
        TrackList.Visibility = page is "Home" or "Songs" or "Search" ? Visibility.Visible : Visibility.Collapsed;
        TrackSortHeader.Visibility = page is "Songs" or "Search" ? Visibility.Visible : Visibility.Collapsed;
        DetailTrackSortHeader.Visibility = Visibility.Collapsed;
        CollectionList.Visibility = page is "Albums" or "Artists" or "Playlists" ? Visibility.Visible : Visibility.Collapsed;
        CollectionListView.Visibility = Visibility.Collapsed;
        CollectionListHeader.Visibility = Visibility.Collapsed;
        CollectionViewControls.Visibility = page is "Albums" or "Artists" or "Playlists" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page;

        if (page == "Home")
        {
            _activePlaylist = null;
            UpdateQueueFooter();
            PageTitle.Text = "Home";
            RecentList.ItemsSource = await BuildHomeCardsAsync();
            TrackList.ItemsSource = _libraryTracks.OrderByDescending(t => t.ImportedAtUtc).Take(10).ToList();
            UpdateNavVisuals();
            return;
        }


        if (page == "Songs")
        {
            _activePlaylist = null;
            UpdateQueueFooter();
            TrackList.ItemsSource = SortTracks(_libraryTracks, _trackSortKey, _trackSortAscending).ToList();
            UpdateNavVisuals();
            return;
        }

        if (page == "Search")
        {
            _activePlaylist = null;
            UpdateQueueFooter();
            TrackList.ItemsSource = SortTracks(_libraryTracks, _trackSortKey, _trackSortAscending).ToList();
            UpdateNavVisuals();
            return;
        }

        DetailPanel.Visibility = Visibility.Collapsed;
        CollectionList.Visibility = Visibility.Visible;
        if (page == "Albums")
        {
            _activePlaylist = null;
            UpdateQueueFooter();
            var cards = _libraryTracks.Where(t => !string.IsNullOrWhiteSpace(t.Album)).GroupBy(t => t.Album, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key).Select(g => new CollectionCard { Name = g.Key, ArtworkUri = g.Select(t => t.ArtworkUri).FirstOrDefault(u => u is not null), ArtworkUris = g.Select(t => t.ArtworkUri).Where(u => !string.IsNullOrWhiteSpace(u)).Take(1).Cast<string>().ToList(), UseSingleCover = true, Count = g.Count(), DurationSeconds = g.Sum(t => t.DurationSeconds) }).ToList();
            CollectionList.ItemsSource = cards; CollectionListView.ItemsSource = cards; ApplyCollectionViewMode();
        }
        else if (page == "Artists")
        {
            _activePlaylist = null;
            UpdateQueueFooter();
            var artistEntries = _libraryTracks
                .SelectMany(t => SplitArtists(t.Artist).Select(artist => new { Artist = artist, Track = t }))
                .GroupBy(entry => entry.Artist, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
            var cards = artistEntries.Select(g =>
            {
                var tracks = g.Select(entry => entry.Track).DistinctBy(t => t.Id).ToList();
                return new CollectionCard { Name = g.Key, ArtworkUri = tracks.Select(t => t.ArtworkUri).FirstOrDefault(u => u is not null), ArtworkUris = tracks.Select(t => t.ArtworkUri).Where(u => !string.IsNullOrWhiteSpace(u)).Take(1).Cast<string>().ToList(), UseSingleCover = true, Count = tracks.Count, DurationSeconds = tracks.Sum(t => t.DurationSeconds) };
            }).ToList();
            CollectionList.ItemsSource = cards; CollectionListView.ItemsSource = cards; ApplyCollectionViewMode();
        }
        else
        {
            var names = await App.Database.GetPlaylistNamesAsync();
            var cards = new List<CollectionCard>();
            foreach (var name in names)
            {
                var tracks = await App.Database.GetPlaylistTracksAsync(name);
                var created = await App.Database.GetPlaylistCreatedAtUtcAsync(name);
                var cover = await App.Database.GetPlaylistCoverPathAsync(name);
                var hasExplicitCover = cover is not null && File.Exists(cover);
                var coverUris = hasExplicitCover
                    ? new List<string> { new Uri(Path.GetFullPath(cover!)).AbsoluteUri }
                    : tracks.Where(t => !string.IsNullOrWhiteSpace(t.ArtworkUri)).Select(t => t.ArtworkUri!).Take(4).ToList();
                cards.Add(new CollectionCard { Name = name, ArtworkUri = hasExplicitCover ? new Uri(Path.GetFullPath(cover!)).AbsoluteUri : coverUris.FirstOrDefault(), ArtworkUris = coverUris, UseSingleCover = hasExplicitCover, Count = tracks.Count, DurationSeconds = tracks.Sum(t => t.DurationSeconds), CreatedAtUtc = created });
            }
            CollectionList.ItemsSource = cards; CollectionListView.ItemsSource = cards; ApplyCollectionViewMode();
        }
        UpdateNavVisuals();
    }

    private async Task RefreshPlaylistCoverDisplaysAsync()
    {
        var names = await App.Database.GetPlaylistNamesAsync();
        var cards = new List<CollectionCard>();
        foreach (var name in names)
        {
            var tracks = await App.Database.GetPlaylistTracksAsync(name);
            var created = await App.Database.GetPlaylistCreatedAtUtcAsync(name);
            var cover = await App.Database.GetPlaylistCoverPathAsync(name);
            var hasExplicitCover = cover is not null && File.Exists(cover);
            var coverUris = hasExplicitCover
                ? new List<string> { new Uri(Path.GetFullPath(cover!)).AbsoluteUri }
                : tracks.Where(t => !string.IsNullOrWhiteSpace(t.ArtworkUri)).Select(t => t.ArtworkUri!).Take(4).ToList();
            cards.Add(new CollectionCard { Name = name, ArtworkUri = coverUris.FirstOrDefault(), ArtworkUris = coverUris, UseSingleCover = hasExplicitCover, Count = tracks.Count, DurationSeconds = tracks.Sum(t => t.DurationSeconds), CreatedAtUtc = created });
        }
        CollectionList.ItemsSource = cards;
        CollectionListView.ItemsSource = cards;
        RecentList.ItemsSource = await BuildHomeCardsAsync();
    }

    private async Task<List<HomeCard>> BuildHomeCardsAsync()
    {
        var cards = new List<HomeCard>();
        foreach (var t in _libraryTracks.OrderByDescending(t => t.ImportedAtUtc).Take(2))
            cards.Add(new HomeCard { Title = t.Title, Subtitle = t.Artist, ArtworkUri = t.ArtworkUri, ArtworkUris = t.ArtworkUri is null ? [] : [t.ArtworkUri], Tracks = [t] });
        foreach (var name in (await App.Database.GetPlaylistNamesAsync()).Take(2))
        {
            var tracks = await App.Database.GetPlaylistTracksAsync(name);
            if (tracks.Count == 0) continue;
            var cover = await App.Database.GetPlaylistCoverPathAsync(name);
            var hasExplicitCover = cover is not null && File.Exists(cover);
            var coverUris = hasExplicitCover
                ? new List<string> { new Uri(Path.GetFullPath(cover!)).AbsoluteUri }
                : tracks.Where(t => !string.IsNullOrWhiteSpace(t.ArtworkUri)).Select(t => t.ArtworkUri!).Take(4).ToList();
            cards.Add(new HomeCard { Title = name, Subtitle = "Playlist", ArtworkUri = coverUris.FirstOrDefault(), ArtworkUris = coverUris, UseSingleCover = hasExplicitCover, IsPlaylist = true, Tracks = tracks });
        }
        return cards.Take(4).ToList();
    }

    private void UpdateNavVisuals()
    {
        var active = new[] { HomeNav, SearchNav, SongsNav, AlbumsNav, ArtistsNav, PlaylistsNav };
        foreach (var b in active)
        {
            var isActive = IsActiveNav(b);
            b.BorderThickness = isActive ? new Thickness(1) : new Thickness(0);
            b.BorderBrush = new SolidColorBrush((Application.Current.Resources[isActive ? "SectionBorderBrush" : "Surface3Brush"] as SolidColorBrush)?.Color ?? ParseColor(_accent!));
            b.Background = isActive
                ? new SolidColorBrush((Application.Current.Resources["SectionActiveBrush"] as SolidColorBrush)?.Color ?? ParseColor(_accent!))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }
    }

    private void OnHomeNavigation(object sender, RoutedEventArgs e) => _ = ShowPageAsync("Home");
    private void OnSearchNavigation(object sender, RoutedEventArgs e) { _ = ShowPageAsync("Search"); SearchBox.Focus(FocusState.Programmatic); }
    private void OnSongsNavigation(object sender, RoutedEventArgs e) => _ = ShowPageAsync("Songs");
    private void OnAlbumsNavigation(object sender, RoutedEventArgs e) => _ = ShowPageAsync("Albums");
    private void OnArtistsNavigation(object sender, RoutedEventArgs e) => _ = ShowPageAsync("Artists");
    private void OnPlaylistsNavigation(object sender, RoutedEventArgs e) => _ = ShowPageAsync("Playlists");
    private void OnBack(object sender, RoutedEventArgs e) => _ = ShowPageAsync("Home");

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        AddMenu.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
        LibraryLocationText.Text = App.Library.LibraryRoot;
        BuildAccentOptions();
    }

    private void OnCloseSettings(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = Visibility.Collapsed;


    private void BuildAccentOptions()
    {
        AccentOptionsPanel.Children.Clear();
        foreach (var hex in new[] { "#1DB954", "#8B5CF6", "#3B82F6", "#F97316", "#EC4899", "#EF4444", "#14B8A6" })
        {
            var b = new Button
            {
                Tag = hex, Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
                Background = new SolidColorBrush(ParseColor(hex)), BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(ParseColor(hex)), Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
            };
            b.Click += (_, _) => ApplyAccent(hex);
            AccentOptionsPanel.Children.Add(b);
        }
    }

    private void LoadSettings()
    {
        var dir = App.DataRoot;
        var accentFile = Path.Combine(dir, "settings.txt");
        var themeFile = Path.Combine(dir, "theme.txt");
        _accent = File.Exists(accentFile) ? File.ReadAllText(accentFile).Trim() : "#1DB954";
        if (!IsHex(_accent)) _accent = "#1DB954";
        _darkTheme = !File.Exists(themeFile) || !string.Equals(File.ReadAllText(themeFile).Trim(), "Light", StringComparison.OrdinalIgnoreCase);
        ApplyThemeAndAccent(false);
    }

    private void ApplyAccent(string hex, bool save = true)
    {
        if (!IsHex(hex)) return;
        _accent = hex;
        ApplyThemeAndAccent(save);
        ShowToast("Accent color updated");
    }

    private void ApplyThemeAndAccent(bool save)
    {
        var accent = ParseColor(_accent!);
        var white = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        var darkGray = Windows.UI.Color.FromArgb(255, 31, 34, 39);
        // Dark mode stays muted and grayish: the accent is visible as a tint rather than
        // turning the entire interface into a saturated color field. The sidebar/player
        // are intentionally a little stronger than the main content.
        var lightAccent = Blend(accent, white, 0.12);
        var darkAccent = Blend(accent, Windows.UI.Color.FromArgb(255, 12, 14, 18), 0.28);
        SetBrushColor("AccentBrush", accent);
        SetBrushColor("AccentLightBrush", lightAccent);
        SetBrushColor("AccentDarkBrush", darkAccent);
        // Dark mode is intentionally near-black in the main content, with a restrained
        // but saturated accent tint retained by the sidebar/player. Light mode stays airy.
        var black = Windows.UI.Color.FromArgb(255, 10, 12, 15);
        SetBrushColor("AppBackgroundBrush", _darkTheme ? Blend(accent, black, 0.90) : Blend(accent, white, 0.92));
        SetBrushColor("SidebarBrush", _darkTheme ? Blend(accent, black, 0.70) : Blend(accent, white, 0.80));
        SetBrushColor("SurfaceBrush", _darkTheme ? Blend(accent, black, 0.86) : Blend(accent, white, 0.90));
        SetBrushColor("Surface2Brush", _darkTheme ? Blend(accent, black, 0.79) : Blend(accent, white, 0.84));
        SetBrushColor("Surface3Brush", _darkTheme ? Blend(accent, black, 0.70) : Blend(accent, white, 0.76));
        SetBrushColor("SectionHoverBrush", _darkTheme ? Blend(accent, black, 0.56) : Blend(accent, white, 0.70));
        SetBrushColor("SectionActiveBrush", _darkTheme ? Blend(accent, black, 0.48) : Blend(accent, white, 0.62));
        SetBrushColor("SectionBorderBrush", _darkTheme ? Blend(accent, black, 0.62) : Blend(accent, white, 0.58));
        SetBrushColor("PrimaryTextBrush", ParseColor(_darkTheme ? "#F7F7F7" : "#171717"));
        SetBrushColor("SecondaryTextBrush", ParseColor(_darkTheme ? "#D1D5DB" : "#252525"));
        SetBrushColor("MutedTextBrush", ParseColor(_darkTheme ? "#A8ADB9" : "#4D4D4D"));

        // Controls with local values need their values refreshed explicitly.
        AddButton.Background = new SolidColorBrush(accent);
        PlayPauseButton.Background = new SolidColorBrush(accent);
        QueuePanel.BorderBrush = new SolidColorBrush(accent);
        Sidebar.BorderBrush = new SolidColorBrush(accent);
        PlayerBar.BorderBrush = new SolidColorBrush(_darkTheme ? Blend(accent, darkGray, 0.45) : Blend(accent, white, 0.68));
        AddMenu.Background = new SolidColorBrush(_darkTheme ? Blend(accent, darkGray, 0.72) : Blend(accent, white, 0.84));
        SettingsPanel.Background = new SolidColorBrush(_darkTheme ? Blend(accent, darkGray, 0.78) : Blend(accent, white, 0.92));
        Toast.BorderBrush = new SolidColorBrush(accent);
        ProgressTrack.Background = new SolidColorBrush(_darkTheme ? Blend(accent, black, 0.42) : Blend(accent, white, 0.58));
        ProgressFill.Background = new SolidColorBrush(accent);
        ProgressPlayhead.Fill = new SolidColorBrush(accent);

        // Keep the window's actual XAML theme synchronized too, so native controls such as
        // TextBox placeholders and popup chrome follow light/dark mode.
        Root.RequestedTheme = _darkTheme ? ElementTheme.Dark : ElementTheme.Light;
        UpdateSortButtonVisuals();
        ApplyCollectionViewMode();
        UpdateNavVisuals();
        SetVolumeVisual(_player.Volume);

        if (save)
        {
            var dir = App.DataRoot;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.txt"), _accent);
            File.WriteAllText(Path.Combine(dir, "theme.txt"), _darkTheme ? "Dark" : "Light");
        }
    }

    private static void SetBrushColor(string key, Windows.UI.Color color)
    {
        if (Application.Current.Resources[key] is SolidColorBrush brush)
            brush.Color = color;
        else
            Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private void OnThemeButton(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string theme) return;
        _darkTheme = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
        ApplyThemeAndAccent(true);
        ShowToast($"{theme} theme selected");
    }

    private static bool IsHex(string? value) => value is not null && value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static Windows.UI.Color ParseColor(string hex) => Windows.UI.Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
    private static Windows.UI.Color Blend(Windows.UI.Color a, Windows.UI.Color b, double amount) => Windows.UI.Color.FromArgb(255, (byte)(a.R + (b.R - a.R) * amount), (byte)(a.G + (b.G - a.G) * amount), (byte)(a.B + (b.B - a.B) * amount));

    private async void OnRefreshLibrary(object sender, RoutedEventArgs e) => await LoadLibraryAsync(true);

    private void OnImportMenu(object sender, RoutedEventArgs e) => AddMenu.Visibility = AddMenu.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    private async void OnImportSongsMenu(object sender, RoutedEventArgs e) { AddMenu.Visibility = Visibility.Collapsed; await PickSongsAsync(); }
    private async void OnImportFolderMenu(object sender, RoutedEventArgs e) { AddMenu.Visibility = Visibility.Collapsed; await PickFolderAsync(); }
    private async void OnCreatePlaylistMenu(object sender, RoutedEventArgs e) { AddMenu.Visibility = Visibility.Collapsed; await CreatePlaylistAsync(); }

    private async Task PickSongsAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        foreach (var ext in new[] { ".mp3", ".flac", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".wma", ".aiff" }) picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        if (files is not null && files.Count > 0) await ImportPathsAsync(files.Select(f => f.Path));
    }

    private async Task PickFolderAsync()
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) await ImportPathsAsync([folder.Path]);
    }

    private void OnDragOver(object sender, DragEventArgs e) => e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        await ImportPathsAsync(items.Select(i => i.Path));
    }

    private async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        var result = await App.Library.ImportAsync(paths);
        await LoadLibraryAsync();
        ShowToast($"Imported {result.Imported} track{(result.Imported == 1 ? "" : "s")}");
    }

    private async void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_page != "Search") return;
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query)) { TrackList.ItemsSource = SortTracks(_libraryTracks, _trackSortKey, _trackSortAscending).ToList(); return; }
        var tracks = _libraryTracks.Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || t.Artist.Contains(query, StringComparison.OrdinalIgnoreCase) || t.Album.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        TrackList.ItemsSource = SortTracks(tracks, _trackSortKey, _trackSortAscending).ToList();
    }

    private async void OnTrackTapped(object sender, TappedRoutedEventArgs e)
    {
        // Menu/artist buttons live inside the track row. Their clicks must never start playback.
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if ((sender as FrameworkElement)?.DataContext is not Track track) return;
        var source = DetailPanel.Visibility == Visibility.Visible
            ? (_collectionSort == "Custom" && _page == "Playlists" ? CustomOrderListView.Items.Cast<Track>().ToList() : StandardCollectionList.ItemsSource as IEnumerable<Track>) ?? _collectionTracks
            : ((IEnumerable<Track>?)TrackList.ItemsSource)?.ToList() ?? _libraryTracks;
        SetQueueFromTracks(source, track, DetailPanel.Visibility == Visibility.Visible ? _activeCollection : null);
        await PlayTrackAsync(track);
    }

    private async void OnRecentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not HomeCard card || card.Tracks.Count == 0) return;
        SetQueueFromTracks(card.Tracks, card.Tracks[0], card.IsPlaylist ? card.Title : null);
        await PlayTrackAsync(card.Tracks[0]);
    }

    private void OnCollectionViewMode(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string mode) { _collectionViewMode = mode; ApplyCollectionViewMode(); }
    }

    private void ApplyCollectionViewMode()
    {
        var tiles = _collectionViewMode == "Tiles";
        CollectionList.Visibility = tiles ? Visibility.Visible : Visibility.Collapsed;
        CollectionListView.Visibility = tiles ? Visibility.Collapsed : Visibility.Visible;
        CollectionListHeader.Visibility = tiles ? Visibility.Collapsed : Visibility.Visible;
        CollectionTileButton.Visibility = Visibility.Visible;
        CollectionListButton.Visibility = Visibility.Visible;
        CollectionTileButton.Background = new SolidColorBrush(tiles ? ParseColor(_accent!) : ParseColor(_darkTheme ? "#30343C" : "#E1E3E7"));
        CollectionListButton.Background = new SolidColorBrush(!tiles ? ParseColor(_accent!) : ParseColor(_darkTheme ? "#30343C" : "#E1E3E7"));
    }

    private async void OnCollectionButtonClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name) return;
        _activeCollection = name;
        _activePlaylist = _page == "Playlists" ? name : null;
        _collectionSort = _page == "Playlists" ? "Custom" : "Name";
        if (_page == "Playlists")
            _collectionTracks = await App.Database.GetPlaylistTracksAsync(name);
        else if (_page == "Albums")
            _collectionTracks = _libraryTracks.Where(t => string.Equals(t.Album, name, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
        else
            _collectionTracks = _libraryTracks.Where(t => SplitArtists(t.Artist).Any(artist => string.Equals(artist, name, StringComparison.OrdinalIgnoreCase))).OrderBy(t => t.Album).ThenBy(t => t.TrackNumber).ToList();
        CollectionList.Visibility = Visibility.Collapsed;
        HomePanel.Visibility = Visibility.Collapsed;
        TrackList.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        PageTitle.Text = name;
        DetailSubtitle.Text = $"{_collectionTracks.Count} tracks • {FormatTime(TimeSpan.FromSeconds(_collectionTracks.Sum(t => t.DurationSeconds)))}";
        DetailTitle.Text = name;
        DetailType.Text = _page == "Albums" ? "ALBUM" : _page == "Artists" ? "ARTIST" : "PLAYLIST";
        DetailTrackSortHeader.Visibility = Visibility.Visible;
        DetailSortBar.Visibility = _page == "Artists" ? Visibility.Collapsed : Visibility.Visible;
        CustomSortButton.Visibility = _page == "Playlists" ? Visibility.Visible : Visibility.Collapsed;
        PlaylistControls.Visibility = _page == "Playlists" ? Visibility.Visible : Visibility.Collapsed;
        PlaylistCoverActions.Visibility = _page == "Playlists" ? Visibility.Visible : Visibility.Collapsed;
        PlaylistRepeatDot.Visibility = Visibility.Collapsed;
        PlaylistShuffleDot.Visibility = Visibility.Collapsed;
        if (_page == "Playlists")
        {
            var cover = await App.Database.GetPlaylistCoverPathAsync(name);
            await RenderCollectionCoverAsync(_collectionTracks, cover);
        }
        else
            await RenderCollectionCoverAsync(_collectionTracks, _collectionTracks.FirstOrDefault()?.ArtworkPath, _page == "Albums");
        ApplyCollectionSort();
    }

    private static IEnumerable<string> SplitArtists(string? artistText)
    {
        if (string.IsNullOrWhiteSpace(artistText))
            return [];

        // Music metadata commonly stores collaborating artists as a comma- or semicolon-separated list.
        // Trim names and de-duplicate case-insensitively so "Bob, bob" only counts once for Bob.
        return artistText
            .Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private async void OnArtistClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Track track) return;
        await ShowArtistAsync(SplitArtists(track.Artist).FirstOrDefault() ?? track.Artist);
    }

    private async Task ShowArtistAsync(string artist)
    {
        _page = "Artists";
        _activePlaylist = null;
        _activeCollection = artist;
        _collectionTracks = _libraryTracks.Where(t => SplitArtists(t.Artist).Any(name => string.Equals(name, artist, StringComparison.OrdinalIgnoreCase))).OrderBy(t => t.Album).ThenBy(t => t.TrackNumber).ToList();
        HomePanel.Visibility = Visibility.Collapsed; CollectionList.Visibility = Visibility.Collapsed; TrackList.Visibility = Visibility.Collapsed; DetailPanel.Visibility = Visibility.Visible;
        PageTitle.Text = artist; DetailType.Text = "ARTIST"; DetailTitle.Text = artist; DetailSubtitle.Text = $"{_collectionTracks.Count} tracks • {FormatTime(TimeSpan.FromSeconds(_collectionTracks.Sum(t => t.DurationSeconds)))}";
        DetailSortBar.Visibility = Visibility.Collapsed; DetailTrackSortHeader.Visibility = Visibility.Visible; CustomSortButton.Visibility = Visibility.Collapsed; PlaylistControls.Visibility = Visibility.Collapsed; PlaylistCoverActions.Visibility = Visibility.Collapsed;
        await RenderCollectionCoverAsync(_collectionTracks, _collectionTracks.FirstOrDefault()?.ArtworkPath, false);
        ApplyCollectionSort();
    }

    private void OnCollectionBack(object sender, RoutedEventArgs e) => _ = ShowPageAsync(_page);


    private void OnTrackColumnSort(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string key) return;
        if (string.Equals(_trackSortKey, key, StringComparison.OrdinalIgnoreCase))
            _trackSortAscending = !_trackSortAscending;
        else
        {
            _trackSortKey = key;
            _trackSortAscending = true;
        }

        if (DetailPanel.Visibility == Visibility.Visible)
        {
            _collectionSort = key == "Title" ? "Name" : key;
            ApplyCollectionSort();
        }
        else
        {
            var source = (TrackList.ItemsSource as IEnumerable<Track>)?.ToList() ?? _libraryTracks.ToList();
            TrackList.ItemsSource = SortTracks(source, _trackSortKey, _trackSortAscending).ToList();
        }
        UpdateTrackHeaderLabels();
    }

    private static IOrderedEnumerable<Track> SortTracks(IEnumerable<Track> source, string key, bool ascending) => key switch
    {
        "Artist" => ascending ? source.OrderBy(t => t.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase) : source.OrderByDescending(t => t.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase),
        "Album" => ascending ? source.OrderBy(t => t.Album, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase) : source.OrderByDescending(t => t.Album, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase),
        "Duration" => ascending ? source.OrderBy(t => t.DurationSeconds).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase) : source.OrderByDescending(t => t.DurationSeconds).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase),
        _ => ascending ? source.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase) : source.OrderByDescending(t => t.Title, StringComparer.OrdinalIgnoreCase)
    };

    private void UpdateTrackHeaderLabels()
    {
        static string Label(string name, string key, bool ascending) => string.Equals(name, key, StringComparison.OrdinalIgnoreCase) ? $"{name} {(ascending ? "↑" : "↓")}" : name;
        TrackTitleHeaderText.Text = Label("Title", _trackSortKey, _trackSortAscending);
        TrackArtistHeaderText.Text = Label("Artist", _trackSortKey, _trackSortAscending);
        TrackAlbumHeaderText.Text = Label("Album", _trackSortKey, _trackSortAscending);
        TrackDurationHeaderText.Text = Label("Duration", _trackSortKey, _trackSortAscending);
        DetailTrackTitleHeaderText.Text = TrackTitleHeaderText.Text;
        DetailTrackArtistHeaderText.Text = TrackArtistHeaderText.Text;
        DetailTrackAlbumHeaderText.Text = TrackAlbumHeaderText.Text;
        DetailTrackDurationHeaderText.Text = TrackDurationHeaderText.Text;
    }

    private void OnCollectionCardSort(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string key) return;
        if (string.Equals(_collectionCardSortKey, key, StringComparison.OrdinalIgnoreCase))
            _collectionCardSortAscending = !_collectionCardSortAscending;
        else
        {
            _collectionCardSortKey = key;
            _collectionCardSortAscending = true;
        }
        var cards = (CollectionListView.ItemsSource as IEnumerable<CollectionCard>)?.ToList() ?? [];
        IEnumerable<CollectionCard> sorted = key switch
        {
            "Count" => _collectionCardSortAscending ? cards.OrderBy(c => c.Count) : cards.OrderByDescending(c => c.Count),
            "Duration" => _collectionCardSortAscending ? cards.OrderBy(c => c.DurationSeconds) : cards.OrderByDescending(c => c.DurationSeconds),
            _ => _collectionCardSortAscending ? cards.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase) : cards.OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase)
        };
        CollectionListView.ItemsSource = sorted.ToList();
        CollectionTitleHeaderText.Text = key == "Name" ? $"Title {(_collectionCardSortAscending ? "↑" : "↓")}" : "Title";
        CollectionCountHeaderText.Text = key == "Count" ? $"Songs {(_collectionCardSortAscending ? "↑" : "↓")}" : "Songs";
        CollectionDurationHeaderText.Text = key == "Duration" ? $"Duration {(_collectionCardSortAscending ? "↑" : "↓")}" : "Duration";
    }

    private void OnCollectionSort(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string sort) { _collectionSort = sort; ApplyCollectionSort(); }
    }

    private void UpdateSortButtonVisuals()
    {
        foreach (var child in DetailSortBar.Children)
        {
            if (child is not Button button || button.Tag is not string tag) continue;
            var selected = string.Equals(tag, _collectionSort, StringComparison.OrdinalIgnoreCase);
            var surface = ((SolidColorBrush)Application.Current.Resources["Surface2Brush"]).Color;
            button.Background = new SolidColorBrush(ParseColor(selected ? _accent! : $"#{surface.R:X2}{surface.G:X2}{surface.B:X2}"));
            button.Foreground = new SolidColorBrush(ParseColor(selected ? (IsLightColor(ParseColor(_accent!)) ? "#111318" : "#F7F7F7") : (_darkTheme ? "#D1D5DB" : "#252525")));
        }
    }

    private static bool IsLightColor(Windows.UI.Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 160;

    private void ApplyCollectionSort()
    {
        IEnumerable<Track> sorted = _collectionTracks;
        if (_collectionSort == "Duration") sorted = _trackSortKey == "Duration" ? SortTracks(_collectionTracks, "Duration", _trackSortAscending) : _collectionTracks.OrderByDescending(t => t.DurationSeconds);
        else if (_collectionSort == "Name") sorted = SortTracks(_collectionTracks, "Title", _trackSortAscending);
        else if (_collectionSort == "Artist") sorted = SortTracks(_collectionTracks, "Artist", _trackSortAscending);
        else if (_collectionSort == "Album") sorted = SortTracks(_collectionTracks, "Album", _trackSortAscending);
        else sorted = _collectionTracks;
        StandardCollectionList.ItemsSource = sorted.ToList();
        CustomOrderListView.Visibility = _page == "Playlists" && _collectionSort == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        StandardCollectionList.Visibility = CustomOrderListView.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (CustomOrderListView.Visibility == Visibility.Visible)
        {
            _customPlaylistOrder = new ObservableCollection<Track>(_collectionTracks);
            CustomOrderListView.ItemsSource = _customPlaylistOrder;
        }
        UpdateSortButtonVisuals();
    }

    private async void OnCustomPlaylistReordered(object sender, DragItemsCompletedEventArgs e)
    {
        _collectionTracks = _customPlaylistOrder.ToList();

        if (_activePlaylist is not null)
            await App.Database.SavePlaylistOrderAsync(_activePlaylist, _collectionTracks.Select(t => t.Id).ToList());

        StandardCollectionList.ItemsSource = _collectionTracks;
        ShowToast("Playlist order saved");
    }

    private Task RenderCollectionCoverAsync(IReadOnlyList<Track> tracks, string? explicitCover, bool singleCover = false)
    {
        PlaylistCoverGrid.Children.Clear();
        var coverPaths = new List<string>();
        var useSingle = false;
        if (!string.IsNullOrWhiteSpace(explicitCover) && File.Exists(explicitCover))
        {
            coverPaths.Add(new Uri(Path.GetFullPath(explicitCover)).AbsoluteUri);
            useSingle = true;
        }
        else if (singleCover)
        {
            var source = tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.ArtworkUri))?.ArtworkUri;
            if (!string.IsNullOrWhiteSpace(source)) coverPaths.Add(source);
            useSingle = true;
        }
        else
        {
            coverPaths.AddRange(tracks.Where(t => !string.IsNullOrWhiteSpace(t.ArtworkUri)).Select(t => t.ArtworkUri!).Take(4));
        }

        AddCoverImages(PlaylistCoverGrid, coverPaths, useSingle);
        return Task.CompletedTask;
    }

    private void OnHomeCardCoverLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid grid || grid.DataContext is not HomeCard card) return;
        AddCoverImages(grid, card.ArtworkUris, card.UseSingleCover);
    }

    private void OnCollectionCoverLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid grid || grid.DataContext is not CollectionCard card) return;
        AddCoverImages(grid, card.ArtworkUris, card.UseSingleCover);
    }

    private static void AddCoverImages(Grid grid, IEnumerable<string> sourceUris, bool useSingleCover)
    {
        grid.Children.Clear();
        var uris = sourceUris.Where(u => !string.IsNullOrWhiteSpace(u)).Take(useSingleCover ? 1 : 4).ToList();
        if (uris.Count == 0) return;

        for (var i = 0; i < uris.Count; i++)
        {
            var image = new Microsoft.UI.Xaml.Controls.Image
            {
                Source = new BitmapImage(new Uri(uris[i])),
                Stretch = useSingleCover ? Stretch.UniformToFill : Stretch.Uniform
            };
            if (useSingleCover)
            {
                Grid.SetRowSpan(image, 2);
                Grid.SetColumnSpan(image, 2);
                grid.Children.Add(image);
                break;
            }

            Grid.SetRow(image, i / 2);
            Grid.SetColumn(image, i % 2);
            grid.Children.Add(image);
        }
    }

    private async void OnPlaylistCover(object sender, RoutedEventArgs e)
    {
        if (_activePlaylist is null) return;
        var picker = new FileOpenPicker(); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" }) picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var coverDir = Path.Combine(App.Library.LibraryRoot, ".playlist-covers"); Directory.CreateDirectory(coverDir);
        var destination = Path.Combine(coverDir, $"{Guid.NewGuid():N}{Path.GetExtension(file.Path)}"); File.Copy(file.Path, destination, true);
        await App.Database.SetPlaylistCoverPathAsync(_activePlaylist, destination);
        await RenderCollectionCoverAsync(_collectionTracks, destination);
        await RefreshPlaylistCoverDisplaysAsync();
        ShowToast("Playlist cover updated");
    }

    private async void OnPlaylistCoverFromTrack(object sender, RoutedEventArgs e)
    {
        if (_activePlaylist is null) return; var track = _collectionTracks.FirstOrDefault(t => t.ArtworkPath is not null && File.Exists(t.ArtworkPath));
        if (track?.ArtworkPath is null) { ShowToast("No artwork is available in this playlist"); return; }
        await App.Database.SetPlaylistCoverPathAsync(_activePlaylist, track.ArtworkPath);
        await RenderCollectionCoverAsync(_collectionTracks, track.ArtworkPath);
        await RefreshPlaylistCoverDisplaysAsync();
        ShowToast("Playlist cover set from a song");
    }

    private async void OnPlaylistDefaultCover(object sender, RoutedEventArgs e)
    {
        if (_activePlaylist is null) return;
        await App.Database.SetPlaylistCoverPathAsync(_activePlaylist, null);
        await RenderCollectionCoverAsync(_collectionTracks, null);
        await RefreshPlaylistCoverDisplaysAsync();
        ShowToast("Playlist cover reset");
    }

    private void OnPlaylistPlay(object sender, RoutedEventArgs e)
    {
        if (_collectionTracks.Count == 0) return; _repeatPlaylist = false; SetQueueFromTracks(_collectionTracks, _collectionTracks[0], _activeCollection); _ = PlayTrackAsync(_collectionTracks[0]);
    }

    private void OnPlaylistShuffle(object sender, RoutedEventArgs e)
    {
        if (_collectionTracks.Count == 0) return; _repeatPlaylist = false; var shuffled = _collectionTracks.OrderBy(_ => _random.Next()).ToList(); _queue.Clear(); _queueSourceName = _activeCollection; foreach (var t in shuffled.Skip(1)) _queue.Add(t); _queueIndex = -1; UpdateQueueFooter(); PlaylistShuffleDot.Visibility = Visibility.Visible; _ = PlayTrackAsync(shuffled[0]);
    }

    private void OnPlaylistRepeat(object sender, RoutedEventArgs e)
    {
        _repeatPlaylist = !_repeatPlaylist; PlaylistRepeatDot.Visibility = _repeatPlaylist ? Visibility.Visible : Visibility.Collapsed; ShowToast(_repeatPlaylist ? "Repeat playlist on" : "Repeat playlist off");
    }

    private void OnArtistHover(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button b && b.Content is TextBlock text) text.TextDecorations = TextDecorations.Underline;
    }

    private void OnArtistUnhover(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button b && b.Content is TextBlock text) text.TextDecorations = TextDecorations.None;
    }

    private void OnLinkTextHover(object sender, PointerRoutedEventArgs e)
    {
        if (sender is TextBlock text) text.TextDecorations = TextDecorations.Underline;
    }

    private void OnLinkTextUnhover(object sender, PointerRoutedEventArgs e)
    {
        if (sender is TextBlock text) text.TextDecorations = TextDecorations.None;
    }

    private async void OnAlbumClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Track track || string.IsNullOrWhiteSpace(track.Album)) return;
        _page = "Albums";
        _activePlaylist = null;
        _activeCollection = track.Album;
        _collectionSort = "Name";
        _collectionTracks = _libraryTracks.Where(t => string.Equals(t.Album, track.Album, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
        HomePanel.Visibility = Visibility.Collapsed; CollectionList.Visibility = Visibility.Collapsed; TrackList.Visibility = Visibility.Collapsed; DetailPanel.Visibility = Visibility.Visible;
        PageTitle.Text = track.Album; DetailType.Text = "ALBUM"; DetailTitle.Text = track.Album; DetailSubtitle.Text = $"{_collectionTracks.Count} tracks • {FormatTime(TimeSpan.FromSeconds(_collectionTracks.Sum(t => t.DurationSeconds)))}";
        DetailSortBar.Visibility = Visibility.Visible; DetailTrackSortHeader.Visibility = Visibility.Visible; CustomSortButton.Visibility = Visibility.Collapsed; PlaylistControls.Visibility = Visibility.Collapsed; PlaylistCoverActions.Visibility = Visibility.Collapsed;
        await RenderCollectionCoverAsync(_collectionTracks, _collectionTracks.FirstOrDefault()?.ArtworkPath, true);
        ApplyCollectionSort();
        UpdateNavVisuals();
    }

    private void SetQueueFromTracks(IEnumerable<Track> tracks, Track selected, string? playlistName)
    {
        var list = tracks.ToList();
        _queue.Clear();
        var selectedIndex = list.FindIndex(t => t.Id == selected.Id);
        foreach (var t in selectedIndex >= 0 ? list.Skip(selectedIndex + 1) : list)
            if (t.Id != selected.Id) _queue.Add(t);
        _queueIndex = -1;
        _queueSourceName = playlistName;
        UpdateQueueFooter();
    }

    private async Task PlayTrackAsync(Track track)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(track.FilePath) || !File.Exists(track.FilePath))
            {
                await HandleMissingTrackAsync(track);
                return;
            }

            // Recheck immediately before opening: the file can be deleted after the initial check.
            var file = await StorageFile.GetFileFromPathAsync(track.FilePath);
            var targetPlayer = ActivePlayer;
            targetPlayer.Source = MediaSource.CreateFromStorageFile(file);
            _currentTrackId = track.Id;
            NowPlayingTitle.Text = track.Title;
            NowPlayingArtist.Text = track.Artist;
            NowPlayingArtwork.Source = !string.IsNullOrWhiteSpace(track.ArtworkPath) && File.Exists(track.ArtworkPath) ? new BitmapImage(new Uri(Path.GetFullPath(track.ArtworkPath))) : null;
            FullscreenArtwork.Source = !string.IsNullOrWhiteSpace(track.ArtworkPath) && File.Exists(track.ArtworkPath) ? new BitmapImage(new Uri(Path.GetFullPath(track.ArtworkPath))) : null;
            FullscreenTitle.Text = track.Title;
            FullscreenArtist.Text = track.Artist;
            if (_isFullscreen) _ = UpdateFullscreenBackgroundAsync(track.ArtworkPath);
            targetPlayer.Play();
            if (_isCasting) _ = RefreshCastingSourceAsync();
        }
        catch (Exception ex)
        {
            // WinRT can report a missing file as a COMException or IOException, not just FileNotFoundException.
            if (string.IsNullOrWhiteSpace(track.FilePath) || !File.Exists(track.FilePath))
            {
                await HandleMissingTrackAsync(track);
                return;
            }

            ShowToast($"Could not play {track.Title}: {ex.Message}");
        }
    }

    private async Task HandleMissingTrackAsync(Track track)
    {
        // Remove every stale entry from the current session so playback cannot keep selecting it.
        _libraryTracks.RemoveAll(t => string.IsNullOrWhiteSpace(t.FilePath) || !File.Exists(t.FilePath));
        _collectionTracks.RemoveAll(t => string.IsNullOrWhiteSpace(t.FilePath) || !File.Exists(t.FilePath));
        for (var i = _queue.Count - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(_queue[i].FilePath) || !File.Exists(_queue[i].FilePath))
                _queue.RemoveAt(i);
        }
        _queueIndex = -1;

        // Keep the database in sync too; this method only deletes tracks whose files are gone.
        try { await App.Database.RemoveMissingTracksAsync(); }
        catch { /* Playback should still recover even if database cleanup is temporarily unavailable. */ }

        UpdateQueueFooter();
        if (_page is "Home" or "Songs" or "Search")
            TrackList.ItemsSource = _page == "Home"
                ? _libraryTracks.OrderByDescending(t => t.ImportedAtUtc).Take(10).ToList()
                : SortTracks(_libraryTracks, _trackSortKey, _trackSortAscending).ToList();
        ShowToast($"Skipped missing track: {track.Title}");
        PlayNext();
    }

    private void RemoveTrackFromQueue(Track track)
    {
        var idx = _queue.ToList().FindIndex(t => t.Id == track.Id);
        if (idx >= 0) _queue.RemoveAt(idx);
        _queueIndex = -1;
        UpdateQueueFooter();
    }

    private async void OnCastingButton(object sender, RoutedEventArgs e)
    {
        CastingFlyout.ShowAt(CastingButton);
        await RefreshDeviceMenuAsync();
    }

    private async Task RefreshDeviceMenuAsync()
    {
        CastDeviceListPanel.Children.Clear();
        AudioDeviceListPanel.Children.Clear();
        CastDeviceLoading.Visibility = Visibility.Visible;
        AudioDeviceLoading.Visibility = Visibility.Visible;

        try
        {
            var locator = new ChromecastLocator();
            var devices = (await locator.FindReceiversAsync(TimeSpan.FromSeconds(5))).OrderBy(d => d.Name).ToList();
            CastDeviceLoading.Visibility = Visibility.Collapsed;
            if (devices.Count == 0) CastDeviceListPanel.Children.Add(DeviceMessage("No Chromecast devices found"));
            foreach (var device in devices)
            {
                var button = DeviceButton(device.Name, device);
                button.Click += OnCustomChromecastSelected;
                CastDeviceListPanel.Children.Add(button);
            }
        }
        catch (Exception ex)
        {
            CastDeviceLoading.Visibility = Visibility.Collapsed;
            CastDeviceListPanel.Children.Add(DeviceMessage($"Chromecast discovery unavailable: {ex.Message}"));
        }

        try
        {
            var outputs = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
            AudioDeviceLoading.Visibility = Visibility.Collapsed;

            var activeAudioId = _selectedAudioDeviceId;
            if (string.IsNullOrWhiteSpace(activeAudioId))
            {
                try
                {
                    activeAudioId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
                }
                catch { }
            }

            if (outputs.Count == 0) AudioDeviceListPanel.Children.Add(DeviceMessage("No Windows audio outputs found"));
            foreach (var device in outputs.OrderBy(d => d.Name))
            {
                var isActive = string.Equals(device.Id, activeAudioId, StringComparison.OrdinalIgnoreCase);
                var button = DeviceButton(device.Name, device, isActive);
                button.Click += OnAudioOutputSelected;
                AudioDeviceListPanel.Children.Add(button);
            }
        }
        catch (Exception ex)
        {
            AudioDeviceLoading.Visibility = Visibility.Collapsed;
            AudioDeviceListPanel.Children.Add(DeviceMessage($"Audio devices unavailable: {ex.Message}"));
        }
    }

    private Button DeviceButton(string name, object tag, bool isActive = false)
    {
        var label = new Grid();
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(icon, 0);
        label.Children.Add(icon);

        var text = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        label.Children.Add(text);

        if (isActive)
        {
            var active = new TextBlock
            {
                Text = "Connected",
                Foreground = (Brush)Application.Current.Resources["AccentBrush"],
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(active, 2);
            label.Children.Add(active);
        }

        return new Button
        {
            Content = label,
            Tag = tag,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 2),
            Background = isActive
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(45, 255, 45, 45))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
        };
    }

    private TextBlock DeviceMessage(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
        FontSize = 11,
        Margin = new Thickness(4, 2, 4, 4)
    };

    private async void OnCustomChromecastSelected(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ChromecastReceiver receiver) return;
        if (ActivePlayer.Source is null || _currentTrackId < 0)
        {
            ShowToast("Start playing a song before connecting to a Chromecast.");
            return;
        }

        CastingFlyout.Hide();
        try { await ConnectChromecastAsync(receiver); }
        catch (Exception ex) { ShowToast($"Could not connect to Chromecast: {ex.Message}"); }
    }

    private async void OnAudioOutputSelected(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DeviceInformation device) return;
        try
        {
            if (_isCasting) await DisconnectCastingConnectionAsync(restoreLocal: false);
            ActivePlayer.AudioDevice = device;
            _selectedAudioDeviceId = device.Id;
            _castingDeviceName = null;
            UpdateCastingUi();
            CastingStatusText.Text = $"Playing on {device.Name}";
            CastingFlyout.Hide();
            ShowToast($"Audio output: {device.Name}");
        }
        catch (Exception ex) { ShowToast($"Could not use {device.Name}: {ex.Message}"); }
    }

    private async Task ConnectChromecastAsync(ChromecastReceiver receiver)
    {
        var current = FindCurrentTrack();
        if (current is null || !File.Exists(current.FilePath))
        {
            ShowToast("The current song is no longer available.");
            return;
        }

        _castRequestInProgress = true;
        try
        {
            if (_isCasting) await DisconnectCastingConnectionAsync(restoreLocal: false);
            _volumeBeforeCasting = Math.Clamp(_player.Volume, 0, 1);
            var position = ActivePlayer.PlaybackSession.Position;
            var wasPlaying = ActivePlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

            // A small LAN-only server streams the selected file to the Cast receiver. It is stopped on disconnect.
            var mediaUrl = _castServer.Start(current.FilePath, current.ArtworkPath);
            var client = new ChromecastClient();
            await client.ConnectChromecast(receiver);
            client.MediaChannel.StatusChanged += OnChromecastMediaStatusChanged;
            client.Disconnected += OnChromecastDisconnected;
            await client.LaunchApplicationAsync("CC1AD845"); // Google's unregistered Default Media Receiver.
            _castClient = client;

            var ext = Path.GetExtension(current.FilePath).ToLowerInvariant();
            if (ext is not (".mp3" or ".m4a" or ".aac" or ".wav" or ".mp4" or ".flac" or ".ogg" or ".opus" or ".aiff" or ".aif"))
                throw new NotSupportedException($"The current file type ({ext}) may not be supported by Chromecast. Use MP3 or M4A for the most reliable audio compatibility.");

            var media = new Media
            {
                ContentUrl = mediaUrl,
                ContentType = CastMediaServer.GetMimeType(current.FilePath),
                Metadata = new MusicTrackMetadata
                {
                    Title = current.Title,
                    Artist = current.Artist,
                    MetadataType = (MetadataType)5, // Cast protocol: MUSIC_TRACK
                    Images = !string.IsNullOrWhiteSpace(_castServer.ArtworkUrl)
                        ? [new Sharpcaster.Models.Media.Image { Url = _castServer.ArtworkUrl! }]
                        : []
                }
            };
            await client.MediaChannel.LoadAsync(media, wasPlaying);
            _castingDeviceName = receiver.Name;
            _isCasting = true;
            _player.Pause();
            _player.Volume = 0;
            var file = await StorageFile.GetFileFromPathAsync(current.FilePath);
            _castingPlayer.Source = MediaSource.CreateFromStorageFile(file);
            _castingPlayer.Volume = 0;
            try { _castingPlayer.PlaybackSession.Position = position; } catch { }
            if (wasPlaying) _castingPlayer.Play(); else _castingPlayer.Pause();
            UpdateCastingUi();
            ShowToast($"Casting to {receiver.Name}");
        }
        catch
        {
            await DisconnectCastingConnectionAsync(restoreLocal: true);
            throw;
        }
        finally { _castRequestInProgress = false; }
    }

    private Track? FindCurrentTrack() => _libraryTracks.FirstOrDefault(t => t.Id == _currentTrackId)
        ?? _collectionTracks.FirstOrDefault(t => t.Id == _currentTrackId);

    private async Task RefreshCastingSourceAsync()
    {
        if (!_isCasting || _castClient is null || _castRequestInProgress) return;
        var current = FindCurrentTrack();
        if (current is null || !File.Exists(current.FilePath)) return;
        try
        {
            _castRequestInProgress = true;
            var url = _castServer.Start(current.FilePath, current.ArtworkPath);
            var media = new Media
            {
                ContentUrl = url,
                ContentType = CastMediaServer.GetMimeType(current.FilePath),
                Metadata = new MusicTrackMetadata
                {
                    Title = current.Title,
                    Artist = current.Artist,
                    MetadataType = (MetadataType)5, // Cast protocol: MUSIC_TRACK
                    Images = !string.IsNullOrWhiteSpace(_castServer.ArtworkUrl)
                        ? [new Sharpcaster.Models.Media.Image { Url = _castServer.ArtworkUrl! }]
                        : []
                }
            };
            var position = _castingPlayer.PlaybackSession.Position.TotalSeconds;
            var wasPlaying = _castingPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
            await _castClient.MediaChannel.LoadAsync(media, wasPlaying);
            if (position > 0) await _castClient.MediaChannel.SeekAsync(position);
        }
        catch (Exception ex) { ShowToast($"Could not update the cast stream: {ex.Message}"); }
        finally { _castRequestInProgress = false; }
    }

    private void OnChromecastMediaStatusChanged(object? sender, Sharpcaster.Models.Media.MediaStatus status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isCasting || _castStatusUpdateInProgress) return;
            _castStatusUpdateInProgress = true;
            try
            {
                if (status.CurrentTime >= 0)
                    try { _castingPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(status.CurrentTime); } catch { }
                var state = status.PlayerState.ToString();
                if (state.Equals("PLAYING", StringComparison.OrdinalIgnoreCase)) _castingPlayer.Play();
                else if (state.Equals("PAUSED", StringComparison.OrdinalIgnoreCase)) _castingPlayer.Pause();
                else if (state.Equals("IDLE", StringComparison.OrdinalIgnoreCase) && status.IdleReason?.ToString() == "FINISHED")
                    OnMediaEnded(_castingPlayer, EventArgs.Empty);
                UpdateCastingUi();
            }
            finally { _castStatusUpdateInProgress = false; }
        });
    }

    private void OnChromecastDisconnected(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_isCasting && !_isStoppingCasting)
            {
                await DisconnectCastingConnectionAsync(restoreLocal: true);
                ShowToast("Chromecast disconnected");
            }
        });
    }

    private async void OnStopCasting(object sender, RoutedEventArgs e)
    {
        CastingFlyout.Hide();
        await DisconnectCastingConnectionAsync(restoreLocal: true);
    }

    private async Task DisconnectCastingConnectionAsync(bool restoreLocal)
    {
        if (_isStoppingCasting) return;
        _isStoppingCasting = true;
        var client = _castClient;
        _castClient = null;
        var castPosition = _castingPlayer.PlaybackSession.Position;
        try
        {
            if (client is not null)
            {
                client.MediaChannel.StatusChanged -= OnChromecastMediaStatusChanged;
                client.Disconnected -= OnChromecastDisconnected;
                try { await client.MediaChannel.StopAsync(); } catch { }
                try { await client.DisconnectAsync(); } catch { }
            }
        }
        catch { /* Receiver may already have disconnected. */ }
        _castingPlayer.Pause();
        _castingPlayer.Source = null;
        _castServer.Stop();
        _isCasting = false;
        _castingDeviceName = null;
        _castRequestInProgress = false;
        _player.Volume = Math.Clamp(_volumeBeforeCasting, 0, 1);
        if (restoreLocal && _currentTrackId >= 0)
        {
            var current = FindCurrentTrack();
            if (current is not null && !string.IsNullOrWhiteSpace(current.FilePath) && File.Exists(current.FilePath))
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(current.FilePath);
                    _player.Source = MediaSource.CreateFromStorageFile(file);
                    try { _player.PlaybackSession.Position = castPosition; } catch { }
                    _player.Pause(); // Do not unexpectedly resume audio on the PC after casting ends.
                }
                catch { _player.Pause(); }
            }
        }
        UpdateCastingUi();
        _isStoppingCasting = false;
        if (restoreLocal) ShowToast("Casting stopped");
    }

    private void UpdateCastingUi()
    {
        CastingIcon.Foreground = _isCasting
            ? new SolidColorBrush(ParseColor(_accent ?? "#1DB954"))
            : new SolidColorBrush(ParseColor(_darkTheme ? "#B3B3B3" : "#555555"));
        CastingStatusText.Text = _isCasting ? $"Casting to {_castingDeviceName ?? "device"}" : "Not casting";
        StopCastingButton.Visibility = _isCasting ? Visibility.Visible : Visibility.Collapsed;
        VolumeCanvas.Opacity = _isCasting ? 0.45 : 1.0;
        VolumeCanvas.IsHitTestVisible = !_isCasting;
        var player = ActivePlayer;
        var playing = player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        PlayIcon.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PauseIcon.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnFullscreenButton(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen) ExitFullscreen();
        else await EnterFullscreenAsync();
    }

    private async Task EnterFullscreenAsync()
    {
        if (_appWindow is null) return;
        try
        {
            _isFullscreen = true;
            _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            FullscreenOverlay.Visibility = Visibility.Visible;
            PlayerBar.Visibility = Visibility.Visible;
            Root.RowDefinitions[1].Height = new GridLength(112);
            FullscreenTrackInfo.Visibility = Visibility.Collapsed;
            await UpdateFullscreenBackgroundAsync(_currentTrackId >= 0 ? _libraryTracks.FirstOrDefault(t => t.Id == _currentTrackId)?.ArtworkPath : null);
            RestartFullscreenHideTimer();
        }
        catch (Exception ex)
        {
            _isFullscreen = false;
            FullscreenOverlay.Visibility = Visibility.Collapsed;
            ShowToast($"Could not enter fullscreen: {ex.Message}");
        }
    }

    private void ExitFullscreen()
    {
        _fullscreenHideTimer?.Stop();
        _isFullscreen = false;
        FullscreenOverlay.Visibility = Visibility.Collapsed;
        FullscreenTrackInfo.Visibility = Visibility.Collapsed;
        PlayerBar.Visibility = Visibility.Visible;
        Root.RowDefinitions[1].Height = new GridLength(112);
        _appWindow?.SetPresenter(AppWindowPresenterKind.Overlapped);
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isFullscreen || e.Key != Windows.System.VirtualKey.Escape) return;
        ExitFullscreen();
        e.Handled = true;
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isFullscreen) return;
        if (PlayerBar.Visibility != Visibility.Visible)
        {
            PlayerBar.Visibility = Visibility.Visible;
            Root.RowDefinitions[1].Height = new GridLength(112);
            FullscreenTrackInfo.Visibility = Visibility.Collapsed;
        }
        RestartFullscreenHideTimer();
    }

    private void RestartFullscreenHideTimer()
    {
        if (!_isFullscreen) return;
        _fullscreenHideTimer?.Stop();
        _fullscreenHideTimer?.Start();
    }

    private void HideFullscreenPlayerBar()
    {
        _fullscreenHideTimer?.Stop();
        if (!_isFullscreen) return;
        PlayerBar.Visibility = Visibility.Collapsed;
        Root.RowDefinitions[1].Height = new GridLength(0);
        FullscreenTrackInfo.Visibility = Visibility.Visible;
    }

    private async Task UpdateFullscreenBackgroundAsync(string? artworkPath)
    {
        Color baseColor = Color.FromArgb(255, 36, 36, 42);
        if (!string.IsNullOrWhiteSpace(artworkPath) && File.Exists(artworkPath))
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(artworkPath);
                using var stream = await file.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var transform = new BitmapTransform { ScaledWidth = 32, ScaledHeight = 32 };
                var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
                long red = 0, green = 0, blue = 0, count = 0;
                for (int i = 0; i + 3 < pixels.Length; i += 4)
                {
                    blue += pixels[i]; green += pixels[i + 1]; red += pixels[i + 2]; count++;
                }
                if (count > 0) baseColor = Color.FromArgb(255, (byte)(red / count), (byte)(green / count), (byte)(blue / count));
            }
            catch { /* Artwork decoding can fail for unusual image formats; retain the neutral fallback. */ }
        }

        static byte ScaleChannel(byte value, double factor) => (byte)Math.Clamp((int)Math.Round(value * factor), 0, 255);
        var top = Color.FromArgb(255, ScaleChannel(baseColor.R, 1.28), ScaleChannel(baseColor.G, 1.28), ScaleChannel(baseColor.B, 1.28));
        var middle = Color.FromArgb(255, ScaleChannel(baseColor.R, 0.92), ScaleChannel(baseColor.G, 0.92), ScaleChannel(baseColor.B, 0.92));
        var bottom = Color.FromArgb(255, ScaleChannel(baseColor.R, 0.62), ScaleChannel(baseColor.G, 0.62), ScaleChannel(baseColor.B, 0.62));
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        brush.GradientStops.Add(new GradientStop { Color = top, Offset = 0.0 });
        brush.GradientStops.Add(new GradientStop { Color = middle, Offset = 0.52 });
        brush.GradientStops.Add(new GradientStop { Color = bottom, Offset = 1.0 });
        FullscreenOverlay.Background = brush;
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        var player = ActivePlayer;
        if (player.Source is null)
        {
            if (_queue.Count > 0) { var next = _queue[0]; _queue.RemoveAt(0); _ = PlayTrackAsync(next); }
            else if (_libraryTracks.Count > 0) PlayRandomFromLibrary();
            return;
        }
        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            player.Pause();
            if (_isCasting && _castClient is not null) _ = _castClient.MediaChannel.PauseAsync();
        }
        else
        {
            player.Play();
            if (_isCasting && _castClient is not null) _ = _castClient.MediaChannel.PlayAsync();
        }
    }

    private void OnPrevious(object sender, RoutedEventArgs e)
    {
        if (_queue.Count == 0) return;
        if (ActivePlayer.PlaybackSession.Position.TotalSeconds > 3) { ActivePlayer.PlaybackSession.Position = TimeSpan.Zero; if (_isCasting && _castClient is not null) _ = _castClient.MediaChannel.SeekAsync(0); return; }
        _queueIndex = _queueIndex <= 0 ? _queue.Count - 1 : _queueIndex - 1;
        _ = PlayTrackAsync(_queue[_queueIndex]);
    }
    private void OnNext(object sender, RoutedEventArgs e) => PlayNext();

    private void PlayNext()
    {
        // Discard stale queue entries before selecting the next track.
        for (var i = _queue.Count - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(_queue[i].FilePath) || !File.Exists(_queue[i].FilePath))
                _queue.RemoveAt(i);
        }
        _queueIndex = -1;
        UpdateQueueFooter();

        if (_queue.Count == 0)
        {
            PlayRandomFromLibrary();
            return;
        }

        var nextIndex = _shuffle ? _random.Next(_queue.Count) : 0;
        var next = _queue[nextIndex];
        _queue.RemoveAt(nextIndex);
        UpdateQueueFooter();
        _ = PlayTrackAsync(next);
    }

    private void PlayRandomFromLibrary()
    {
        _libraryTracks.RemoveAll(t => string.IsNullOrWhiteSpace(t.FilePath) || !File.Exists(t.FilePath));
        _collectionTracks.RemoveAll(t => string.IsNullOrWhiteSpace(t.FilePath) || !File.Exists(t.FilePath));
        if (_libraryTracks.Count == 0) return;

        var shuffled = _libraryTracks.OrderBy(_ => _random.Next()).ToList();
        var track = shuffled[0];
        _queue.Clear();
        foreach (var upcoming in shuffled.Skip(1))
            _queue.Add(upcoming);

        _queueIndex = -1;
        _queueSourceName = null;
        UpdateQueueFooter();
        _ = PlayTrackAsync(track);
    }

    private void OnShuffle(object sender, RoutedEventArgs e)
    {
        _shuffle = !_shuffle;
        ShuffleButton.Foreground = new SolidColorBrush(_shuffle ? ParseColor(_accent!) : ParseColor(_darkTheme ? "#A8ADB9" : "#30343C"));
        ShuffleDot.Visibility = _shuffle ? Visibility.Visible : Visibility.Collapsed;
        ShowToast(_shuffle ? "Shuffle on" : "Shuffle off");
    }

    private void OnRepeat(object sender, RoutedEventArgs e)
    {
        _repeat = !_repeat;
        RepeatButton.Foreground = new SolidColorBrush(_repeat ? ParseColor(_accent!) : ParseColor(_darkTheme ? "#A8ADB9" : "#30343C"));
        RepeatDot.Visibility = _repeat ? Visibility.Visible : Visibility.Collapsed;
        ShowToast(_repeat ? "Repeat on" : "Repeat off");
    }

    private void OnProgressPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isSeeking = true;
        ProgressCanvas.CapturePointer(e.Pointer);
        SeekFromPointer(e);
    }

    private void OnProgressPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isSeeking) SeekFromPointer(e);
    }

    private void OnProgressPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking) return;
        SeekFromPointer(e);
        _isSeeking = false;
        ProgressCanvas.ReleasePointerCapture(e.Pointer);
    }

    private void SeekFromPointer(PointerRoutedEventArgs e)
    {
        var duration = ActivePlayer.PlaybackSession.NaturalDuration.TotalSeconds;
        var width = Math.Min(ProgressCanvas.ActualWidth, 380);
        if (duration <= 0 || width <= 0) return;
        var x = Math.Clamp(e.GetCurrentPoint(ProgressCanvas).Position.X, 0, width);
        var position = duration * x / width;
        ActivePlayer.PlaybackSession.Position = TimeSpan.FromSeconds(position);
        if (_isCasting && _castClient is not null) _ = _castClient.MediaChannel.SeekAsync(position);
        UpdateProgressVisuals(position, duration);
    }

    private void OnVolumeIconClick(object sender, RoutedEventArgs e)
    {
        if (_isMuted)
        {
            _isMuted = false;
            _player.Volume = Math.Clamp(_volumeBeforeMute, 0, 1);
            SetVolumeVisual(_player.Volume);
            ShowToast("Volume restored");
        }
        else
        {
            _volumeBeforeMute = Math.Clamp(_player.Volume, 0, 1);
            _isMuted = true;
            _player.Volume = 0;
            SetVolumeVisual(0);
            ShowToast("Muted");
        }
    }

    private void OnVolumePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        _isMuted = false;
        _isVolumeSeeking = true;
        VolumeCanvas.CapturePointer(e.Pointer);
        SetVolumeFromPointer(e);
    }
    private void OnVolumePointerMoved(object sender, PointerRoutedEventArgs e) { if (_isVolumeSeeking) SetVolumeFromPointer(e); }
    private void OnVolumePointerReleased(object sender, PointerRoutedEventArgs e) { if (!_isVolumeSeeking) return; SetVolumeFromPointer(e); _isVolumeSeeking = false; VolumeCanvas.ReleasePointerCapture(e.Pointer); }
    private void SetVolumeFromPointer(PointerRoutedEventArgs e)
    {
        var x = Math.Clamp(e.GetCurrentPoint(VolumeCanvas).Position.X - 25, 0, 100);
        _player.Volume = x / 100.0;
        _volumeBeforeMute = _player.Volume;
        SetVolumeVisual(_player.Volume);
    }
    private void SetVolumeVisual(double value)
    {
        var width = 100 * Math.Clamp(value, 0, 1);
        var brush = _isMuted ? ParseColor(_darkTheme ? "#777777" : "#A0A0A0") : ParseColor(_accent!);
        VolumeFill.Background = new SolidColorBrush(brush);
        VolumePlayhead.Fill = new SolidColorBrush(brush);
        VolumeIcon.Glyph = _isMuted ? "\uE74F" : "\uE767";
        VolumeIcon.Foreground = new SolidColorBrush(brush);
        VolumeFill.Width = width;
        Canvas.SetLeft(VolumePlayhead, 25 + width - VolumePlayhead.Width / 2);
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var playing = ActivePlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
            PlayIcon.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            PauseIcon.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() => { if (_repeatPlaylist && _collectionTracks.Count > 0) { SetQueueFromTracks(_collectionTracks, _collectionTracks[0], _activeCollection); _ = PlayTrackAsync(_collectionTracks[0]); return; } if (_queue.Count == 0) { PlayRandomFromLibrary(); return; } if (_repeat) { _ = PlayTrackAsync(_libraryTracks.FirstOrDefault(t => t.Id == _currentTrackId) ?? _collectionTracks.FirstOrDefault(t => t.Id == _currentTrackId) ?? _libraryTracks.First()); } else PlayNext(); });
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => DispatcherQueue.TryEnqueue(async () =>
    {
        var failedTrack = _libraryTracks.FirstOrDefault(t => t.Id == _currentTrackId)
            ?? _collectionTracks.FirstOrDefault(t => t.Id == _currentTrackId);
        if (failedTrack is not null && (string.IsNullOrWhiteSpace(failedTrack.FilePath) || !File.Exists(failedTrack.FilePath)))
            await HandleMissingTrackAsync(failedTrack);
        else
            ShowToast($"Playback failed: {args.ErrorMessage}");
    });

    private void OnProgressCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Recalculate after WinUI has completed the initial layout. The canvas
        // starts with a zero-width track so it cannot overflow before this event.
        UpdateProgressVisuals(ActivePlayer.PlaybackSession.Position.TotalSeconds,
            ActivePlayer.PlaybackSession.NaturalDuration.TotalSeconds);
    }

    private void UpdateProgress()
    {
        var player = ActivePlayer;
        if (player.Source is null || _isSeeking) return;
        var duration = player.PlaybackSession.NaturalDuration.TotalSeconds;
        if (duration <= 0) return;
        var position = player.PlaybackSession.Position.TotalSeconds;
        UpdateProgressVisuals(position, duration);
    }

    private void UpdateProgressVisuals(double position, double duration)
    {
        var width = Math.Min(ProgressCanvas.ActualWidth, 380);
        if (width <= 0) return;
        ProgressTrack.Width = width;
        var ratio = duration <= 0 ? 0 : Math.Clamp(position / duration, 0, 1);
        ProgressFill.Width = width * ratio;
        Canvas.SetLeft(ProgressPlayhead, Math.Clamp(width * ratio - ProgressPlayhead.Width / 2, 0, Math.Max(0, width - ProgressPlayhead.Width)));
        CurrentTimeText.Text = FormatTime(TimeSpan.FromSeconds(Math.Max(0, position)));
        TotalTimeText.Text = FormatTime(TimeSpan.FromSeconds(Math.Max(0, duration)));
    }

    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowWindowClose) return;

        args.Cancel = true;
        var hwnd = WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_HIDE);
    }

    private void InitializeTrayIcon(IntPtr hwnd, string iconPath)
    {
        if (!File.Exists(iconPath)) return;

        _wndProcDelegate = TrayWindowProc;
        _oldWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
        _trayIconHandle = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        if (_trayIconHandle == IntPtr.Zero) return;

        var data = CreateTrayIconData(hwnd);
        _trayIconAdded = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private NOTIFYICONDATA CreateTrayIconData(IntPtr hwnd) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = hwnd,
        uID = TrayIconId,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = TrayCallbackMessage,
        hIcon = _trayIconHandle,
        szTip = "MyMusic"
    };

    private IntPtr TrayWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TrayCallbackMessage)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
            if (mouseMessage is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            {
                RestoreFromTray();
                return IntPtr.Zero;
            }

            if (mouseMessage == WM_RBUTTONUP)
            {
                ShowTrayMenu(hwnd);
                return IntPtr.Zero;
            }
        }

        return CallWindowProc(_oldWndProc, hwnd, msg, wParam, lParam);
    }

    private void RestoreFromTray()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_SHOW);
        Activate();
        SetForegroundWindow(hwnd);
    }

    private void ShowTrayMenu(IntPtr hwnd)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            AppendMenu(menu, MF_STRING, 1, "Open MyMusic");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, 2, "Exit");
            GetCursorPos(out var point);
            SetForegroundWindow(hwnd);
            var command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, 0, hwnd, IntPtr.Zero);
            if (command == 1)
            {
                RestoreFromTray();
            }
            else if (command == 2)
            {
                ExitFromTray();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void ExitFromTray()
    {
        _allowWindowClose = true;
        RemoveTrayIcon();
        try { DisposePlayer(); } catch { }
        try { _appWindow?.Destroy(); } catch { }
        try { Application.Current.Exit(); } catch { }
        Environment.Exit(0);
    }

    private void RemoveTrayIcon()
    {
        if (_trayIconAdded)
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var data = CreateTrayIconData(hwnd);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _trayIconAdded = false;
        }

        if (_trayIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_trayIconHandle);
            _trayIconHandle = IntPtr.Zero;
        }
    }

    private void RestoreWindowProcedure()
    {
        if (_oldWndProc == IntPtr.Zero) return;
        var hwnd = WindowNative.GetWindowHandle(this);
        SetWindowLongPtr(hwnd, GWLP_WNDPROC, _oldWndProc);
        _oldWndProc = IntPtr.Zero;
        _wndProcDelegate = null;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const int GWLP_WNDPROC = -4;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    private void DisposePlayer()
    {
        RemoveTrayIcon();
        RestoreWindowProcedure();
        _positionTimer?.Stop();
        _fullscreenHideTimer?.Stop();
        try { _castClient?.DisconnectAsync(); } catch { }
        _castServer.Stop();
        _player.Pause();
        _castingPlayer.Pause();
        _player.Dispose();
        _castingPlayer.Dispose();
    }

    private async void OnTrackMenu(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Track track) return;
        var flyout = new MenuFlyout();
        var queue = new MenuFlyoutItem { Text = "Add to queue" };
        queue.Click += (_, _) => { if (!_queue.Any(q => q.Id == track.Id)) _queue.Add(track); ShowToast($"Added {track.Title} to queue"); };
        flyout.Items.Add(queue);
        var add = new MenuFlyoutSubItem { Text = "Add to playlist" };
        var names = await App.Database.GetPlaylistNamesAsync();
        foreach (var name in names)
        {
            var item = new MenuFlyoutItem { Text = name };
            item.Click += async (_, _) => { await App.Database.AddTrackToPlaylistAsync(name, track.Id); ShowToast($"Added {track.Title} to {name}"); };
            add.Items.Add(item);
        }
        flyout.Items.Add(add);
        if (!string.IsNullOrWhiteSpace(_activePlaylist))
        {
            var remove = new MenuFlyoutItem { Text = $"Remove from {_activePlaylist}" };
            remove.Click += async (_, _) =>
            {
                await App.Database.RemoveTrackFromPlaylistAsync(_activePlaylist!, track.Id);
                ShowToast($"Removed {track.Title} from {_activePlaylist}");
                if (_page == "Playlists") TrackList.ItemsSource = await App.Database.GetPlaylistTracksAsync(_activePlaylist!);
            };
            flyout.Items.Add(remove);
        }
        var create = new MenuFlyoutItem { Text = "Create playlist" }; create.Click += async (_, _) => await CreatePlaylistAsync(); flyout.Items.Add(create);
        var edit = new MenuFlyoutItem { Text = "Edit" };
        edit.Click += async (_, _) => await OpenTrackEditorAsync(track);
        flyout.Items.Add(edit);
        var download = new MenuFlyoutItem { Text = "Download a copy" }; download.Click += async (_, _) => await DownloadTrackAsync(track); flyout.Items.Add(download);
        flyout.ShowAt((FrameworkElement)sender);
    }

    private Dictionary<long, DateTime> LoadRecentEditTimes()
    {
        try
        {
            if (!File.Exists(RecentEditsPath)) return [];
            return JsonSerializer.Deserialize<Dictionary<long, DateTime>>(File.ReadAllText(RecentEditsPath)) ?? [];
        }
        catch { return []; }
    }

    private void RecordTrackEdited(long trackId)
    {
        try
        {
            var edits = LoadRecentEditTimes();
            edits[trackId] = DateTime.UtcNow;
            var liveIds = _libraryTracks.Select(t => t.Id).ToHashSet();
            var trimmed = edits.Where(kv => liveIds.Contains(kv.Key)).OrderByDescending(kv => kv.Value).Take(50).ToDictionary(kv => kv.Key, kv => kv.Value);
            Directory.CreateDirectory(Path.GetDirectoryName(RecentEditsPath)!);
            File.WriteAllText(RecentEditsPath, JsonSerializer.Serialize(trimmed));
        }
        catch { }
    }

    private async Task OpenTrackEditorAsync(Track track)
    {
        _editReturnPage = _page;
        _editReturnCollection = _activeCollection;
        _editReturnPlaylist = _activePlaylist;
        _editReturnWasDetail = DetailPanel.Visibility == Visibility.Visible;
        _editReturnSearch = SearchBox.Text;
        _editReturnScrollOffset = MainContentScrollViewer.VerticalOffset;
        _editingTrack = track;
        _editArtworkSourcePath = track.ArtworkPath;
        _editArtworkWorkingPath = null;
        EditTitleBox.Text = track.Title;
        EditArtistBox.Text = track.Artist;
        EditAlbumBox.Text = track.Album;
        EditTrackStatus.Text = "";
        EditCropX.Value = 50;
        EditCropY.Value = 50;
        EditCropZoom.Value = 1;
        EditCropPanel.Visibility = Visibility.Collapsed;
        SetEditArtworkPreview(track.ArtworkPath);

        var editTimes = LoadRecentEditTimes();
        EditRecentTracks.ItemsSource = _libraryTracks
            .Where(t => t.Id != track.Id)
            .Select(t => new { Track = t, Activity = editTimes.TryGetValue(t.Id, out var edited) && edited > t.ImportedAtUtc ? edited : t.ImportedAtUtc })
            .OrderByDescending(x => x.Activity)
            .Take(3)
            .Select(x => x.Track)
            .ToList();

        EditTrackOverlay.Visibility = Visibility.Visible;
        await Task.CompletedTask;
    }

    private void SetEditArtworkPreview(string? path)
    {
        try
        {
            EditArtworkPreview.Source = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? new BitmapImage(new Uri(Path.GetFullPath(path) + $"?v={DateTime.UtcNow.Ticks}"))
                : null;
        }
        catch
        {
            try { EditArtworkPreview.Source = !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? new BitmapImage(new Uri(Path.GetFullPath(path))) : null; }
            catch { EditArtworkPreview.Source = null; }
        }
    }

    private void OnCancelTrackEdit(object sender, RoutedEventArgs e)
    {
        EditTrackOverlay.Visibility = Visibility.Collapsed;
        CleanupEditWorkingArtwork();
        _editingTrack = null;
    }

    private async void OnUploadEditArtwork(object sender, RoutedEventArgs e)
    {
        if (_editingTrack is null) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".jpg"); picker.FileTypeFilter.Add(".jpeg"); picker.FileTypeFilter.Add(".png"); picker.FileTypeFilter.Add(".bmp"); picker.FileTypeFilter.Add(".webp");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            CleanupEditWorkingArtwork();
            var dir = Path.Combine(Path.GetTempPath(), "MyMusic", "edit-covers");
            Directory.CreateDirectory(dir);
            var ext = Path.GetExtension(file.Path);
            var temp = Path.Combine(dir, $"{Guid.NewGuid():N}{ext}");
            File.Copy(file.Path, temp, true);
            _editArtworkWorkingPath = temp;
            _editArtworkSourcePath = temp;
            SetEditArtworkPreview(temp);
            EditCropX.Value = 50; EditCropY.Value = 50; EditCropZoom.Value = 1;
            EditTrackStatus.Text = "New cover selected";
        }
        catch (Exception ex) { EditTrackStatus.Text = $"Could not load cover: {ex.Message}"; }
    }

    private void OnToggleCropControls(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_editArtworkSourcePath) || !File.Exists(_editArtworkSourcePath))
        {
            EditTrackStatus.Text = "This song does not have cover art to crop.";
            return;
        }
        EditCropPanel.Visibility = EditCropPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnApplyArtworkCrop(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_editArtworkSourcePath) || !File.Exists(_editArtworkSourcePath)) return;
        try
        {
            var cropped = await CreateSquareCropAsync(_editArtworkSourcePath, EditCropX.Value / 100.0, EditCropY.Value / 100.0, EditCropZoom.Value);
            if (!string.Equals(_editArtworkWorkingPath, _editArtworkSourcePath, StringComparison.OrdinalIgnoreCase)) CleanupEditWorkingArtwork();
            _editArtworkWorkingPath = cropped;
            _editArtworkSourcePath = cropped;
            SetEditArtworkPreview(cropped);
            EditCropX.Value = 50; EditCropY.Value = 50; EditCropZoom.Value = 1;
            EditTrackStatus.Text = "Crop applied";
        }
        catch (Exception ex) { EditTrackStatus.Text = $"Could not crop cover: {ex.Message}"; }
    }

    private static async Task<string> CreateSquareCropAsync(string sourcePath, double xPosition, double yPosition, double zoom)
    {
        var sourceFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(sourcePath));
        using var sourceStream = await sourceFile.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(sourceStream);
        var baseSide = Math.Min(decoder.PixelWidth, decoder.PixelHeight);
        if (baseSide == 0) throw new InvalidOperationException("The selected image has no usable pixels.");
        zoom = Math.Clamp(zoom, 1.0, 3.0);
        var side = Math.Max(1u, (uint)Math.Round(baseSide / zoom));
        var maxX = decoder.PixelWidth - side;
        var maxY = decoder.PixelHeight - side;
        var transform = new BitmapTransform
        {
            Bounds = new BitmapBounds
            {
                X = (uint)Math.Round(maxX * Math.Clamp(xPosition, 0, 1)),
                Y = (uint)Math.Round(maxY * Math.Clamp(yPosition, 0, 1)),
                Width = side,
                Height = side
            }
        };
        var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        var dir = Path.Combine(Path.GetTempPath(), "MyMusic", "edit-covers");
        Directory.CreateDirectory(dir);
        var outputPath = Path.Combine(dir, $"{Guid.NewGuid():N}.jpg");
        var folder = await StorageFolder.GetFolderFromPathAsync(dir);
        var output = await folder.CreateFileAsync(Path.GetFileName(outputPath), CreationCollisionOption.ReplaceExisting);
        using var stream = await output.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, side, side, decoder.DpiX, decoder.DpiY, pixels);
        await encoder.FlushAsync();
        return outputPath;
    }

    private void OnCopyRecentTrackInfo(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Track source || _editingTrack is null) return;
        EditArtistBox.Text = source.Artist;
        EditAlbumBox.Text = source.Album;
        _editArtworkSourcePath = source.ArtworkPath;
        _editArtworkWorkingPath = null;
        SetEditArtworkPreview(source.ArtworkPath);
        EditCropX.Value = 50; EditCropY.Value = 50; EditCropZoom.Value = 1;
        EditTrackStatus.Text = $"Copied cover, artist, and album from {source.Title}";
    }

    private async void OnSaveTrackEdit(object sender, RoutedEventArgs e)
    {
        if (_editingTrack is null) return;
        var title = EditTitleBox.Text.Trim();
        var artist = EditArtistBox.Text.Trim();
        var album = EditAlbumBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
        {
            EditTrackStatus.Text = "Song name, artist, and album cannot be empty.";
            return;
        }

        try
        {
            var track = _editingTrack;
            string? finalArtwork = track.ArtworkPath;
            if (!string.IsNullOrWhiteSpace(_editArtworkSourcePath) && File.Exists(_editArtworkSourcePath) && !string.Equals(_editArtworkSourcePath, track.ArtworkPath, StringComparison.OrdinalIgnoreCase))
            {
                var artDir = Path.GetDirectoryName(track.FilePath)!;
                var finalPath = Path.Combine(artDir, $"{Path.GetFileNameWithoutExtension(track.FilePath)}.edited-cover.{DateTime.UtcNow.Ticks}.jpg");
                await SaveArtworkAsJpegAsync(_editArtworkSourcePath, finalPath);
                finalArtwork = finalPath;
            }

            track.Title = title;
            track.Artist = artist;
            track.Album = album;
            track.AlbumArtist = artist;
            track.ArtworkPath = finalArtwork;
            await App.Database.UpdateTrackInfoAsync(track);
            TryWriteEditedTags(track);
            RecordTrackEdited(track.Id);

            var editedId = track.Id;
            EditTrackOverlay.Visibility = Visibility.Collapsed;
            CleanupEditWorkingArtwork();
            _editingTrack = null;
            _libraryTracks.Clear();
            _libraryTracks.AddRange(await App.Database.GetTracksAsync());
            await App.Library.RepairMissingArtworkAsync(_libraryTracks);
            await RestoreViewAfterTrackEditAsync();

            if (_currentTrackId == editedId)
            {
                var current = _libraryTracks.FirstOrDefault(t => t.Id == editedId);
                if (current is not null)
                {
                    NowPlayingTitle.Text = current.Title;
                    NowPlayingArtist.Text = current.Artist;
                    FullscreenTitle.Text = current.Title;
                    FullscreenArtist.Text = current.Artist;
                    NowPlayingArtwork.Source = !string.IsNullOrWhiteSpace(current.ArtworkPath) && File.Exists(current.ArtworkPath) ? new BitmapImage(new Uri(Path.GetFullPath(current.ArtworkPath))) : null;
                    FullscreenArtwork.Source = !string.IsNullOrWhiteSpace(current.ArtworkPath) && File.Exists(current.ArtworkPath) ? new BitmapImage(new Uri(Path.GetFullPath(current.ArtworkPath))) : null;
                }
            }
            ShowToast("Song info saved");
        }
        catch (Exception ex) { EditTrackStatus.Text = $"Could not save changes: {ex.Message}"; }
    }

    private async Task RestoreViewAfterTrackEditAsync()
    {
        _page = _editReturnPage;
        _activePlaylist = _editReturnPlaylist;
        _activeCollection = _editReturnCollection;
        SearchBox.Text = _editReturnSearch;

        if (_editReturnWasDetail && !string.IsNullOrWhiteSpace(_editReturnCollection))
        {
            bool exists = _editReturnPage switch
            {
                "Albums" => _libraryTracks.Any(t => string.Equals(t.Album, _editReturnCollection, StringComparison.OrdinalIgnoreCase)),
                "Artists" => _libraryTracks.Any(t => SplitArtists(t.Artist).Any(a => string.Equals(a, _editReturnCollection, StringComparison.OrdinalIgnoreCase))),
                "Playlists" => (await App.Database.GetPlaylistNamesAsync()).Any(n => string.Equals(n, _editReturnCollection, StringComparison.OrdinalIgnoreCase)),
                _ => false
            };
            if (exists)
            {
                // Re-open the exact collection the editor was launched from.
                if (_editReturnPage == "Playlists") _collectionTracks = await App.Database.GetPlaylistTracksAsync(_editReturnCollection);
                else if (_editReturnPage == "Albums") _collectionTracks = _libraryTracks.Where(t => string.Equals(t.Album, _editReturnCollection, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList();
                else _collectionTracks = _libraryTracks.Where(t => SplitArtists(t.Artist).Any(a => string.Equals(a, _editReturnCollection, StringComparison.OrdinalIgnoreCase))).OrderBy(t => t.Album).ThenBy(t => t.TrackNumber).ToList();
                HomePanel.Visibility = Visibility.Collapsed; CollectionList.Visibility = Visibility.Collapsed; CollectionListView.Visibility = Visibility.Collapsed; TrackList.Visibility = Visibility.Collapsed; DetailPanel.Visibility = Visibility.Visible; SettingsPanel.Visibility = Visibility.Collapsed; SearchShell.Visibility = Visibility.Collapsed;
                PageTitle.Text = _editReturnCollection; DetailTitle.Text = _editReturnCollection; DetailType.Text = _editReturnPage == "Albums" ? "ALBUM" : _editReturnPage == "Artists" ? "ARTIST" : "PLAYLIST"; DetailSubtitle.Text = $"{_collectionTracks.Count} tracks • {FormatTime(TimeSpan.FromSeconds(_collectionTracks.Sum(t => t.DurationSeconds)))}";
                DetailTrackSortHeader.Visibility = Visibility.Visible; DetailSortBar.Visibility = _editReturnPage == "Artists" ? Visibility.Collapsed : Visibility.Visible; CustomSortButton.Visibility = _editReturnPage == "Playlists" ? Visibility.Visible : Visibility.Collapsed; PlaylistControls.Visibility = _editReturnPage == "Playlists" ? Visibility.Visible : Visibility.Collapsed; PlaylistCoverActions.Visibility = _editReturnPage == "Playlists" ? Visibility.Visible : Visibility.Collapsed;
                if (_editReturnPage == "Playlists") await RenderCollectionCoverAsync(_collectionTracks, await App.Database.GetPlaylistCoverPathAsync(_editReturnCollection));
                else await RenderCollectionCoverAsync(_collectionTracks, _collectionTracks.FirstOrDefault()?.ArtworkPath, _editReturnPage == "Albums");
                ApplyCollectionSort(); UpdateNavVisuals();
                DispatcherQueue.TryEnqueue(() => MainContentScrollViewer.ChangeView(null, _editReturnScrollOffset, null, true));
                return;
            }
        }

        await ShowPageAsync(_editReturnPage);
        if (_editReturnPage == "Search") OnSearchChanged(SearchBox, null!);
        DispatcherQueue.TryEnqueue(() => MainContentScrollViewer.ChangeView(null, _editReturnScrollOffset, null, true));
    }

    private static async Task SaveArtworkAsJpegAsync(string sourcePath, string destinationPath)
    {
        var source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(sourcePath));
        using var input = await source.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destinationPath)!);
        var output = await folder.CreateFileAsync(Path.GetFileName(destinationPath), CreationCollisionOption.ReplaceExisting);
        using var stream = await output.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, decoder.PixelWidth, decoder.PixelHeight, decoder.DpiX, decoder.DpiY, pixels);
        await encoder.FlushAsync();
    }

    private static void TryWriteEditedTags(Track track)
    {
        try
        {
            using var tagged = TagLib.File.Create(track.FilePath);
            tagged.Tag.Title = track.Title;
            tagged.Tag.Performers = [track.Artist];
            tagged.Tag.AlbumArtists = [track.Artist];
            tagged.Tag.Album = track.Album;
            if (!string.IsNullOrWhiteSpace(track.ArtworkPath) && File.Exists(track.ArtworkPath))
            {
                var bytes = File.ReadAllBytes(track.ArtworkPath);
                tagged.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(bytes)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg", Description = "Cover" }];
            }
            tagged.Save();
        }
        catch { /* The database remains authoritative if a codec/tag format cannot be written. */ }
    }

    private void CleanupEditWorkingArtwork()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_editArtworkWorkingPath) && File.Exists(_editArtworkWorkingPath) && _editArtworkWorkingPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) File.Delete(_editArtworkWorkingPath);
        }
        catch { }
        _editArtworkWorkingPath = null;
    }

    private async void OnQueueItemTapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Track track) return;
        var index = _queue.ToList().FindIndex(t => t.Id == track.Id);
        if (index < 0) return;
        for (var i = 0; i <= index && _queue.Count > 0; i++) _queue.RemoveAt(0);
        _queueIndex = -1;
        await PlayTrackAsync(track);
    }

    private void OnQueueItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Track track) return;
        var flyout = new MenuFlyout();
        var remove = new MenuFlyoutItem { Text = "Remove from queue" };
        remove.Click += (_, _) => RemoveTrackFromQueue(track);
        flyout.Items.Add(remove);
        flyout.ShowAt((FrameworkElement)sender);
        e.Handled = true;
    }

    private void OnNavHover(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button b) { b.BorderThickness = new Thickness(1); b.BorderBrush = new SolidColorBrush((Application.Current.Resources["SectionBorderBrush"] as SolidColorBrush)?.Color ?? ParseColor(_accent!)); b.Background = new SolidColorBrush((Application.Current.Resources["SectionHoverBrush"] as SolidColorBrush)?.Color ?? ParseColor(_accent!)); }
    }
    private void OnNavUnhover(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button b && !IsActiveNav(b)) { b.BorderThickness = new Thickness(0); b.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0)); }
    }
    private bool IsActiveNav(Button b) => (_page, b.Name) switch { ("Home", "HomeNav") or ("Search", "SearchNav") or ("Songs", "SongsNav") or ("Albums", "AlbumsNav") or ("Artists", "ArtistsNav") or ("Playlists", "PlaylistsNav") => true, _ => false };
    private void OnQueueHover(object sender, PointerRoutedEventArgs e) { QueuePlayerButton.BorderThickness = new Thickness(1); QueuePlayerButton.BorderBrush = new SolidColorBrush((Application.Current.Resources["SectionBorderBrush"] as SolidColorBrush)?.Color ?? ParseColor(_accent!)); QueuePlayerButton.Background = new SolidColorBrush((Application.Current.Resources["SectionHoverBrush"] as SolidColorBrush)?.Color ?? ParseColor(_accent!)); }
    private void OnQueueUnhover(object sender, PointerRoutedEventArgs e) { if (QueuePanel.Visibility != Visibility.Visible) { QueuePlayerButton.BorderThickness = new Thickness(0); QueuePlayerButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0)); } }

    private void OnQueue(object sender, RoutedEventArgs e) { QueuePanel.Visibility = QueuePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; QueuePlayerButton.BorderThickness = QueuePanel.Visibility == Visibility.Visible ? new Thickness(1) : new Thickness(0); QueuePlayerButton.BorderBrush = new SolidColorBrush((Application.Current.Resources["SectionBorderBrush"] as SolidColorBrush)?.Color ?? ParseColor(_accent!)); QueuePlayerButton.Background = QueuePanel.Visibility == Visibility.Visible ? new SolidColorBrush((Application.Current.Resources["SectionActiveBrush"] as SolidColorBrush)?.Color ?? ParseColor(_accent!)) : new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0)); }

    private void OnQueueReordered(object sender, DragItemsCompletedEventArgs e)
    {
        _queueIndex = -1;
        UpdateQueueFooter();
    }

    private void OnClearQueue(object sender, RoutedEventArgs e)
    {
        _queue.Clear();
        _queueIndex = -1;
        _queueSourceName = null;
        UpdateQueueFooter();
        ShowToast("Queue cleared");
    }

    private void UpdateQueueFooter()
    {
        QueueSubtitle.Text = string.IsNullOrWhiteSpace(_queueSourceName)
            ? "Next from your library"
            : $"Next from {_queueSourceName}";
    }

    private async Task DownloadTrackAsync(Track track)
    {
        if (!File.Exists(track.FilePath)) { RemoveTrackFromQueue(track); ShowToast($"Removed missing track: {track.Title}"); return; }
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        var destination = GetUniquePath(downloads, Path.GetFileName(track.FilePath));
        await Task.Run(() => File.Copy(track.FilePath, destination));
        ShowToast($"Saved {track.Title} to Downloads");
    }

    private void OnOpenLibraryFolder(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo { FileName = App.Library.LibraryRoot, UseShellExecute = true });
    private async void OnChangeLibraryLocation(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var oldRoot = App.Library.LibraryRoot;
        var newRoot = Path.GetFullPath(folder.Path);
        if (string.Equals(oldRoot.TrimEnd(Path.DirectorySeparatorChar), newRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;

        ChangeLibraryLocationButton.IsEnabled = false;
        LibraryLocationStatus.Text = "Moving library…";
        LibraryLocationStatus.Visibility = Visibility.Visible;
        try
        {
            await App.MoveLibraryAsync(oldRoot, newRoot);
            LibraryLocationText.Text = App.Library.LibraryRoot;
            LibraryLocationStatus.Text = "Library moved successfully.";
            await LoadLibraryAsync();
            ShowToast("Library moved");
        }
        catch (Exception ex) { LibraryLocationStatus.Text = $"Could not move library: {ex.Message}"; }
        finally { ChangeLibraryLocationButton.IsEnabled = true; }
    }

    private async Task CreatePlaylistAsync()
    {
        var box = new TextBox { PlaceholderText = "Playlist name", Background = new SolidColorBrush(ParseColor(_darkTheme ? "#191B21" : "#ECEEF2")), BorderThickness = new Thickness(0), Padding = new Thickness(12) };
        var dialog = new ContentDialog { Title = "Create playlist", Content = box, PrimaryButtonText = "Create", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return;
        await App.Database.CreatePlaylistAsync(box.Text.Trim());
        ShowToast($"Created playlist {box.Text.Trim()}");
        await ShowPageAsync("Playlists");
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(3);
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer?.Stop(); };
        _toastTimer.Start();
    }

    private static string GetUniquePath(string folder, string filename)
    {
        var path = Path.Combine(folder, filename);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(filename);
        var ext = Path.GetExtension(filename);
        for (var i = 2; ; i++) { path = Path.Combine(folder, $"{stem} ({i}){ext}"); if (!File.Exists(path)) return path; }
    }
}

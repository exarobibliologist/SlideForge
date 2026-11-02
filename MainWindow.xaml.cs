using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace SlideForge
{
    public enum MediaKind
    {
        StaticImage,
        AnimatedGif,
        Video
    }

    public enum MediaSortMode
    {
        NameAsc,
        NameDesc,
        LocationAsc,
        LocationDesc,
        SizeAsc,
        SizeDesc,
        DateDesc,
        DateAsc
    }

    public class TrackOption
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public override string ToString() => Name;
    }

    // ========================================================================
    // NATURAL SORT COMPARER (So "File2.jpg" sorts before "File10.jpg")
    // ========================================================================
    internal static class NaturalStringComparer
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string psz1, string psz2);

        public static readonly IComparer<string> Instance = new LogicalComparer();

        private sealed class LogicalComparer : IComparer<string>
        {
            public int Compare(string? x, string? y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x is null) return -1;
                if (y is null) return 1;
                return StrCmpLogicalW(x, y);
            }
        }
    }

    // ========================================================================
    // SMPLAYER OSDB 64-BIT MOVIE HASH CALCULATOR (Ported from src/findsubtitles/oshash.cpp)
    // ========================================================================
    internal static class OpenSubtitlesHasher
    {
        private const int ChunkSize = 65536; // 64 KB head + 64 KB tail

        public static (string HashHex, long FileSize) ComputeMovieHash(string filePath)
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists || fi.Length < ChunkSize)
                    return (string.Empty, fi.Exists ? fi.Length : 0);

                long fileSize = fi.Length;
                ulong hash = (ulong)fileSize;

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] buffer = new byte[ChunkSize];

                // 1. Sum the first 64 KB as 8192 little-endian UInt64 words
                int readHead = fs.Read(buffer, 0, ChunkSize);
                for (int i = 0; i + 8 <= readHead; i += 8)
                {
                    unchecked
                    {
                        hash += BitConverter.ToUInt64(buffer, i);
                    }
                }

                // 2. Sum the last 64 KB as 8192 little-endian UInt64 words
                fs.Seek(Math.Max(0, fileSize - ChunkSize), SeekOrigin.Begin);
                int readTail = fs.Read(buffer, 0, ChunkSize);
                for (int i = 0; i + 8 <= readTail; i += 8)
                {
                    unchecked
                    {
                        hash += BitConverter.ToUInt64(buffer, i);
                    }
                }

                return (hash.ToString("x16"), fileSize);
            }
            catch
            {
                return (string.Empty, 0);
            }
        }

        public static string CleanMovieTitleFromFileName(string filePath)
        {
            string raw = Path.GetFileNameWithoutExtension(filePath);
            // Replace dots and underscores with spaces
            raw = raw.Replace('.', ' ').Replace('_', ' ');
            // Strip common release tags like 1080p, 720p, 2160p, x264, x265, BluRay, WEB-DL, etc.
            raw = Regex.Replace(raw, @"\b(2160p|1080p|720p|480p|x264|x265|h264|h265|hevc|bluray|brrip|web-dl|webrip|dvdrip|aac|ac3|dts|hdr|10bit|proper|repack)\b.*$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(raw, @"\s+", " ").Trim();
        }
    }

    // ========================================================================
    // WINDOWS SHELL THUMBNAIL EXTRACTOR (Generates thumbnails for MP4/MKV/AVI/MOV)
    // ========================================================================
    internal static class ShellThumbnailProvider
    {
        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage([In, MarshalAs(UnmanagedType.Struct)] NativeSize size, [In] int flags, [Out] out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width;
            public int Height;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            [In] IntPtr pbc,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        public static BitmapSource? ExtractThumbnail(string filePath, int width = 160, int height = 120)
        {
            IntPtr hBitmap = IntPtr.Zero;
            try
            {
                Guid iid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, iid, out IShellItemImageFactory factory);
                if (hr != 0 || factory == null) return null;

                hr = factory.GetImage(new NativeSize { Width = width, Height = height }, 0x00, out hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero) return null;

                BitmapSource bmp = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            }
        }
    }

    // ========================================================================
    // PLAYLIST DATA MODEL (Saved to .sfplay.json)
    // ========================================================================
    public class PlaylistSession
    {
        public List<string> SourceFolders { get; set; } = new();
        public bool IncludeSubfolders { get; set; }
        public bool RandomizeOrder { get; set; } = true;
        public string SortMode { get; set; } = nameof(MediaSortMode.NameAsc);
        public double IntervalSeconds { get; set; } = 5.0;
        public int TransitionIndex { get; set; } = 0;
        public bool KenBurnsEnabled { get; set; } = false;
        public List<string> FavoriteFiles { get; set; } = new();
        public List<string> CustomFileList { get; set; } = new();
    }

    // ========================================================================
    // LAZY-LOADED VIRTUALIZED FILMSTRIP ITEM
    // ========================================================================
    public class SlideItem : INotifyPropertyChanged
    {
        public string FilePath { get; }
        public string FileName => Path.GetFileName(FilePath);
        public string Extension => Path.GetExtension(FilePath).ToUpperInvariant().TrimStart('.');
        public MediaKind Kind { get; }

        private BitmapSource? _thumbnail;
        private bool _isLoadingThumbnail;
        private bool _isFavorite;

        public SlideItem(string filePath, bool isFavorite, MediaKind kind)
        {
            FilePath = filePath;
            _isFavorite = isFavorite;
            Kind = kind;
        }

        public string MediaBadgeText => Kind switch
        {
            MediaKind.Video => $"🎬 {Extension}",
            MediaKind.AnimatedGif => "GIF",
            _ => Extension
        };

        public Visibility MediaBadgeVisibility => Visibility.Visible;

        public bool IsFavorite
        {
            get => _isFavorite;
            set
            {
                if (_isFavorite != value)
                {
                    _isFavorite = value;
                    OnPropertyChanged(nameof(IsFavorite));
                    OnPropertyChanged(nameof(FavoriteBadgeVisibility));
                }
            }
        }

        public Visibility FavoriteBadgeVisibility => _isFavorite ? Visibility.Visible : Visibility.Collapsed;

        public BitmapSource? Thumbnail
        {
            get
            {
                if (_thumbnail == null && !_isLoadingThumbnail)
                {
                    _isLoadingThumbnail = true;
                    Task.Run(() =>
                    {
                        try
                        {
                            BitmapSource? bmp = null;

                            if (Kind == MediaKind.Video)
                            {
                                bmp = ShellThumbnailProvider.ExtractThumbnail(FilePath, 160, 120);
                            }
                            else
                            {
                                var img = new BitmapImage();
                                img.BeginInit();
                                img.UriSource = new Uri(FilePath, UriKind.Absolute);
                                img.DecodePixelWidth = 140;
                                img.CacheOption = BitmapCacheOption.OnLoad;
                                img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                                img.EndInit();
                                img.Freeze();
                                bmp = img;
                            }

                            bmp ??= ShellThumbnailProvider.ExtractThumbnail(FilePath, 160, 120);

                            if (bmp != null)
                            {
                                Application.Current?.Dispatcher.InvokeAsync(() =>
                                {
                                    _thumbnail = bmp;
                                    OnPropertyChanged(nameof(Thumbnail));
                                }, DispatcherPriority.Background);
                            }
                        }
                        catch
                        {
                            // Ignore unreadable thumbnails
                        }
                    });
                }
                return _thumbnail;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ========================================================================
    // MAIN WINDOW ENGINE
    // ========================================================================
    public partial class MainWindow : Window
    {
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private const string ExplorerMenuKeyFolder = @"Software\Classes\Directory\shell\SlideForge";
        private const string ExplorerMenuKeyBackground = @"Software\Classes\Directory\Background\shell\SlideForge";

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".webp", ".tiff", ".tif",
            ".ico", ".heic", ".heif", ".avif", ".wdp", ".jxr", ".dds", ".tga"
        };

        private static readonly HashSet<string> GifExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".gif"
        };

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v",
            ".mpg", ".mpeg", ".flv", ".ts", ".m2ts", ".mts", ".3gp",
            ".ogv", ".vob", ".divx", ".asf", ".f4v", ".rmvb",
            ".mp3", ".flac", ".wav", ".m4a", ".ogg", ".aac", ".wma", ".opus"
        };

        // Multi-folder, Filter, Sort & Favorites state
        private readonly List<string> _sourceFolders = new();
        private List<string> _masterFiles = new();
        private readonly HashSet<string> _favoriteFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<SlideItem> _playbackQueue = new();
        private string _activeFilterTag = "ALL";
        private MediaSortMode _currentSortMode = MediaSortMode.NameAsc;

        private int _currentIndex = -1;
        private bool _suppressFilmstripSelectionEvent = false;
        private bool _suppressShuffleCheckEvent = false;

        // Slideshow & HUD Timers
        private readonly DispatcherTimer _slideshowTimer = new();
        private readonly DispatcherTimer _hudHideTimer = new();
        private bool _isPlaying = false;
        private bool _isHudVisible = true;
        private bool _isFilmstripPinned = false;
        private bool _isFilmstripVisible = false;
        private Point _lastWindowMousePos;

        // Animated GIF Engine State
        private readonly DispatcherTimer _gifFrameTimer = new();
        private List<BitmapSource> _currentGifFrames = new();
        private List<TimeSpan> _currentGifDelays = new();
        private int _currentGifFrameIndex = 0;

        // ====================================================================
        // LIBVLC ENGINE & DIRECT WPF WRITEABLEBITMAP RENDERER STATE
        // ====================================================================
        private LibVLC? _libVlc;
        private VlcMediaPlayer? _vlcPlayer;
        private Media? _currentVlcMedia;
        private Task? _vlcInitTask;
        private readonly SemaphoreSlim _vlcInitLock = new(1, 1);

        private readonly VlcMediaPlayer.LibVLCVideoFormatCb _vlcFormatDelegate;
        private readonly VlcMediaPlayer.LibVLCVideoLockCb _vlcLockDelegate;
        private readonly VlcMediaPlayer.LibVLCVideoDisplayCb _vlcDisplayDelegate;

        private readonly object _videoBufferLock = new();
        private IntPtr _videoFrameBuffer = IntPtr.Zero;
        private uint _allocatedBufferSize = 0;
        private uint _vlcVideoWidth = 0;
        private uint _vlcVideoHeight = 0;
        private uint _vlcVideoPitch = 0;
        private WriteableBitmap? _videoWriteableBitmap;
        private int _isFrameRenderPending = 0;

        private readonly DispatcherTimer _videoProgressTimer = new();
        private bool _isVideoActive = false;
        private bool _isVideoPaused = false;
        private bool _isDraggingVideoSlider = false;
        private bool _isMuted = false;
        private bool _suppressAudioTrackSelection = false;
        private bool _suppressSubtitleTrackSelection = false;

        // Fullscreen state
        private bool _isFullscreen = false;
        private WindowState _previousWindowState;
        private WindowStyle _previousWindowStyle;

        // Pan & Right-Click Pull-to-Zoom state
        private bool _isLeftPanning = false;
        private Point _lastPanMousePos;
        private bool _isRightZooming = false;
        private Point _zoomAnchorPoint;
        private Point _lastRightZoomPos;
        private double _currentZoom = 1.0;

        public MainWindow()
        {
            InitializeComponent();
            FilmstripList.ItemsSource = _playbackQueue;

            _vlcFormatDelegate = OnVlcVideoFormat;
            _vlcLockDelegate = OnVlcVideoLock;
            _vlcDisplayDelegate = OnVlcVideoDisplay;

            _slideshowTimer.Interval = TimeSpan.FromSeconds(5);
            _slideshowTimer.Tick += (s, e) => StepImage(1, isAutoTick: true);

            _gifFrameTimer.Tick += GifFrameTimer_Tick;

            _videoProgressTimer.Interval = TimeSpan.FromMilliseconds(150);
            _videoProgressTimer.Tick += VideoProgressTimer_Tick;

            _hudHideTimer.Interval = TimeSpan.FromSeconds(2.4);
            _hudHideTimer.Tick += (s, e) =>
            {
                _hudHideTimer.Stop();
                bool anyMenuOpen = (FileFolderMenu?.IsOpen == true) ||
                                   (PlaylistMenu?.IsOpen == true) ||
                                   (SortMediaMenu?.IsOpen == true);
                if (!TopHud.IsMouseOver && !FilmstripDrawer.IsMouseOver && !VideoControlBar.IsMouseOver && !anyMenuOpen)
                {
                    SetHudVisibility(false);
                    if (!_isFilmstripPinned) SetFilmstripVisibility(false);
                }
            };
            _hudHideTimer.Start();

            Activated += (s, e) =>
            {
                if (Keyboard.FocusedElement is not TextBox)
                {
                    ReclaimWindowKeyboardFocus();
                }
            };

            if (FileFolderMenu != null)
                FileFolderMenu.Closed += (s, e) => Dispatcher.BeginInvoke(ReclaimWindowKeyboardFocus);

            if (PlaylistMenu != null)
                PlaylistMenu.Closed += (s, e) => Dispatcher.BeginInvoke(ReclaimWindowKeyboardFocus);

            if (SortMediaMenu != null)
                SortMediaMenu.Closed += (s, e) => Dispatcher.BeginInvoke(ReclaimWindowKeyboardFocus);

            ComponentDispatcher.ThreadPreprocessMessage += ComponentDispatcher_ThreadPreprocessMessage;

            Loaded += Window_Loaded;
        }

        private void ReclaimWindowKeyboardFocus()
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, this);
            Keyboard.Focus(this);
        }

        // ====================================================================
        // COMMAND-LINE LAUNCH ("OPEN WITH...") & WINDOWS EXPLORER CONTEXT MENU
        // ====================================================================

        private static string CleanExplorerPath(string rawArg)
        {
            if (string.IsNullOrWhiteSpace(rawArg)) return string.Empty;
            string cleaned = rawArg.Trim().Trim('"').Trim();
            if (cleaned.Length > 3 && cleaned.EndsWith('\\'))
            {
                cleaned = cleaned.TrimEnd('\\');
            }
            return cleaned;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (Keyboard.FocusedElement is not TextBox)
            {
                ReclaimWindowKeyboardFocus();
            }

            MnuExplorerMenu.IsChecked = IsExplorerContextMenuRegistered();
            UpdateVolumeBadgeVisual();

            // Soft-load LibVLC in the background only after the window and first image have rendered
            _ = Dispatcher.InvokeAsync(() => _ = EnsureLibVlcInitializedAsync(), DispatcherPriority.ApplicationIdle);

            string[] args = Environment.GetCommandLineArgs();
            if (args.Length <= 1) return;

            string firstArg = CleanExplorerPath(args[1]);

            // 1. If the user opened a saved .sfplay.json playlist file
            if (File.Exists(firstArg) &&
                (firstArg.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                 firstArg.EndsWith(".sfplay", StringComparison.OrdinalIgnoreCase)))
            {
                LoadPlaylistFromFile(firstArg);
                return;
            }

            // 2. If the user clicked "Open With..." on a single image, GIF, or video:
            // Preserve the folder's natural sort order and queue the clicked file at its natural position!
            if (args.Length == 2 && File.Exists(firstArg) && IsSupportedMedia(firstArg))
            {
                string? parentDir = Path.GetDirectoryName(firstArg);
                if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                {
                    SetShuffleCheckedSilent(false);
                    _sourceFolders.Clear();
                    _sourceFolders.Add(parentDir);
                    RescanSourceFolders(priorityFirstFile: firstArg);
                }
                return;
            }

            // 3. If launched via "Open Folder in SlideForge..." or multiple selected items
            bool addedFolders = false;
            var customFiles = new List<string>();

            for (int i = 1; i < args.Length; i++)
            {
                string path = CleanExplorerPath(args[i]);
                if (Directory.Exists(path))
                {
                    if (!addedFolders) _sourceFolders.Clear();
                    addedFolders = true;
                    if (!_sourceFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
                        _sourceFolders.Add(path);
                }
                else if (File.Exists(path) && IsSupportedMedia(path))
                {
                    customFiles.Add(path);
                }
            }

            if (addedFolders)
            {
                RescanSourceFolders();
            }
            else if (customFiles.Count > 0)
            {
                SetShuffleCheckedSilent(false);
                _sourceFolders.Clear();
                _masterFiles = customFiles;
                RebuildPlaybackQueue(keepCurrentImage: false, priorityFirstFile: customFiles[0]);
            }
        }

        private void SetShuffleCheckedSilent(bool isChecked)
        {
            _suppressShuffleCheckEvent = true;
            ChkShuffle.IsChecked = isChecked;
            _suppressShuffleCheckEvent = false;
        }

        private static bool IsExplorerContextMenuRegistered()
        {
            try
            {
                using RegistryKey? hkcuKey = Registry.CurrentUser.OpenSubKey(ExplorerMenuKeyFolder);
                if (hkcuKey != null) return true;

                using RegistryKey? hkcrKey = Registry.ClassesRoot.OpenSubKey(@"Directory\shell\SlideForge");
                return hkcrKey != null;
            }
            catch
            {
                return false;
            }
        }

        private void MnuExplorerMenu_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return;

                if (MnuExplorerMenu.IsChecked)
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ExplorerMenuKeyFolder))
                    {
                        key.SetValue("", "Open Folder in SlideForge...");
                        key.SetValue("Icon", $"\"{exePath}\",0");
                        using RegistryKey cmdKey = key.CreateSubKey("command");
                        cmdKey.SetValue("", $"\"{exePath}\" \"%1\"");
                    }

                    using (RegistryKey bgKey = Registry.CurrentUser.CreateSubKey(ExplorerMenuKeyBackground))
                    {
                        bgKey.SetValue("", "Open Folder in SlideForge...");
                        bgKey.SetValue("Icon", $"\"{exePath}\",0");
                        using RegistryKey cmdKey = bgKey.CreateSubKey("command");
                        cmdKey.SetValue("", $"\"{exePath}\" \"%V\"");
                    }

                    TxtStatusRight.Text = "📌 Added 'Open Folder in SlideForge...' to Windows Explorer right-click menu!";
                }
                else
                {
                    Registry.CurrentUser.DeleteSubKeyTree(ExplorerMenuKeyFolder, throwOnMissingSubKey: false);
                    Registry.CurrentUser.DeleteSubKeyTree(ExplorerMenuKeyBackground, throwOnMissingSubKey: false);
                    TxtStatusRight.Text = "Removed 'Open Folder in SlideForge...' from Windows Explorer.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not update Explorer context menu:\n{ex.Message}", "Registry Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task EnsureLibVlcInitializedAsync()
        {
            if (_libVlc != null && _vlcPlayer != null) return;

            await _vlcInitLock.WaitAsync();
            try
            {
                if (_libVlc != null && _vlcPlayer != null) return;

                _vlcInitTask ??= Task.Run(() =>
                {
                    Core.Initialize();
                    var vlc = new LibVLC(
                        "--no-video-title-show",
                        "--sub-autodetect-file",
                        "--plugins-cache",
                        "--no-stats",
                        "--no-media-library",
                        "--no-interact",
                        "--quiet");

                    var player = new VlcMediaPlayer(vlc);
                    player.SetVideoFormatCallbacks(_vlcFormatDelegate, null);
                    player.SetVideoCallbacks(_vlcLockDelegate, null, _vlcDisplayDelegate);

                    player.Playing += VlcPlayer_Playing;
                    player.EndReached += VlcPlayer_EndReached;
                    player.EncounteredError += VlcPlayer_EncounteredError;

                    _libVlc = vlc;
                    _vlcPlayer = player;
                });

                await _vlcInitTask;
            }
            catch (Exception ex)
            {
                _vlcInitTask = null;
                _ = Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        $"LibVLC initialization error:\n{ex.Message}\n\nMake sure 'VideoLAN.LibVLC.Windows' and 'LibVLCSharp.WPF' NuGet packages are installed.",
                        "LibVLC Startup", MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            }
            finally
            {
                _vlcInitLock.Release();
            }
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            ComponentDispatcher.ThreadPreprocessMessage -= ComponentDispatcher_ThreadPreprocessMessage;

            _isVideoActive = false;
            _slideshowTimer.Stop();
            _gifFrameTimer.Stop();
            _videoProgressTimer.Stop();

            try
            {
                _vlcPlayer?.Stop();
                _currentVlcMedia?.Dispose();
                _vlcPlayer?.Dispose();
                _libVlc?.Dispose();
            }
            catch
            {
                // Ignore shutdown disposal exceptions
            }

            lock (_videoBufferLock)
            {
                if (_videoFrameBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_videoFrameBuffer);
                    _videoFrameBuffer = IntPtr.Zero;
                }
            }
        }

        private static bool IsSupportedMedia(string filePath)
        {
            string ext = Path.GetExtension(filePath);
            return ImageExtensions.Contains(ext) || GifExtensions.Contains(ext) || VideoExtensions.Contains(ext);
        }

        private static MediaKind GetMediaKind(string filePath)
        {
            string ext = Path.GetExtension(filePath);
            if (VideoExtensions.Contains(ext)) return MediaKind.Video;
            if (GifExtensions.Contains(ext)) return MediaKind.AnimatedGif;
            return MediaKind.StaticImage;
        }

        private bool FileMatchesActiveFilter(string filePath)
        {
            if (string.IsNullOrEmpty(_activeFilterTag) || _activeFilterTag == "ALL")
                return true;

            string ext = Path.GetExtension(filePath);
            if (_activeFilterTag == "IMAGES")
                return ImageExtensions.Contains(ext);
            if (_activeFilterTag == "VIDEOS")
                return VideoExtensions.Contains(ext);

            var allowedExts = _activeFilterTag.Split(';', StringSplitOptions.RemoveEmptyEntries);
            return allowedExts.Any(a => string.Equals(a, ext, StringComparison.OrdinalIgnoreCase));
        }

        // ====================================================================
        // 1. DROP-DOWN SUBMENU BUTTONS & MULTI-FOLDER / FILE LOADING
        // ====================================================================

        private void BtnFileFolder_Click(object sender, RoutedEventArgs e)
        {
            if (BtnFileFolder.ContextMenu != null)
            {
                BtnFileFolder.ContextMenu.PlacementTarget = BtnFileFolder;
                BtnFileFolder.ContextMenu.Placement = PlacementMode.Bottom;
                BtnFileFolder.ContextMenu.IsOpen = true;
            }
        }

        private void BtnPlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (BtnPlaylist.ContextMenu != null)
            {
                BtnPlaylist.ContextMenu.PlacementTarget = BtnPlaylist;
                BtnPlaylist.ContextMenu.Placement = PlacementMode.Bottom;
                BtnPlaylist.ContextMenu.IsOpen = true;
            }
        }

        private void BtnSortMedia_Click(object sender, RoutedEventArgs e)
        {
            if (BtnSortMedia.ContextMenu != null)
            {
                BtnSortMedia.ContextMenu.PlacementTarget = BtnSortMedia;
                BtnSortMedia.ContextMenu.Placement = PlacementMode.Top;
                BtnSortMedia.ContextMenu.IsOpen = true;
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Open Media Folder (Replaces Current Session)" };
            if (dialog.ShowDialog() == true)
            {
                _sourceFolders.Clear();
                _sourceFolders.Add(dialog.FolderName);
                RescanSourceFolders();
            }
        }

        private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Add Another Folder to Slideshow Pool" };
            if (dialog.ShowDialog() == true)
            {
                if (!_sourceFolders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
                {
                    _sourceFolders.Add(dialog.FolderName);
                    RescanSourceFolders();
                }
            }
        }

        private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Images, GIFs, Videos, or Audio to Add",
                Multiselect = true,
                Filter = "All Supported Media|*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.webp;*.gif;*.tiff;*.tif;*.ico;*.heic;*.avif;*.mp4;*.mkv;*.mov;*.avi;*.webm;*.wmv;*.m4v;*.mpg;*.mpeg;*.flv;*.ts;*.m2ts;*.mp3;*.flac;*.wav;*.m4a;*.ogg;*.aac;*.wma;*.opus|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
            {
                var combined = new HashSet<string>(_masterFiles, StringComparer.OrdinalIgnoreCase);
                foreach (string file in dialog.FileNames)
                {
                    if (File.Exists(file) && IsSupportedMedia(file))
                    {
                        combined.Add(file);
                    }
                }
                _masterFiles = combined.OrderBy(f => f, NaturalStringComparer.Instance).ToList();
                RebuildPlaybackQueue(keepCurrentImage: _currentIndex >= 0, priorityFirstFile: _currentIndex < 0 ? dialog.FileNames[0] : null);
            }
        }

        private void ChkSubfolders_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _sourceFolders.Count == 0) return;
            RescanSourceFolders();
        }

        private void RescanSourceFolders(string? priorityFirstFile = null)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = MnuSubfolders.IsChecked == true,
                IgnoreInaccessible = true
            };

            var combined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string folder in _sourceFolders)
            {
                if (!Directory.Exists(folder)) continue;
                try
                {
                    foreach (string file in Directory.EnumerateFiles(folder, "*.*", options))
                    {
                        if (IsSupportedMedia(file))
                        {
                            combined.Add(file);
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible folders cleanly
                }
            }

            if (!string.IsNullOrEmpty(priorityFirstFile) && File.Exists(priorityFirstFile) && IsSupportedMedia(priorityFirstFile))
            {
                combined.Add(priorityFirstFile);
            }

            _masterFiles = combined.OrderBy(f => f, NaturalStringComparer.Instance).ToList();
            RebuildPlaybackQueue(keepCurrentImage: false, priorityFirstFile: priorityFirstFile);
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var dropped = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (dropped == null || dropped.Length == 0) return;

            bool addedFolders = false;
            foreach (string item in dropped)
            {
                if (Directory.Exists(item))
                {
                    if (!addedFolders) _sourceFolders.Clear();
                    addedFolders = true;
                    if (!_sourceFolders.Contains(item, StringComparer.OrdinalIgnoreCase))
                        _sourceFolders.Add(item);
                }
            }

            if (addedFolders)
            {
                RescanSourceFolders();
            }
            else
            {
                string? parentDir = Path.GetDirectoryName(dropped[0]);
                if (dropped.Length == 1 && parentDir != null && IsSupportedMedia(dropped[0]))
                {
                    // Single file dropped: preserve natural sort order and queue the dropped file right at its folder position
                    string targetFile = dropped[0];
                    SetShuffleCheckedSilent(false);
                    _sourceFolders.Clear();
                    _sourceFolders.Add(parentDir);
                    RescanSourceFolders(priorityFirstFile: targetFile);
                }
                else
                {
                    SetShuffleCheckedSilent(false);
                    _sourceFolders.Clear();
                    _masterFiles = dropped
                        .Where(f => File.Exists(f) && IsSupportedMedia(f))
                        .ToList();
                    RebuildPlaybackQueue(keepCurrentImage: false, priorityFirstFile: _masterFiles.FirstOrDefault());
                }
            }
        }

        // ====================================================================
        // 2. QUEUE BUILDING, CONDITIONAL SORTING, FILTERING & SHUFFLE
        // ====================================================================

        private void SortMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem clickedItem &&
                clickedItem.Tag is string modeStr &&
                Enum.TryParse<MediaSortMode>(modeStr, out MediaSortMode selectedMode))
            {
                ApplySortModeSelection(selectedMode);

                // Automatically uncheck Randomize so the chosen sort order takes effect immediately
                if (ChkShuffle.IsChecked == true)
                {
                    SetShuffleCheckedSilent(false);
                }

                RebuildPlaybackQueue(keepCurrentImage: true);
            }
        }

        private void ApplySortModeSelection(MediaSortMode mode)
        {
            _currentSortMode = mode;

            if (SortMediaMenu != null)
            {
                foreach (object item in SortMediaMenu.Items)
                {
                    if (item is MenuItem mi && mi.Tag is string tag)
                    {
                        mi.IsChecked = string.Equals(tag, mode.ToString(), StringComparison.OrdinalIgnoreCase);
                    }
                }
            }

            if (BtnSortMedia != null)
            {
                BtnSortMedia.Content = mode switch
                {
                    MediaSortMode.NameAsc => "↕ Sort: Name (A–Z) ▾",
                    MediaSortMode.NameDesc => "↕ Sort: Name (Z–A) ▾",
                    MediaSortMode.LocationAsc => "↕ Sort: Location (A–Z) ▾",
                    MediaSortMode.LocationDesc => "↕ Sort: Location (Z–A) ▾",
                    MediaSortMode.SizeAsc => "↕ Sort: Size (Small→Large) ▾",
                    MediaSortMode.SizeDesc => "↕ Sort: Size (Large→Small) ▾",
                    MediaSortMode.DateDesc => "↕ Sort: Date (Newest) ▾",
                    MediaSortMode.DateAsc => "↕ Sort: Date (Oldest) ▾",
                    _ => "↕ Sort ▾"
                };
            }
        }

        private List<string> SortFileList(IEnumerable<string> files)
        {
            return _currentSortMode switch
            {
                MediaSortMode.NameAsc => files
                    .OrderBy(f => Path.GetFileName(f), NaturalStringComparer.Instance)
                    .ThenBy(f => f, NaturalStringComparer.Instance)
                    .ToList(),

                MediaSortMode.NameDesc => files
                    .OrderByDescending(f => Path.GetFileName(f), NaturalStringComparer.Instance)
                    .ThenByDescending(f => f, NaturalStringComparer.Instance)
                    .ToList(),

                MediaSortMode.LocationAsc => files
                    .OrderBy(f => Path.GetDirectoryName(f) ?? "", NaturalStringComparer.Instance)
                    .ThenBy(f => Path.GetFileName(f), NaturalStringComparer.Instance)
                    .ToList(),

                MediaSortMode.LocationDesc => files
                    .OrderByDescending(f => Path.GetDirectoryName(f) ?? "", NaturalStringComparer.Instance)
                    .ThenByDescending(f => Path.GetFileName(f), NaturalStringComparer.Instance)
                    .ToList(),

                MediaSortMode.SizeAsc => files
                    .OrderBy(GetSafeFileLength)
                    .ThenBy(f => Path.GetFileName(f), NaturalStringComparer.Instance)
                    .ToList(),

                MediaSortMode.SizeDesc => files
                    .OrderByDescending(GetSafeFileLength)
                    .ThenBy(f => Path.GetFileName(f), NaturalStringComparer.Instance)
                    .ToList(),

                MediaSortMode.DateDesc => files
                    .OrderByDescending(GetSafeLastWriteTimeTicks)
                    .ThenBy(f => Path.GetFileName(f), NaturalStringComparer.Instance)
                    .ToList(),

                MediaSortMode.DateAsc => files
                    .OrderBy(GetSafeLastWriteTimeTicks)
                    .ThenBy(f => Path.GetFileName(f), NaturalStringComparer.Instance)
                    .ToList(),

                _ => files.OrderBy(f => Path.GetFileName(f), NaturalStringComparer.Instance).ToList()
            };
        }

        private static long GetSafeFileLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0L; }
        }

        private static long GetSafeLastWriteTimeTicks(string path)
        {
            try { return File.GetLastWriteTimeUtc(path).Ticks; }
            catch { return 0L; }
        }

        private void FilterPill_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (sender is RadioButton rb && rb.Tag is string tag)
            {
                _activeFilterTag = tag;
                RebuildPlaybackQueue(keepCurrentImage: true);
            }
        }

        private void RebuildPlaybackQueue(bool keepCurrentImage, string? priorityFirstFile = null)
        {
            string? currentFile = (_currentIndex >= 0 && _currentIndex < _playbackQueue.Count)
                ? _playbackQueue[_currentIndex].FilePath
                : null;

            IEnumerable<string> pool = _masterFiles.Where(FileMatchesActiveFilter);

            if (ChkFavoritesOnly.IsChecked == true)
            {
                pool = pool.Where(f => _favoriteFiles.Contains(f));
            }

            // 1. Apply the active conditional sort order (Name, Location, Size, or Timestamp)
            List<string> workingList = SortFileList(pool);

            // 2. If Randomize is enabled, shuffle the pool
            if (ChkShuffle.IsChecked == true && workingList.Count > 1)
            {
                ShuffleStrings(workingList, avoidFirstItem: currentFile);

                if (!string.IsNullOrEmpty(priorityFirstFile))
                {
                    int targetIdx = workingList.FindIndex(f => string.Equals(f, priorityFirstFile, StringComparison.OrdinalIgnoreCase));
                    if (targetIdx > 0)
                    {
                        (workingList[0], workingList[targetIdx]) = (workingList[targetIdx], workingList[0]);
                    }
                }
            }

            _suppressFilmstripSelectionEvent = true;
            _playbackQueue.Clear();
            foreach (string path in workingList)
            {
                _playbackQueue.Add(new SlideItem(path, _favoriteFiles.Contains(path), GetMediaKind(path)));
            }
            _suppressFilmstripSelectionEvent = false;

            if (TxtFilterCount != null)
            {
                TxtFilterCount.Text = $"{_playbackQueue.Count} / {_masterFiles.Count} shown";
            }

            if (_playbackQueue.Count == 0)
            {
                StopAllActiveMedia();
                _currentIndex = -1;
                EmptyStatePanel.Visibility = Visibility.Visible;
                ImgFront.Source = null;
                ImgBack.Source = null;
                TxtFileInfo.Text = ChkFavoritesOnly.IsChecked == true
                    ? "No ★ Favorited media in current filter. Uncheck '★ Favs Only' in the bottom bar or press F on items to star them."
                    : _masterFiles.Count > 0
                        ? $"No files match the current filter ({_activeFilterTag}). Select 'All Media' in the filmstrip bar."
                        : "No supported images, GIFs, or videos found.";
                return;
            }

            EmptyStatePanel.Visibility = Visibility.Collapsed;

            // If we have a priority target file (from Open With... or single-file drop), queue it directly at its sorted index
            if (!string.IsNullOrEmpty(priorityFirstFile))
            {
                int priorityIdx = -1;
                for (int i = 0; i < _playbackQueue.Count; i++)
                {
                    if (string.Equals(_playbackQueue[i].FilePath, priorityFirstFile, StringComparison.OrdinalIgnoreCase))
                    {
                        priorityIdx = i;
                        break;
                    }
                }

                ShowMediaAtIndex(priorityIdx >= 0 ? priorityIdx : 0, useTransition: false, direction: 1);
                return;
            }

            if (keepCurrentImage && currentFile != null)
            {
                int foundIdx = -1;
                for (int i = 0; i < _playbackQueue.Count; i++)
                {
                    if (string.Equals(_playbackQueue[i].FilePath, currentFile, StringComparison.OrdinalIgnoreCase))
                    {
                        foundIdx = i;
                        break;
                    }
                }

                if (foundIdx >= 0)
                {
                    _currentIndex = foundIdx;
                    SyncFilmstripSelection();
                    UpdateStatusBar();
                }
                else
                {
                    ShowMediaAtIndex(0, useTransition: false, direction: 1);
                }
            }
            else
            {
                ShowMediaAtIndex(0, useTransition: false, direction: 1);
            }
        }

        private static void ShuffleStrings(List<string> list, string? avoidFirstItem = null)
        {
            int n = list.Count;
            for (int i = n - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }

            if (avoidFirstItem != null && list.Count > 1 &&
                string.Equals(list[0], avoidFirstItem, StringComparison.OrdinalIgnoreCase))
            {
                int swapIdx = Random.Shared.Next(1, list.Count);
                (list[0], list[swapIdx]) = (list[swapIdx], list[0]);
            }
        }

        private static void ShuffleSlideItems(ObservableCollection<SlideItem> items, string? avoidFirstPath = null)
        {
            var temp = items.ToList();
            int n = temp.Count;
            for (int i = n - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (temp[i], temp[j]) = (temp[j], temp[i]);
            }

            if (avoidFirstPath != null && temp.Count > 1 &&
                string.Equals(temp[0].FilePath, avoidFirstPath, StringComparison.OrdinalIgnoreCase))
            {
                int swapIdx = Random.Shared.Next(1, temp.Count);
                (temp[0], temp[swapIdx]) = (temp[swapIdx], temp[0]);
            }

            items.Clear();
            foreach (var item in temp) items.Add(item);
        }

        private void ChkShuffle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressShuffleCheckEvent || !IsLoaded || _masterFiles.Count == 0) return;
            RebuildPlaybackQueue(keepCurrentImage: true);
        }

        private void BtnReshuffle_Click(object sender, RoutedEventArgs e)
        {
            if (_masterFiles.Count <= 1) return;
            SetShuffleCheckedSilent(true);
            RebuildPlaybackQueue(keepCurrentImage: false);
        }

        // ====================================================================
        // 3. UNIFIED MEDIA DISPLAY (IMAGES, ANIMATED GIFS & LIBVLC VIDEOS)
        // ====================================================================

        private void StepImage(int delta, bool isAutoTick = false)
        {
            if (_playbackQueue.Count == 0) return;

            if (isAutoTick && _isVideoActive && ChkWaitVideoFinish.IsChecked == true && !_isVideoPaused)
            {
                return;
            }

            int nextIndex = _currentIndex + delta;

            if (nextIndex >= _playbackQueue.Count)
            {
                if (ChkShuffle.IsChecked == true && _playbackQueue.Count > 1)
                {
                    string lastShown = _playbackQueue[_currentIndex].FilePath;
                    _suppressFilmstripSelectionEvent = true;
                    ShuffleSlideItems(_playbackQueue, avoidFirstPath: lastShown);
                    _suppressFilmstripSelectionEvent = false;
                }
                nextIndex = 0;
            }
            else if (nextIndex < 0)
            {
                nextIndex = _playbackQueue.Count - 1;
            }

            ShowMediaAtIndex(nextIndex, useTransition: true, direction: delta >= 0 ? 1 : -1);

            if (!isAutoTick && _isPlaying)
            {
                _slideshowTimer.Stop();
                _slideshowTimer.Start();
            }
        }

        private void ShowMediaAtIndex(int index, bool useTransition, int direction)
        {
            if (index < 0 || index >= _playbackQueue.Count) return;

            BitmapSource? outgoingSnapshot = CaptureCurrentVisualSnapshot();

            _currentIndex = index;
            SlideItem currentItem = _playbackQueue[_currentIndex];

            StopAllActiveMedia(stopVlcToo: currentItem.Kind != MediaKind.Video);

            ResetZoomMatrix();
            ClearLayerTransforms();

            if (currentItem.Kind == MediaKind.Video)
            {
                LoadAndPlayVideo(currentItem.FilePath, outgoingSnapshot, useTransition, direction);
            }
            else if (currentItem.Kind == MediaKind.AnimatedGif)
            {
                LoadAndPlayGif(currentItem.FilePath, outgoingSnapshot, useTransition, direction);
            }
            else
            {
                BitmapSource? newBitmap = DecodeBitmapSafe(currentItem.FilePath);
                if (newBitmap != null)
                {
                    ImgFront.Visibility = Visibility.Visible;
                    RunTransitionOnFrontLayer(ImgFront, FrontScale, FrontTranslate, newBitmap, outgoingSnapshot, useTransition, direction);

                    if (ChkKenBurns?.IsChecked == true)
                    {
                        StartKenBurnsAnimation(FrontScale, FrontTranslate, _slideshowTimer.Interval.TotalSeconds);
                    }
                }
            }

            SyncFilmstripSelection();
            UpdateFavoriteButtonVisual();
            UpdateStatusBar();
        }

        private BitmapSource? CaptureCurrentVisualSnapshot()
        {
            try
            {
                if (_isVideoActive && _videoWriteableBitmap != null)
                {
                    BitmapSource clone = _videoWriteableBitmap.Clone();
                    clone.Freeze();
                    return clone;
                }
                return ImgFront.Source as BitmapSource;
            }
            catch
            {
                return ImgFront.Source as BitmapSource;
            }
        }

        private void StopAllActiveMedia(bool stopVlcToo = true)
        {
            _gifFrameTimer.Stop();
            _currentGifFrames.Clear();
            _currentGifDelays.Clear();
            _currentGifFrameIndex = 0;

            if (_isVideoActive)
            {
                _videoProgressTimer.Stop();
                _isVideoActive = false;
                _isVideoPaused = false;
                VideoSurface.Visibility = Visibility.Collapsed;
                VideoControlBar.Visibility = Visibility.Collapsed;

                if (Keyboard.FocusedElement is not TextBox)
                {
                    ReclaimWindowKeyboardFocus();
                }

                if (stopVlcToo && _vlcPlayer != null)
                {
                    var player = _vlcPlayer;
                    Task.Run(() =>
                    {
                        try { player.Stop(); } catch { }
                    });
                }
            }
        }

        // ====================================================================
        // 4. ANIMATED GIF89a ENGINE
        // ====================================================================

        private void LoadAndPlayGif(string filePath, BitmapSource? outgoingSnapshot, bool useTransition, int direction)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

                if (decoder.Frames.Count <= 1)
                {
                    BitmapSource? single = decoder.Frames.FirstOrDefault();
                    if (single != null)
                    {
                        single.Freeze();
                        ImgFront.Visibility = Visibility.Visible;
                        RunTransitionOnFrontLayer(ImgFront, FrontScale, FrontTranslate, single, outgoingSnapshot, useTransition, direction);
                    }
                    return;
                }

                int canvasWidth = decoder.Frames[0].PixelWidth;
                int canvasHeight = decoder.Frames[0].PixelHeight;

                if (decoder.Metadata is BitmapMetadata rootMeta)
                {
                    if (rootMeta.ContainsQuery("/logscrdesc/Width") && rootMeta.GetQuery("/logscrdesc/Width") is ushort lw && lw > 0)
                        canvasWidth = lw;
                    if (rootMeta.ContainsQuery("/logscrdesc/Height") && rootMeta.GetQuery("/logscrdesc/Height") is ushort lh && lh > 0)
                        canvasHeight = lh;
                }

                _currentGifFrames = new List<BitmapSource>(decoder.Frames.Count);
                _currentGifDelays = new List<TimeSpan>(decoder.Frames.Count);

                BitmapSource? previousCanvas = null;

                foreach (BitmapFrame rawFrame in decoder.Frames)
                {
                    int left = 0, top = 0, delayCs = 10, disposal = 0;

                    if (rawFrame.Metadata is BitmapMetadata meta)
                    {
                        if (meta.ContainsQuery("/imgdesc/Left") && meta.GetQuery("/imgdesc/Left") is ushort l) left = l;
                        if (meta.ContainsQuery("/imgdesc/Top") && meta.GetQuery("/imgdesc/Top") is ushort t) top = t;
                        if (meta.ContainsQuery("/grctlext/Delay") && meta.GetQuery("/grctlext/Delay") is ushort d) delayCs = d;
                        if (meta.ContainsQuery("/grctlext/Disposal") && meta.GetQuery("/grctlext/Disposal") is byte disp) disposal = disp;
                    }

                    int delayMs = delayCs < 2 ? 100 : delayCs * 10;
                    _currentGifDelays.Add(TimeSpan.FromMilliseconds(delayMs));

                    var visual = new DrawingVisual();
                    using (DrawingContext dc = visual.RenderOpen())
                    {
                        if (previousCanvas != null)
                        {
                            dc.DrawImage(previousCanvas, new Rect(0, 0, canvasWidth, canvasHeight));
                        }
                        dc.DrawImage(rawFrame, new Rect(left, top, rawFrame.PixelWidth, rawFrame.PixelHeight));
                    }

                    var composited = new RenderTargetBitmap(canvasWidth, canvasHeight, 96, 96, PixelFormats.Pbgra32);
                    composited.Render(visual);
                    composited.Freeze();

                    _currentGifFrames.Add(composited);
                    previousCanvas = (disposal == 2) ? null : composited;
                }

                _currentGifFrameIndex = 0;
                ImgFront.Visibility = Visibility.Visible;
                RunTransitionOnFrontLayer(ImgFront, FrontScale, FrontTranslate, _currentGifFrames[0], outgoingSnapshot, useTransition, direction);

                if (ChkKenBurns?.IsChecked == true)
                {
                    StartKenBurnsAnimation(FrontScale, FrontTranslate, _slideshowTimer.Interval.TotalSeconds);
                }

                _gifFrameTimer.Interval = _currentGifDelays[0];
                _gifFrameTimer.Start();
            }
            catch
            {
                BitmapSource? fallback = DecodeBitmapSafe(filePath);
                if (fallback != null)
                {
                    ImgFront.Visibility = Visibility.Visible;
                    RunTransitionOnFrontLayer(ImgFront, FrontScale, FrontTranslate, fallback, outgoingSnapshot, useTransition, direction);
                }
            }
        }

        private void GifFrameTimer_Tick(object? sender, EventArgs e)
        {
            if (_currentGifFrames.Count <= 1)
            {
                _gifFrameTimer.Stop();
                return;
            }

            _currentGifFrameIndex = (_currentGifFrameIndex + 1) % _currentGifFrames.Count;
            ImgFront.Source = _currentGifFrames[_currentGifFrameIndex];
            _gifFrameTimer.Interval = _currentGifDelays[_currentGifFrameIndex];
        }

        // ====================================================================
        // 5. LIBVLC VIDEO ENGINE (200% AUDIO OVERDRIVE + SUBTITLES + SNAPSHOT)
        // ====================================================================

        private uint OnVlcVideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
        {
            byte[] rv32 = { (byte)'R', (byte)'V', (byte)'3', (byte)'2' };
            Marshal.Copy(rv32, 0, chroma, 4);

            uint w = width == 0 ? 1920 : width;
            uint h = height == 0 ? 1080 : height;
            uint pitch = w * 4;

            pitches = pitch;
            lines = h;

            uint requiredSize = pitch * h;

            lock (_videoBufferLock)
            {
                if (requiredSize > _allocatedBufferSize || _videoFrameBuffer == IntPtr.Zero)
                {
                    if (_videoFrameBuffer != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_videoFrameBuffer);
                    }
                    _videoFrameBuffer = Marshal.AllocHGlobal((int)requiredSize);
                    _allocatedBufferSize = requiredSize;
                }

                _vlcVideoWidth = w;
                _vlcVideoHeight = h;
                _vlcVideoPitch = pitch;
            }

            Dispatcher.InvokeAsync(() =>
            {
                if (_vlcVideoWidth > 0 && _vlcVideoHeight > 0)
                {
                    _videoWriteableBitmap = new WriteableBitmap(
                        (int)_vlcVideoWidth,
                        (int)_vlcVideoHeight,
                        96, 96,
                        PixelFormats.Bgr32,
                        null);
                    VideoSurface.Source = _videoWriteableBitmap;
                    UpdateStatusBar();
                }
            }, DispatcherPriority.Send);

            return 1;
        }

        private IntPtr OnVlcVideoLock(IntPtr opaque, IntPtr planes)
        {
            Marshal.WriteIntPtr(planes, _videoFrameBuffer);
            return _videoFrameBuffer;
        }

        private void OnVlcVideoDisplay(IntPtr opaque, IntPtr picture)
        {
            if (!_isVideoActive) return;
            if (Interlocked.CompareExchange(ref _isFrameRenderPending, 1, 0) != 0) return;

            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    lock (_videoBufferLock)
                    {
                        if (_isVideoActive &&
                            _videoWriteableBitmap != null &&
                            _videoFrameBuffer != IntPtr.Zero &&
                            _videoWriteableBitmap.PixelWidth == (int)_vlcVideoWidth &&
                            _videoWriteableBitmap.PixelHeight == (int)_vlcVideoHeight)
                        {
                            int byteCount = (int)(_vlcVideoPitch * _vlcVideoHeight);
                            _videoWriteableBitmap.WritePixels(
                                new Int32Rect(0, 0, (int)_vlcVideoWidth, (int)_vlcVideoHeight),
                                _videoFrameBuffer,
                                byteCount,
                                (int)_vlcVideoPitch);
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _isFrameRenderPending, 0);
                }
            }, DispatcherPriority.Render);
        }

        private async void LoadAndPlayVideo(string filePath, BitmapSource? outgoingSnapshot, bool useTransition, int direction)
        {
            _isVideoActive = true;
            _isVideoPaused = false;

            ImgFront.Visibility = Visibility.Collapsed;
            ImgFront.Source = null;

            _videoWriteableBitmap = null;
            BitmapSource? initialThumb = ShellThumbnailProvider.ExtractThumbnail(filePath, 1280, 720);
            VideoSurface.Source = initialThumb;

            VideoSurface.Visibility = Visibility.Visible;
            BtnVideoPlayPause.Content = "⏸ Video";
            TxtAudioBadge.Text = "🔊 Audio:";
            TxtSubBadge.Text = "💬 Sub:";
            VideoControlBar.Visibility = Visibility.Visible;

            RunTransitionOnFrontLayer(VideoSurface, VideoScale, VideoTranslate, null, outgoingSnapshot, useTransition, direction);

            if (_libVlc == null || _vlcPlayer == null)
            {
                TxtFileInfo.Text = $"⏳ Loading video engine for {Path.GetFileName(filePath)}...";
                await EnsureLibVlcInitializedAsync();
            }

            // Ensure the user hasn't skipped away to another slide while LibVLC was finishing initialization
            if (!_isVideoActive || _libVlc == null || _vlcPlayer == null) return;
            if (_currentIndex < 0 || _currentIndex >= _playbackQueue.Count ||
                !string.Equals(_playbackQueue[_currentIndex].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _currentVlcMedia?.Dispose();
            _currentVlcMedia = new Media(_libVlc, filePath, FromType.FromPath);

            _vlcPlayer.Mute = _isMuted;
            _vlcPlayer.Volume = _isMuted ? 0 : (int)Math.Round(VolumeSlider.Value);
            _vlcPlayer.Play(_currentVlcMedia);

            _videoProgressTimer.Start();
            UpdateStatusBar();
        }

        private void VlcPlayer_Playing(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                if (!_isVideoActive || _vlcPlayer == null) return;

                await Task.Delay(200);
                if (!_isVideoActive || _vlcPlayer == null) return;

                _vlcPlayer.Mute = _isMuted;
                _vlcPlayer.Volume = _isMuted ? 0 : (int)Math.Round(VolumeSlider.Value);

                PopulateAudioTrackSelector();
                PopulateSubtitleTrackSelector();

                long lengthMs = _vlcPlayer.Length;
                if (lengthMs > 0)
                {
                    double totalSec = lengthMs / 1000.0;
                    VideoSeekSlider.Maximum = Math.Max(totalSec, 1.0);

                    if (ChkKenBurns?.IsChecked == true)
                    {
                        StartKenBurnsAnimation(VideoScale, VideoTranslate, totalSec);
                    }
                }
                UpdateStatusBar();
            });
        }

        private void PopulateAudioTrackSelector()
        {
            if (_vlcPlayer == null) return;

            _suppressAudioTrackSelection = true;
            CmbAudioTracks.Items.Clear();

            var descriptions = _vlcPlayer.AudioTrackDescription;
            int currentTrackId = _vlcPlayer.AudioTrack;
            int selectedIdx = 0;

            if (descriptions != null && descriptions.Length > 0)
            {
                foreach (var desc in descriptions)
                {
                    if (desc.Id == -1 && descriptions.Length > 1) continue;

                    int idx = CmbAudioTracks.Items.Add(new TrackOption
                    {
                        Id = desc.Id,
                        Name = string.IsNullOrWhiteSpace(desc.Name) ? $"Track {desc.Id}" : desc.Name
                    });

                    if (desc.Id == currentTrackId)
                    {
                        selectedIdx = idx;
                    }
                }
            }

            if (CmbAudioTracks.Items.Count > 0)
            {
                CmbAudioTracks.SelectedIndex = selectedIdx;
                CmbAudioTracks.IsEnabled = CmbAudioTracks.Items.Count > 1;
                TxtAudioBadge.Text = $"🔊 Audio ({CmbAudioTracks.Items.Count}):";
                TxtAudioBadge.Foreground = new SolidColorBrush(Color.FromRgb(163, 190, 140));
            }
            else
            {
                CmbAudioTracks.Items.Add(new TrackOption { Id = -1, Name = "No Audio" });
                CmbAudioTracks.SelectedIndex = 0;
                CmbAudioTracks.IsEnabled = false;
                TxtAudioBadge.Text = "🔇 Audio:";
                TxtAudioBadge.Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 170));
            }

            _suppressAudioTrackSelection = false;
        }

        private void CmbAudioTracks_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAudioTrackSelection || !_isVideoActive || _vlcPlayer == null) return;
            if (CmbAudioTracks.SelectedItem is TrackOption selectedTrack)
            {
                _vlcPlayer.SetAudioTrack(selectedTrack.Id);
            }
        }

        // ====================================================================
        // SUBTITLE TRACK SELECTOR, EXTERNAL .SRT LOADER & SMPLAYER FINDER
        // ====================================================================

        private void PopulateSubtitleTrackSelector()
        {
            if (_vlcPlayer == null) return;

            _suppressSubtitleTrackSelection = true;
            CmbSubtitleTracks.Items.Clear();

            var spuDescriptions = _vlcPlayer.SpuDescription;
            int currentSpuId = _vlcPlayer.Spu;
            int selectedIdx = 0;

            if (spuDescriptions != null && spuDescriptions.Length > 0)
            {
                foreach (var desc in spuDescriptions)
                {
                    string displayName = desc.Id == -1
                        ? "Disabled"
                        : (string.IsNullOrWhiteSpace(desc.Name) ? $"Subtitle {desc.Id}" : desc.Name);

                    int idx = CmbSubtitleTracks.Items.Add(new TrackOption
                    {
                        Id = desc.Id,
                        Name = displayName
                    });

                    if (desc.Id == currentSpuId)
                    {
                        selectedIdx = idx;
                    }
                }
            }

            if (CmbSubtitleTracks.Items.Count == 0)
            {
                CmbSubtitleTracks.Items.Add(new TrackOption { Id = -1, Name = "None" });
                CmbSubtitleTracks.SelectedIndex = 0;
                CmbSubtitleTracks.IsEnabled = false;
                TxtSubBadge.Text = "💬 Sub:";
                TxtSubBadge.Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 170));
            }
            else
            {
                CmbSubtitleTracks.SelectedIndex = selectedIdx;
                CmbSubtitleTracks.IsEnabled = CmbSubtitleTracks.Items.Count > 1;
                int realSubCount = Math.Max(0, CmbSubtitleTracks.Items.Count - 1);
                TxtSubBadge.Text = $"💬 Sub ({realSubCount}):";
                TxtSubBadge.Foreground = realSubCount > 0
                    ? new SolidColorBrush(Color.FromRgb(136, 192, 208))
                    : new SolidColorBrush(Color.FromRgb(152, 152, 170));
            }

            _suppressSubtitleTrackSelection = false;
        }

        private void CmbSubtitleTracks_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSubtitleTrackSelection || !_isVideoActive || _vlcPlayer == null) return;
            if (CmbSubtitleTracks.SelectedItem is TrackOption selectedSub)
            {
                _vlcPlayer.SetSpu(selectedSub.Id);
            }
        }

        private void CycleSubtitleTrack()
        {
            if (!_isVideoActive || CmbSubtitleTracks.Items.Count <= 1) return;
            CmbSubtitleTracks.SelectedIndex = (CmbSubtitleTracks.SelectedIndex + 1) % CmbSubtitleTracks.Items.Count;

            if (CmbSubtitleTracks.SelectedItem is TrackOption selectedSub)
            {
                TxtStatusRight.Text = $"💬 Subtitle: {selectedSub.Name}";
            }
        }

        private async void BtnLoadExternalSub_Click(object sender, RoutedEventArgs e)
        {
            if (!_isVideoActive || _vlcPlayer == null) return;

            var dialog = new OpenFileDialog
            {
                Title = "Select External Subtitle File (.SRT, .ASS, .SSA, .VTT, .SUB)",
                Filter = "Subtitle Files (*.srt;*.ass;*.ssa;*.vtt;*.sub)|*.srt;*.ass;*.ssa;*.vtt;*.sub|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                await AttachSubtitleFileToCurrentVideoAsync(dialog.FileName);
            }
        }

        public async Task AttachSubtitleFileToCurrentVideoAsync(string subtitleFilePath)
        {
            if (!_isVideoActive || _vlcPlayer == null || !File.Exists(subtitleFilePath)) return;

            try
            {
                string subUri = new Uri(subtitleFilePath, UriKind.Absolute).AbsoluteUri;
                _vlcPlayer.AddSlave(MediaSlaveType.Subtitle, subUri, select: true);

                await Task.Delay(250);
                PopulateSubtitleTrackSelector();
                TxtStatusRight.Text = $"💬 Loaded Subtitle: {Path.GetFileName(subtitleFilePath)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not load subtitle file:\n{ex.Message}", "Subtitle Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnFindSubtitles_Click(object sender, RoutedEventArgs e) => OpenFindSubtitlesDialog();

        private void OpenFindSubtitlesDialog()
        {
            if (!_isVideoActive || _currentIndex < 0 || _currentIndex >= _playbackQueue.Count)
            {
                MessageBox.Show("Play or select a video file first to look up subtitles.", "Find Subtitles", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string videoPath = _playbackQueue[_currentIndex].FilePath;
            var finderDialog = new FindSubtitlesDialog(this, videoPath)
            {
                Owner = this
            };
            finderDialog.ShowDialog();
            ReclaimWindowKeyboardFocus();
        }

        // ====================================================================
        // VIDEO FRAME CAPTURE / SNAPSHOT (C Key = Native Res, Shift+C = Zoomed View)
        // ====================================================================

        private void BtnSnapshot_Click(object sender, RoutedEventArgs e)
        {
            bool captureZoomedViewport = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            CaptureMediaSnapshot(captureZoomedViewport);
        }

        private void CaptureMediaSnapshot(bool captureZoomedViewport)
        {
            if (_currentIndex < 0 || _currentIndex >= _playbackQueue.Count) return;

            BitmapSource? capturedBitmap = null;

            if (captureZoomedViewport && _currentZoom > 1.01)
            {
                int vw = Math.Max((int)ViewportContainer.ActualWidth, 1);
                int vh = Math.Max((int)ViewportContainer.ActualHeight, 1);
                var rtb = new RenderTargetBitmap(vw, vh, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(StageContainer);
                rtb.Freeze();
                capturedBitmap = rtb;
            }
            else if (_isVideoActive && _videoWriteableBitmap != null)
            {
                lock (_videoBufferLock)
                {
                    BitmapSource clone = _videoWriteableBitmap.Clone();
                    clone.Freeze();
                    capturedBitmap = clone;
                }
            }
            else if (ImgFront.Source is BitmapSource imgBmp)
            {
                capturedBitmap = imgBmp;
            }

            if (capturedBitmap == null) return;

            try
            {
                SlideItem currentItem = _playbackQueue[_currentIndex];
                string baseDir = Path.GetDirectoryName(currentItem.FilePath)
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                string snapshotDir = Path.Combine(baseDir, "Snapshots");
                Directory.CreateDirectory(snapshotDir);

                string safeBaseName = Path.GetFileNameWithoutExtension(currentItem.FilePath);
                string timeTag;

                if (_isVideoActive && _vlcPlayer != null && _vlcPlayer.Time >= 0)
                {
                    TimeSpan ts = TimeSpan.FromMilliseconds(_vlcPlayer.Time);
                    timeTag = ts.TotalHours >= 1
                        ? $"{ts:hh\\hmm\\mss\\sfff\\m\\s}"
                        : $"{ts:mm\\mss\\sfff\\m\\s}";
                }
                else if (_currentGifFrames.Count > 1)
                {
                    timeTag = $"Frame{_currentGifFrameIndex + 1:D3}";
                }
                else
                {
                    timeTag = DateTime.Now.ToString("HHmmss_fff");
                }

                string viewSuffix = (captureZoomedViewport && _currentZoom > 1.01) ? "_Zoomed" : "";
                string fileName = $"{safeBaseName}_{timeTag}{viewSuffix}.png";
                string fullSavePath = Path.Combine(snapshotDir, fileName);

                using (var fs = new FileStream(fullSavePath, FileMode.Create, FileAccess.Write))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(capturedBitmap));
                    encoder.Save(fs);
                }

                Clipboard.SetImage(capturedBitmap);

                TriggerShutterFlashEffect();
                TxtStatusRight.Text = $"📷 Saved & Copied: Snapshots\\{fileName} ({capturedBitmap.PixelWidth}×{capturedBitmap.PixelHeight})";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Snapshot error:\n{ex.Message}", "Snapshot Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void TriggerShutterFlashEffect()
        {
            SnapshotFlashOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            SnapshotFlashOverlay.Opacity = 0.65;
            var fadeOut = new DoubleAnimation(0.65, 0.0, new Duration(TimeSpan.FromMilliseconds(260)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            SnapshotFlashOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }

        // ====================================================================
        // VIDEO PLAYBACK EVENTS & VLC 200% VOLUME OVERDRIVE CONTROLS
        // ====================================================================

        private void VlcPlayer_EndReached(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isVideoActive || _vlcPlayer == null) return;

                if (ChkWaitVideoFinish.IsChecked == true)
                {
                    if (_isPlaying)
                    {
                        _slideshowTimer.Stop();
                        _slideshowTimer.Start();
                    }
                    StepImage(1, isAutoTick: false);
                }
                else if (_currentVlcMedia != null)
                {
                    var player = _vlcPlayer;
                    var media = _currentVlcMedia;
                    Task.Run(() =>
                    {
                        try
                        {
                            player.Stop();
                            player.Play(media);
                        }
                        catch { }
                    });
                }
            }));
        }

        private void VlcPlayer_EncounteredError(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TxtFileInfo.Text = "LibVLC encountered a stream error while decoding this file.";
            }));
        }

        private void VideoProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isVideoActive || _isDraggingVideoSlider || _vlcPlayer == null) return;

            long lengthMs = _vlcPlayer.Length;
            long currentMs = _vlcPlayer.Time;
            if (lengthMs <= 0 || currentMs < 0) return;

            TimeSpan pos = TimeSpan.FromMilliseconds(currentMs);
            TimeSpan total = TimeSpan.FromMilliseconds(lengthMs);

            VideoSeekSlider.Maximum = Math.Max(total.TotalSeconds, 1.0);
            VideoSeekSlider.Value = pos.TotalSeconds;
            TxtVideoTime.Text = $"{FormatTime(pos)} / {FormatTime(total)}";
        }

        private static string FormatTime(TimeSpan ts) =>
            ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");

        private void BtnVideoPlayPause_Click(object sender, RoutedEventArgs e) => ToggleVideoPlayPause();

        private void ToggleVideoPlayPause()
        {
            if (!_isVideoActive || _vlcPlayer == null) return;

            _isVideoPaused = !_isVideoPaused;
            _vlcPlayer.SetPause(_isVideoPaused);
            BtnVideoPlayPause.Content = _isVideoPaused ? "▶ Video" : "⏸ Video";
        }

        private void BtnRewind30_Click(object sender, RoutedEventArgs e) => SeekVideoRelative(TimeSpan.FromSeconds(-30));
        private void BtnFastForward30_Click(object sender, RoutedEventArgs e) => SeekVideoRelative(TimeSpan.FromSeconds(30));

        private void BtnStepPrevFrame_Click(object sender, RoutedEventArgs e) => StepMediaFrame(-1);
        private void BtnStepNextFrame_Click(object sender, RoutedEventArgs e) => StepMediaFrame(1);

        private void SeekVideoRelative(TimeSpan offset)
        {
            if (!_isVideoActive || _vlcPlayer == null) return;

            long lengthMs = _vlcPlayer.Length;
            if (lengthMs <= 0) return;

            long targetMs = Math.Clamp(_vlcPlayer.Time + (long)offset.TotalMilliseconds, 0, lengthMs);
            _vlcPlayer.Time = targetMs;

            TimeSpan target = TimeSpan.FromMilliseconds(targetMs);
            TimeSpan total = TimeSpan.FromMilliseconds(lengthMs);
            VideoSeekSlider.Value = target.TotalSeconds;
            TxtVideoTime.Text = $"{FormatTime(target)} / {FormatTime(total)}";
        }

        private void StepMediaFrame(int frameDelta)
        {
            if (_currentGifFrames.Count > 1)
            {
                _gifFrameTimer.Stop();
                _currentGifFrameIndex = (_currentGifFrameIndex + frameDelta + _currentGifFrames.Count) % _currentGifFrames.Count;
                ImgFront.Source = _currentGifFrames[_currentGifFrameIndex];
                UpdateStatusBar();
                return;
            }

            if (!_isVideoActive || _vlcPlayer == null) return;

            if (!_isVideoPaused)
            {
                _isVideoPaused = true;
                BtnVideoPlayPause.Content = "▶ Video";
            }

            if (frameDelta > 0)
            {
                _vlcPlayer.NextFrame();
            }
            else
            {
                _vlcPlayer.SetPause(true);
                float fps = _vlcPlayer.Fps;
                double frameMs = (fps > 1.0f && fps < 240.0f) ? (1000.0 / fps) : 41.67;
                SeekVideoRelative(TimeSpan.FromMilliseconds(-frameMs));
            }
        }

        private void VideoSeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingVideoSlider = true;
        }

        private void VideoSeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isVideoActive && _vlcPlayer != null)
            {
                _vlcPlayer.Time = (long)(VideoSeekSlider.Value * 1000.0);
            }
            _isDraggingVideoSlider = false;
        }

        private void VideoSeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDraggingVideoSlider && _isVideoActive && _vlcPlayer != null)
            {
                long targetMs = (long)(VideoSeekSlider.Value * 1000.0);
                _vlcPlayer.Time = targetMs;

                long lengthMs = Math.Max(_vlcPlayer.Length, 1);
                TxtVideoTime.Text = $"{FormatTime(TimeSpan.FromMilliseconds(targetMs))} / {FormatTime(TimeSpan.FromMilliseconds(lengthMs))}";
            }
        }

        private void BtnMute_Click(object sender, RoutedEventArgs e) => ToggleMute();

        private void ToggleMute()
        {
            _isMuted = !_isMuted;
            UpdateVolumeBadgeVisual();
            if (_vlcPlayer != null)
            {
                _vlcPlayer.Mute = _isMuted;
                _vlcPlayer.Volume = _isMuted ? 0 : (int)Math.Round(VolumeSlider.Value);
            }
        }

        private void AdjustVolumeByStep(int deltaPercent)
        {
            if (VolumeSlider == null) return;
            double newVol = Math.Clamp(Math.Round(VolumeSlider.Value) + deltaPercent, 0, 200);
            VolumeSlider.Value = newVol;

            string boostLabel = newVol > 100 ? " (Overdrive Boost!)" : "";
            TxtStatusRight.Text = $"🔊 Volume: {newVol:0}%{boostLabel}";
        }

        private void VolumeControl_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int step = e.Delta > 0 ? 5 : -5;
            AdjustVolumeByStep(step);
            e.Handled = true;
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;

            int vol = (int)Math.Round(VolumeSlider.Value);
            if (_isMuted && vol > 0)
            {
                _isMuted = false;
            }

            UpdateVolumeBadgeVisual();

            if (_vlcPlayer != null)
            {
                _vlcPlayer.Mute = _isMuted;
                _vlcPlayer.Volume = _isMuted ? 0 : vol;
            }
        }

        private void UpdateVolumeBadgeVisual()
        {
            if (TxtVolumePercent == null || BtnMute == null || VolumeSlider == null) return;

            int vol = (int)Math.Round(VolumeSlider.Value);
            if (_isMuted || vol == 0)
            {
                BtnMute.Content = "🔇";
                TxtVolumePercent.Text = _isMuted ? "MUTE" : "0%";
                TxtVolumePercent.Foreground = new SolidColorBrush(Color.FromRgb(152, 152, 170));
            }
            else if (vol <= 100)
            {
                BtnMute.Content = "🔊";
                TxtVolumePercent.Text = $"{vol}%";
                TxtVolumePercent.Foreground = new SolidColorBrush(Color.FromRgb(208, 208, 222));
            }
            else if (vol <= 150)
            {
                // 101% - 150% Warm Gold Overdrive
                BtnMute.Content = "🔊+";
                TxtVolumePercent.Text = $"{vol}%";
                TxtVolumePercent.Foreground = new SolidColorBrush(Color.FromRgb(235, 203, 139));
            }
            else
            {
                // 151% - 200% High Overdrive Boost
                BtnMute.Content = "🔥";
                TxtVolumePercent.Text = $"{vol}%";
                TxtVolumePercent.Foreground = new SolidColorBrush(Color.FromRgb(255, 122, 102));
            }
        }

        // ====================================================================
        // 6. GPU TRANSITIONS (CROSS-FADE, SLIDE/PUSH, ZOOM-FADE) & KEN BURNS
        // ====================================================================

        private void RunTransitionOnFrontLayer(
            UIElement frontElement,
            ScaleTransform frontScale,
            TranslateTransform frontTranslate,
            BitmapSource? newImageSource,
            BitmapSource? outgoingSnapshot,
            bool useTransition,
            int direction)
        {
            if (frontElement == ImgFront && newImageSource != null)
            {
                ImgFront.Source = newImageSource;
            }

            int transitionStyle = CmbTransition?.SelectedIndex ?? 0;
            if (transitionStyle == 3)
            {
                transitionStyle = Random.Shared.Next(0, 3);
            }

            if (useTransition && transitionStyle != 4 && outgoingSnapshot != null)
            {
                ImgBack.Source = outgoingSnapshot;
                ImgBack.Opacity = 1.0;

                var duration = new Duration(TimeSpan.FromMilliseconds(480));
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

                switch (transitionStyle)
                {
                    case 0: // Cross-Fade
                        frontElement.Opacity = 0.0;
                        frontElement.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = ease });
                        ImgBack.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, 0.0, duration) { EasingFunction = ease });
                        break;

                    case 1: // Slide / Push
                        double slideDistance = Math.Max(ActualWidth * 0.45, 450) * (direction >= 0 ? 1 : -1);
                        frontElement.Opacity = 1.0;

                        frontTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(slideDistance, 0.0, duration) { EasingFunction = ease });
                        BackTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, -slideDistance, duration) { EasingFunction = ease });
                        frontElement.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.2, 1.0, duration) { EasingFunction = ease });
                        ImgBack.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, 0.0, duration) { EasingFunction = ease });
                        break;

                    case 2: // Zoom-Fade
                        frontElement.Opacity = 0.0;
                        frontElement.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, duration) { EasingFunction = ease });
                        ImgBack.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, 0.0, duration) { EasingFunction = ease });

                        frontScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1.0, duration) { EasingFunction = ease });
                        frontScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1.0, duration) { EasingFunction = ease });
                        BackScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.14, duration) { EasingFunction = ease });
                        BackScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.14, duration) { EasingFunction = ease });
                        break;
                }
            }
            else
            {
                ImgBack.Source = null;
                ImgBack.Opacity = 0.0;
                frontElement.Opacity = 1.0;
            }
        }

        private void ClearLayerTransforms()
        {
            ImgFront.BeginAnimation(UIElement.OpacityProperty, null);
            ImgBack.BeginAnimation(UIElement.OpacityProperty, null);
            VideoSurface.BeginAnimation(UIElement.OpacityProperty, null);

            FrontScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            FrontScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            FrontTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            FrontTranslate.BeginAnimation(TranslateTransform.YProperty, null);

            VideoScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            VideoScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            VideoTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            VideoTranslate.BeginAnimation(TranslateTransform.YProperty, null);

            BackScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            BackScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            BackTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            BackTranslate.BeginAnimation(TranslateTransform.YProperty, null);

            FrontScale.ScaleX = 1.0; FrontScale.ScaleY = 1.0;
            FrontTranslate.X = 0.0; FrontTranslate.Y = 0.0;

            VideoScale.ScaleX = 1.0; VideoScale.ScaleY = 1.0;
            VideoTranslate.X = 0.0; VideoTranslate.Y = 0.0;

            BackScale.ScaleX = 1.0; BackScale.ScaleY = 1.0;
            BackTranslate.X = 0.0; BackTranslate.Y = 0.0;
        }

        private static void StartKenBurnsAnimation(ScaleTransform targetScale, TranslateTransform targetTranslate, double durationSeconds)
        {
            double clampedSec = Math.Clamp(durationSeconds, 3.0, 25.0);
            var kbDuration = new Duration(TimeSpan.FromSeconds(clampedSec + 1.2));

            bool zoomIn = Random.Shared.Next(2) == 0;
            double startScale = zoomIn ? 1.0 : 1.15;
            double endScale = zoomIn ? 1.15 : 1.0;

            double panX = (Random.Shared.NextDouble() - 0.5) * 65.0;
            double panY = (Random.Shared.NextDouble() - 0.5) * 42.0;

            targetScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(startScale, endScale, kbDuration));
            targetScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(startScale, endScale, kbDuration));
            targetTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-panX * 0.5, panX * 0.5, kbDuration));
            targetTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-panY * 0.5, panY * 0.5, kbDuration));
        }

        private void ChkKenBurns_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (ChkKenBurns.IsChecked == true)
            {
                if (_isVideoActive)
                    StartKenBurnsAnimation(VideoScale, VideoTranslate, _slideshowTimer.Interval.TotalSeconds);
                else if (ImgFront.Source != null)
                    StartKenBurnsAnimation(FrontScale, FrontTranslate, _slideshowTimer.Interval.TotalSeconds);
            }
            else
            {
                ClearLayerTransforms();
            }
        }

        private static BitmapSource? DecodeBitmapSafe(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return ShellThumbnailProvider.ExtractThumbnail(path, 1920, 1080);
            }
        }

        // ====================================================================
        // 7. SLIDE-UP THUMBNAIL FILMSTRIP
        // ====================================================================

        private void SyncFilmstripSelection()
        {
            if (_currentIndex < 0 || _currentIndex >= _playbackQueue.Count) return;

            _suppressFilmstripSelectionEvent = true;
            FilmstripList.SelectedIndex = _currentIndex;
            FilmstripList.ScrollIntoView(FilmstripList.SelectedItem);
            _suppressFilmstripSelectionEvent = false;
        }

        private void FilmstripList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressFilmstripSelectionEvent) return;
            if (FilmstripList.SelectedIndex >= 0 && FilmstripList.SelectedIndex != _currentIndex)
            {
                int dir = FilmstripList.SelectedIndex > _currentIndex ? 1 : -1;
                ShowMediaAtIndex(FilmstripList.SelectedIndex, useTransition: true, direction: dir);

                if (_isPlaying)
                {
                    _slideshowTimer.Stop();
                    _slideshowTimer.Start();
                }
            }
        }

        private void FilmstripItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem lbi && lbi.DataContext is SlideItem clickedItem)
            {
                int targetIndex = _playbackQueue.IndexOf(clickedItem);
                if (targetIndex >= 0)
                {
                    if (targetIndex != _currentIndex)
                    {
                        int dir = targetIndex > _currentIndex ? 1 : -1;
                        ShowMediaAtIndex(targetIndex, useTransition: true, direction: dir);

                        if (_isPlaying)
                        {
                            _slideshowTimer.Stop();
                            _slideshowTimer.Start();
                        }
                    }
                    else if (_isVideoActive && _isVideoPaused)
                    {
                        ToggleVideoPlayPause();
                    }
                }

                ReclaimWindowKeyboardFocus();
                e.Handled = true;
            }
        }

        private void FilmstripList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (FindVisualChild<ScrollViewer>(FilmstripList) is ScrollViewer sv)
            {
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset - (e.Delta * 0.8));
                e.Handled = true;
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild) return typedChild;
                T? result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void BtnPinFilmstrip_Click(object sender, RoutedEventArgs e) => TogglePinFilmstrip();

        private void BtnHideFilmstrip_Click(object sender, RoutedEventArgs e)
        {
            _isFilmstripPinned = false;
            BtnPinFilmstrip.Content = "🎞 Strip";
            SetFilmstripVisibility(false);
        }

        private void TogglePinFilmstrip()
        {
            _isFilmstripPinned = !_isFilmstripPinned;
            BtnPinFilmstrip.Content = _isFilmstripPinned ? "📌 Strip" : "🎞 Strip";
            SetFilmstripVisibility(_isFilmstripPinned);
        }

        private void SetFilmstripVisibility(bool visible)
        {
            if (_isFilmstripVisible == visible) return;
            _isFilmstripVisible = visible;

            var duration = new Duration(TimeSpan.FromMilliseconds(220));
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            if (visible)
            {
                FilmstripDrawer.Visibility = Visibility.Visible;
                FilmstripDrawer.IsHitTestVisible = true;
                FilmstripDrawer.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(156.0, duration) { EasingFunction = ease });
                FilmstripDrawer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, duration));
                SyncFilmstripSelection();
            }
            else
            {
                FilmstripDrawer.IsHitTestVisible = false;
                if (Keyboard.FocusedElement is not TextBox)
                {
                    ReclaimWindowKeyboardFocus();
                }

                var collapseAnim = new DoubleAnimation(0.0, duration) { EasingFunction = ease };
                collapseAnim.Completed += (s, e) =>
                {
                    if (!_isFilmstripVisible)
                    {
                        FilmstripDrawer.Visibility = Visibility.Collapsed;
                        if (Keyboard.FocusedElement is not TextBox)
                        {
                            ReclaimWindowKeyboardFocus();
                        }
                    }
                };
                FilmstripDrawer.BeginAnimation(FrameworkElement.HeightProperty, collapseAnim);
                FilmstripDrawer.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, duration));
            }
        }

        // ====================================================================
        // 8. FAVORITES TAGGING & EXPORT
        // ====================================================================

        private void BtnToggleFav_Click(object sender, RoutedEventArgs e) => ToggleCurrentFavorite();

        private void ToggleCurrentFavorite()
        {
            if (_currentIndex < 0 || _currentIndex >= _playbackQueue.Count) return;

            SlideItem item = _playbackQueue[_currentIndex];
            if (_favoriteFiles.Contains(item.FilePath))
            {
                _favoriteFiles.Remove(item.FilePath);
                item.IsFavorite = false;
            }
            else
            {
                _favoriteFiles.Add(item.FilePath);
                item.IsFavorite = true;
            }

            UpdateFavoriteButtonVisual();
            UpdateStatusBar();
        }

        private void UpdateFavoriteButtonVisual()
        {
            if (_currentIndex >= 0 && _currentIndex < _playbackQueue.Count && _playbackQueue[_currentIndex].IsFavorite)
            {
                BtnToggleFav.Content = "★ Fav";
                BtnToggleFav.Foreground = new SolidColorBrush(Color.FromRgb(255, 209, 102));
            }
            else
            {
                BtnToggleFav.Content = "☆ Fav";
                BtnToggleFav.Foreground = new SolidColorBrush(Color.FromRgb(240, 240, 245));
            }
        }

        private void ChkFavoritesOnly_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _masterFiles.Count == 0) return;
            RebuildPlaybackQueue(keepCurrentImage: true);
        }

        private void BtnExportFavorites_Click(object sender, RoutedEventArgs e)
        {
            var existingFavs = _favoriteFiles.Where(File.Exists).ToList();
            if (existingFavs.Count == 0)
            {
                MessageBox.Show("Press 'F' or click '☆ Fav' on images, GIFs, or videos during your slideshow to star them before exporting.",
                    "No Favorites Marked", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = $"Select Destination Folder to Copy {existingFavs.Count} Favorite File(s)"
            };

            if (dialog.ShowDialog() == true)
            {
                int copied = 0;
                foreach (string src in existingFavs)
                {
                    try
                    {
                        string dest = Path.Combine(dialog.FolderName, Path.GetFileName(src));
                        File.Copy(src, dest, overwrite: true);
                        copied++;
                    }
                    catch
                    {
                        // Continue copying remaining files
                    }
                }

                MessageBox.Show($"Copied {copied} favorite file(s) to:\n{dialog.FolderName}",
                    "Favorites Exported", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // ====================================================================
        // 9. RIGHT-CLICK PULL TO ZOOM & MOUSE WHEEL PANNING
        // ====================================================================

        private bool HasActiveMedia => _isVideoActive || ImgFront.Source != null;

        private void Viewport_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!HasActiveMedia) return;

            _isRightZooming = true;
            _zoomAnchorPoint = e.GetPosition(ViewportContainer);
            _lastRightZoomPos = _zoomAnchorPoint;

            ViewportContainer.CaptureMouse();
            Cursor = Cursors.SizeNS;
            e.Handled = true;
        }

        private void Viewport_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isRightZooming) return;

            _isRightZooming = false;
            ViewportContainer.ReleaseMouseCapture();
            Cursor = _isHudVisible ? Cursors.Arrow : Cursors.None;
            e.Handled = true;
        }

        private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ReclaimWindowKeyboardFocus();

            if (e.ClickCount == 2)
            {
                ToggleFullscreen();
                return;
            }

            if (!HasActiveMedia) return;

            _isLeftPanning = true;
            _lastPanMousePos = e.GetPosition(ViewportContainer);
            ViewportContainer.CaptureMouse();
            Cursor = Cursors.ScrollAll;
        }

        private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isLeftPanning) return;

            _isLeftPanning = false;
            ViewportContainer.ReleaseMouseCapture();
            Cursor = _isHudVisible ? Cursors.Arrow : Cursors.None;
        }

        private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                ResetZoomMatrix();
            }
        }

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            Point currentPos = e.GetPosition(ViewportContainer);

            if (_isRightZooming)
            {
                double deltaY = _lastRightZoomPos.Y - currentPos.Y;
                double deltaX = currentPos.X - _lastRightZoomPos.X;
                double combinedPull = deltaY + (deltaX * 0.5);

                if (Math.Abs(combinedPull) > 0.1)
                {
                    double zoomFactor = Math.Pow(1.0075, combinedPull);
                    ApplyZoomAtAnchor(zoomFactor, _zoomAnchorPoint);
                    _lastRightZoomPos = currentPos;
                }
                return;
            }

            if (_isLeftPanning)
            {
                Vector delta = currentPos - _lastPanMousePos;
                Matrix m = StageMatrixTransform.Matrix;
                m.Translate(delta.X, delta.Y);
                StageMatrixTransform.Matrix = m;
                _lastPanMousePos = currentPos;
            }
        }

        private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!HasActiveMedia) return;

            if (_currentZoom <= 1.005) return;

            double panPixels = (e.Delta / 120.0) * 70.0;
            Matrix m = StageMatrixTransform.Matrix;

            bool isShiftDown = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            if (isShiftDown)
            {
                m.Translate(panPixels, 0);
            }
            else
            {
                m.Translate(0, panPixels);
            }

            StageMatrixTransform.Matrix = m;
            e.Handled = true;
        }

        private void ApplyZoomAtAnchor(double factor, Point anchor)
        {
            Matrix m = StageMatrixTransform.Matrix;
            double currentScale = m.M11;
            double targetScale = Math.Clamp(currentScale * factor, 0.10, 40.0);
            double actualFactor = targetScale / currentScale;

            m.ScaleAt(actualFactor, actualFactor, anchor.X, anchor.Y);
            StageMatrixTransform.Matrix = m;

            _currentZoom = targetScale;
            UpdateStatusBar();
        }

        private void ResetZoomMatrix()
        {
            StageMatrixTransform.Matrix = Matrix.Identity;
            _currentZoom = 1.0;
            UpdateStatusBar();
        }

        private void BtnResetZoom_Click(object sender, RoutedEventArgs e) => ResetZoomMatrix();

        // ====================================================================
        // 10. SLIDESHOW CONTROLS, PLAYLISTS & AUTO-HIDE HUD
        // ====================================================================

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => ToggleSlideshow();

        private void ToggleSlideshow()
        {
            if (_playbackQueue.Count == 0) return;

            _isPlaying = !_isPlaying;
            if (_isPlaying)
            {
                _slideshowTimer.Start();
                BtnPlayPause.Content = "⏸ Pause";
            }
            else
            {
                _slideshowTimer.Stop();
                BtnPlayPause.Content = "▶ Play";
            }
            UpdateStatusBar();
        }

        private void BtnPrev_Click(object sender, RoutedEventArgs e) => StepImage(-1);
        private void BtnNext_Click(object sender, RoutedEventArgs e) => StepImage(1);

        private void TxtInterval_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(TxtInterval.Text, out double seconds) && seconds >= 0.5)
            {
                _slideshowTimer.Interval = TimeSpan.FromSeconds(seconds);
            }
        }

        private void BtnSavePlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (_masterFiles.Count == 0)
            {
                MessageBox.Show("Load at least one folder or file before saving a playlist.", "Empty Playlist", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Save Multi-Folder Slideshow Playlist",
                Filter = "SlideForge Playlist (*.sfplay.json)|*.sfplay.json|JSON Files (*.json)|*.json",
                DefaultExt = ".sfplay.json",
                FileName = "MySlideshow.sfplay.json"
            };

            if (dialog.ShowDialog() == true)
            {
                var session = new PlaylistSession
                {
                    SourceFolders = _sourceFolders.ToList(),
                    IncludeSubfolders = MnuSubfolders.IsChecked == true,
                    RandomizeOrder = ChkShuffle.IsChecked == true,
                    SortMode = _currentSortMode.ToString(),
                    IntervalSeconds = _slideshowTimer.Interval.TotalSeconds,
                    TransitionIndex = CmbTransition.SelectedIndex,
                    KenBurnsEnabled = ChkKenBurns.IsChecked == true,
                    FavoriteFiles = _favoriteFiles.ToList(),
                    CustomFileList = _masterFiles
                };

                string json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dialog.FileName, json);
            }
        }

        private void BtnLoadPlaylist_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Load Slideshow Playlist",
                Filter = "SlideForge Playlist (*.sfplay.json;*.json)|*.sfplay.json;*.json|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                LoadPlaylistFromFile(dialog.FileName);
            }
        }

        private void LoadPlaylistFromFile(string playlistPath)
        {
            try
            {
                string json = File.ReadAllText(playlistPath);
                var session = JsonSerializer.Deserialize<PlaylistSession>(json);
                if (session == null) return;

                MnuSubfolders.IsChecked = session.IncludeSubfolders;
                SetShuffleCheckedSilent(session.RandomizeOrder);
                if (Enum.TryParse<MediaSortMode>(session.SortMode, out MediaSortMode savedSort))
                {
                    ApplySortModeSelection(savedSort);
                }

                TxtInterval.Text = session.IntervalSeconds.ToString("0.#");
                CmbTransition.SelectedIndex = Math.Clamp(session.TransitionIndex, 0, 4);
                ChkKenBurns.IsChecked = session.KenBurnsEnabled;

                _favoriteFiles.Clear();
                foreach (string fav in session.FavoriteFiles)
                {
                    _favoriteFiles.Add(fav);
                }

                _sourceFolders.Clear();
                foreach (string folder in session.SourceFolders.Where(Directory.Exists))
                {
                    _sourceFolders.Add(folder);
                }

                if (_sourceFolders.Count > 0)
                {
                    RescanSourceFolders();
                }
                else if (session.CustomFileList.Count > 0)
                {
                    _masterFiles = session.CustomFileList.Where(File.Exists).ToList();
                    RebuildPlaybackQueue(keepCurrentImage: false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not load playlist:\n{ex.Message}", "Playlist Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

        private void ToggleFullscreen()
        {
            _isFullscreen = !_isFullscreen;
            if (_isFullscreen)
            {
                _previousWindowState = WindowState;
                _previousWindowStyle = WindowStyle;
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                BtnFullscreen.Content = "🗗 Exit";
            }
            else
            {
                WindowStyle = _previousWindowStyle;
                WindowState = _previousWindowState;
                BtnFullscreen.Content = "⛶ Full";
            }
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            Point pos = e.GetPosition(this);
            if ((pos - _lastWindowMousePos).Length > 2.0)
            {
                _lastWindowMousePos = pos;
                if (!_isHudVisible) SetHudVisibility(true);

                bool atVeryBottomEdge = pos.Y >= (ActualHeight - 24);
                if (atVeryBottomEdge || _isFilmstripPinned)
                {
                    SetFilmstripVisibility(true);
                }
                else if (!_isFilmstripPinned && _isFilmstripVisible && !FilmstripDrawer.IsMouseOver && SortMediaMenu?.IsOpen != true)
                {
                    SetFilmstripVisibility(false);
                }

                _hudHideTimer.Stop();
                _hudHideTimer.Start();
            }
        }

        private void SetHudVisibility(bool visible)
        {
            _isHudVisible = visible;
            double targetOpacity = visible ? 1.0 : 0.0;
            var anim = new DoubleAnimation(targetOpacity, new Duration(TimeSpan.FromMilliseconds(240)));

            TopHud.BeginAnimation(UIElement.OpacityProperty, anim);
            BottomHud.BeginAnimation(UIElement.OpacityProperty, anim);
            VideoControlBar.BeginAnimation(UIElement.OpacityProperty, anim);

            TopHud.IsHitTestVisible = visible;
            VideoControlBar.IsHitTestVisible = visible;

            if (!visible && Keyboard.FocusedElement is not TextBox)
            {
                ReclaimWindowKeyboardFocus();
            }

            if (!_isRightZooming && !_isLeftPanning)
            {
                Cursor = visible ? Cursors.Arrow : Cursors.None;
            }
        }

        // ====================================================================
        // 11. KEYBOARD SHORTCUT ROUTING + WIN32 THREAD PREPROCESS FALLBACK
        // ====================================================================

        private void ComponentDispatcher_ThreadPreprocessMessage(ref MSG msg, ref bool handled)
        {
            if (handled) return;
            if (msg.message != WM_KEYDOWN && msg.message != WM_SYSKEYDOWN) return;
            if (!IsActive) return;

            if (FileFolderMenu?.IsOpen == true ||
                PlaylistMenu?.IsOpen == true ||
                SortMediaMenu?.IsOpen == true ||
                CmbTransition?.IsDropDownOpen == true ||
                CmbAudioTracks?.IsDropDownOpen == true ||
                CmbSubtitleTracks?.IsDropDownOpen == true)
            {
                return;
            }

            IntPtr mainHwnd = new WindowInteropHelper(this).Handle;
            if (mainHwnd != IntPtr.Zero && msg.hwnd != mainHwnd)
            {
                return;
            }

            if (Keyboard.FocusedElement is not DependencyObject focusedObj ||
                PresentationSource.FromDependencyObject(focusedObj) == null)
            {
                ReclaimWindowKeyboardFocus();
            }

            Key key = KeyInterop.KeyFromVirtualKey((int)msg.wParam);
            if (TryHandleShortcutKey(key))
            {
                handled = true;
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (TryHandleShortcutKey(key))
            {
                e.Handled = true;
            }
        }

        private bool TryHandleShortcutKey(Key key)
        {
            if (Keyboard.FocusedElement is TextBox)
            {
                if (key == Key.Enter || key == Key.Escape)
                {
                    ReclaimWindowKeyboardFocus();
                    return true;
                }
                return false;
            }

            switch (key)
            {
                case Key.Right:
                case Key.Down:
                    StepImage(1);
                    return true;
                case Key.Left:
                case Key.Up:
                    StepImage(-1);
                    return true;
                case Key.Space:
                    if (_isVideoActive)
                        ToggleVideoPlayPause();
                    else
                        ToggleSlideshow();
                    return true;
                case Key.P:
                    ToggleSlideshow();
                    return true;
                case Key.OemOpenBrackets:
                    SeekVideoRelative(TimeSpan.FromSeconds(-30));
                    return true;
                case Key.OemCloseBrackets:
                    SeekVideoRelative(TimeSpan.FromSeconds(30));
                    return true;
                case Key.OemComma:
                    StepMediaFrame(-1);
                    return true;
                case Key.OemPeriod:
                    StepMediaFrame(1);
                    return true;
                case Key.OemPlus:
                case Key.Add:
                    AdjustVolumeByStep(5);
                    return true;
                case Key.OemMinus:
                case Key.Subtract:
                    AdjustVolumeByStep(-5);
                    return true;
                case Key.C:
                    bool shiftHeld = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                    CaptureMediaSnapshot(captureZoomedViewport: shiftHeld);
                    return true;
                case Key.V:
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                        OpenFindSubtitlesDialog();
                    else
                        CycleSubtitleTrack();
                    return true;
                case Key.M:
                    ToggleMute();
                    return true;
                case Key.F:
                    ToggleCurrentFavorite();
                    return true;
                case Key.T:
                    TogglePinFilmstrip();
                    return true;
                case Key.K:
                    ChkKenBurns.IsChecked = !(ChkKenBurns.IsChecked == true);
                    return true;
                case Key.F11:
                    ToggleFullscreen();
                    return true;
                case Key.Escape:
                    if (_isFullscreen)
                    {
                        ToggleFullscreen();
                        return true;
                    }
                    return false;
                case Key.R:
                    ResetZoomMatrix();
                    return true;
                case Key.S:
                    BtnReshuffle_Click(this, new RoutedEventArgs());
                    return true;
                default:
                    return false;
            }
        }

        private void UpdateStatusBar()
        {
            if (_currentIndex < 0 || _currentIndex >= _playbackQueue.Count) return;

            SlideItem item = _playbackQueue[_currentIndex];
            string folderName = Path.GetFileName(Path.GetDirectoryName(item.FilePath) ?? "");

            string resText = "";
            if (_isVideoActive && _vlcVideoWidth > 0 && _vlcVideoHeight > 0)
            {
                float fps = _vlcPlayer?.Fps ?? 0f;
                string fpsText = fps > 1f ? $" • {fps:0.##} fps" : "";
                resText = $"  [🎬 VLC {_vlcVideoWidth} × {_vlcVideoHeight}{fpsText}]";
            }
            else if (_currentGifFrames.Count > 1)
            {
                resText = $"  [GIF • Frame {_currentGifFrameIndex + 1}/{_currentGifFrames.Count} • {_currentGifFrames[0].PixelWidth} × {_currentGifFrames[0].PixelHeight}]";
            }
            else if (ImgFront.Source is BitmapSource bmp)
            {
                resText = $"  ({bmp.PixelWidth} × {bmp.PixelHeight})";
            }

            string favStar = item.IsFavorite ? " ★" : "";
            string orderBadge = ChkShuffle.IsChecked == true ? " [SHUFFLED]" : $" [Sort: {_currentSortMode}]";
            string playBadge = _isPlaying ? " ▶ SLIDESHOW" : "";
            string filterBadge = _activeFilterTag != "ALL" ? $" [Filter: {_activeFilterTag}]" : "";

            TxtFileInfo.Text = $"[{_currentIndex + 1} / {_playbackQueue.Count}]{favStar}{orderBadge}{filterBadge}{playBadge}   {folderName}\\{item.FileName}{resText}";
            TxtStatusRight.Text = $"Zoom: {_currentZoom * 100:0}%  |  C: Snapshot  |  V / Shift+V: Subtitles  |  +/-: Vol Boost  |  F: Star  |  T: Strip";
        }
    }

    // ========================================================================
    // SMPLAYER-STYLE "FIND SUBTITLES" DIALOG WINDOW
    // (Computes 64-bit OSDB MovieHash + OpenSubtitles REST API + Auto-Downloads Importer)
    // ========================================================================
    public class SubtitleSearchResultItem
    {
        public int FileId { get; set; }
        public string MatchBadge { get; set; } = "";
        public string Language { get; set; } = "";
        public string ReleaseName { get; set; } = "";
        public string FpsText { get; set; } = "";
        public int Downloads { get; set; }
        public double Rating { get; set; }
    }

    public class SubtitleConfigData
    {
        public string OpenSubtitlesApiKey { get; set; } = "";
        public string PreferredLanguage { get; set; } = "en";
    }

    public class FindSubtitlesDialog : Window
    {
        private static readonly HttpClient SharedHttp = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private readonly MainWindow _ownerWindow;
        private readonly string _videoFilePath;
        private readonly string _movieHash;
        private readonly long _movieByteSize;

        private readonly TextBox _txtQuery;
        private readonly ComboBox _cmbLang;
        private readonly CheckBox _chkIncludeHash;
        private readonly TextBox _txtApiKey;
        private readonly ListView _lvResults;
        private readonly TextBlock _txtStatus;
        private readonly Button _btnDownloadSelected;
        private FileSystemWatcher? _downloadsWatcher;

        private static string ConfigFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlideForge", "subtitles_config.json");

        public FindSubtitlesDialog(MainWindow owner, string videoFilePath)
        {
            _ownerWindow = owner;
            _videoFilePath = videoFilePath;
            (_movieHash, _movieByteSize) = OpenSubtitlesHasher.ComputeMovieHash(videoFilePath);

            Title = $"Find Subtitles (SMPlayer OSDB Engine) — {Path.GetFileName(videoFilePath)}";
            Width = 860;
            Height = 560;
            MinWidth = 720;
            MinHeight = 440;
            Background = new SolidColorBrush(Color.FromRgb(18, 18, 24));
            Foreground = new SolidColorBrush(Color.FromRgb(235, 235, 245));
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            SubtitleConfigData config = LoadConfig();

            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: Hash & File Info Banner
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: Search Bar
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: API Key Row
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 3: Results ListView
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 4: Browser Lookup + Auto-Import Bar
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 5: Status Footer

            // ----------------------------------------------------------------
            // ROW 0: SMPLAYER OSDB 64-BIT HASH BANNER
            // ----------------------------------------------------------------
            var hashBanner = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(28, 28, 38)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(52, 52, 70)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var hashStack = new DockPanel();
            var btnCopyHash = CreateStyledButton("📋 Copy Hash", 95);
            btnCopyHash.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(_movieHash))
                {
                    Clipboard.SetText(_movieHash);
                    _txtStatus.Text = $"✔ Copied 64-bit OSDB MovieHash '{_movieHash}' to clipboard.";
                }
            };
            DockPanel.SetDock(btnCopyHash, Dock.Right);
            hashStack.Children.Add(btnCopyHash);

            var txtHashInfo = new TextBlock
            {
                Text = $"🎬 File: {Path.GetFileName(videoFilePath)}   •   Size: {_movieByteSize:N0} bytes   •   OSDB 64-bit Hash: {(string.IsNullOrEmpty(_movieHash) ? "N/A (<64KB)" : _movieHash)}",
                Foreground = new SolidColorBrush(Color.FromRgb(136, 192, 208)),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            hashStack.Children.Add(txtHashInfo);
            hashBanner.Child = hashStack;
            Grid.SetRow(hashBanner, 0);
            root.Children.Add(hashBanner);

            // ----------------------------------------------------------------
            // ROW 1: SEARCH QUERY + LANGUAGE + HASH CHECKBOX + SEARCH BUTTON
            // ----------------------------------------------------------------
            var searchGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            searchGrid.Children.Add(new TextBlock
            {
                Text = "Title / Query:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            _txtQuery = new TextBox
            {
                Text = OpenSubtitlesHasher.CleanMovieTitleFromFileName(videoFilePath),
                Background = new SolidColorBrush(Color.FromRgb(28, 28, 38)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(64, 64, 84)),
                Padding = new Thickness(7, 4, 7, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _txtQuery.KeyDown += async (s, e) =>
            {
                if (e.Key == Key.Enter) await ExecuteApiSearchAsync();
            };
            Grid.SetColumn(_txtQuery, 1);
            searchGrid.Children.Add(_txtQuery);

            _cmbLang = new ComboBox
            {
                Width = 120,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            string[] langs = { "en (English)", "es (Spanish)", "fr (French)", "de (German)", "it (Italian)", "pt-br (Portuguese)", "ja (Japanese)", "ko (Korean)", "zh-cn (Chinese)", "ru (Russian)", "all (All Languages)" };
            int selLangIdx = 0;
            for (int i = 0; i < langs.Length; i++)
            {
                _cmbLang.Items.Add(langs[i]);
                if (langs[i].StartsWith(config.PreferredLanguage + " ", StringComparison.OrdinalIgnoreCase))
                    selLangIdx = i;
            }
            _cmbLang.SelectedIndex = selLangIdx;
            Grid.SetColumn(_cmbLang, 2);
            searchGrid.Children.Add(_cmbLang);

            _chkIncludeHash = new CheckBox
            {
                Content = "Include OSDB File Hash",
                IsChecked = !string.IsNullOrEmpty(_movieHash),
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 232)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 10, 0)
            };
            Grid.SetColumn(_chkIncludeHash, 3);
            searchGrid.Children.Add(_chkIncludeHash);

            var btnSearchApi = CreateStyledButton("🔍 Search OpenSubtitles API", 190);
            btnSearchApi.Background = new SolidColorBrush(Color.FromRgb(59, 91, 132));
            btnSearchApi.Click += async (s, e) => await ExecuteApiSearchAsync();
            Grid.SetColumn(btnSearchApi, 4);
            searchGrid.Children.Add(btnSearchApi);

            Grid.SetRow(searchGrid, 1);
            root.Children.Add(searchGrid);

            // ----------------------------------------------------------------
            // ROW 2: OPENSUBTITLES REST API KEY BAR (Saved locally)
            // ----------------------------------------------------------------
            var apiGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            apiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            apiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            apiGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            apiGrid.Children.Add(new TextBlock
            {
                Text = "OpenSubtitles.com API Key (Optional for in-app table):",
                Foreground = new SolidColorBrush(Color.FromRgb(165, 165, 185)),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            _txtApiKey = new TextBox
            {
                Text = config.OpenSubtitlesApiKey,
                Background = new SolidColorBrush(Color.FromRgb(24, 24, 32)),
                Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 225)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(54, 54, 70)),
                Padding = new Thickness(6, 3, 6, 3),
                FontSize = 11.5
            };
            _txtApiKey.TextChanged += (s, e) => SaveCurrentConfig();
            Grid.SetColumn(_txtApiKey, 1);
            apiGrid.Children.Add(_txtApiKey);

            var btnGetKey = CreateStyledButton("🔑 Get Free API Key", 135);
            btnGetKey.Margin = new Thickness(8, 0, 0, 0);
            btnGetKey.Click += (s, e) => OpenUrlInBrowser("https://www.opensubtitles.com/en/consumers");
            Grid.SetColumn(btnGetKey, 2);
            apiGrid.Children.Add(btnGetKey);

            Grid.SetRow(apiGrid, 2);
            root.Children.Add(apiGrid);

            // ----------------------------------------------------------------
            // ROW 3: SUBTITLE RESULTS LISTVIEW TABLE
            // ----------------------------------------------------------------
            _lvResults = new ListView
            {
                Background = new SolidColorBrush(Color.FromRgb(22, 22, 30)),
                Foreground = new SolidColorBrush(Color.FromRgb(235, 235, 245)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(52, 52, 70)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var gridView = new GridView();
            gridView.Columns.Add(new GridViewColumn { Header = "Match", Width = 85, DisplayMemberBinding = new System.Windows.Data.Binding("MatchBadge") });
            gridView.Columns.Add(new GridViewColumn { Header = "Lang", Width = 55, DisplayMemberBinding = new System.Windows.Data.Binding("Language") });
            gridView.Columns.Add(new GridViewColumn { Header = "Subtitle Release / File Name", Width = 445, DisplayMemberBinding = new System.Windows.Data.Binding("ReleaseName") });
            gridView.Columns.Add(new GridViewColumn { Header = "FPS", Width = 65, DisplayMemberBinding = new System.Windows.Data.Binding("FpsText") });
            gridView.Columns.Add(new GridViewColumn { Header = "Downloads", Width = 85, DisplayMemberBinding = new System.Windows.Data.Binding("Downloads") });
            gridView.Columns.Add(new GridViewColumn { Header = "Rating", Width = 65, DisplayMemberBinding = new System.Windows.Data.Binding("Rating") });
            _lvResults.View = gridView;

            _lvResults.MouseDoubleClick += async (s, e) => await DownloadAndAttachSelectedApiResultAsync();
            Grid.SetRow(_lvResults, 3);
            root.Children.Add(_lvResults);

            // ----------------------------------------------------------------
            // ROW 4: ZERO-API-KEY BROWSER LOOKUP & 1-CLICK DOWNLOADS IMPORTER
            // ----------------------------------------------------------------
            var actionBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };

            _btnDownloadSelected = CreateStyledButton("⬇ Download & Load Selected", 195);
            _btnDownloadSelected.Background = new SolidColorBrush(Color.FromRgb(46, 104, 78));
            _btnDownloadSelected.Margin = new Thickness(0, 0, 8, 4);
            _btnDownloadSelected.Click += async (s, e) => await DownloadAndAttachSelectedApiResultAsync();
            actionBar.Children.Add(_btnDownloadSelected);

            var btnWebHash = CreateStyledButton("🌐 Lookup Exact Hash (OpenSubtitles.org)", 245);
            btnWebHash.Margin = new Thickness(0, 0, 8, 4);
            btnWebHash.Click += (s, e) =>
            {
                string langCode = GetSelectedLangCode();
                string lang3 = MapTwoLetterToThreeLetterLang(langCode);
                string url = !string.IsNullOrEmpty(_movieHash)
                    ? $"https://www.opensubtitles.org/en/search/sublanguageid-{lang3}/moviehash-{_movieHash}"
                    : $"https://www.opensubtitles.org/en/search2/sublanguageid-{lang3}/moviename-{Uri.EscapeDataString(_txtQuery.Text)}";
                OpenUrlInBrowser(url);
                _txtStatus.Text = "🌐 Opened exact OSDB Hash search in browser. Download any .ZIP/.SRT and click '📥 Import Latest from Downloads'!";
            };
            actionBar.Children.Add(btnWebHash);

            var btnSubdl = CreateStyledButton("🌐 Search Title on Subdl.com", 185);
            btnSubdl.Margin = new Thickness(0, 0, 8, 4);
            btnSubdl.Click += (s, e) =>
            {
                string url = $"https://subdl.com/search?query={Uri.EscapeDataString(_txtQuery.Text)}";
                OpenUrlInBrowser(url);
                _txtStatus.Text = "🌐 Opened Subdl search in browser. Download any .ZIP/.SRT and click '📥 Import Latest from Downloads'!";
            };
            actionBar.Children.Add(btnSubdl);

            var btnImportLatest = CreateStyledButton("📥 Import Latest .SRT / .ZIP from Downloads", 265);
            btnImportLatest.Background = new SolidColorBrush(Color.FromRgb(94, 73, 30));
            btnImportLatest.Margin = new Thickness(0, 0, 0, 4);
            btnImportLatest.Click += async (s, e) => await ImportLatestSubtitleFromDownloadsAsync();
            actionBar.Children.Add(btnImportLatest);

            Grid.SetRow(actionBar, 4);
            root.Children.Add(actionBar);

            // ----------------------------------------------------------------
            // ROW 5: STATUS BAR
            // ----------------------------------------------------------------
            _txtStatus = new TextBlock
            {
                Text = "Ready. Click '🔍 Search OpenSubtitles API' (with API key) or '🌐 Lookup Exact Hash' (no API key needed).",
                Foreground = new SolidColorBrush(Color.FromRgb(163, 190, 140)),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetRow(_txtStatus, 5);
            root.Children.Add(_txtStatus);

            Content = root;

            StartDownloadsFolderWatcher();
            Closed += (s, e) => StopDownloadsFolderWatcher();
        }

        private static Button CreateStyledButton(string text, double width)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Padding = new Thickness(8, 5, 8, 5),
                Background = new SolidColorBrush(Color.FromRgb(42, 42, 56)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(74, 74, 96)),
                Cursor = Cursors.Hand
            };
        }

        private string GetSelectedLangCode()
        {
            string raw = _cmbLang.SelectedItem?.ToString() ?? "en";
            int spaceIdx = raw.IndexOf(' ');
            return spaceIdx > 0 ? raw[..spaceIdx].Trim() : raw.Trim();
        }

        private static string MapTwoLetterToThreeLetterLang(string lang2) => lang2.ToLowerInvariant() switch
        {
            "en" => "eng",
            "es" => "spa",
            "fr" => "fre",
            "de" => "ger",
            "it" => "ita",
            "pt-br" => "pob",
            "ja" => "jpn",
            "ko" => "kor",
            "zh-cn" => "chi",
            "ru" => "rus",
            _ => "all"
        };

        private static SubtitleConfigData LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string json = File.ReadAllText(ConfigFilePath);
                    return JsonSerializer.Deserialize<SubtitleConfigData>(json) ?? new SubtitleConfigData();
                }
            }
            catch { }
            return new SubtitleConfigData();
        }

        private void SaveCurrentConfig()
        {
            try
            {
                string? dir = Path.GetDirectoryName(ConfigFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var cfg = new SubtitleConfigData
                {
                    OpenSubtitlesApiKey = _txtApiKey.Text.Trim(),
                    PreferredLanguage = GetSelectedLangCode()
                };
                File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        // ====================================================================
        // OPENSUBTITLES REST API v1 SEARCH & DIRECT DOWNLOAD
        // ====================================================================

        private async Task ExecuteApiSearchAsync()
        {
            SaveCurrentConfig();
            string apiKey = _txtApiKey.Text.Trim();
            if (string.IsNullOrEmpty(apiKey))
            {
                _txtStatus.Text = "ℹ Enter a free OpenSubtitles.com API Key above for in-app search, OR click '🌐 Lookup Exact Hash' below!";
                return;
            }

            _lvResults.Items.Clear();
            _txtStatus.Text = "⏳ Querying OpenSubtitles REST API v1...";

            try
            {
                string lang = GetSelectedLangCode();
                var queryParams = new List<string>();

                if (lang != "all")
                    queryParams.Add($"languages={Uri.EscapeDataString(lang)}");

                if (_chkIncludeHash.IsChecked == true && !string.IsNullOrEmpty(_movieHash))
                    queryParams.Add($"moviehash={_movieHash}");

                if (!string.IsNullOrWhiteSpace(_txtQuery.Text))
                    queryParams.Add($"query={Uri.EscapeDataString(_txtQuery.Text.Trim())}");

                string url = $"https://api.opensubtitles.com/api/v1/subtitles?{string.Join("&", queryParams)}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Api-Key", apiKey);
                req.Headers.UserAgent.ParseAdd("SlideForgeMediaViewer/1.1.6");

                using HttpResponseMessage resp = await SharedHttp.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    _txtStatus.Text = $"⚠ OpenSubtitles API returned {(int)resp.StatusCode} ({resp.ReasonPhrase}). Check your API Key or use Browser Lookup below.";
                    return;
                }

                using JsonDocument doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out JsonElement dataArray) || dataArray.ValueKind != JsonValueKind.Array)
                {
                    _txtStatus.Text = "No results returned.";
                    return;
                }

                var items = new List<SubtitleSearchResultItem>();

                foreach (JsonElement entry in dataArray.EnumerateArray())
                {
                    if (!entry.TryGetProperty("attributes", out JsonElement attrs)) continue;

                    bool hashMatch = attrs.TryGetProperty("moviehash_match", out JsonElement hm) && hm.ValueKind == JsonValueKind.True;
                    string subLang = attrs.TryGetProperty("language", out JsonElement l) ? (l.GetString() ?? "") : "";
                    string release = attrs.TryGetProperty("release", out JsonElement r) ? (r.GetString() ?? "") : "";
                    double fps = attrs.TryGetProperty("fps", out JsonElement f) && f.TryGetDouble(out double dFps) ? dFps : 0.0;
                    int dlCount = attrs.TryGetProperty("download_count", out JsonElement dc) && dc.TryGetInt32(out int dCount) ? dCount : 0;
                    double rating = attrs.TryGetProperty("ratings", out JsonElement rt) && rt.TryGetDouble(out double dRat) ? dRat : 0.0;

                    int fileId = -1;
                    string fileName = release;
                    if (attrs.TryGetProperty("files", out JsonElement filesArr) &&
                        filesArr.ValueKind == JsonValueKind.Array &&
                        filesArr.GetArrayLength() > 0)
                    {
                        JsonElement firstFile = filesArr[0];
                        if (firstFile.TryGetProperty("file_id", out JsonElement fid) && fid.TryGetInt32(out int idVal))
                            fileId = idVal;
                        if (firstFile.TryGetProperty("file_name", out JsonElement fn) && !string.IsNullOrWhiteSpace(fn.GetString()))
                            fileName = fn.GetString()!;
                    }

                    if (fileId > 0)
                    {
                        items.Add(new SubtitleSearchResultItem
                        {
                            FileId = fileId,
                            MatchBadge = hashMatch ? "★ HASH" : "Title",
                            Language = subLang.ToUpperInvariant(),
                            ReleaseName = string.IsNullOrWhiteSpace(release) ? fileName : release,
                            FpsText = fps > 0 ? $"{fps:0.##}" : "-",
                            Downloads = dlCount,
                            Rating = rating
                        });
                    }
                }

                // Rank exact OSDB hash matches first, then highest downloads
                foreach (var item in items.OrderByDescending(i => i.MatchBadge == "★ HASH").ThenByDescending(i => i.Downloads))
                {
                    _lvResults.Items.Add(item);
                }

                if (_lvResults.Items.Count > 0)
                {
                    _lvResults.SelectedIndex = 0;
                    _txtStatus.Text = $"✔ Found {_lvResults.Items.Count} subtitle(s). Double-click any row to download and attach to the playing video!";
                }
                else
                {
                    _txtStatus.Text = "No matching subtitles found. Try unchecking 'Include OSDB File Hash' or refining the title.";
                }
            }
            catch (Exception ex)
            {
                _txtStatus.Text = $"Search error: {ex.Message}";
            }
        }

        private async Task DownloadAndAttachSelectedApiResultAsync()
        {
            if (_lvResults.SelectedItem is not SubtitleSearchResultItem selected)
            {
                _txtStatus.Text = "Select a subtitle from the table first.";
                return;
            }

            string apiKey = _txtApiKey.Text.Trim();
            if (string.IsNullOrEmpty(apiKey)) return;

            _txtStatus.Text = $"⏳ Requesting download stream for '{selected.ReleaseName}'...";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.opensubtitles.com/api/v1/download");
                req.Headers.Add("Api-Key", apiKey);
                req.Headers.UserAgent.ParseAdd("SlideForgeMediaViewer/1.1.6");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Content = new StringContent(JsonSerializer.Serialize(new { file_id = selected.FileId }), System.Text.Encoding.UTF8, "application/json");

                using HttpResponseMessage resp = await SharedHttp.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    _txtStatus.Text = $"⚠ Download request returned {(int)resp.StatusCode}. Use '🌐 Lookup Exact Hash' + '📥 Import Latest' as fallback.";
                    return;
                }

                using JsonDocument doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("link", out JsonElement linkElem) || string.IsNullOrWhiteSpace(linkElem.GetString()))
                {
                    _txtStatus.Text = "⚠ Could not retrieve download URL from response.";
                    return;
                }

                string downloadUrl = linkElem.GetString()!;
                byte[] subBytes = await SharedHttp.GetByteArrayAsync(downloadUrl);

                string videoDir = Path.GetDirectoryName(_videoFilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                string videoBase = Path.GetFileNameWithoutExtension(_videoFilePath);
                string targetSrtPath = Path.Combine(videoDir, $"{videoBase}.{selected.Language.ToLowerInvariant()}.srt");

                await File.WriteAllBytesAsync(targetSrtPath, subBytes);
                await _ownerWindow.AttachSubtitleFileToCurrentVideoAsync(targetSrtPath);

                _txtStatus.Text = $"✔ Saved & Loaded: {Path.GetFileName(targetSrtPath)}";
            }
            catch (Exception ex)
            {
                _txtStatus.Text = $"Download failed: {ex.Message}";
            }
        }

        // ====================================================================
        // DOWNLOADS FOLDER AUTO-WATCHER & 1-CLICK .ZIP / .SRT IMPORTER
        // ====================================================================

        private static string GetDownloadsFolderPath() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        private void StartDownloadsFolderWatcher()
        {
            try
            {
                string downloadsDir = GetDownloadsFolderPath();
                if (!Directory.Exists(downloadsDir)) return;

                _downloadsWatcher = new FileSystemWatcher(downloadsDir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };

                _downloadsWatcher.Created += OnDownloadsFileEvent;
                _downloadsWatcher.Renamed += OnDownloadsFileEvent;
            }
            catch { }
        }

        private void StopDownloadsFolderWatcher()
        {
            if (_downloadsWatcher != null)
            {
                _downloadsWatcher.EnableRaisingEvents = false;
                _downloadsWatcher.Dispose();
                _downloadsWatcher = null;
            }
        }

        private void OnDownloadsFileEvent(object sender, FileSystemEventArgs e)
        {
            string ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
            if (ext is ".srt" or ".ass" or ".ssa" or ".vtt" or ".zip")
            {
                Dispatcher.InvokeAsync(() =>
                {
                    _txtStatus.Text = $"📥 Detected new download '{Path.GetFileName(e.FullPath)}'! Click '📥 Import Latest .SRT / .ZIP from Downloads' to auto-extract & load!";
                });
            }
        }

        private async Task ImportLatestSubtitleFromDownloadsAsync()
        {
            try
            {
                string downloadsDir = GetDownloadsFolderPath();
                if (!Directory.Exists(downloadsDir))
                {
                    _txtStatus.Text = "Could not locate Windows Downloads folder.";
                    return;
                }

                string[] validExts = { ".srt", ".ass", ".ssa", ".vtt", ".sub", ".zip" };
                FileInfo? latestFile = new DirectoryInfo(downloadsDir)
                    .EnumerateFiles()
                    .Where(f => validExts.Contains(f.Extension.ToLowerInvariant()))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latestFile == null)
                {
                    _txtStatus.Text = "No .SRT or .ZIP subtitle files found in your Downloads folder.";
                    return;
                }

                string videoDir = Path.GetDirectoryName(_videoFilePath) ?? downloadsDir;
                string videoBase = Path.GetFileNameWithoutExtension(_videoFilePath);

                if (string.Equals(latestFile.Extension, ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using ZipArchive archive = ZipFile.OpenRead(latestFile.FullName);
                    ZipArchiveEntry? subEntry = archive.Entries
                        .Where(e => e.Length > 0 && new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub" }.Contains(Path.GetExtension(e.Name).ToLowerInvariant()))
                        .OrderByDescending(e => e.Length)
                        .FirstOrDefault();

                    if (subEntry == null)
                    {
                        _txtStatus.Text = $"No subtitle file (.srt/.ass/.vtt) found inside '{latestFile.Name}'.";
                        return;
                    }

                    string subExt = Path.GetExtension(subEntry.Name);
                    string destPath = Path.Combine(videoDir, $"{videoBase}{subExt}");
                    subEntry.ExtractToFile(destPath, overwrite: true);

                    await _ownerWindow.AttachSubtitleFileToCurrentVideoAsync(destPath);
                    _txtStatus.Text = $"✔ Extracted '{subEntry.Name}' from '{latestFile.Name}' → Saved & Loaded as '{Path.GetFileName(destPath)}'!";
                }
                else
                {
                    string destPath = Path.Combine(videoDir, $"{videoBase}{latestFile.Extension}");
                    File.Copy(latestFile.FullName, destPath, overwrite: true);

                    await _ownerWindow.AttachSubtitleFileToCurrentVideoAsync(destPath);
                    _txtStatus.Text = $"✔ Imported '{latestFile.Name}' → Saved & Loaded as '{Path.GetFileName(destPath)}'!";
                }
            }
            catch (Exception ex)
            {
                _txtStatus.Text = $"Import error: {ex.Message}";
            }
        }

        private static void OpenUrlInBrowser(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
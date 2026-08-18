using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using TubeForge.App.Commands;
using TubeForge.Core.Diagnostics;
using TubeForge.Core.Errors;
using TubeForge.Core.Files;
using TubeForge.Core.Media;
using TubeForge.Core.Networking;
using TubeForge.Core.Results;
using TubeForge.Core.Settings;
using TubeForge.Core.YouTube;
using TubeForge.Downloads;
using TubeForge.Downloads.Archives;
using TubeForge.Downloads.History;
using TubeForge.Downloads.Hls;
using TubeForge.Downloads.Queue;
using TubeForge.Media;
using TubeForge.Transcoding;
using TubeForge.Updates;
using TubeForge.YouTube;
using TubeForge.YouTube.Captions;
using TubeForge.YouTube.Collections;
using TubeForge.YouTube.Sidecars;
using TubeForge.YouTube.SponsorBlock;

namespace TubeForge.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly Uri YouTubeOrigin = new("https://www.youtube.com/");
    private static readonly Uri SponsorBlockOrigin = new("https://sponsor.ajay.app/");

    private static readonly IReadOnlyList<FilterOption<NetworkProxyMode>> ProxyModeChoices =
    [
        new("Use Windows system proxy", NetworkProxyMode.System),
        new("Manual HTTP/HTTPS proxy", NetworkProxyMode.Manual),
        new("No proxy", NetworkProxyMode.None)
    ];

    private static readonly IReadOnlyList<FilterOption<SponsorBlockMode>> SponsorBlockModeChoices =
    [
        new("Write chapters", SponsorBlockMode.Chapters),
        new("Remove segments", SponsorBlockMode.Remove)
    ];

    private static readonly IReadOnlyList<DownloadModeOption> BaseModeChoices =
    [
        new(DownloadMode.AudioVideo, "Audio + video", "Best video + best audio, muxed locally"),
        new(DownloadMode.AudioOnly, "Audio only", "Native M4A/AAC or WebM/Opus audio"),
        new(DownloadMode.VideoOnly, "Video only", "Maximum video quality; audio not included")
    ];

    private static readonly IReadOnlyList<DownloadPresetOption> DownloadPresetChoices =
    [
        new(DownloadPresetKind.BestOriginal, "Best original", "Highest available quality; stream copy when possible"),
        new(DownloadPresetKind.WindowsCompatibleMp4, "Windows MP4", "Highest source converted to H.264/AAC MP4"),
        new(DownloadPresetKind.SmallFile, "Small file", "Efficient H.265/AAC MP4; prefers 720p when available"),
        new(DownloadPresetKind.Mp3_320, "MP3 320", "Best audio converted to high-bitrate MP3"),
        new(DownloadPresetKind.Custom, "Custom", "Use the detailed controls below")
    ];

    private static readonly IReadOnlyList<OutputProcessingOption> AudioProcessingChoices =
    [
        new(OutputProfile.Native, "Native stream · no conversion", "Fastest; preserves source quality"),
        new(OutputProfile.Mp3(320), "MP3 · 320 kbps", "Highest MP3 bitrate; largest file"),
        new(OutputProfile.Mp3(256), "MP3 · 256 kbps", "High-quality MP3"),
        new(OutputProfile.Mp3(192), "MP3 · 192 kbps", "Balanced quality and size"),
        new(OutputProfile.Mp3(128), "MP3 · 128 kbps", "Smallest MP3 file"),
        new(OutputProfile.Aac(256), "AAC/M4A · 256 kbps", "Broad Apple and Windows compatibility"),
        new(OutputProfile.Opus(160), "Opus/OGG · 160 kbps", "Efficient open audio format"),
        new(OutputProfile.Flac, "FLAC · lossless", "Lossless compressed audio"),
        new(OutputProfile.Wav, "WAV · PCM", "Uncompressed 16-bit audio; largest file")
    ];

    private static readonly IReadOnlyList<OutputProcessingOption> VideoProcessingChoices =
    [
        new(OutputProfile.Native, "Original quality · stream copy", "Fastest; preserves selected source codecs"),
        new(OutputProfile.H264AacMp4, "Compatible MP4 · H.264/AAC", "Broad Windows/device playback; re-encodes locally"),
        new(OutputProfile.H265AacMp4, "Compact MP4 · H.265/AAC", "Smaller modern MP4; slower conversion"),
        new(OutputProfile.Vp9OpusWebM, "Open WebM · VP9/Opus", "Open codecs; slower conversion")
    ];

    private static readonly IReadOnlyList<CaptionFormatOption> CaptionOutputChoices =
    [
        new(CaptionOutputFormat.SubRip, "SRT · broad player support", "srt"),
        new(CaptionOutputFormat.WebVtt, "WebVTT · native timed text", "vtt")
    ];

    private static readonly IReadOnlyList<ArchiveOutputPresetOption> ArchiveOutputPresetChoices =
    [
        new(ArchiveOutputPreset.BestOriginal, "Best original"),
        new(ArchiveOutputPreset.WindowsCompatibleMp4, "Windows MP4"),
        new(ArchiveOutputPreset.SmallFile, "Small file"),
        new(ArchiveOutputPreset.Mp3_320, "MP3 320")
    ];

    private static readonly IReadOnlyList<ArchiveCaptionPreferenceOption> ArchiveCaptionPreferenceChoices =
    [
        new(ArchiveCaptionPreference.None, "No embedded captions"),
        new(ArchiveCaptionPreference.ManualPreferred, "Embed all manual captions"),
        new(ArchiveCaptionPreference.ManualOrAutomatic, "Embed up to 8 manual + auto captions")
    ];

    private static readonly IReadOnlyList<FilterOption<LibrarySortOrder>> LibrarySortChoices =
    [
        new("Newest first", LibrarySortOrder.NewestFirst),
        new("Oldest first", LibrarySortOrder.OldestFirst),
        new("Title A–Z", LibrarySortOrder.TitleAscending),
        new("Largest first", LibrarySortOrder.LargestFirst)
    ];

    private readonly HttpClient _httpClient;
    private readonly HttpClient _sidecarHttpClient;
    private readonly HttpClient _updateHttpClient;
    private YouTubeMetadataResolver _resolver;
    private DirectDownloadEngine _downloader;
    private AdaptiveDownloadEngine _adaptiveDownloader;
    private readonly FfmpegMediaProcessor _mediaProcessor;
    private readonly FfmpegChapterSplitter _chapterSplitter;
    private readonly FfmpegAudioTranscoder _audioTranscoder;
    private readonly FfmpegVideoTranscoder _videoTranscoder;
    private readonly CaptionDownloadEngine _captionDownloader;
    private readonly YouTubeCollectionResolver _collectionResolver;
    private readonly ThumbnailDownloadEngine _thumbnailDownloader;
    private readonly SponsorBlockClient _sponsorBlockClient;
    private readonly DownloadQueueStore _queueStore;
    private readonly DownloadHistoryStore _historyStore;
    private readonly LibraryTransferService _libraryTransferService = new();
    private readonly CollectionArchiveStore _archiveStore;
    private readonly TubeForgeSettingsStore _settingsStore;
    private readonly GitHubUpdateClient _updateClient;
    private readonly Version _currentVersion;
    private readonly string _updateDirectory;
    private readonly DownloadQueueDispatcher _queueDispatcher = new(2);
    private HostRequestGate _hostRequestGate;
    private RateLimitedRequestExecutor _rateLimitedRequests;
    private readonly ConfigurableWebProxy _networkProxy;
    private readonly SemaphoreSlim _queueMutationLock = new(1, 1);
    private readonly SemaphoreSlim _historyMutationLock = new(1, 1);
    private readonly Dictionary<Guid, QueuedDownloadWork> _preparedQueueWork = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _downloadCancellations = [];
    private readonly HashSet<Guid> _cancelledQueueItems = [];
    private readonly HashSet<Guid> _undispatchableQueueItems = [];
    private readonly HashSet<Guid> _relinkedQueueItems = [];
    private readonly Dictionary<string, bool> _historyFilePresence = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _analysisCancellation;
    private string _urlText = string.Empty;
    private string _downloadFolder;
    private string _videoTitle = string.Empty;
    private string _videoChannel = string.Empty;
    private TimeSpan? _videoDuration;
    private Uri? _thumbnailUrl;
    private string _statusMessage = "Paste a URL to begin";
    private string _progressDetail = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _isAnalyzing;
    private bool _isDownloading;
    private double _downloadFraction;
    private FormatItemViewModel? _selectedFormat;
    private VideoMetadata? _metadata;
    private string _extractionStatus = string.Empty;
    private IReadOnlyList<StreamFormat> _allFormats = [];
    private bool _updatingFormatFilters;
    private IReadOnlyList<DownloadModeOption> _downloadModes = BaseModeChoices;
    private DownloadModeOption _selectedDownloadMode = BaseModeChoices[0];
    private DownloadPresetOption _selectedDownloadPreset = DownloadPresetChoices[0];
    private DownloadPresetOption _defaultDownloadPreset = DownloadPresetChoices[0];
    private bool _showAdvancedDownloadOptions;
    private bool _applyingDownloadPreset;
    private IReadOnlyList<FilterOption<int>> _resolutionOptions = [];
    private FilterOption<int>? _selectedResolution;
    private IReadOnlyList<FilterOption<MediaContainer>> _containerOptions = [];
    private FilterOption<MediaContainer>? _selectedContainer;
    private IReadOnlyList<FilterOption<VideoCodec>> _videoCodecOptions = [];
    private FilterOption<VideoCodec>? _selectedVideoCodec;
    private IReadOnlyList<FilterOption<int>> _frameRateOptions = [];
    private FilterOption<int>? _selectedFrameRate;
    private IReadOnlyList<FilterOption<bool>> _dynamicRangeOptions = [];
    private FilterOption<bool>? _selectedDynamicRange;
    private IReadOnlyList<FilterOption<long>> _bitrateOptions = [];
    private FilterOption<long>? _selectedBitrate;
    private IReadOnlyList<FilterOption<AudioCodec>> _audioCodecOptions = [];
    private FilterOption<AudioCodec>? _selectedAudioCodec;
    private OutputProcessingOption _selectedAudioProcessing = AudioProcessingChoices[0];
    private OutputProcessingOption _selectedVideoProcessing = VideoProcessingChoices[0];
    private DownloadQueueSnapshot _queueSnapshot = new();
    private DownloadHistorySnapshot _historySnapshot = new();
    private CollectionArchiveSnapshot _archiveSnapshot = new();
    private bool _isInitialized;
    private bool _queueUnavailable;
    private bool _historyUnavailable;
    private string _librarySearchText = string.Empty;
    private string _libraryStatus = "Export, import, or rescan durable local history.";
    private string _archiveStatus = "Save a playlist or channel once, then check it for new items later.";
    private CollectionArchiveProfile? _selectedArchiveProfile;
    private ArchiveOutputPresetOption _selectedArchiveOutputPreset = ArchiveOutputPresetChoices[0];
    private ArchiveCaptionPreferenceOption _selectedArchiveCaptionPreference = ArchiveCaptionPreferenceChoices[0];
    private bool _archiveEmbedChapters;
    private CollectionQueueConfiguration? _activeCollectionQueueConfiguration;
    private FilterOption<LibrarySortOrder> _selectedLibrarySort = LibrarySortChoices[0];
    private string _downloadActionLabel = "Add to queue";
    private AppPage _activePage = AppPage.Download;
    private int _selectedMaxConcurrentDownloads = 2;
    private bool _enableAcceleratedTransfers = true;
    private bool _enableAutomaticUpdateChecks = true;
    private FilterOption<NetworkProxyMode> _selectedProxyMode = ProxyModeChoices[0];
    private string _manualProxyUri = string.Empty;
    private int _metadataTimeoutSeconds = 20;
    private int _downloadRetryAttempts = 3;
    private int _perHostConcurrency = 2;
    private string _fileNameTemplate = FileNameTemplate.Default;
    private bool _includeQualityInFileName;
    private TubeForgeSettings _settings;
    private bool _showResponsibleUseNotice = true;
    private string _settingsStatus = "Settings stay on this device.";
    private string _updateStatus = "TubeForge can check GitHub for verified stable releases.";
    private bool _isCheckingForUpdate;
    private bool _isDownloadingUpdate;
    private bool _isUpdateInProgress;
    private double _updateDownloadFraction;
    private string _updateProgressStage = "Ready";
    private bool _hasUpdateError;
    private UpdateRelease? _availableUpdate;
    private UpdateDownloadReceipt? _readyUpdate;
    private string _diagnosticsStatus = "Export contains whitelisted technical state only.";
    private string _diagnosticsExtractionStage = "NotRun";
    private IReadOnlyList<CaptionTrackOption> _captionTracks = [];
    private CaptionTrackOption? _selectedCaptionTrack;
    private CaptionFormatOption _selectedCaptionFormat = CaptionOutputChoices[0];
    private bool _embedChapters;
    private bool _splitChapters;
    private bool _enableTrim;
    private string _trimStartText = "00:00:00";
    private string _trimEndText = "00:00:00";
    private bool _enableSponsorBlock;
    private FilterOption<SponsorBlockMode> _selectedSponsorBlockMode = SponsorBlockModeChoices[0];
    private bool _sponsorCategory = true;
    private bool _introCategory;
    private bool _outroCategory;
    private bool _selfPromotionCategory;
    private bool _interactionCategory;
    private bool _previewCategory;
    private bool _fillerCategory;
    private string _liveDurationMinutesText = "60";
    private string _liveMaximumSizeGiBText = "4";
    private string _liveMaximumWaitMinutesText = "360";
    private bool _isSavingCaption;
    private bool _isSavingSidecar;
    private YouTubeCollectionResult? _collection;

    public MainViewModel() : this(applicationDataDirectory: null)
    {
    }

    internal MainViewModel(
        string? applicationDataDirectory,
        HttpMessageHandler? updateMessageHandler = null,
        Version? currentVersion = null)
    {
        _hostRequestGate = new HostRequestGate(2);
        _rateLimitedRequests = new RateLimitedRequestExecutor(_hostRequestGate);
        _networkProxy = new ConfigurableWebProxy(new NetworkProxyConfiguration(NetworkProxyMode.System));
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            Proxy = _networkProxy,
            UseProxy = true
        };
        _httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _resolver = new YouTubeMetadataResolver(_httpClient);
        _downloader = new DirectDownloadEngine(_httpClient);
        _mediaProcessor = new FfmpegMediaProcessor(
            FfmpegMediaProcessor.BundledExecutablePath(AppContext.BaseDirectory));
        _chapterSplitter = new FfmpegChapterSplitter(_mediaProcessor.ExecutablePath);
        _audioTranscoder = new FfmpegAudioTranscoder(
            FfmpegAudioTranscoder.BundledExecutablePath(AppContext.BaseDirectory));
        _videoTranscoder = new FfmpegVideoTranscoder(
            FfmpegVideoTranscoder.BundledExecutablePath(AppContext.BaseDirectory));
        _adaptiveDownloader = new AdaptiveDownloadEngine(_downloader, _mediaProcessor);
        _captionDownloader = new CaptionDownloadEngine(_httpClient);
        _collectionResolver = new YouTubeCollectionResolver(_httpClient);
        var sidecarHandler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            Proxy = _networkProxy,
            UseProxy = true
        };
        _sidecarHttpClient = new HttpClient(sidecarHandler) { Timeout = TimeSpan.FromSeconds(60) };
        _thumbnailDownloader = new ThumbnailDownloadEngine(_sidecarHttpClient);
        _sponsorBlockClient = new SponsorBlockClient(_sidecarHttpClient);
        var updateHandler = updateMessageHandler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            Proxy = _networkProxy,
            UseProxy = true
        };
        _updateHttpClient = new HttpClient(updateHandler) { Timeout = TimeSpan.FromSeconds(60) };
        _updateClient = new GitHubUpdateClient(_updateHttpClient);
        var executingVersion = currentVersion ??
            Assembly.GetExecutingAssembly().GetName().Version ??
            new Version(1, 0, 0);
        _currentVersion = new Version(
            executingVersion.Major,
            executingVersion.Minor,
            Math.Max(0, executingVersion.Build));
        applicationDataDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TubeForge");
        applicationDataDirectory = Path.GetFullPath(applicationDataDirectory);
        _queueStore = new DownloadQueueStore(Path.Combine(applicationDataDirectory, "queue.json"));
        _historyStore = new DownloadHistoryStore(Path.Combine(applicationDataDirectory, "history.json"));
        _archiveStore = new CollectionArchiveStore(Path.Combine(applicationDataDirectory, "archives.json"));
        _settingsStore = new TubeForgeSettingsStore(Path.Combine(applicationDataDirectory, "settings.json"));
        _updateDirectory = Path.Combine(applicationDataDirectory, "updates");
        _downloadFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        _settings = new TubeForgeSettings { DownloadFolder = Path.GetFullPath(_downloadFolder) };

        AnalyzeCommand = new AsyncRelayCommand(
            AnalyzeAsync,
            () => !ShowResponsibleUseNotice && !IsAnalyzing && !string.IsNullOrWhiteSpace(UrlText));
        DownloadCommand = new AsyncRelayCommand(
            DownloadAsync,
            () => !ShowResponsibleUseNotice && !IsAnalyzing && SelectedFormat is not null && HasVideo);
        DownloadCaptionCommand = new AsyncRelayCommand(
            DownloadCaptionAsync,
            () => !ShowResponsibleUseNotice && !IsAnalyzing && !IsSavingCaption && SelectedCaptionTrack is not null);
        SaveThumbnailCommand = new AsyncRelayCommand(
            SaveThumbnailAsync,
            () => !ShowResponsibleUseNotice && !IsAnalyzing && !IsSavingSidecar && _metadata?.ThumbnailUrl is not null);
        SaveMetadataCommand = new AsyncRelayCommand(
            SaveMetadataAsync,
            () => !ShowResponsibleUseNotice && !IsAnalyzing && !IsSavingSidecar && _metadata is not null);
        SelectAllCollectionCommand = new RelayCommand(
            () => SetCollectionSelection(isSelected: true),
            () => !IsAnalyzing && CollectionItems.Any(item => !item.IsSelected));
        SelectNoneCollectionCommand = new RelayCommand(
            () => SetCollectionSelection(isSelected: false),
            () => !IsAnalyzing && CollectionItems.Any(item => item.IsSelected));
        QueueCollectionCommand = new AsyncRelayCommand(
            QueueSelectedCollectionAsync,
            () => !ShowResponsibleUseNotice && !IsAnalyzing && CollectionItems.Any(item => item.IsSelected));
        SelectMissingCollectionCommand = new RelayCommand(
            SelectMissingCollection,
            () => !IsAnalyzing && CollectionItems.Count > 0);
        SaveArchiveProfileCommand = new AsyncRelayCommand(
            SaveArchiveProfileAsync,
            () => !ShowResponsibleUseNotice && !IsAnalyzing && _collection is not null);
        CheckArchiveProfilesCommand = new AsyncRelayCommand(
            CheckArchiveProfilesAsync,
            () => !ShowResponsibleUseNotice && !IsAnalyzing && _archiveSnapshot.Profiles.Count > 0);
        RemoveArchiveProfileCommand = new AsyncRelayCommand(
            RemoveArchiveProfileAsync,
            () => !IsAnalyzing && SelectedArchiveProfile is not null);
        CancelAnalysisCommand = new RelayCommand(CancelAnalysis, () => IsAnalyzing);
        CancelCommand = new RelayCommand(PauseAll, () => IsDownloading);
        ShowDownloadCommand = new RelayCommand(() => ShowPage(AppPage.Download));
        ShowQueueCommand = new RelayCommand(() => ShowPage(AppPage.Queue));
        ShowLibraryCommand = new RelayCommand(() => ShowPage(AppPage.Library));
        ShowSettingsCommand = new RelayCommand(() => ShowPage(AppPage.Settings));
        ShowDiagnosticsCommand = new RelayCommand(() => ShowPage(AppPage.Diagnostics));
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => !IsBusy);
        AcceptResponsibleUseCommand = new AsyncRelayCommand(AcceptResponsibleUseAsync);
        CheckForUpdatesCommand = new AsyncRelayCommand(
            () => CheckForUpdatesAsync(isAutomatic: false),
            () => !ShowResponsibleUseNotice && !IsCheckingForUpdate && !IsUpdateInProgress);
        UpdateNowCommand = new AsyncRelayCommand(
            UpdateNowAsync,
            () => _availableUpdate is not null && !IsCheckingForUpdate && !IsUpdateInProgress);
        ClearCompletedCommand = new AsyncRelayCommand(ClearCompletedAsync, () => QueueItems.Any(item => item.Status == DownloadQueueStatus.Completed));
        ClearHistoryCommand = new AsyncRelayCommand(
            ClearHistoryAsync,
            () => _historySnapshot.Entries.Count > 0 || _historyUnavailable);
        RemoveMissingHistoryCommand = new AsyncRelayCommand(
            RemoveMissingHistoryAsync,
            // Answered from the presence cache: the predicate runs on the UI thread every time
            // the Library changes, and stat-ing thousands of recorded paths there stalls the window.
            () => _historySnapshot.Entries.Any(entry => !IsHistoryFilePresent(entry.DestinationPath)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;

    public event EventHandler? UpdateInstallerStarted;

    public ObservableCollection<FormatItemViewModel> Formats { get; } = [];

    public ObservableCollection<QueueItemViewModel> QueueItems { get; } = [];

    public ObservableCollection<CollectionItemViewModel> CollectionItems { get; } = [];

    public ObservableCollection<HistoryItemViewModel> HistoryItems { get; } = [];

    public ObservableCollection<CollectionArchiveProfile> ArchiveProfiles { get; } = [];

    public IReadOnlyList<DownloadModeOption> DownloadModes => _downloadModes;

    public IReadOnlyList<DownloadPresetOption> DownloadPresets => DownloadPresetChoices;

    public IReadOnlyList<DownloadPresetOption> DefaultDownloadPresets =>
        DownloadPresetChoices.Where(option => option.Value != DownloadPresetKind.Custom).ToArray();

    public IReadOnlyList<OutputProcessingOption> AudioProcessingOptions => AudioProcessingChoices;

    public IReadOnlyList<OutputProcessingOption> VideoProcessingOptions => VideoProcessingChoices;

    public AsyncRelayCommand AnalyzeCommand { get; }

    public AsyncRelayCommand DownloadCommand { get; }

    public AsyncRelayCommand DownloadCaptionCommand { get; }

    public AsyncRelayCommand SaveThumbnailCommand { get; }

    public AsyncRelayCommand SaveMetadataCommand { get; }

    public RelayCommand SelectAllCollectionCommand { get; }

    public RelayCommand SelectNoneCollectionCommand { get; }

    public AsyncRelayCommand QueueCollectionCommand { get; }

    public RelayCommand SelectMissingCollectionCommand { get; }

    public AsyncRelayCommand SaveArchiveProfileCommand { get; }

    public AsyncRelayCommand CheckArchiveProfilesCommand { get; }

    public AsyncRelayCommand RemoveArchiveProfileCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand CancelAnalysisCommand { get; }

    public RelayCommand ShowDownloadCommand { get; }

    public RelayCommand ShowQueueCommand { get; }

    public RelayCommand ShowLibraryCommand { get; }

    public RelayCommand ShowSettingsCommand { get; }

    public RelayCommand ShowDiagnosticsCommand { get; }

    public AsyncRelayCommand SaveSettingsCommand { get; }

    public AsyncRelayCommand AcceptResponsibleUseCommand { get; }

    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public AsyncRelayCommand UpdateNowCommand { get; }

    public AsyncRelayCommand ClearCompletedCommand { get; }

    public AsyncRelayCommand ClearHistoryCommand { get; }

    public AsyncRelayCommand RemoveMissingHistoryCommand { get; }

    public IReadOnlyList<int> MaxConcurrentDownloadOptions { get; } = [1, 2, 3, 4];

    public IReadOnlyList<FilterOption<NetworkProxyMode>> ProxyModeOptions => ProxyModeChoices;

    public IReadOnlyList<int> MetadataTimeoutOptions { get; } = [5, 10, 20, 30, 60, 120];

    public IReadOnlyList<int> DownloadRetryOptions { get; } = [1, 2, 3, 4, 5];

    public IReadOnlyList<int> PerHostConcurrencyOptions { get; } = [1, 2, 3, 4];

    public string UrlText
    {
        get => _urlText;
        set
        {
            if (Set(ref _urlText, value)) RefreshCommands();
        }
    }

    public string DownloadFolder
    {
        get => _downloadFolder;
        set => Set(ref _downloadFolder, value);
    }

    public string VideoTitle => _videoTitle;

    public string VideoMetaLine => string.Join("  ·  ", new[]
    {
        _videoChannel,
        _videoDuration is null ? string.Empty : FormatDuration(_videoDuration.Value),
        _metadata?.ContentKind switch
        {
            VideoContentKind.Short => "Short",
            VideoContentKind.LiveReplay => "Completed live replay",
            VideoContentKind.LiveActive => "Active live · record from now",
            VideoContentKind.LiveUpcoming => "Upcoming live · wait then record",
            _ => string.Empty
        },
        _metadata?.Chapters.Count > 0 ? $"{_metadata.Chapters.Count} chapters" : string.Empty
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public Uri? ThumbnailUrl => _thumbnailUrl;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public string ProgressDetail
    {
        get => _progressDetail;
        private set => Set(ref _progressDetail, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (Set(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasVideo => _metadata is not null;

    public bool HasCollection => _collection is not null;

    public string CollectionTitle => _collection?.Title ?? string.Empty;

    public string CollectionSummary => _collection is null
        ? string.Empty
        : $"{CollectionItems.Count} videos · {_collection.PagesRead} pages" +
          (_collection.IsTruncated ? " · bounded result" : string.Empty);

    public string CollectionSelectionSummary =>
        $"{CollectionItems.Count(item => item.IsSelected)} of {CollectionItems.Count} selected";

    public IReadOnlyList<ArchiveOutputPresetOption> ArchiveOutputPresets => ArchiveOutputPresetChoices;

    public ArchiveOutputPresetOption SelectedArchiveOutputPreset
    {
        get => _selectedArchiveOutputPreset;
        set
        {
            if (value is not null)
            {
                Set(ref _selectedArchiveOutputPreset, value);
            }
        }
    }

    public IReadOnlyList<ArchiveCaptionPreferenceOption> ArchiveCaptionPreferences =>
        ArchiveCaptionPreferenceChoices;

    public ArchiveCaptionPreferenceOption SelectedArchiveCaptionPreference
    {
        get => _selectedArchiveCaptionPreference;
        set
        {
            if (value is not null)
            {
                Set(ref _selectedArchiveCaptionPreference, value);
            }
        }
    }

    public bool ArchiveEmbedChapters
    {
        get => _archiveEmbedChapters;
        set => Set(ref _archiveEmbedChapters, value);
    }

    public CollectionArchiveProfile? SelectedArchiveProfile
    {
        get => _selectedArchiveProfile;
        set
        {
            if (Set(ref _selectedArchiveProfile, value))
            {
                RemoveArchiveProfileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ArchiveStatus
    {
        get => _archiveStatus;
        private set => Set(ref _archiveStatus, value);
    }

    public IReadOnlyList<CaptionTrackOption> CaptionTracks
    {
        get => _captionTracks;
        private set
        {
            if (Set(ref _captionTracks, value))
            {
                OnPropertyChanged(nameof(HasCaptions));
                OnPropertyChanged(nameof(CanEmbedSelectedCaption));
            }
        }
    }

    public bool HasCaptions => CaptionTracks.Count > 0;

    public bool HasChapters => _metadata?.Chapters.Count > 0;

    public CaptionTrackOption? SelectedCaptionTrack
    {
        get => _selectedCaptionTrack;
        set
        {
            if (Set(ref _selectedCaptionTrack, value))
            {
                OnPropertyChanged(nameof(CanEmbedSelectedCaption));
                if (!CanEmbedSelectedCaption)
                {
                    ClearEmbeddedCaptionSelections();
                }
                RefreshCommands();
            }
        }
    }

    public IReadOnlyList<CaptionFormatOption> CaptionFormats => CaptionOutputChoices;

    public CaptionFormatOption SelectedCaptionFormat
    {
        get => _selectedCaptionFormat;
        set
        {
            if (value is not null)
            {
                Set(ref _selectedCaptionFormat, value);
            }
        }
    }

    public bool IsSavingCaption
    {
        get => _isSavingCaption;
        private set
        {
            if (Set(ref _isSavingCaption, value))
            {
                RefreshCommands();
            }
        }
    }

    public bool IsSavingSidecar
    {
        get => _isSavingSidecar;
        private set
        {
            if (Set(ref _isSavingSidecar, value))
            {
                RefreshCommands();
            }
        }
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (Set(ref _isAnalyzing, value)) BusyStateChanged();
        }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (Set(ref _isDownloading, value)) BusyStateChanged();
        }
    }

    public bool IsBusy => IsAnalyzing || IsDownloading;

    public bool IsDownloadPage => _activePage == AppPage.Download;

    public bool IsQueuePage => _activePage == AppPage.Queue;

    public bool IsLibraryPage => _activePage == AppPage.Library;

    public bool IsSettingsPage => _activePage == AppPage.Settings;

    public bool IsDiagnosticsPage => _activePage == AppPage.Diagnostics;

    public bool ShowResponsibleUseNotice
    {
        get => _showResponsibleUseNotice;
        private set
        {
            if (Set(ref _showResponsibleUseNotice, value))
            {
                OnPropertyChanged(nameof(IsMainContentEnabled));
                RefreshCommands();
            }
        }
    }

    public bool IsMainContentEnabled => !ShowResponsibleUseNotice;

    public string SettingsStatus
    {
        get => _settingsStatus;
        private set => Set(ref _settingsStatus, value);
    }

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => Set(ref _updateStatus, value);
    }

    public bool IsCheckingForUpdate
    {
        get => _isCheckingForUpdate;
        private set
        {
            if (Set(ref _isCheckingForUpdate, value)) RefreshCommands();
        }
    }

    public bool IsDownloadingUpdate
    {
        get => _isDownloadingUpdate;
        private set
        {
            if (Set(ref _isDownloadingUpdate, value))
            {
                OnPropertyChanged(nameof(CanDismissUpdatePrompt));
                RefreshCommands();
            }
        }
    }

    public bool IsUpdateInProgress
    {
        get => _isUpdateInProgress;
        private set
        {
            if (Set(ref _isUpdateInProgress, value))
            {
                OnPropertyChanged(nameof(CanDismissUpdatePrompt));
                OnPropertyChanged(nameof(UpdateActionLabel));
                RefreshCommands();
            }
        }
    }

    public double UpdateDownloadFraction
    {
        get => _updateDownloadFraction;
        private set
        {
            var clamped = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
            if (Set(ref _updateDownloadFraction, clamped))
            {
                OnPropertyChanged(nameof(UpdateProgressPercent));
            }
        }
    }

    public string UpdateProgressStage
    {
        get => _updateProgressStage;
        private set => Set(ref _updateProgressStage, value);
    }

    public bool HasUpdateError
    {
        get => _hasUpdateError;
        private set => Set(ref _hasUpdateError, value);
    }

    public bool HasUpdateAvailable => _availableUpdate is not null && _readyUpdate is null;

    public bool IsUpdateReady => _readyUpdate is not null;

    public bool IsUpdateActionAvailable => _availableUpdate is not null;

    public bool CanDismissUpdatePrompt => !IsUpdateInProgress;

    public string UpdateActionLabel => IsUpdateInProgress ? "Updating…" : "Update now";

    public string UpdateProgressPercent => $"{Math.Round(UpdateDownloadFraction * 100):0}%";

    public string AvailableUpdateVersion => _availableUpdate?.Version.ToString(3) ?? string.Empty;

    public string AvailableUpdateSummary => _availableUpdate is null
        ? string.Empty
        : $"TubeForge {_currentVersion.ToString(3)}  →  {_availableUpdate.Version.ToString(3)}  ·  " +
          $"{FormatBytes(_availableUpdate.SetupLength)} installer";

    public string DiagnosticsStatus
    {
        get => _diagnosticsStatus;
        private set => Set(ref _diagnosticsStatus, value);
    }

    public string ApplicationVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "development";

    public string RuntimeDescription => RuntimeInformation.FrameworkDescription;

    public string ProcessArchitecture => RuntimeInformation.ProcessArchitecture.ToString();

    public string DependencyStatus => _mediaProcessor.IsAvailable
        ? "Bundled FFmpeg media engine available"
        : "Bundled FFmpeg media engine missing — reinstall TubeForge";

    public string QueueStoragePath => _queueStore.StoragePath;

    public string HistoryStoragePath => _historyStore.StoragePath;

    public string ArchiveStoragePath => _archiveStore.StoragePath;

    public string LibraryStatus
    {
        get => _libraryStatus;
        private set => Set(ref _libraryStatus, value);
    }

    public string SettingsStoragePath => _settingsStore.StoragePath;

    public string DiagnosticsExtractionStatus => _diagnosticsExtractionStage;

    public string DiagnosticsFormatSummary => _metadata is null
        ? "No active media metadata"
        : $"{_allFormats.Count} direct formats · {Formats.Count} matching outputs";

    public bool HasQueueItems => QueueItems.Count > 0;

    public bool HasHistory => HistoryItems.Count > 0;

    public string HistorySummary => HistoryItems.Count == _historySnapshot.Entries.Count
        ? HistoryItems.Count == 1 ? "1 completed output" : $"{HistoryItems.Count} completed outputs"
        : $"{HistoryItems.Count} matching · {_historySnapshot.Entries.Count} total";

    public string HistoryEmptyMessage => _historySnapshot.Entries.Count == 0
        ? "Completed downloads appear here and remain recorded after queue cleanup."
        : "No downloads match the current Library search.";

    public IReadOnlyList<FilterOption<LibrarySortOrder>> LibrarySortOptions => LibrarySortChoices;

    public string LibrarySearchText
    {
        get => _librarySearchText;
        set
        {
            if (Set(ref _librarySearchText, value ?? string.Empty))
            {
                RebuildHistoryItems();
            }
        }
    }

    public FilterOption<LibrarySortOrder> SelectedLibrarySort
    {
        get => _selectedLibrarySort;
        set
        {
            if (value is null || !Set(ref _selectedLibrarySort, value))
            {
                return;
            }

            RebuildHistoryItems();
            if (_isInitialized && value.Value is { } order)
            {
                _ = SaveLibrarySortOrderAsync(order);
            }
        }
    }

    public string QueueSummary
    {
        get
        {
            var active = QueueItems.Count(item => item.Status == DownloadQueueStatus.Downloading);
            var waiting = QueueItems.Count(item => item.Status == DownloadQueueStatus.Queued);
            var completed = QueueItems.Count(item => item.Status == DownloadQueueStatus.Completed);
            return $"{active} active · {waiting} waiting · {completed} completed";
        }
    }

    public int SelectedMaxConcurrentDownloads
    {
        get => _selectedMaxConcurrentDownloads;
        set
        {
            if (!Set(ref _selectedMaxConcurrentDownloads, value))
            {
                return;
            }

            _queueDispatcher.MaximumConcurrency = value;
            PumpQueue();
        }
    }

    public bool EnableAcceleratedTransfers
    {
        get => _enableAcceleratedTransfers;
        set => Set(ref _enableAcceleratedTransfers, value);
    }

    public bool EnableAutomaticUpdateChecks
    {
        get => _enableAutomaticUpdateChecks;
        set => Set(ref _enableAutomaticUpdateChecks, value);
    }

    public FilterOption<NetworkProxyMode> SelectedProxyMode
    {
        get => _selectedProxyMode;
        set
        {
            if (value is not null && Set(ref _selectedProxyMode, value))
            {
                OnPropertyChanged(nameof(UsesManualProxy));
            }
        }
    }

    public bool UsesManualProxy => SelectedProxyMode.Value == NetworkProxyMode.Manual;

    public string ManualProxyUri
    {
        get => _manualProxyUri;
        set => Set(ref _manualProxyUri, value ?? string.Empty);
    }

    public int MetadataTimeoutSeconds
    {
        get => _metadataTimeoutSeconds;
        set => Set(ref _metadataTimeoutSeconds, value);
    }

    public int DownloadRetryAttempts
    {
        get => _downloadRetryAttempts;
        set => Set(ref _downloadRetryAttempts, value);
    }

    public int PerHostConcurrency
    {
        get => _perHostConcurrency;
        set => Set(ref _perHostConcurrency, value);
    }

    public string FileNameTemplateText
    {
        get => _fileNameTemplate;
        set => Set(ref _fileNameTemplate, value);
    }

    public bool IncludeQualityInFileName
    {
        get => _includeQualityInFileName;
        set => Set(ref _includeQualityInFileName, value);
    }

    public double DownloadFraction
    {
        get => _downloadFraction;
        private set => Set(ref _downloadFraction, value);
    }

    public FormatItemViewModel? SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            if (Set(ref _selectedFormat, value))
            {
                OnPropertyChanged(nameof(CanEmbedSelectedCaption));
                OnPropertyChanged(nameof(CanEmbedChapters));
                OnPropertyChanged(nameof(CanSplitChapters));
                OnPropertyChanged(nameof(CanTrim));
                OnPropertyChanged(nameof(CanUseSponsorBlock));
                OnPropertyChanged(nameof(IsLiveCapture));
                OnPropertyChanged(nameof(IsUpcomingLiveCapture));
                OnPropertyChanged(nameof(LiveCaptureModeNotice));
                if (IsLiveCapture && _selectedVideoProcessing.Value != OutputProfile.Native)
                {
                    _selectedVideoProcessing = VideoProcessingChoices[0];
                    OnPropertyChanged(nameof(SelectedVideoProcessing));
                }
                if (!_updatingFormatFilters)
                {
                    RevalidateSelectedFormatCapabilities();
                }

                RefreshCommands();
            }
        }
    }

    public DownloadPresetOption SelectedDownloadPreset
    {
        get => _selectedDownloadPreset;
        set
        {
            var accepted = IsLiveCapture
                ? DownloadPresetChoices.First(option => option.Value == DownloadPresetKind.BestOriginal)
                : value;
            if (accepted is null)
            {
                return;
            }

            if (accepted.Value == DownloadPresetKind.Custom)
            {
                ShowAdvancedDownloadOptions = true;
            }
            if (!Set(ref _selectedDownloadPreset, accepted))
            {
                return;
            }

            ApplyDownloadPreset(accepted);
        }
    }

    public DownloadPresetOption DefaultDownloadPreset
    {
        get => _defaultDownloadPreset;
        set
        {
            if (value is not null && value.Value != DownloadPresetKind.Custom)
            {
                Set(ref _defaultDownloadPreset, value);
            }
        }
    }

    public bool ShowAdvancedDownloadOptions
    {
        get => _showAdvancedDownloadOptions;
        set => Set(ref _showAdvancedDownloadOptions, value);
    }

    public bool CanEmbedSelectedCaption =>
        !IsLiveCapture &&
        IsAudioVideo &&
        SelectedCaptionTrack is not null &&
        SelectedFormat is not null &&
        IsCaptionContainerSupported(FinalMediaContainer(
            SelectedFormat,
            OutputProfileFor(SelectedFormat))) &&
        new CaptionEmbedSelection(
            SelectedCaptionTrack.Track.LanguageCode,
            SelectedCaptionTrack.Track.IsAutoGenerated).IsValid;

    public bool EmbedSelectedCaption
    {
        get => SelectedCaptionTrack?.IsSelectedForEmbedding == true;
        set
        {
            if (SelectedCaptionTrack is null)
            {
                return;
            }

            SelectedCaptionTrack.IsSelectedForEmbedding = value && CanEmbedSelectedCaption;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedCaptionEmbeds));
        }
    }

    public bool HasSelectedCaptionEmbeds => CaptionTracks.Any(track => track.IsSelectedForEmbedding);

    public bool CanEmbedChapters =>
        HasChapters &&
        _metadata?.Duration is { } duration && duration > TimeSpan.Zero &&
        !IsAudioOnly &&
        SelectedFormat is not null &&
        IsCaptionContainerSupported(FinalMediaContainer(
            SelectedFormat,
            OutputProfileFor(SelectedFormat)));

    public bool EmbedChapters
    {
        get => _embedChapters;
        set => Set(ref _embedChapters, value && CanEmbedChapters);
    }

    public bool CanSplitChapters => CanEmbedChapters;

    public bool SplitChapters
    {
        get => _splitChapters;
        set => Set(ref _splitChapters, value && CanSplitChapters);
    }

    public bool CanTrim =>
        _metadata?.Duration is { } duration && duration > TimeSpan.Zero &&
        SelectedFormat is not null;

    public bool EnableTrim
    {
        get => _enableTrim;
        set => Set(ref _enableTrim, value && CanTrim);
    }

    public string TrimStartText
    {
        get => _trimStartText;
        set => Set(ref _trimStartText, value ?? string.Empty);
    }

    public string TrimEndText
    {
        get => _trimEndText;
        set => Set(ref _trimEndText, value ?? string.Empty);
    }

    public IReadOnlyList<FilterOption<SponsorBlockMode>> SponsorBlockModeOptions => SponsorBlockModeChoices;

    public bool CanUseSponsorBlock => HasVideo && SelectedFormat is not null && !IsLiveCapture;

    public bool IsLiveCapture => SelectedFormat?.Format.IsLiveHls == true;

    public bool IsUpcomingLiveCapture => SelectedFormat?.Format.IsLiveManifestPending == true;

    public string LiveCaptureModeNotice => IsUpcomingLiveCapture
        ? "TubeForge will wait locally for this public stream to start, then record until either limit is reached."
        : "TubeForge will record this public stream from now until either limit is reached.";

    public string LiveDurationMinutesText
    {
        get => _liveDurationMinutesText;
        set => Set(ref _liveDurationMinutesText, value ?? string.Empty);
    }

    public string LiveMaximumSizeGiBText
    {
        get => _liveMaximumSizeGiBText;
        set => Set(ref _liveMaximumSizeGiBText, value ?? string.Empty);
    }

    public string LiveMaximumWaitMinutesText
    {
        get => _liveMaximumWaitMinutesText;
        set => Set(ref _liveMaximumWaitMinutesText, value ?? string.Empty);
    }

    public bool EnableSponsorBlock
    {
        get => _enableSponsorBlock;
        set => Set(ref _enableSponsorBlock, value && CanUseSponsorBlock);
    }

    public FilterOption<SponsorBlockMode> SelectedSponsorBlockMode
    {
        get => _selectedSponsorBlockMode;
        set
        {
            if (value is not null && Set(ref _selectedSponsorBlockMode, value))
            {
                OnPropertyChanged(nameof(SponsorBlockModeNotice));
            }
        }
    }

    public string SponsorBlockModeNotice =>
        SelectedSponsorBlockMode.Value == SponsorBlockMode.Remove
            ? "Removal requires a selected audio/video conversion preset. Embedded captions are rebased; chapter workflows remain unavailable."
            : "Chapter mode keeps media unchanged and adds local timeline markers after the opt-in lookup.";

    public bool SponsorCategory
    {
        get => _sponsorCategory;
        set => Set(ref _sponsorCategory, value);
    }

    public bool IntroCategory
    {
        get => _introCategory;
        set => Set(ref _introCategory, value);
    }

    public bool OutroCategory
    {
        get => _outroCategory;
        set => Set(ref _outroCategory, value);
    }

    public bool SelfPromotionCategory
    {
        get => _selfPromotionCategory;
        set => Set(ref _selfPromotionCategory, value);
    }

    public bool InteractionCategory
    {
        get => _interactionCategory;
        set => Set(ref _interactionCategory, value);
    }

    public bool PreviewCategory
    {
        get => _previewCategory;
        set => Set(ref _previewCategory, value);
    }

    public bool FillerCategory
    {
        get => _fillerCategory;
        set => Set(ref _fillerCategory, value);
    }

    public DownloadModeOption SelectedDownloadMode
    {
        get => _selectedDownloadMode;
        set
        {
            if (value is null || !Set(ref _selectedDownloadMode, value))
            {
                return;
            }

            MarkPresetCustom();

            OnPropertyChanged(nameof(HasVideoFilters));
            OnPropertyChanged(nameof(IsAudioOnly));
            OnPropertyChanged(nameof(IsAudioVideo));
            OnPropertyChanged(nameof(CanEmbedSelectedCaption));
            OnPropertyChanged(nameof(CanEmbedChapters));
            OnPropertyChanged(nameof(CanSplitChapters));
            if (!CanEmbedSelectedCaption)
            {
                ClearEmbeddedCaptionSelections();
            }
            if (!CanEmbedChapters)
            {
                EmbedChapters = false;
            }
            if (!CanSplitChapters)
            {
                SplitChapters = false;
            }
            OnPropertyChanged(nameof(ModeNotice));
            RebuildFormatFilters(resetSelections: true);
        }
    }

    public IReadOnlyList<FilterOption<int>> ResolutionOptions
    {
        get => _resolutionOptions;
        private set => Set(ref _resolutionOptions, value);
    }

    public FilterOption<int>? SelectedResolution
    {
        get => _selectedResolution;
        set => SetFilterSelection(ref _selectedResolution, value);
    }

    public IReadOnlyList<FilterOption<MediaContainer>> ContainerOptions
    {
        get => _containerOptions;
        private set => Set(ref _containerOptions, value);
    }

    public FilterOption<MediaContainer>? SelectedContainer
    {
        get => _selectedContainer;
        set => SetFilterSelection(ref _selectedContainer, value);
    }

    public IReadOnlyList<FilterOption<VideoCodec>> VideoCodecOptions
    {
        get => _videoCodecOptions;
        private set => Set(ref _videoCodecOptions, value);
    }

    public FilterOption<VideoCodec>? SelectedVideoCodec
    {
        get => _selectedVideoCodec;
        set => SetFilterSelection(ref _selectedVideoCodec, value);
    }

    public IReadOnlyList<FilterOption<int>> FrameRateOptions
    {
        get => _frameRateOptions;
        private set => Set(ref _frameRateOptions, value);
    }

    public FilterOption<int>? SelectedFrameRate
    {
        get => _selectedFrameRate;
        set => SetFilterSelection(ref _selectedFrameRate, value);
    }

    public IReadOnlyList<FilterOption<bool>> DynamicRangeOptions
    {
        get => _dynamicRangeOptions;
        private set => Set(ref _dynamicRangeOptions, value);
    }

    public FilterOption<bool>? SelectedDynamicRange
    {
        get => _selectedDynamicRange;
        set => SetFilterSelection(ref _selectedDynamicRange, value);
    }

    public IReadOnlyList<FilterOption<long>> BitrateOptions
    {
        get => _bitrateOptions;
        private set => Set(ref _bitrateOptions, value);
    }

    public FilterOption<long>? SelectedBitrate
    {
        get => _selectedBitrate;
        set => SetFilterSelection(ref _selectedBitrate, value);
    }

    public IReadOnlyList<FilterOption<AudioCodec>> AudioCodecOptions
    {
        get => _audioCodecOptions;
        private set => Set(ref _audioCodecOptions, value);
    }

    public FilterOption<AudioCodec>? SelectedAudioCodec
    {
        get => _selectedAudioCodec;
        set => SetFilterSelection(ref _selectedAudioCodec, value);
    }

    public OutputProcessingOption SelectedAudioProcessing
    {
        get => _selectedAudioProcessing;
        set
        {
            if (value is not null && Set(ref _selectedAudioProcessing, value))
            {
                MarkPresetCustom();
                OnPropertyChanged(nameof(ModeNotice));
            }
        }
    }

    public OutputProcessingOption SelectedVideoProcessing
    {
        get => _selectedVideoProcessing;
        set
        {
            var accepted = IsLiveCapture ? VideoProcessingChoices[0] : value;
            if (accepted is not null && Set(ref _selectedVideoProcessing, accepted))
            {
                MarkPresetCustom();
                OnPropertyChanged(nameof(CanEmbedSelectedCaption));
                OnPropertyChanged(nameof(CanEmbedChapters));
                OnPropertyChanged(nameof(CanSplitChapters));
                if (!CanEmbedSelectedCaption)
                {
                    ClearEmbeddedCaptionSelections();
                }
                if (!CanEmbedChapters)
                {
                    EmbedChapters = false;
                }
                if (!CanSplitChapters)
                {
                    SplitChapters = false;
                }
                OnPropertyChanged(nameof(ModeNotice));
            }
        }
    }

    public bool HasVideoFilters => SelectedDownloadMode.Value is not DownloadMode.AudioOnly;

    public bool IsAudioOnly => SelectedDownloadMode.Value is DownloadMode.AudioOnly;

    public bool IsAudioVideo => SelectedDownloadMode.Value is DownloadMode.AudioVideo;

    public string ModeNotice => SelectedDownloadMode.Value switch
    {
        DownloadMode.AudioVideo =>
            SelectedVideoProcessing.Value.Kind == OutputProfileKind.Native
                ? CombinedModeNotice()
                : VideoProcessingNotice(SelectedVideoProcessing.Value),
        DownloadMode.AudioOnly =>
            SelectedAudioProcessing.Value.Kind == OutputProfileKind.Native
                ? "Native AAC/Opus: fastest path with no re-encoding or quality loss."
                : AudioProcessingNotice(SelectedAudioProcessing.Value),
        DownloadMode.VideoOnly =>
            VideoOnlyModeNotice(),
        _ => string.Empty
    };

    public string FormatCountLabel => $"{Formats.Count} matching · {_allFormats.Count} total";

    public string DownloadActionLabel
    {
        get => _downloadActionLabel;
        private set => Set(ref _downloadActionLabel, value);
    }

    public string ExtractionStatus
    {
        get => _extractionStatus;
        private set
        {
            if (Set(ref _extractionStatus, value))
            {
                OnPropertyChanged(nameof(DiagnosticsExtractionStatus));
            }
        }
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        // The four state files are independent, so they are read together rather than in series;
        // each one costs a file open plus a JSON parse on a cold process.
        var settingsTask = _settingsStore.LoadAsync(_settings);
        var queueTask = _queueStore.LoadAsync();
        var historyTask = _historyStore.LoadAsync();
        var archiveTask = _archiveStore.LoadAsync();
        var settingsResult = await settingsTask;
        if (settingsResult.IsSuccess)
        {
            _settings = settingsResult.Value;
            _downloadFolder = _settings.DownloadFolder;
            OnPropertyChanged(nameof(DownloadFolder));
            _selectedMaxConcurrentDownloads = _settings.MaximumConcurrentDownloads;
            _queueDispatcher.MaximumConcurrency = _selectedMaxConcurrentDownloads;
            OnPropertyChanged(nameof(SelectedMaxConcurrentDownloads));
            _enableAcceleratedTransfers = _settings.EnableAcceleratedTransfers;
            OnPropertyChanged(nameof(EnableAcceleratedTransfers));
            _enableAutomaticUpdateChecks = _settings.EnableAutomaticUpdateChecks;
            OnPropertyChanged(nameof(EnableAutomaticUpdateChecks));
            _fileNameTemplate = _settings.FileNameTemplate;
            OnPropertyChanged(nameof(FileNameTemplateText));
            _includeQualityInFileName = _settings.IncludeQualityInFileName;
            OnPropertyChanged(nameof(IncludeQualityInFileName));
            _selectedProxyMode = ProxyModeChoices.First(option => option.Value == _settings.ProxyMode);
            OnPropertyChanged(nameof(SelectedProxyMode));
            OnPropertyChanged(nameof(UsesManualProxy));
            _manualProxyUri = _settings.ManualProxyUri;
            OnPropertyChanged(nameof(ManualProxyUri));
            _metadataTimeoutSeconds = _settings.MetadataTimeoutSeconds;
            OnPropertyChanged(nameof(MetadataTimeoutSeconds));
            _downloadRetryAttempts = _settings.DownloadRetryAttempts;
            OnPropertyChanged(nameof(DownloadRetryAttempts));
            _perHostConcurrency = _settings.PerHostConcurrency;
            OnPropertyChanged(nameof(PerHostConcurrency));
            ApplyNetworkSettings(_settings);
            _selectedLibrarySort = LibrarySortChoices.First(option => option.Value == _settings.LibrarySortOrder);
            OnPropertyChanged(nameof(SelectedLibrarySort));
            _defaultDownloadPreset = DownloadPresetChoices.First(option =>
                option.Value == DownloadPresetFromSettings(_settings.DefaultDownloadPreset));
            _selectedDownloadPreset = _defaultDownloadPreset;
            OnPropertyChanged(nameof(DefaultDownloadPreset));
            OnPropertyChanged(nameof(SelectedDownloadPreset));
            _showAdvancedDownloadOptions = _settings.ShowAdvancedDownloadOptions;
            OnPropertyChanged(nameof(ShowAdvancedDownloadOptions));
            ShowResponsibleUseNotice = !_settings.ResponsibleUseAccepted;
        }
        else
        {
            ErrorMessage = $"{settingsResult.Error!.Message} ({settingsResult.Error.Code})";
            SettingsStatus = "Settings unavailable; defaults remain active.";
            ShowResponsibleUseNotice = true;
        }

        var queueResult = await queueTask;
        if (!queueResult.IsSuccess)
        {
            _queueUnavailable = true;
            ErrorMessage = $"{queueResult.Error!.Message} ({queueResult.Error.Code})";
            StatusMessage = "Queue recovery unavailable";
        }
        else
        {
            _queueSnapshot = queueResult.Value;
            RebuildQueueItems();
            var recoverableCount = _queueSnapshot.Items.Count(item =>
                item.Status is DownloadQueueStatus.Queued or DownloadQueueStatus.Paused);
            if (recoverableCount > 0)
            {
                StatusMessage = recoverableCount == 1
                    ? "Recovered 1 interrupted download"
                    : $"Recovered {recoverableCount} interrupted downloads";
            }
        }

        var historyResult = await historyTask;
        if (!historyResult.IsSuccess)
        {
            _historyUnavailable = true;
            ErrorMessage = $"{historyResult.Error!.Message} ({historyResult.Error.Code})";
            NotifyHistoryProperties();
        }
        else
        {
            _historySnapshot = historyResult.Value;
            RebuildHistoryItems();
        }

        var archiveResult = await archiveTask;
        if (!archiveResult.IsSuccess)
        {
            ArchiveStatus = $"{archiveResult.Error!.Message} ({archiveResult.Error.Code})";
        }
        else
        {
            _archiveSnapshot = archiveResult.Value;
            RebuildArchiveProfiles();
        }

        if (!_queueUnavailable)
        {
            PumpQueue();
        }

        // Installers downloaded by earlier updates are never needed again and are large enough to
        // matter; this runs off the UI thread and is best-effort.
        _ = Task.Run(() => GitHubUpdateClient.PruneObsoleteInstallers(_updateDirectory, _currentVersion));

        if (!ShowResponsibleUseNotice && EnableAutomaticUpdateChecks)
        {
            _ = CheckForUpdatesAsync(isAutomatic: true);
        }
    }

    private async Task SaveSettingsAsync()
    {
        if (!FileNameTemplate.IsValid(FileNameTemplateText))
        {
            ErrorMessage = "The filename template contains an unknown token or unmatched brace. (FileName.InvalidTemplate)";
            SettingsStatus = "Filename template was not saved.";
            return;
        }

        TubeForgeSettings next;
        try
        {
            if (!Path.IsPathFullyQualified(DownloadFolder))
            {
                throw new ArgumentException("Download folder must be an absolute path.");
            }

            var proxyMode = SelectedProxyMode.Value ?? NetworkProxyMode.System;
            if (proxyMode == NetworkProxyMode.Manual &&
                !NetworkProxyPolicy.TryParseManualUri(ManualProxyUri, out _))
            {
                ErrorMessage = "Enter an HTTP or HTTPS proxy endpoint without credentials, path, query, or fragment. (Settings.InvalidProxy)";
                SettingsStatus = "Proxy settings were not saved.";
                return;
            }

            next = _settings with
            {
                DownloadFolder = Path.GetFullPath(DownloadFolder),
                MaximumConcurrentDownloads = SelectedMaxConcurrentDownloads,
                FileNameTemplate = FileNameTemplateText,
                IncludeQualityInFileName = IncludeQualityInFileName,
                EnableAcceleratedTransfers = EnableAcceleratedTransfers,
                EnableAutomaticUpdateChecks = EnableAutomaticUpdateChecks,
                ProxyMode = proxyMode,
                ManualProxyUri = proxyMode == NetworkProxyMode.Manual ? ManualProxyUri.Trim() : string.Empty,
                MetadataTimeoutSeconds = MetadataTimeoutSeconds,
                DownloadRetryAttempts = DownloadRetryAttempts,
                PerHostConcurrency = PerHostConcurrency,
                LibrarySortOrder = SelectedLibrarySort.Value ?? LibrarySortOrder.NewestFirst,
                DefaultDownloadPreset = DownloadPresetToSettings(DefaultDownloadPreset.Value),
                ShowAdvancedDownloadOptions = ShowAdvancedDownloadOptions
            };
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ErrorMessage = "The download folder setting is invalid. (Settings.InvalidState)";
            SettingsStatus = exception.GetType().Name;
            return;
        }

        var result = await _settingsStore.SaveAsync(next);
        if (!result.IsSuccess)
        {
            ErrorMessage = $"{result.Error!.Message} ({result.Error.Code})";
            SettingsStatus = "Settings were not saved.";
            return;
        }

        _settings = next;
        ApplyNetworkSettings(next);
        ErrorMessage = string.Empty;
        SettingsStatus = "Settings saved locally and applied to new requests.";
    }

    private void ApplyNetworkSettings(TubeForgeSettings settings)
    {
        _networkProxy.Update(NetworkProxyConfiguration.FromSettings(settings));
        _resolver = new YouTubeMetadataResolver(
            _httpClient,
            TimeSpan.FromSeconds(settings.MetadataTimeoutSeconds));
        _downloader = new DirectDownloadEngine(
            _httpClient,
            maximumAttempts: settings.DownloadRetryAttempts);
        _adaptiveDownloader = new AdaptiveDownloadEngine(_downloader, _mediaProcessor);
        _hostRequestGate = new HostRequestGate(settings.PerHostConcurrency);
        _rateLimitedRequests = new RateLimitedRequestExecutor(_hostRequestGate);
    }

    private async Task AcceptResponsibleUseAsync()
    {
        if (!FileNameTemplate.IsValid(FileNameTemplateText))
        {
            ErrorMessage = "The filename template contains an unknown token or unmatched brace. (FileName.InvalidTemplate)";
            return;
        }

        TubeForgeSettings accepted;
        try
        {
            if (!Path.IsPathFullyQualified(DownloadFolder))
            {
                throw new ArgumentException("Download folder must be an absolute path.");
            }

            accepted = _settings with
            {
                DownloadFolder = Path.GetFullPath(DownloadFolder),
                MaximumConcurrentDownloads = SelectedMaxConcurrentDownloads,
                FileNameTemplate = FileNameTemplateText,
                IncludeQualityInFileName = IncludeQualityInFileName,
                EnableAcceleratedTransfers = EnableAcceleratedTransfers,
                EnableAutomaticUpdateChecks = EnableAutomaticUpdateChecks,
                LibrarySortOrder = SelectedLibrarySort.Value ?? LibrarySortOrder.NewestFirst,
                DefaultDownloadPreset = DownloadPresetToSettings(DefaultDownloadPreset.Value),
                ShowAdvancedDownloadOptions = ShowAdvancedDownloadOptions,
                ResponsibleUseAccepted = true
            };
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ErrorMessage = "The download folder setting is invalid. (Settings.InvalidState)";
            SettingsStatus = exception.GetType().Name;
            return;
        }

        var result = await _settingsStore.SaveAsync(accepted);
        if (!result.IsSuccess)
        {
            ErrorMessage = $"{result.Error!.Message} ({result.Error.Code})";
            return;
        }

        _settings = accepted;
        ShowResponsibleUseNotice = false;
        SettingsStatus = "Responsible-use acknowledgement saved locally.";
        if (EnableAutomaticUpdateChecks)
        {
            _ = CheckForUpdatesAsync(isAutomatic: true);
        }
    }

    private void ShowPage(AppPage page)
    {
        if (_activePage == page)
        {
            return;
        }

        _activePage = page;
        OnPropertyChanged(nameof(IsDownloadPage));
        OnPropertyChanged(nameof(IsQueuePage));
        OnPropertyChanged(nameof(IsLibraryPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsDiagnosticsPage));
    }

    public async Task AnalyzeAsync()
    {
        CancelAnalysis();
        ClearAnalysis();
        var collectionReference = YouTubeCollectionUrlParser.Parse(UrlText);
        if (collectionReference.IsSuccess)
        {
            await AnalyzeCollectionAsync(collectionReference.Value);
            return;
        }

        var parsed = YouTubeUrlParser.ParseVideoId(UrlText);
        if (!parsed.IsSuccess)
        {
            _diagnosticsExtractionStage = "InputRejected";
            OnPropertyChanged(nameof(DiagnosticsExtractionStatus));
            ErrorMessage = $"{parsed.Error!.Message} ({parsed.Error.Code})";
            StatusMessage = "URL rejected";
            return;
        }

        _analysisCancellation = new CancellationTokenSource();
        IsAnalyzing = true;
        StatusMessage = "Analyzing video…";
        try
        {
            var result = await _rateLimitedRequests.ExecuteAsync(
                YouTubeOrigin,
                token => _resolver.ResolveAsync(parsed.Value, token),
                delay => StatusMessage = $"Rate limited · retrying in {FormatRateLimitDelay(delay)}",
                _analysisCancellation.Token);
            if (!result.IsSuccess)
            {
                _diagnosticsExtractionStage = "ResolutionFailed";
                OnPropertyChanged(nameof(DiagnosticsExtractionStatus));
                ErrorMessage = $"{result.Error!.Message} ({result.Error.Code})";
                StatusMessage = "Analysis failed";
                return;
            }

            _metadata = result.Value.Metadata;
            CaptionTracks = _metadata.CaptionTracks.Select(track => new CaptionTrackOption(track)).ToArray();
            SelectedCaptionTrack = CaptionTracks.FirstOrDefault(track => !track.Track.IsAutoGenerated) ??
                                   CaptionTracks.FirstOrDefault();
            _diagnosticsExtractionStage = result.Value.Diagnostics?.Stage ?? "WatchPageResolved";
            OnPropertyChanged(nameof(DiagnosticsExtractionStatus));
            _videoTitle = _metadata.Title;
            _videoChannel = _metadata.Channel;
            _videoDuration = _metadata.Duration;
            _trimStartText = "00:00:00";
            _trimEndText = _metadata.Duration is { } trimDuration
                ? FormatTimelineInput(trimDuration)
                : "00:00:00";
            OnPropertyChanged(nameof(TrimStartText));
            OnPropertyChanged(nameof(TrimEndText));
            _thumbnailUrl = _metadata.ThumbnailUrl;
            _allFormats = _metadata.Formats;
            RefreshDownloadModes();
            if (_allFormats.Any(format => format.IsLiveHls) &&
                _selectedDownloadPreset.Value != DownloadPresetKind.BestOriginal)
            {
                _selectedDownloadPreset = DownloadPresetChoices.First(option =>
                    option.Value == DownloadPresetKind.BestOriginal);
                OnPropertyChanged(nameof(SelectedDownloadPreset));
            }
            ApplyDownloadPreset(_selectedDownloadPreset);
            if (_selectedDownloadPreset.Value == DownloadPresetKind.Custom)
            {
                var defaultMode = DownloadModes.FirstOrDefault(choice =>
                    HasFormatsForMode(choice.Value)) ??
                    DownloadModes[0];
                if (SelectedDownloadMode != defaultMode)
                {
                    SelectedDownloadMode = defaultMode;
                }
                else
                {
                    RebuildFormatFilters(resetSelections: true);
                }
            }

            var extractionStage = result.Value.Diagnostics?.Stage;
            var extractionStatus = extractionStage == "AndroidClientResolved" ||
                                   extractionStage?.StartsWith("ClientResolved:", StringComparison.Ordinal) == true
                ? "DIRECT STREAMS VERIFIED"
                : "WATCH PAGE RESOLVED";
            ExtractionStatus = _metadata.ContentKind switch
            {
                VideoContentKind.Short => "SHORT · " + extractionStatus,
                VideoContentKind.LiveReplay => "LIVE REPLAY · " + extractionStatus,
                VideoContentKind.LiveActive => "LIVE · PUBLIC HLS READY",
                VideoContentKind.LiveUpcoming => "UPCOMING LIVE · WAIT MODE",
                _ => extractionStatus
            };
            StatusMessage = Formats.Count > 0
                ? "Choose a stream and download folder"
                : "Metadata found, but no downloadable stream was resolved";
            if (Formats.Count == 0)
            {
                ErrorMessage = "YouTube returned no directly downloadable formats. The player may have changed. (Extractor.NoStreams)";
            }

            NotifyVideoProperties();
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private async Task AnalyzeCollectionAsync(YouTubeCollectionReference source)
    {
        _analysisCancellation = new CancellationTokenSource();
        IsAnalyzing = true;
        StatusMessage = source.Kind == YouTubeCollectionKind.Playlist
            ? "Enumerating playlist…"
            : "Enumerating channel videos…";
        try
        {
            var result = await _rateLimitedRequests.ExecuteAsync(
                YouTubeOrigin,
                token => _collectionResolver.ResolveAsync(source, maximumItems: 1_000, token),
                delay => StatusMessage = $"Rate limited · retrying in {FormatRateLimitDelay(delay)}",
                _analysisCancellation.Token);
            if (!result.IsSuccess)
            {
                _diagnosticsExtractionStage = "CollectionResolutionFailed";
                OnPropertyChanged(nameof(DiagnosticsExtractionStatus));
                ErrorMessage = $"{result.Error!.Message} ({result.Error.Code})";
                StatusMessage = "Collection analysis failed";
                return;
            }

            _collection = result.Value;
            foreach (var item in _collection.Items)
            {
                CollectionItems.Add(new CollectionItemViewModel(item, CollectionSelectionChanged));
            }

            _diagnosticsExtractionStage = "CollectionResolved";
            OnPropertyChanged(nameof(DiagnosticsExtractionStatus));
            OnPropertyChanged(nameof(HasCollection));
            OnPropertyChanged(nameof(CollectionTitle));
            OnPropertyChanged(nameof(CollectionSummary));
            OnPropertyChanged(nameof(CollectionSelectionSummary));
            StatusMessage = CollectionItems.Count > 0
                ? "Choose collection items to queue"
                : "Collection contains no downloadable video entries";
            ProgressDetail = CollectionSummary;
            RefreshCommands();
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    public async Task DownloadAsync()
    {
        if (_metadata is null || SelectedFormat is null)
        {
            return;
        }

        await InitializeAsync();
        ErrorMessage = string.Empty;
        var selection = SelectedFormat;
        var format = selection.Format;
        var output = OutputProfileFor(selection);
        var caption = SelectedCaptionEmbedSelections();
        if (HasSelectedCaptionEmbeds && caption is null)
        {
            ErrorMessage = $"Select no more than {CaptionEmbedSelectionSet.MaximumTracks} unique caption tracks. (Queue.InvalidCaptionSelection)";
            StatusMessage = "Download not queued";
            return;
        }
        var embedChapters = EmbedChapters && CanEmbedChapters;
        var splitChapters = SplitChapters && CanSplitChapters;
        if (!TryGetLiveCaptureOptions(
                selection,
                output,
                caption,
                embedChapters,
                splitChapters,
                out var liveCapture,
                out var liveError))
        {
            ErrorMessage = $"{liveError!.Message} ({liveError.Code})";
            StatusMessage = "Download not queued";
            return;
        }

        MediaTrimRange? trim = null;
        SponsorBlockSelection? sponsorBlock = null;
        if (liveCapture is null && !TryGetSelectedTrim(out trim, out var trimError))
        {
            ErrorMessage = $"{trimError!.Message} ({trimError.Code})";
            StatusMessage = "Download not queued";
            return;
        }
        if (liveCapture is null &&
            !TryGetSponsorBlockSelection(output, out sponsorBlock, out var sponsorError))
        {
            ErrorMessage = $"{sponsorError!.Message} ({sponsorError.Code})";
            StatusMessage = "Download not queued";
            return;
        }
        try
        {
            var renderedName = RenderFileName(_metadata, selection, index: null, indexWidth: 2, output);
            if (!renderedName.IsSuccess)
            {
                ErrorMessage = $"{renderedName.Error!.Message} ({renderedName.Error.Code})";
                ProgressDetail = renderedName.Error.TechnicalDetail ?? string.Empty;
                StatusMessage = "Download not queued";
                return;
            }

            var destination = FileNamePolicy.AvailablePath(
                DownloadFolder,
                renderedName.Value,
                OutputExtension(selection, output),
                path => File.Exists(path) ||
                        (splitChapters && Directory.Exists(ChapterSplitDirectoryPath(path))) ||
                        File.Exists(path + ".part") ||
                        _queueSnapshot.Items.Any(item => item.DestinationPath.Equals(path, StringComparison.OrdinalIgnoreCase)) ||
                        _historySnapshot.Entries.Any(item => item.DestinationPath.Equals(path, StringComparison.OrdinalIgnoreCase)));
            var duplicate = DownloadDuplicateDetector.Find(
                SelectionIdentity(
                    _metadata,
                    selection,
                    output,
                    caption,
                    embedChapters,
                    splitChapters,
                    trim,
                    sponsorBlock,
                    liveCapture),
                destination,
                _queueSnapshot.Items,
                _historySnapshot.Entries);
            if (duplicate is not null)
            {
                ErrorMessage = $"This exact output is already queued or recorded in Library. Remove that record to download it again. (Queue.DuplicateOutput)";
                ProgressDetail = DuplicateDescription(duplicate.Kind);
                StatusMessage = "Duplicate not queued";
                return;
            }

            var queueItem = CreateQueueItem(
                _metadata,
                selection,
                destination,
                output,
                caption,
                embedChapters,
                splitChapters,
                trim,
                sponsorBlock,
                liveCapture);
            var queueError = await UpsertQueueItemAsync(queueItem);
            if (queueError is not null)
            {
                ErrorMessage = $"{queueError.Message} ({queueError.Code})";
                StatusMessage = "Download not queued";
                return;
            }

            if (sponsorBlock is null && liveCapture is null)
            {
                _preparedQueueWork[queueItem.Id] = new QueuedDownloadWork(
                    _metadata,
                    selection,
                    destination,
                    output,
                    caption,
                    embedChapters,
                    splitChapters,
                    trim);
            }
            StatusMessage = $"Queued: {Path.GetFileName(destination)}";
            ProgressDetail = $"Global concurrency: {SelectedMaxConcurrentDownloads}";
            PumpQueue();
        }
        catch (ArgumentException exception)
        {
            ErrorMessage = "The download folder or generated filename is invalid. (Queue.InvalidDestination)";
            ProgressDetail = exception.GetType().Name;
            StatusMessage = "Download not queued";
        }
        catch (IOException exception)
        {
            ErrorMessage = "TubeForge could not prepare the queued destination. (Queue.WriteFailed)";
            ProgressDetail = exception.GetType().Name;
            StatusMessage = "Download not queued";
        }
    }

    private async Task QueueSelectedCollectionAsync()
    {
        if (_collection is null)
        {
            return;
        }

        var selected = CollectionItems.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var configuration = _activeCollectionQueueConfiguration ?? CurrentCollectionQueueConfiguration();

        await InitializeAsync();
        CancelAnalysis();
        _analysisCancellation = new CancellationTokenSource();
        var cancellationToken = _analysisCancellation.Token;
        IsAnalyzing = true;
        ErrorMessage = string.Empty;
        var queued = 0;
        var skipped = 0;
        var failed = 0;
        var deferred = 0;
        var stoppedForRateLimit = false;
        var indexWidth = Math.Max(2, CollectionItems.Count.ToString().Length);
        try
        {
            for (var itemIndex = 0; itemIndex < selected.Length; itemIndex++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var card = selected[itemIndex];
                StatusMessage = $"Preparing collection item {itemIndex + 1} of {selected.Length}…";
                card.SetStatus("Resolving streams…");
                var resolved = await _rateLimitedRequests.ExecuteAsync(
                    YouTubeOrigin,
                    token => _resolver.ResolveAsync(card.Item.VideoId, token),
                    delay => card.SetStatus($"Rate limited · retrying in {FormatRateLimitDelay(delay)}"),
                    cancellationToken);
                if (!resolved.IsSuccess)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        card.SetStatus("Cancelled");
                        break;
                    }

                    card.SetStatus($"Failed · {resolved.Error!.Code}");
                    failed++;
                    if (resolved.Error.Code == "Network.RateLimited")
                    {
                        stoppedForRateLimit = true;
                        foreach (var remaining in selected.Skip(itemIndex + 1))
                        {
                            remaining.SetStatus("Deferred · rate limited");
                            deferred++;
                        }

                        break;
                    }

                    continue;
                }

                var metadata = resolved.Value.Metadata;
                var presetSelection = CollectionPresetSelection(metadata.Formats, configuration.OutputPreset);
                if (presetSelection is null)
                {
                    card.SetStatus("Failed · no complete output");
                    failed++;
                    continue;
                }

                var selection = presetSelection.Value.Selection;
                var output = presetSelection.Value.Output.ForVideoHeight(selection.Format.Height);
                var caption = CollectionCaptionSelection(metadata, selection, output, configuration.CaptionPreference);
                var embedChapters = configuration.EmbedChapters &&
                                    !output.IsAudioTranscode &&
                                    metadata.Chapters.Count > 0;

                try
                {
                    var position = card.Item.Index ?? itemIndex + 1;
                    var renderedName = RenderCollectionFileName(
                        metadata,
                        selection,
                        position,
                        indexWidth,
                        output,
                        configuration.FileNameTemplate,
                        configuration.IncludeQualityInFileName);
                    if (!renderedName.IsSuccess)
                    {
                        card.SetStatus($"Failed · {renderedName.Error!.Code}");
                        failed++;
                        continue;
                    }

                    var destination = FileNamePolicy.AvailablePath(
                        configuration.DestinationPath,
                        renderedName.Value,
                        OutputExtension(selection, output),
                        path => File.Exists(path) ||
                                File.Exists(path + ".part") ||
                                _queueSnapshot.Items.Any(existing => existing.DestinationPath.Equals(
                                    path,
                                    StringComparison.OrdinalIgnoreCase)) ||
                                _historySnapshot.Entries.Any(existing => existing.DestinationPath.Equals(
                                    path,
                                    StringComparison.OrdinalIgnoreCase)));
                    var duplicate = DownloadDuplicateDetector.Find(
                        SelectionIdentity(metadata, selection, output, caption, embedChapters),
                        destination,
                        _queueSnapshot.Items,
                        _historySnapshot.Entries);
                    if (duplicate is not null)
                    {
                        card.SetStatus($"Skipped · {DuplicateDescription(duplicate.Kind)}");
                        skipped++;
                        continue;
                    }

                    var queueItem = CreateQueueItem(
                        metadata,
                        selection,
                        destination,
                        output,
                        caption,
                        embedChapters);
                    var queueError = await UpsertQueueItemAsync(queueItem, cancellationToken);
                    if (queueError is not null)
                    {
                        card.SetStatus($"Failed · {queueError.Code}");
                        failed++;
                        continue;
                    }

                    card.SetStatus($"Queued · {Path.GetFileName(destination)}");
                    queued++;
                    PumpQueue();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  ArgumentException or NotSupportedException)
                {
                    card.SetStatus($"Failed · {exception.GetType().Name}");
                    failed++;
                }
            }

            StatusMessage = cancellationToken.IsCancellationRequested
                ? "Collection queue preparation cancelled"
                : stoppedForRateLimit
                    ? "YouTube rate limited collection preparation"
                    : $"Queued {queued} collection items";
            ProgressDetail = $"{queued} queued · {skipped} skipped · {failed} failed · {deferred} deferred";
            if (failed > 0)
            {
                ErrorMessage = stoppedForRateLimit
                    ? "TubeForge stopped bulk requests after repeated rate limits. Retry deferred items later. (Network.RateLimited)"
                    : "Some collection items could not be prepared. Review item statuses. (Collection.PartialFailure)";
            }
        }
        finally
        {
            _activeCollectionQueueConfiguration = null;
            IsAnalyzing = false;
        }
    }

    private CollectionQueueConfiguration CurrentCollectionQueueConfiguration() => new(
        DownloadFolder,
        FileNameTemplateText,
        IncludeQualityInFileName,
        SelectedArchiveOutputPreset.Value,
        SelectedArchiveCaptionPreference.Value,
        ArchiveEmbedChapters);

    private void SelectMissingCollection()
    {
        var present = _queueSnapshot.Items
            .Where(item => item.Status != DownloadQueueStatus.Cancelled)
            .Select(item => item.VideoId)
            .Concat(_historySnapshot.Entries.Select(entry => entry.VideoId))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in CollectionItems)
        {
            item.IsSelected = !present.Contains(item.Item.VideoId.Value);
        }

        CollectionSelectionChanged();
        StatusMessage = $"Selected {CollectionItems.Count(item => item.IsSelected)} items missing from Queue and Library";
    }

    private async Task SaveArchiveProfileAsync()
    {
        if (_collection is null)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var sourceUrl = _collection.Source.CanonicalUrl.AbsoluteUri;
            var existing = _archiveSnapshot.Profiles.FirstOrDefault(profile =>
                profile.SourceUrl.Equals(sourceUrl, StringComparison.OrdinalIgnoreCase));
            var profile = new CollectionArchiveProfile
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                SourceKind = _collection.Source.Kind,
                SourceUrl = sourceUrl,
                DisplayName = _collection.Title,
                DestinationPath = Path.GetFullPath(DownloadFolder),
                FileNameTemplate = FileNameTemplateText,
                IncludeQualityInFileName = IncludeQualityInFileName,
                OutputPreset = SelectedArchiveOutputPreset.Value,
                CaptionPreference = SelectedArchiveCaptionPreference.Value,
                EmbedChapters = ArchiveEmbedChapters,
                LastCheckedVideoIds = CollectionItems
                    .Select(item => item.Item.VideoId.Value)
                    .Distinct(StringComparer.Ordinal)
                    .Take(CollectionArchiveStore.MaximumCheckedItems)
                    .ToArray(),
                CreatedAtUtc = existing?.CreatedAtUtc ?? now,
                LastCheckedAtUtc = now
            };
            var next = new CollectionArchiveSnapshot
            {
                Profiles = _archiveSnapshot.Profiles
                    .Where(candidate => candidate.Id != profile.Id)
                    .Append(profile)
                    .OrderBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray()
            };
            var result = await _archiveStore.SaveAsync(next);
            if (!result.IsSuccess)
            {
                ArchiveStatus = $"{result.Error!.Message} ({result.Error.Code})";
                return;
            }

            _archiveSnapshot = next;
            RebuildArchiveProfiles(profile.Id);
            ArchiveStatus = existing is null
                ? $"Saved archive profile for {_collection.Title}."
                : $"Updated archive profile for {_collection.Title}.";
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ArchiveStatus = $"The archive destination is invalid. (Archive.InvalidDestination: {exception.GetType().Name})";
        }
    }

    private async Task RemoveArchiveProfileAsync()
    {
        if (SelectedArchiveProfile is null)
        {
            return;
        }

        var removedName = SelectedArchiveProfile.DisplayName;
        var next = new CollectionArchiveSnapshot
        {
            Profiles = _archiveSnapshot.Profiles
                .Where(profile => profile.Id != SelectedArchiveProfile.Id)
                .ToArray()
        };
        var result = await _archiveStore.SaveAsync(next);
        if (!result.IsSuccess)
        {
            ArchiveStatus = $"{result.Error!.Message} ({result.Error.Code})";
            return;
        }

        _archiveSnapshot = next;
        RebuildArchiveProfiles();
        ArchiveStatus = $"Removed archive profile for {removedName}. Downloaded media and Library records were unchanged.";
    }

    private async Task CheckArchiveProfilesAsync()
    {
        var profiles = _archiveSnapshot.Profiles.ToArray();
        var updated = _archiveSnapshot.Profiles.ToDictionary(profile => profile.Id);
        var totalQueued = 0;
        var checkedProfiles = 0;
        foreach (var profile in profiles)
        {
            var parsed = YouTubeCollectionUrlParser.Parse(profile.SourceUrl);
            if (!parsed.IsSuccess)
            {
                ArchiveStatus = $"Could not parse saved source for {profile.DisplayName}. ({parsed.Error!.Code})";
                continue;
            }

            CancelAnalysis();
            _analysisCancellation = new CancellationTokenSource();
            IsAnalyzing = true;
            ArchiveStatus = $"Checking {profile.DisplayName} for new items…";
            Result<YouTubeCollectionResult> result;
            try
            {
                result = await _rateLimitedRequests.ExecuteAsync(
                    YouTubeOrigin,
                    token => _collectionResolver.ResolveAsync(parsed.Value, maximumItems: 1_000, token),
                    delay => ArchiveStatus = $"Rate limited · retrying in {FormatRateLimitDelay(delay)}",
                    _analysisCancellation.Token);
            }
            finally
            {
                IsAnalyzing = false;
            }
            if (!result.IsSuccess)
            {
                ArchiveStatus = $"{result.Error!.Message} ({result.Error.Code})";
                if (result.Error.Code == "Network.RateLimited")
                {
                    break;
                }

                continue;
            }

            var known = profile.LastCheckedVideoIds.ToHashSet(StringComparer.Ordinal);
            var candidates = result.Value.Items.Where(item => !known.Contains(item.VideoId.Value)).ToArray();
            _collection = result.Value;
            CollectionItems.Clear();
            foreach (var item in candidates)
            {
                CollectionItems.Add(new CollectionItemViewModel(item, CollectionSelectionChanged));
            }
            NotifyCollectionProperties();
            checkedProfiles++;
            if (candidates.Length == 0)
            {
                updated[profile.Id] = profile with { LastCheckedAtUtc = DateTimeOffset.UtcNow };
                continue;
            }

            _activeCollectionQueueConfiguration = new CollectionQueueConfiguration(
                profile.DestinationPath,
                profile.FileNameTemplate,
                profile.IncludeQualityInFileName,
                profile.OutputPreset,
                profile.CaptionPreference,
                profile.EmbedChapters);
            await QueueSelectedCollectionAsync();
            var handled = CollectionItems
                .Where(item => item.Status.StartsWith("Queued", StringComparison.Ordinal) ||
                               item.Status.StartsWith("Skipped", StringComparison.Ordinal))
                .Select(item => item.Item.VideoId.Value)
                .ToArray();
            totalQueued += CollectionItems.Count(item => item.Status.StartsWith("Queued", StringComparison.Ordinal));
            updated[profile.Id] = profile with
            {
                LastCheckedVideoIds = profile.LastCheckedVideoIds
                    .Concat(handled)
                    .Distinct(StringComparer.Ordinal)
                    .TakeLast(CollectionArchiveStore.MaximumCheckedItems)
                    .ToArray(),
                LastCheckedAtUtc = DateTimeOffset.UtcNow
            };
        }

        var next = new CollectionArchiveSnapshot
        {
            Profiles = updated.Values
                .OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray()
        };
        var saved = await _archiveStore.SaveAsync(next);
        if (!saved.IsSuccess)
        {
            ArchiveStatus = $"Queue preparation finished, but profile checkpoints could not be saved. ({saved.Error!.Code})";
            return;
        }

        _archiveSnapshot = next;
        RebuildArchiveProfiles(SelectedArchiveProfile?.Id);
        ArchiveStatus = $"Checked {checkedProfiles} archive profiles; queued {totalQueued} new items.";
    }

    private void RebuildArchiveProfiles(Guid? selectedId = null)
    {
        ArchiveProfiles.Clear();
        foreach (var profile in _archiveSnapshot.Profiles)
        {
            ArchiveProfiles.Add(profile);
        }

        SelectedArchiveProfile = ArchiveProfiles.FirstOrDefault(profile => profile.Id == selectedId) ??
                                 ArchiveProfiles.FirstOrDefault();
        CheckArchiveProfilesCommand.RaiseCanExecuteChanged();
        RemoveArchiveProfileCommand.RaiseCanExecuteChanged();
    }

    private void NotifyCollectionProperties()
    {
        OnPropertyChanged(nameof(HasCollection));
        OnPropertyChanged(nameof(CollectionTitle));
        OnPropertyChanged(nameof(CollectionSummary));
        OnPropertyChanged(nameof(CollectionSelectionSummary));
        RefreshCommands();
    }

    private static (FormatItemViewModel Selection, OutputProfile Output)? CollectionPresetSelection(
        IReadOnlyList<StreamFormat> formats,
        ArchiveOutputPreset preset)
    {
        if (preset == ArchiveOutputPreset.Mp3_320)
        {
            var audio = formats
                .Where(format => format.Kind == StreamKind.AudioOnly)
                .OrderByDescending(format => format.Bitrate ?? 0)
                .ThenByDescending(format => format.AudioSampleRate ?? 0)
                .ThenBy(format => format.FormatId)
                .FirstOrDefault();
            return audio is null ? null : (new FormatItemViewModel(audio), OutputProfile.Mp3(320));
        }

        var eligible = formats;
        if (preset == ArchiveOutputPreset.SmallFile)
        {
            var bounded = formats
                .Where(format => !format.HasVideo || format.Height is > 0 and <= 720)
                .ToArray();
            if (bounded.Any(format => format.HasVideo))
            {
                eligible = bounded;
            }
        }

        var selection = BestCompleteFileSelection(eligible);
        if (selection is null)
        {
            return null;
        }

        var output = preset switch
        {
            ArchiveOutputPreset.WindowsCompatibleMp4 => OutputProfile.H264AacMp4,
            ArchiveOutputPreset.SmallFile => OutputProfile.H265AacMp4,
            _ => OutputProfile.Native
        };
        return (selection, output);
    }

    private static CaptionEmbedSelectionSet? CollectionCaptionSelection(
        VideoMetadata metadata,
        FormatItemViewModel selection,
        OutputProfile output,
        ArchiveCaptionPreference preference)
    {
        if (preference == ArchiveCaptionPreference.None || output.IsAudioTranscode ||
            !IsCaptionContainerSupported(FinalMediaContainer(selection, output)))
        {
            return null;
        }

        var tracks = metadata.CaptionTracks
            .Where(candidate => preference == ArchiveCaptionPreference.ManualOrAutomatic ||
                                !candidate.IsAutoGenerated)
            .Select(track => new CaptionEmbedSelection(track.LanguageCode, track.IsAutoGenerated))
            .Where(track => track.IsValid)
            .DistinctBy(track => track.Identity, StringComparer.Ordinal)
            .Take(CaptionEmbedSelectionSet.MaximumTracks)
            .ToArray();
        if (!CaptionEmbedSelectionSet.TryCreate(tracks, out var captions))
        {
            return null;
        }

        return captions;
    }

    private static FormatItemViewModel? BestCompleteFileSelection(IReadOnlyList<StreamFormat> formats)
    {
        var audio = formats
            .Where(format => format.Kind == StreamKind.AudioOnly)
            .OrderBy(format => format.IsDrc)
            .ThenByDescending(format => format.IsOriginalAudio)
            .ThenByDescending(format => format.Bitrate ?? 0)
            .ThenByDescending(format => format.AudioSampleRate ?? 0)
            .ThenBy(format => format.FormatId)
            .ToArray();
        var adaptiveVideos = formats
            .Where(format => format.Kind == StreamKind.VideoOnly &&
                             audio.Any(candidate => AdaptiveFormatSelector.AreMkvMuxCompatible(format, candidate)))
            .OrderByDescending(format => format.Height ?? 0)
            .ThenByDescending(format => format.FramesPerSecond ?? 0)
            .ThenByDescending(format => format.IsHdr)
            .ThenByDescending(format => format.Container == MediaContainer.Mp4)
            .ThenByDescending(format => format.Bitrate ?? 0)
            .ThenBy(format => format.FormatId);
        foreach (var video in adaptiveVideos)
        {
            var companion = AdaptiveFormatSelector.SelectCompanionAudio(video, audio);
            if (companion is not null)
            {
                return new FormatItemViewModel(video, companion);
            }
        }

        var progressive = formats
            .Where(format => format.Kind == StreamKind.Progressive)
            .OrderByDescending(format => format.Height ?? 0)
            .ThenByDescending(format => format.FramesPerSecond ?? 0)
            .ThenByDescending(format => format.IsHdr)
            .ThenByDescending(format => format.Container == MediaContainer.Mp4)
            .ThenByDescending(format => format.Bitrate ?? 0)
            .ThenBy(format => format.FormatId)
            .FirstOrDefault();
        return progressive is null ? null : new FormatItemViewModel(progressive);
    }

    private async Task DownloadCaptionAsync()
    {
        if (_metadata is null || SelectedCaptionTrack is null)
        {
            return;
        }

        IsSavingCaption = true;
        ErrorMessage = string.Empty;
        try
        {
            var track = SelectedCaptionTrack.Track;
            var kind = track.IsAutoGenerated ? " auto" : string.Empty;
            var destination = FileNamePolicy.AvailablePath(
                DownloadFolder,
                $"{_metadata.Title} [{track.LanguageCode}{kind}]",
                SelectedCaptionFormat.Extension,
                path => File.Exists(path) || File.Exists(path + ".part"));
            var result = await _captionDownloader.DownloadAsync(new CaptionDownloadRequest
            {
                SourceUrl = track.Url,
                DestinationPath = destination,
                OutputFormat = SelectedCaptionFormat.Value
            });
            if (!result.IsSuccess)
            {
                ErrorMessage = $"{result.Error!.Message} ({result.Error.Code})";
                StatusMessage = "Caption save failed";
                return;
            }

            StatusMessage = $"Saved caption: {Path.GetFileName(result.Value.DestinationPath)}";
            ProgressDetail = $"{result.Value.CueCount} cues · {SelectedCaptionFormat.Label}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            ErrorMessage = "The caption destination is invalid or unavailable. (Caption.InvalidDestination)";
            ProgressDetail = exception.GetType().Name;
            StatusMessage = "Caption save failed";
        }
        finally
        {
            IsSavingCaption = false;
        }
    }

    private async Task SaveThumbnailAsync()
    {
        if (_metadata?.ThumbnailUrl is not { } thumbnailUrl)
        {
            return;
        }

        IsSavingSidecar = true;
        ErrorMessage = string.Empty;
        try
        {
            var extension = ThumbnailDownloadEngine.FileExtensionFor(thumbnailUrl);
            var destination = FileNamePolicy.AvailablePath(
                DownloadFolder,
                $"{_metadata.Title}.thumbnail",
                extension,
                path => File.Exists(path) || File.Exists(path + ".part"));
            var result = await _thumbnailDownloader.DownloadAsync(new ThumbnailDownloadRequest
            {
                SourceUrl = thumbnailUrl,
                DestinationPath = destination
            });
            if (!result.IsSuccess)
            {
                ErrorMessage = $"{result.Error!.Message} ({result.Error.Code})";
                StatusMessage = "Thumbnail save failed";
                return;
            }

            StatusMessage = $"Saved thumbnail: {Path.GetFileName(result.Value.DestinationPath)}";
            ProgressDetail = $"{result.Value.MediaType} · {FormatBytes(result.Value.BytesWritten)}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            ErrorMessage = "The thumbnail destination is invalid or unavailable. (Thumbnail.InvalidDestination)";
            ProgressDetail = exception.GetType().Name;
            StatusMessage = "Thumbnail save failed";
        }
        finally
        {
            IsSavingSidecar = false;
        }
    }

    private async Task SaveMetadataAsync()
    {
        if (_metadata is null)
        {
            return;
        }

        IsSavingSidecar = true;
        ErrorMessage = string.Empty;
        try
        {
            var destination = FileNamePolicy.AvailablePath(
                DownloadFolder,
                $"{_metadata.Title}.info",
                "json",
                path => File.Exists(path) || File.Exists(path + ".part"));
            var result = await MetadataSidecarWriter.WriteAsync(_metadata, destination);
            if (!result.IsSuccess)
            {
                ErrorMessage = $"{result.Error!.Message} ({result.Error.Code})";
                StatusMessage = "Metadata save failed";
                return;
            }

            StatusMessage = $"Saved metadata: {Path.GetFileName(result.Value.DestinationPath)}";
            ProgressDetail = $"JSON · {FormatBytes(result.Value.BytesWritten)} · stream URLs excluded";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException)
        {
            ErrorMessage = "The metadata destination is invalid or unavailable. (Sidecar.InvalidDestination)";
            ProgressDetail = exception.GetType().Name;
            StatusMessage = "Metadata save failed";
        }
        finally
        {
            IsSavingSidecar = false;
        }
    }

    public void Cancel() => PauseAll();

    public string BuildDiagnosticReport() => RedactedDiagnosticReportBuilder.Build(new DiagnosticReportInput
    {
        GeneratedAtUtc = DateTimeOffset.UtcNow,
        ApplicationVersion = ApplicationVersion,
        RuntimeDescription = RuntimeDescription,
        ProcessArchitecture = ProcessArchitecture,
        ExtractionStage = DiagnosticsExtractionStatus,
        TotalFormats = _allFormats.Count,
        MatchingOutputs = Formats.Count,
        ActiveQueueItems = _queueSnapshot.Items.Count(item => item.Status == DownloadQueueStatus.Downloading),
        WaitingQueueItems = _queueSnapshot.Items.Count(item => item.Status == DownloadQueueStatus.Queued),
        PausedQueueItems = _queueSnapshot.Items.Count(item => item.Status == DownloadQueueStatus.Paused),
        CompletedQueueItems = _queueSnapshot.Items.Count(item => item.Status == DownloadQueueStatus.Completed),
        FailedQueueItems = _queueSnapshot.Items.Count(item => item.Status == DownloadQueueStatus.Failed),
        CancelledQueueItems = _queueSnapshot.Items.Count(item => item.Status == DownloadQueueStatus.Cancelled),
        ProxyMode = _settings.ProxyMode.ToString()
    });

    public void SetDiagnosticsStatus(string status) => DiagnosticsStatus = status;

    public async Task<bool> StartReadyUpdateAsync()
    {
        var ready = _readyUpdate;
        if (ready is null)
        {
            HasUpdateError = true;
            UpdateProgressStage = "Update stopped safely";
            UpdateStatus = "Download and verify an update before installing it.";
            return false;
        }

        try
        {
            SetUpdateProgress(0.82, "Final safety check");
            UpdateStatus = $"Rechecking TubeForge {ready.Version.ToString(3)} before it can run…";
            var installerPath = Path.GetFullPath(ready.InstallerPath);
            var expectedDirectory = Path.GetFullPath(_updateDirectory);
            if (!string.Equals(
                    Path.GetDirectoryName(installerPath),
                    expectedDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Update installer escaped its staging directory.");
            }

            var info = new FileInfo(installerPath);
            if (!info.Exists || info.Length != ready.BytesWritten)
            {
                throw new IOException("Update installer size changed after verification.");
            }

            var hashProgress = new Progress<double>(value =>
                SetUpdateProgress(0.82 + (value * 0.16), "Final safety check"));
            var hash = await ComputeFileSha256Async(installerPath, ready.BytesWritten, hashProgress);
            if (!hash.Equals(ready.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Update installer hash changed after verification.");
            }

            SetUpdateProgress(0.99, "Starting installer");
            var start = CreateUpdateInstallerStartInfo(installerPath, Environment.ProcessId);
            _ = Process.Start(start) ?? throw new IOException("The verified update installer did not start.");
            SetUpdateProgress(1, "Installer started");
            UpdateStatus = $"TubeForge {ready.Version.ToString(3)} verified. Closing this version to install and relaunch…";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          InvalidOperationException or Win32Exception)
        {
            HasUpdateError = true;
            UpdateProgressStage = "Update stopped safely";
            UpdateStatus = $"Update install failed safely ({exception.GetType().Name}).";
            return false;
        }
    }

    private async Task CheckForUpdatesAsync(bool isAutomatic)
    {
        UpdateRelease? detectedUpdate = null;
        HasUpdateError = false;
        IsCheckingForUpdate = true;
        UpdateProgressStage = "Checking for updates";
        UpdateStatus = "Checking GitHub for a verified stable release…";
        try
        {
            var result = await _updateClient.CheckForUpdateAsync(_currentVersion);
            if (!result.IsSuccess)
            {
                HasUpdateError = true;
                UpdateProgressStage = "Check unavailable";
                UpdateStatus = isAutomatic
                    ? "Automatic update check unavailable; use Check now to retry."
                    : $"Update check failed safely ({result.Error!.Code}).";
                return;
            }

            _availableUpdate = result.Value;
            _readyUpdate = null;
            UpdateDownloadFraction = 0;
            UpdateProgressStage = _availableUpdate is null ? "Up to date" : "Ready to update";
            UpdateStatus = _availableUpdate is null
                ? $"TubeForge {_currentVersion.ToString(3)} is up to date."
                : $"TubeForge {_availableUpdate.Version.ToString(3)} is available and ready to download.";
            detectedUpdate = _availableUpdate;
            NotifyUpdateProperties();
        }
        finally
        {
            IsCheckingForUpdate = false;
        }

        if (detectedUpdate is not null)
        {
            UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs(detectedUpdate.Version));
        }
    }

    private async Task UpdateNowAsync()
    {
        var installerStarted = false;
        HasUpdateError = false;
        UpdateDownloadFraction = 0;
        UpdateProgressStage = "Preparing update";
        UpdateStatus = "Preparing secure update…";
        IsUpdateInProgress = true;
        try
        {
            if (_readyUpdate is null && !await DownloadUpdateAsync())
            {
                return;
            }

            if (await StartReadyUpdateAsync())
            {
                installerStarted = true;
                UpdateInstallerStarted?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            if (!installerStarted)
            {
                IsUpdateInProgress = false;
            }
        }
    }

    private async Task<bool> DownloadUpdateAsync()
    {
        var release = _availableUpdate;
        if (release is null)
        {
            return false;
        }

        IsDownloadingUpdate = true;
        SetUpdateProgress(0.02, "Download + release checks");
        UpdateStatus = $"Downloading and validating official TubeForge {release.Version.ToString(3)} installer…";
        try
        {
            var progress = new Progress<double>(value =>
                SetUpdateProgress(0.02 + (Math.Clamp(value, 0, 1) * 0.78), "Download + release checks"));
            var result = await _updateClient.DownloadInstallerAsync(release, _updateDirectory, progress);
            if (!result.IsSuccess)
            {
                HasUpdateError = true;
                UpdateProgressStage = "Update stopped safely";
                UpdateStatus = $"Update download rejected safely ({result.Error!.Code}).";
                return false;
            }

            _readyUpdate = result.Value;
            SetUpdateProgress(0.82, "Installer downloaded and verified");
            UpdateStatus = $"TubeForge {release.Version.ToString(3)} download verified.";
            NotifyUpdateProperties();
            return true;
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    private void NotifyUpdateProperties()
    {
        OnPropertyChanged(nameof(HasUpdateAvailable));
        OnPropertyChanged(nameof(IsUpdateReady));
        OnPropertyChanged(nameof(IsUpdateActionAvailable));
        OnPropertyChanged(nameof(AvailableUpdateVersion));
        OnPropertyChanged(nameof(AvailableUpdateSummary));
        RefreshCommands();
    }

    private void SetUpdateProgress(double value, string stage)
    {
        var clamped = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        if (clamped >= UpdateDownloadFraction)
        {
            UpdateDownloadFraction = clamped;
        }

        UpdateProgressStage = stage;
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        long expectedLength,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedLength)
        {
            throw new IOException("Update installer size changed before verification.");
        }

        progress?.Report(0);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total = checked(total + read);
            if (total > expectedLength)
            {
                throw new IOException("Update installer grew during verification.");
            }

            hash.AppendData(buffer.AsSpan(0, read));
            progress?.Report(expectedLength == 0 ? 1 : (double)total / expectedLength);
        }

        if (total != expectedLength)
        {
            throw new IOException("Update installer size changed during verification.");
        }

        progress?.Report(1);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static ProcessStartInfo CreateUpdateInstallerStartInfo(string installerPath, int waitProcessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        if (waitProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(waitProcessId));
        }

        var start = new ProcessStartInfo
        {
            FileName = installerPath,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(installerPath))!,
            UseShellExecute = true
        };
        start.ArgumentList.Add("/update");
        start.ArgumentList.Add("/quiet");
        start.ArgumentList.Add("/wait-pid");
        start.ArgumentList.Add(waitProcessId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("/launch");
        return start;
    }

    public void Dispose()
    {
        CancelAnalysis();
        PauseAll();
        _updateHttpClient.Dispose();
        _sidecarHttpClient.Dispose();
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private void PumpQueue()
    {
        while (_queueDispatcher.ActiveCount < _queueDispatcher.MaximumConcurrency)
        {
            var next = _queueSnapshot.Items.FirstOrDefault(item =>
                item.Status == DownloadQueueStatus.Queued &&
                !_queueDispatcher.IsActive(item.Id) &&
                !_undispatchableQueueItems.Contains(item.Id));
            if (next is null || !_queueDispatcher.TryStart(next.Id))
            {
                break;
            }

            IsDownloading = true;
            NotifyQueueProperties();
            _ = RunQueueItemAsync(next.Id);
        }
    }

    private async Task RunQueueItemAsync(Guid itemId)
    {
        var cancellation = new CancellationTokenSource();
        _downloadCancellations[itemId] = cancellation;
        try
        {
            var startError = await UpdateQueueItemAsync(
                itemId,
                DownloadQueueStatus.Downloading,
                failureCode: null,
                cancellationToken: cancellation.Token);
            if (startError is not null)
            {
                // The item is still persisted as Queued, so the pump would pick it straight back
                // up and spin. Park it until the user retries or the queue file becomes writable.
                _undispatchableQueueItems.Add(itemId);
                ReportQueueError(startError);
                return;
            }

            var prepared = await PrepareQueueWorkAsync(itemId, cancellation.Token);
            if (prepared.Error is not null || prepared.Work is null)
            {
                var status = prepared.Error?.Code == "Operation.Cancelled"
                    ? CancellationStatus(itemId)
                    : DownloadQueueStatus.Failed;
                await CompleteQueueRunAsync(itemId, status, prepared.Error, completedBytes: null);
                return;
            }

            var finalWork = prepared.Work;
            if (finalWork.LiveCapture is { } liveCapture)
            {
                await RunLiveCaptureAsync(itemId, finalWork, liveCapture, cancellation.Token);
                return;
            }

            var mediaDestination = finalWork.RequiresMetadataFinalization
                ? PostProcessMediaSourcePath(
                    finalWork.Destination,
                    finalWork.EmbedChapters || finalWork.SponsorBlock is { Mode: SponsorBlockMode.Chapters })
                : finalWork.Destination;
            var downloadDestination = finalWork.RequiresNativeTrim
                ? TrimSourcePath(mediaDestination)
                : mediaDestination;
            var work = finalWork with
            {
                Destination = downloadDestination,
                Captions = null,
                EmbedChapters = false
            };
            if ((finalWork.RequiresMetadataFinalization || finalWork.RequiresNativeTrim || finalWork.SplitChapters) &&
                File.Exists(finalWork.Destination))
            {
                var recoveredBytes = new FileInfo(finalWork.Destination).Length;
                if (finalWork.RequiresMetadataFinalization)
                {
                    var recovered = await FinalizeMetadataAsync(finalWork, mediaDestination, cancellation.Token);
                    if (recovered.Error is not null)
                    {
                        await CompleteQueueRunAsync(
                            itemId,
                            DownloadQueueStatus.Failed,
                            recovered.Error,
                            recovered.BytesWritten);
                        return;
                    }

                    recoveredBytes = recovered.BytesWritten;
                }
                else if (finalWork.RequiresNativeTrim)
                {
                    var recovered = await FinalizeNativeTrimAsync(
                        finalWork,
                        downloadDestination,
                        mediaDestination,
                        cancellation.Token);
                    if (recovered.Error is not null)
                    {
                        await CompleteQueueRunAsync(
                            itemId,
                            DownloadQueueStatus.Failed,
                            recovered.Error,
                            recovered.BytesWritten);
                        return;
                    }

                    recoveredBytes = recovered.BytesWritten;
                }

                if (finalWork.SplitChapters)
                {
                    var splitError = await SplitChaptersAsync(finalWork, cancellation.Token);
                    if (splitError is not null)
                    {
                        await CompleteQueueRunAsync(
                            itemId,
                            DownloadQueueStatus.Failed,
                            splitError,
                            recoveredBytes);
                        return;
                    }
                }

                await CompleteQueueRunAsync(
                    itemId,
                    DownloadQueueStatus.Completed,
                    error: null,
                    recoveredBytes);
                await RecordHistoryAsync(finalWork, recoveredBytes, CancellationToken.None);
                StatusMessage = "Completed: " + Path.GetFileName(finalWork.Destination);
                return;
            }

            var existingBytes = SelectionPartialLength(work.Destination, work.Selection, work.Output);
            var reservedBytes = SelectionReservedLength(work.Destination, work.Selection, work.Output);
            var diskForecast = DiskSpacePolicy.Check(
                work.Destination,
                ForecastLength(work),
                reservedBytes,
                !File.Exists(work.Destination) &&
                (work.Selection.RequiresMuxing ||
                 RequiresMp4Normalization(work.Selection) ||
                 work.Output.RequiresTranscode ||
                 finalWork.RequiresNativeTrim ||
                 finalWork.RequiresMetadataFinalization),
                finalWork.SplitChapters ? 1 : 0);
            if (!diskForecast.IsSuccess)
            {
                await CompleteQueueRunAsync(
                    itemId,
                    DownloadQueueStatus.Failed,
                    diskForecast.Error,
                    existingBytes);
                return;
            }

            var progress = new Progress<DownloadProgress>(value => UpdateQueueProgress(itemId, value));
            TubeForgeError? downloadError;
            long completedBytes;
            // The per-host slot exists to bound concurrent requests to one media host. Everything
            // after a track reaches disk — muxing, remuxing, transcoding — is local FFmpeg work,
            // so the slot is released at the end of each branch's network phase rather than being
            // held for the whole run. Releasing is idempotent, so the backstop below is safe.
            using var mediaLease = await _hostRequestGate.EnterAsync(
                work.Selection.Format.Url,
                cancellation.Token);
            var releaseTransferSlot = mediaLease.Dispose;
            if (work.Output.IsAudioTranscode)
            {
                var sourcePath = AudioSourcePath(work.Destination, work.Selection.Format);
                var sourceResult = await EnsureTrackDownloadedAsync(
                    TrackRequest(work.Metadata, work.Selection.Format, sourcePath),
                    progress,
                    cancellation.Token);
                if (!sourceResult.IsSuccess)
                {
                    downloadError = sourceResult.Error;
                    completedBytes = SelectionPartialLength(work.Destination, work.Selection, work.Output);
                }
                else
                {
                    releaseTransferSlot();
                    StatusMessage = work.Output.BitrateKbps > 0
                        ? $"Converting to {work.Output.DisplayName} · {work.Output.BitrateKbps} kbps"
                        : $"Converting to {work.Output.DisplayName}";
                    UpdateQueueProcessing(itemId, StatusMessage);
                    var transcodeResult = await _audioTranscoder.TranscodeAsync(new AudioTranscodeRequest
                    {
                        SourcePath = sourcePath,
                        DestinationPath = work.Destination,
                        Output = work.Output,
                        Trim = finalWork.Trim,
                        RemovedSegments = finalWork.RemovedSponsorSegments,
                        AllowExistingValidatedOutput = true
                    }, cancellation.Token);
                    downloadError = transcodeResult.Error;
                    completedBytes = transcodeResult.IsSuccess
                        ? transcodeResult.Value.BytesWritten
                        : SelectionPartialLength(work.Destination, work.Selection, work.Output);
                    if (transcodeResult.IsSuccess)
                    {
                        File.Delete(sourcePath);
                    }
                }
            }
            else if (work.Output.IsVideoTranscode)
            {
                var sourcePath = VideoSourcePath(work.Destination, work.Selection);
                TubeForgeError? sourceError = null;
                if (!File.Exists(work.Destination))
                {
                    StatusMessage = "Preparing source tracks for video conversion";
                    sourceError = await EnsureVideoTranscodeSourceAsync(
                        work,
                        sourcePath,
                        progress,
                        cancellation.Token);
                }

                if (sourceError is not null)
                {
                    downloadError = sourceError;
                    completedBytes = SelectionPartialLength(work.Destination, work.Selection, work.Output);
                }
                else
                {
                    releaseTransferSlot();
                    StatusMessage = $"Transcoding to {work.Output.DisplayName} · local FFmpeg";
                    UpdateQueueProcessing(itemId, StatusMessage);
                    var transcodeResult = await _videoTranscoder.TranscodeAsync(new VideoTranscodeRequest
                    {
                        SourcePath = sourcePath,
                        DestinationPath = work.Destination,
                        Output = work.Output,
                        Trim = finalWork.Trim,
                        RemovedSegments = finalWork.RemovedSponsorSegments,
                        AllowExistingValidatedOutput = true
                    }, cancellation.Token);
                    downloadError = transcodeResult.Error;
                    completedBytes = transcodeResult.IsSuccess
                        ? transcodeResult.Value.BytesWritten
                        : SelectionPartialLength(work.Destination, work.Selection, work.Output);
                    if (transcodeResult.IsSuccess && File.Exists(sourcePath))
                    {
                        File.Delete(sourcePath);
                    }
                }
            }
            else if (work.Selection.AudioFormat is StreamFormat audioFormat)
            {
                var outputContainer = work.Selection.OutputContainer;
                StatusMessage = outputContainer switch
                {
                    MediaContainer.Mp4 => "Finalizing compatible MP4 · stream copy, no quality loss",
                    MediaContainer.Mkv => "Muxing MKV tracks · stream copy, no quality loss",
                    _ => "Muxing WebM tracks · no quality loss"
                };
                var result = await _adaptiveDownloader.DownloadAsync(new AdaptiveDownloadRequest
                {
                    Video = TrackRequest(
                        work.Metadata,
                        work.Selection.Format,
                        IntermediateTrackPath(work.Destination, "video", work.Selection.Format)),
                    Audio = TrackRequest(
                        work.Metadata,
                        audioFormat,
                        IntermediateTrackPath(work.Destination, "audio", audioFormat)),
                    DestinationPath = work.Destination,
                    OutputContainer = outputContainer,
                    AllowExistingValidatedOutput = true
                }, progress, cancellation.Token, releaseTransferSlot);
                downloadError = result.Error;
                completedBytes = result.IsSuccess
                    ? result.Value.BytesWritten
                    : SelectionPartialLength(work.Destination, work.Selection, work.Output);
            }
            else if (RequiresMp4Normalization(work.Selection))
            {
                var sourcePath = IntermediateTrackPath(
                    work.Destination,
                    "video",
                    work.Selection.Format);
                if (File.Exists(work.Destination))
                {
                    releaseTransferSlot();
                    var recovered = await _mediaProcessor.RemuxMp4Async(
                        sourcePath,
                        work.Destination,
                        cancellation.Token,
                        allowExistingValidatedOutput: true);
                    downloadError = recovered.Error;
                    completedBytes = recovered.IsSuccess
                        ? recovered.Value.BytesWritten
                        : SelectionPartialLength(work.Destination, work.Selection, work.Output);
                    if (recovered.IsSuccess && File.Exists(sourcePath))
                    {
                        File.Delete(sourcePath);
                    }
                }
                else
                {
                    var sourceResult = await EnsureTrackDownloadedAsync(
                        TrackRequest(work.Metadata, work.Selection.Format, sourcePath),
                        progress,
                        cancellation.Token);
                    if (!sourceResult.IsSuccess)
                    {
                        downloadError = sourceResult.Error;
                        completedBytes = SelectionPartialLength(
                            work.Destination,
                            work.Selection,
                            work.Output);
                    }
                    else
                    {
                        releaseTransferSlot();
                        StatusMessage = "Finalizing compatible MP4 · stream copy, no quality loss";
                        var processResult = await _mediaProcessor.RemuxMp4Async(
                            sourcePath,
                            work.Destination,
                            cancellation.Token,
                            allowExistingValidatedOutput: true);
                        downloadError = processResult.Error;
                        completedBytes = processResult.IsSuccess
                            ? processResult.Value.BytesWritten
                            : SelectionPartialLength(work.Destination, work.Selection, work.Output);
                        if (processResult.IsSuccess)
                        {
                            File.Delete(sourcePath);
                        }
                    }
                }
            }
            else
            {
                var request = TrackRequest(work.Metadata, work.Selection.Format, work.Destination);
                var result = !finalWork.RequiresMetadataFinalization
                    ? await _downloader.DownloadAsync(request, progress, cancellation.Token)
                    : await EnsureTrackDownloadedAsync(request, progress, cancellation.Token);
                downloadError = result.Error;
                completedBytes = result.IsSuccess
                    ? result.Value.BytesWritten
                    : SelectionPartialLength(work.Destination, work.Selection, work.Output);
            }

            releaseTransferSlot();
            if (downloadError?.Code == "Network.RateLimited")
            {
                _hostRequestGate.Defer(work.Selection.Format.Url, downloadError.RetryAfter);
            }

            if (downloadError?.Code == DirectDownloadEngine.MediaUrlRejectedCode && await TryRequeueWithFreshLinksAsync(itemId))
            {
                return;
            }

            if (downloadError is not null)
            {
                var status = downloadError.Code == "Operation.Cancelled"
                    ? CancellationStatus(itemId)
                    : DownloadQueueStatus.Failed;
                await CompleteQueueRunAsync(itemId, status, downloadError, completedBytes);
                return;
            }

            if (finalWork.RequiresNativeTrim)
            {
                var trimmed = await FinalizeNativeTrimAsync(
                    finalWork,
                    work.Destination,
                    mediaDestination,
                    cancellation.Token);
                downloadError = trimmed.Error;
                completedBytes = trimmed.BytesWritten;
                if (downloadError is not null)
                {
                    var status = downloadError.Code == "Operation.Cancelled"
                        ? CancellationStatus(itemId)
                        : DownloadQueueStatus.Failed;
                    await CompleteQueueRunAsync(itemId, status, downloadError, completedBytes);
                    return;
                }
            }

            if (finalWork.RequiresMetadataFinalization)
            {
                var embedded = await FinalizeMetadataAsync(finalWork, mediaDestination, cancellation.Token);
                downloadError = embedded.Error;
                completedBytes = embedded.BytesWritten;
                if (downloadError is not null)
                {
                    var status = downloadError.Code == "Operation.Cancelled"
                        ? CancellationStatus(itemId)
                        : DownloadQueueStatus.Failed;
                    await CompleteQueueRunAsync(itemId, status, downloadError, completedBytes);
                    return;
                }
            }

            if (finalWork.SplitChapters)
            {
                var splitError = await SplitChaptersAsync(finalWork, cancellation.Token);
                if (splitError is not null)
                {
                    var status = splitError.Code == "Operation.Cancelled"
                        ? CancellationStatus(itemId)
                        : DownloadQueueStatus.Failed;
                    await CompleteQueueRunAsync(itemId, status, splitError, completedBytes);
                    return;
                }
            }

            await CompleteQueueRunAsync(
                itemId,
                DownloadQueueStatus.Completed,
                error: null,
                completedBytes);
            await RecordHistoryAsync(finalWork, completedBytes, CancellationToken.None);
            StatusMessage = "Completed: " + Path.GetFileName(finalWork.Destination);
        }
        catch (OperationCanceledException)
        {
            await CompleteQueueRunAsync(
                itemId,
                CancellationStatus(itemId),
                new TubeForgeError("Operation.Cancelled", "The download was paused."),
                QueuePartialLength(itemId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            await CompleteQueueRunAsync(
                itemId,
                DownloadQueueStatus.Failed,
                new TubeForgeError("Download.UnexpectedFailure", "The queued download failed.", exception.GetType().Name),
                QueuePartialLength(itemId));
        }
        catch (Exception exception)
        {
            await CompleteQueueRunAsync(
                itemId,
                DownloadQueueStatus.Failed,
                new TubeForgeError("Download.InternalFailure", "The queued download hit an internal failure.", exception.GetType().Name),
                QueuePartialLength(itemId));
        }
        finally
        {
            _preparedQueueWork.Remove(itemId);
            _cancelledQueueItems.Remove(itemId);
            _downloadCancellations.Remove(itemId);
            cancellation.Dispose();
            _queueDispatcher.Complete(itemId);
            IsDownloading = _queueDispatcher.ActiveCount > 0;
            NotifyQueueProperties();
            PumpQueue();
        }
    }

    private async Task<(QueuedDownloadWork? Work, TubeForgeError? Error)> PrepareQueueWorkAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        if (_preparedQueueWork.TryGetValue(itemId, out var prepared))
        {
            // Signed media URLs captured at analysis time expire. Reusing them for a queue that
            // has been sitting for hours produces an unrecoverable HTTP 403 partway through.
            if (HasUsableMediaUrls(prepared))
            {
                return (prepared, null);
            }

            _preparedQueueWork.Remove(itemId);
        }

        var item = _queueSnapshot.Items.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is null)
        {
            return (null, new TubeForgeError("Queue.ItemMissing", "The queued download no longer exists."));
        }

        if (!YouTubeVideoId.TryCreate(item.VideoId, out var videoId))
        {
            return (null, new TubeForgeError("Queue.InvalidVideoId", "The queued video ID is invalid."));
        }

        var resolved = await _rateLimitedRequests.ExecuteAsync(
            YouTubeOrigin,
            token => _resolver.ResolveAsync(videoId, token),
            cancellationToken: cancellationToken);
        if (!resolved.IsSuccess)
        {
            return (null, resolved.Error);
        }

        var primary = resolved.Value.Metadata.Formats.FirstOrDefault(format => format.FormatId == item.FormatId);
        if (primary is null)
        {
            return (null, new TubeForgeError(
                "Queue.FormatUnavailable",
                "The queued format is no longer available. Analyze the video again to choose another output."));
        }

        if (!DownloadSourceIdentity.TryParse(item.SourceIdentity, out var sourceIdentity) ||
            sourceIdentity.VideoId != videoId || sourceIdentity.PrimaryFormatId != item.FormatId)
        {
            return (null, new TubeForgeError("Queue.InvalidSourceIdentity", "The queued source identity is invalid."));
        }

        if (sourceIdentity.Output.IsAudioTranscode && primary.Kind != StreamKind.AudioOnly)
        {
            return (null, new TubeForgeError(
                "Queue.InvalidOutputProfile",
                "The queued audio conversion profile is valid only for audio-only media."));
        }

        if (sourceIdentity.Output.IsVideoTranscode && primary.Kind == StreamKind.AudioOnly)
        {
            return (null, new TubeForgeError(
                "Queue.InvalidOutputProfile",
                "The queued video conversion profile requires video and audio media."));
        }

        if (sourceIdentity.LiveCapture is { } liveCapture &&
            (!liveCapture.IsValid || !primary.IsLiveHls ||
             sourceIdentity.Output != OutputProfile.Native || sourceIdentity.AudioFormatId is not null ||
             sourceIdentity.Captions is not null || sourceIdentity.EmbedChapters ||
             sourceIdentity.SplitChapters || sourceIdentity.Trim is not null ||
             sourceIdentity.SponsorBlock is not null) ||
            sourceIdentity.LiveCapture is null && primary.IsLiveHls)
        {
            return (null, new TubeForgeError(
                "Queue.InvalidLiveCapture",
                "The queued live capture settings are invalid or no longer available."));
        }

        if (sourceIdentity.Captions is not null && primary.Kind == StreamKind.AudioOnly)
        {
            return (null, new TubeForgeError(
                "Queue.InvalidCaptionSelection",
                "Embedded captions require a video output."));
        }

        if (sourceIdentity.Captions is { } captions && captions.Selections.Any(caption =>
                !resolved.Value.Metadata.CaptionTracks.Any(track => CaptionMatches(track, caption))))
        {
            return (null, new TubeForgeError(
                "Queue.CaptionUnavailable",
                "The selected caption track is no longer available. Analyze the video again."));
        }

        if ((sourceIdentity.EmbedChapters || sourceIdentity.SplitChapters) && primary.Kind == StreamKind.AudioOnly)
        {
            return (null, new TubeForgeError(
                "Queue.InvalidChapterSelection",
                "Chapter media workflows require a video output."));
        }

        if ((sourceIdentity.EmbedChapters || sourceIdentity.SplitChapters) &&
            (resolved.Value.Metadata.Chapters.Count == 0 ||
             resolved.Value.Metadata.Duration is not { } duration || duration <= TimeSpan.Zero))
        {
            return (null, new TubeForgeError(
                "Queue.ChaptersUnavailable",
                "Chapter metadata is no longer available. Analyze the video again."));
        }

        if (sourceIdentity.Trim is { } trim &&
            (resolved.Value.Metadata.Duration is not { } mediaDuration || trim.End > mediaDuration))
        {
            return (null, new TubeForgeError(
                "Queue.InvalidTrim",
                "The queued trim range is outside the current media duration."));
        }

        if (sourceIdentity.SponsorBlock is { Mode: SponsorBlockMode.Remove } &&
            (!sourceIdentity.Output.RequiresTranscode || sourceIdentity.EmbedChapters ||
             sourceIdentity.SplitChapters))
        {
            return (null, new TubeForgeError(
                "Queue.InvalidSponsorBlockSelection",
                "SponsorBlock removal requires conversion without chapter workflows."));
        }

        if (sourceIdentity.SponsorBlock is { Mode: SponsorBlockMode.Chapters } &&
            (primary.Kind == StreamKind.AudioOnly ||
             resolved.Value.Metadata.Duration is not { } sponsorDuration || sponsorDuration <= TimeSpan.Zero))
        {
            return (null, new TubeForgeError(
                "Queue.InvalidSponsorBlockSelection",
                "SponsorBlock chapter markers require a timed video output."));
        }

        StreamFormat? audio = null;
        var audioFormatId = sourceIdentity.AudioFormatId;
        if (audioFormatId is not null)
        {
            audio = resolved.Value.Metadata.Formats.FirstOrDefault(format =>
                format.FormatId == audioFormatId && format.Kind == StreamKind.AudioOnly);
            if (audio is null || !AdaptiveFormatSelector.AreMkvMuxCompatible(primary, audio))
            {
                return (null, new TubeForgeError(
                    "Queue.AudioFormatUnavailable",
                    "The queued companion audio format is no longer available."));
            }
        }

        if (sourceIdentity.Output.IsVideoTranscode &&
            primary.Kind == StreamKind.VideoOnly &&
            audio is null)
        {
            return (null, new TubeForgeError(
                "Queue.AudioFormatUnavailable",
                "The queued video conversion requires a companion audio format."));
        }

        var selection = new FormatItemViewModel(primary, audio);
        IReadOnlyList<SponsorBlockSegment> sponsorSegments = [];
        if (sourceIdentity.SponsorBlock is { } sponsorBlock)
        {
            var sponsorResult = await _rateLimitedRequests.ExecuteAsync(
                SponsorBlockOrigin,
                token => _sponsorBlockClient.GetSegmentsAsync(videoId, sponsorBlock, token),
                cancellationToken: cancellationToken);
            if (!sponsorResult.IsSuccess)
            {
                return (null, sponsorResult.Error);
            }

            try
            {
                sponsorSegments = MediaTimelineEditor.NormalizeSponsorSegments(
                    sponsorResult.Value,
                    resolved.Value.Metadata.Duration ?? TimeSpan.Zero,
                    sourceIdentity.Trim);
                if (sponsorBlock.Mode == SponsorBlockMode.Remove && sponsorSegments.Count > 0)
                {
                    var removed = MediaTimelineEditor.MergeRemovalRanges(sponsorSegments)
                        .Aggregate(TimeSpan.Zero, (total, range) => total + range.Duration);
                    var outputDuration = sourceIdentity.Trim?.Duration ??
                        resolved.Value.Metadata.Duration ?? TimeSpan.Zero;
                    if (removed >= outputDuration)
                    {
                        return (null, new TubeForgeError(
                            "SponsorBlock.NoMediaRemaining",
                            "The selected SponsorBlock categories would remove the entire output."));
                    }
                }
            }
            catch (ArgumentException exception)
            {
                return (null, new TubeForgeError(
                    "SponsorBlock.InvalidTimeline",
                    "SponsorBlock returned segments outside the current media timeline.",
                    exception.GetType().Name));
            }
        }

        var work = new QueuedDownloadWork(
            resolved.Value.Metadata,
            selection,
            item.DestinationPath,
            sourceIdentity.Output,
            sourceIdentity.Captions,
            sourceIdentity.EmbedChapters,
            sourceIdentity.SplitChapters,
            sourceIdentity.Trim,
            sourceIdentity.SponsorBlock,
            sponsorSegments,
            sourceIdentity.LiveCapture);
        _preparedQueueWork[itemId] = work;
        return (work, null);
    }

    private static string FormatRateLimitDelay(TimeSpan delay) => delay.TotalMinutes >= 1
        ? $"{Math.Ceiling(delay.TotalMinutes)}m"
        : $"{Math.Max(1, Math.Ceiling(delay.TotalSeconds))}s";

    private async Task CompleteQueueRunAsync(
        Guid itemId,
        DownloadQueueStatus status,
        TubeForgeError? error,
        long? completedBytes)
    {
        var queueError = await UpdateQueueItemAsync(itemId, status, error?.Code, completedBytes);
        if (queueError is not null)
        {
            // The run has ended either way. Leaving the row rendered as Downloading would strand
            // it with no Retry or Remove affordance, so reflect the terminal state in memory.
            ApplyUnsavedQueueStatus(itemId, status, error?.Code, completedBytes);
            ReportQueueError(queueError);
            return;
        }

        if (error is not null && error.Code != "Operation.Cancelled")
        {
            ErrorMessage = $"{error.Message} ({error.Code})";
        }
    }

    private void UpdateQueueProgress(Guid itemId, DownloadProgress progress)
    {
        var card = QueueItems.FirstOrDefault(item => item.Id == itemId);
        card?.UpdateProgress(progress);
        DownloadFraction = progress.Fraction ?? 0;
        ProgressDetail = $"{_queueDispatcher.ActiveCount} active · global limit {SelectedMaxConcurrentDownloads} · host limit {PerHostConcurrency}";
    }

    private void UpdateQueueProcessing(Guid itemId, string detail)
    {
        var card = QueueItems.FirstOrDefault(item => item.Id == itemId);
        card?.UpdateProcessing(detail);
    }

    private long? QueuePartialLength(Guid itemId)
    {
        if (_preparedQueueWork.TryGetValue(itemId, out var work))
        {
            var mediaDestination = !work.RequiresMetadataFinalization
                ? work.Destination
                : PostProcessMediaSourcePath(
                    work.Destination,
                    work.EmbedChapters || work.SponsorBlock is { Mode: SponsorBlockMode.Chapters });
            var downloadDestination = work.RequiresNativeTrim
                ? TrimSourcePath(mediaDestination)
                : mediaDestination;
            return File.Exists(work.Destination)
                ? CompletedOrPartialLength(work.Destination)
                : SelectionPartialLength(downloadDestination, work.Selection, work.Output);
        }

        return _queueSnapshot.Items.FirstOrDefault(item => item.Id == itemId)?.BytesReceived;
    }

    private async Task ResumeQueueItemAsync(Guid itemId)
    {
        var item = _queueSnapshot.Items.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is null || item.Status is not (DownloadQueueStatus.Paused or DownloadQueueStatus.Failed or DownloadQueueStatus.Cancelled))
        {
            return;
        }

        ErrorMessage = string.Empty;
        _undispatchableQueueItems.Remove(itemId);
        var error = await UpdateQueueItemAsync(itemId, DownloadQueueStatus.Queued, failureCode: null);
        if (error is not null)
        {
            ReportQueueError(error);
            return;
        }

        PumpQueue();
    }

    private void PauseQueueItem(Guid itemId) => TryCancelDownload(itemId);

    /// <summary>
    /// Cancels a running download. The run owns its <see cref="CancellationTokenSource"/> and
    /// disposes it in its finally block, so a cancel raised from the UI can always race a run
    /// that just completed. Losing that race must not end the process.
    /// </summary>
    private bool TryCancelDownload(Guid itemId)
    {
        if (!_downloadCancellations.TryGetValue(itemId, out var cancellation))
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (AggregateException)
        {
            // A callback registered on the token threw; the run is cancelled regardless.
            return true;
        }
    }

    private async Task CancelQueueItemAsync(Guid itemId)
    {
        var item = _queueSnapshot.Items.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is null || item.Status is not (DownloadQueueStatus.Queued or DownloadQueueStatus.Downloading))
        {
            return;
        }

        _cancelledQueueItems.Add(itemId);
        if (_downloadCancellations.ContainsKey(itemId))
        {
            var persistenceError = await UpdateQueueItemAsync(
                itemId,
                DownloadQueueStatus.Cancelled,
                "Operation.Cancelled",
                QueuePartialLength(itemId));
            if (persistenceError is not null)
            {
                _cancelledQueueItems.Remove(itemId);
                ReportQueueError(persistenceError);
                return;
            }

            // The run may have finished and disposed its source during the await above.
            TryCancelDownload(itemId);
            return;
        }

        var error = await UpdateQueueItemAsync(itemId, DownloadQueueStatus.Cancelled, "Operation.Cancelled");
        _cancelledQueueItems.Remove(itemId);
        if (error is not null)
        {
            ReportQueueError(error);
        }
    }

    private void PauseAll()
    {
        foreach (var itemId in _downloadCancellations.Keys.ToArray())
        {
            TryCancelDownload(itemId);
        }
    }

    private async Task RemoveQueueItemAsync(Guid itemId)
    {
        if (_queueDispatcher.IsActive(itemId))
        {
            return;
        }

        await RemoveQueueItemsAsync([itemId]);
    }

    private async Task ClearCompletedAsync()
    {
        var completedIds = _queueSnapshot.Items
            .Where(item => item.Status == DownloadQueueStatus.Completed)
            .Select(item => item.Id)
            .ToArray();
        await RemoveQueueItemsAsync(completedIds);
    }

    private async Task RecordHistoryAsync(
        QueuedDownloadWork work,
        long bytesWritten,
        CancellationToken cancellationToken)
    {
        var entry = new DownloadHistoryEntry
        {
            Id = Guid.NewGuid(),
            VideoId = work.Metadata.Id.Value,
            SourceIdentity = SelectionIdentity(
                work.Metadata,
                work.Selection,
                work.Output,
                work.Captions,
                work.EmbedChapters,
                work.SplitChapters,
                work.Trim,
                work.SponsorBlock),
            DisplayTitle = work.Metadata.Title,
            DestinationPath = work.Destination,
            BytesWritten = bytesWritten,
            CompletedAtUtc = DateTimeOffset.UtcNow
        };
        await _historyMutationLock.WaitAsync(cancellationToken);
        try
        {
            var entries = _historySnapshot.Entries
                .Where(existing =>
                    !existing.SourceIdentity.Equals(entry.SourceIdentity, StringComparison.Ordinal) &&
                    !existing.DestinationPath.Equals(entry.DestinationPath, StringComparison.OrdinalIgnoreCase))
                .Prepend(entry)
                .Take(DownloadHistoryStore.MaximumEntries)
                .ToArray();
            var next = _historySnapshot with { Entries = entries };
            var result = await _historyStore.SaveAsync(next, cancellationToken);
            if (!result.IsSuccess)
            {
                ErrorMessage = $"Download completed, but Library history could not be saved. ({result.Error!.Code})";
                return;
            }

            _historySnapshot = next;
            _historyUnavailable = false;
            RebuildHistoryItems();
        }
        finally
        {
            _historyMutationLock.Release();
        }
    }

    private async Task RemoveHistoryItemAsync(Guid itemId)
    {
        await SaveHistoryEntriesAsync(_historySnapshot.Entries.Where(entry => entry.Id != itemId).ToArray());
    }

    private async Task ClearHistoryAsync() => await SaveHistoryEntriesAsync([]);

    private async Task RemoveMissingHistoryAsync()
    {
        // Re-checked for real rather than read from the presence cache: this removes records, so
        // it must not act on a stale reading. The check runs off the UI thread because a large
        // Library or an offline volume would otherwise freeze the window.
        var entries = _historySnapshot.Entries.ToArray();
        var existing = await Task.Run(() => entries
            .Where(entry =>
            {
                try
                {
                    return File.Exists(entry.DestinationPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  ArgumentException or NotSupportedException)
                {
                    return true;
                }
            })
            .ToArray());
        await SaveHistoryEntriesAsync(existing);
    }

    public async Task ExportLibraryAsync(string path)
    {
        var result = await _libraryTransferService.ExportAsync(_historySnapshot, path);
        if (!result.IsSuccess)
        {
            LibraryStatus = $"{result.Error!.Message} ({result.Error.Code})";
            return;
        }

        LibraryStatus = $"Exported {_historySnapshot.Entries.Count} Library records.";
    }

    public async Task ImportLibraryAsync(string path)
    {
        var imported = await _libraryTransferService.ImportAsync(path);
        if (!imported.IsSuccess)
        {
            LibraryStatus = $"{imported.Error!.Message} ({imported.Error.Code})";
            return;
        }

        await _historyMutationLock.WaitAsync();
        try
        {
            var merged = LibraryTransferService.Merge(_historySnapshot, imported.Value);
            if (!merged.IsSuccess)
            {
                LibraryStatus = $"{merged.Error!.Message} ({merged.Error.Code})";
                return;
            }

            var result = await _historyStore.SaveAsync(merged.Value);
            if (!result.IsSuccess)
            {
                LibraryStatus = $"{result.Error!.Message} ({result.Error.Code})";
                return;
            }

            var added = merged.Value.Entries.Count - _historySnapshot.Entries.Count;
            _historySnapshot = merged.Value;
            _historyUnavailable = false;
            RebuildHistoryItems();
            LibraryStatus = added > 0
                ? $"Imported {added} new Library records; duplicates were merged."
                : "Import complete; no new Library records were found.";
        }
        finally
        {
            _historyMutationLock.Release();
        }
    }

    public async Task RescanLibraryAsync(string rootPath)
    {
        await _historyMutationLock.WaitAsync();
        try
        {
            LibraryStatus = "Scanning for moved files…";
            var rescanned = await Task.Run(() => LibraryRescanner.Rescan(_historySnapshot, rootPath));
            if (!rescanned.IsSuccess)
            {
                LibraryStatus = $"{rescanned.Error!.Message} ({rescanned.Error.Code})";
                return;
            }

            var result = await _historyStore.SaveAsync(rescanned.Value.Snapshot);
            if (!result.IsSuccess)
            {
                LibraryStatus = $"{result.Error!.Message} ({result.Error.Code})";
                return;
            }

            _historySnapshot = rescanned.Value.Snapshot;
            _historyUnavailable = false;
            RebuildHistoryItems();
            LibraryStatus = $"Scanned {rescanned.Value.FilesScanned} files; repaired {rescanned.Value.RecordsRepaired} records" +
                            (rescanned.Value.AmbiguousMatches > 0
                                ? $"; left {rescanned.Value.AmbiguousMatches} ambiguous matches unchanged."
                                : ".");
        }
        finally
        {
            _historyMutationLock.Release();
        }
    }

    private async Task SaveLibrarySortOrderAsync(LibrarySortOrder order)
    {
        var next = _settings with { LibrarySortOrder = order };
        var result = await _settingsStore.SaveAsync(next);
        if (!result.IsSuccess)
        {
            SettingsStatus = "Library sort preference could not be saved.";
            return;
        }

        if (SelectedLibrarySort.Value == order)
        {
            _settings = next;
        }
    }

    private async Task SaveHistoryEntriesAsync(IReadOnlyList<DownloadHistoryEntry> entries)
    {
        await _historyMutationLock.WaitAsync();
        try
        {
            var next = new DownloadHistorySnapshot { Entries = entries };
            var result = await _historyStore.SaveAsync(next);
            if (!result.IsSuccess)
            {
                ErrorMessage = $"{result.Error!.Message} ({result.Error.Code})";
                return;
            }

            _historySnapshot = next;
            _historyUnavailable = false;
            RebuildHistoryItems();
            StatusMessage = entries.Count == 0 ? "Library history cleared" : "Library history updated";
        }
        finally
        {
            _historyMutationLock.Release();
        }
    }

    private async Task RemoveQueueItemsAsync(IReadOnlyCollection<Guid> itemIds)
    {
        if (itemIds.Count == 0 || _queueUnavailable)
        {
            return;
        }

        await _queueMutationLock.WaitAsync();
        try
        {
            var ids = itemIds.ToHashSet();
            var nextSnapshot = _queueSnapshot with
            {
                Items = _queueSnapshot.Items.Where(item => !ids.Contains(item.Id)).ToArray()
            };
            var result = await _queueStore.SaveAsync(nextSnapshot);
            if (!result.IsSuccess)
            {
                ReportQueueError(result.Error!);
                return;
            }

            _queueSnapshot = nextSnapshot;
            _undispatchableQueueItems.ExceptWith(ids);
            _relinkedQueueItems.ExceptWith(ids);
            foreach (var card in QueueItems.Where(item => ids.Contains(item.Id)).ToArray())
            {
                QueueItems.Remove(card);
            }

            foreach (var id in ids)
            {
                _preparedQueueWork.Remove(id);
            }

            NotifyQueueProperties();
        }
        finally
        {
            _queueMutationLock.Release();
        }
    }

    private void RebuildQueueItems()
    {
        QueueItems.Clear();
        foreach (var item in _queueSnapshot.Items.OrderBy(item => item.CreatedAtUtc))
        {
            QueueItems.Add(CreateQueueCard(item));
        }

        NotifyQueueProperties();
    }

    private void RebuildHistoryItems()
    {
        HistoryItems.Clear();
        var matching = _historySnapshot.Entries.Where(entry =>
            string.IsNullOrWhiteSpace(LibrarySearchText) ||
            entry.DisplayTitle.Contains(LibrarySearchText.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
            entry.DestinationPath.Contains(LibrarySearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        matching = SelectedLibrarySort.Value switch
        {
            LibrarySortOrder.OldestFirst => matching.OrderBy(entry => entry.CompletedAtUtc),
            LibrarySortOrder.TitleAscending => matching
                .OrderBy(entry => entry.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(entry => entry.CompletedAtUtc),
            LibrarySortOrder.LargestFirst => matching
                .OrderByDescending(entry => entry.BytesWritten)
                .ThenByDescending(entry => entry.CompletedAtUtc),
            _ => matching.OrderByDescending(entry => entry.CompletedAtUtc)
        };
        foreach (var entry in matching)
        {
            HistoryItems.Add(new HistoryItemViewModel(
                entry,
                RevealDestination,
                RemoveHistoryItemAsync,
                IsHistoryFilePresent));
        }

        NotifyHistoryProperties();
        _ = RefreshHistoryPresenceAsync();
    }

    /// <summary>
    /// Reports whether a recorded download is still on disk, from a cache that is refreshed off
    /// the UI thread. Paths not yet measured are reported as present so a freshly rendered
    /// Library never claims files are missing before it has looked.
    /// </summary>
    private bool IsHistoryFilePresent(string destinationPath) =>
        !_historyFilePresence.TryGetValue(destinationPath, out var present) || present;

    private async Task RefreshHistoryPresenceAsync()
    {
        var paths = _historySnapshot.Entries
            .Select(entry => entry.DestinationPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            if (_historyFilePresence.Count > 0)
            {
                _historyFilePresence.Clear();
                NotifyHistoryProperties();
            }

            return;
        }

        var measured = await Task.Run(() =>
        {
            var results = new Dictionary<string, bool>(paths.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                try
                {
                    results[path] = File.Exists(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  ArgumentException or NotSupportedException)
                {
                    // An unreachable volume is reported as present so the entry is never removed
                    // just because the drive happened to be offline.
                    results[path] = true;
                }
            }

            return results;
        });

        var changed = measured.Count != _historyFilePresence.Count ||
                      measured.Any(pair => !_historyFilePresence.TryGetValue(pair.Key, out var known) ||
                                           known != pair.Value);
        if (!changed)
        {
            return;
        }

        _historyFilePresence.Clear();
        foreach (var pair in measured)
        {
            _historyFilePresence[pair.Key] = pair.Value;
        }

        NotifyHistoryProperties();
        foreach (var item in HistoryItems)
        {
            item.RevealCommand.RaiseCanExecuteChanged();
        }
    }

    private void RefreshQueueItem(DownloadQueueItem item)
    {
        var existing = QueueItems.FirstOrDefault(candidate => candidate.Id == item.Id);
        if (existing is null)
        {
            QueueItems.Add(CreateQueueCard(item));
        }
        else
        {
            existing.Update(item);
        }

        NotifyQueueProperties();
    }

    private QueueItemViewModel CreateQueueCard(DownloadQueueItem item) => new(
        item,
        ResumeQueueItemAsync,
        PauseQueueItem,
        CancelQueueItemAsync,
        RemoveQueueItemAsync,
        RevealDestination);

    private DownloadQueueStatus CancellationStatus(Guid itemId) =>
        _cancelledQueueItems.Contains(itemId)
            ? DownloadQueueStatus.Cancelled
            : DownloadQueueStatus.Paused;

    private void NotifyQueueProperties()
    {
        OnPropertyChanged(nameof(HasQueueItems));
        OnPropertyChanged(nameof(QueueSummary));
        ClearCompletedCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        CheckForUpdatesCommand.RaiseCanExecuteChanged();
        UpdateNowCommand.RaiseCanExecuteChanged();
    }

    private void NotifyHistoryProperties()
    {
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HistorySummary));
        OnPropertyChanged(nameof(HistoryEmptyMessage));
        ClearHistoryCommand.RaiseCanExecuteChanged();
        RemoveMissingHistoryCommand.RaiseCanExecuteChanged();
    }

    private void RevealDestination(string destination)
    {
        try
        {
            var directory = Path.GetDirectoryName(destination);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                ErrorMessage = "The destination folder no longer exists. (Queue.DirectoryMissing)";
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ErrorMessage = $"Windows could not open the destination folder. (Queue.RevealFailed: {exception.GetType().Name})";
        }
    }

    private void ReportQueueError(TubeForgeError error)
    {
        ErrorMessage = $"{error.Message} ({error.Code})";
        StatusMessage = "Queue state warning";
    }

    private static DownloadQueueItem CreateQueueItem(
        VideoMetadata metadata,
        FormatItemViewModel selection,
        string destination,
        OutputProfile output = default,
        CaptionEmbedSelectionSet? caption = null,
        bool embedChapters = false,
        bool splitChapters = false,
        MediaTrimRange? trim = null,
        SponsorBlockSelection? sponsorBlock = null,
        LiveCaptureOptions? liveCapture = null)
    {
        var now = DateTimeOffset.UtcNow;
        var format = selection.Format;
        var sourceIdentity = SelectionIdentity(
            metadata,
            selection,
            output,
            caption,
            embedChapters,
            splitChapters,
            trim,
            sponsorBlock,
            liveCapture);
        var expectedLength = liveCapture is null ? CombinedLength(selection) : null;
        var embedsTimeline = embedChapters || sponsorBlock is { Mode: SponsorBlockMode.Chapters };
        var mediaDestination = caption is null && !embedsTimeline
            ? destination
            : PostProcessMediaSourcePath(destination, embedsTimeline);
        var downloadDestination = trim is not null && !output.RequiresTranscode
            ? TrimSourcePath(mediaDestination)
            : mediaDestination;
        var partialLength = SelectionPartialLength(
            downloadDestination,
            selection,
            output);

        return new DownloadQueueItem
        {
            Id = Guid.NewGuid(),
            VideoId = metadata.Id.Value,
            FormatId = format.FormatId,
            SourceIdentity = sourceIdentity,
            DisplayTitle = metadata.Title,
            DestinationPath = destination,
            ExpectedLength = expectedLength,
            BytesReceived = partialLength,
            Status = DownloadQueueStatus.Queued,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    private DownloadRequest TrackRequest(
        VideoMetadata metadata,
        StreamFormat format,
        string destination) => new()
        {
            SourceUrl = format.Url,
            HttpUserAgent = format.HttpUserAgent,
            SourceIdentity = $"{metadata.Id.Value}:{format.FormatId}",
            DestinationPath = destination,
            ExpectedLength = format.ContentLength,
            ExpectedContainer = format.Container,
            EnableSegmentedTransfer = _settings.EnableAcceleratedTransfers
        };

    private async Task<Result<DownloadReceipt>> EnsureTrackDownloadedAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(request.DestinationPath))
        {
            return await _downloader.DownloadAsync(request, progress, cancellationToken);
        }

        var length = new FileInfo(request.DestinationPath).Length;
        if (request.ExpectedLength is not null && request.ExpectedLength != length)
        {
            return Result<DownloadReceipt>.Failure(new TubeForgeError(
                "Media.IntermediateConflict",
                "A completed source media track has an unexpected size."));
        }

        var validation = MediaContainerValidator.Validate(request.DestinationPath, request.ExpectedContainer);
        if (!validation.IsSuccess)
        {
            return Result<DownloadReceipt>.Failure(validation.Error!);
        }

        progress?.Report(new DownloadProgress(length, request.ExpectedLength ?? length, 0, TimeSpan.Zero));
        return Result<DownloadReceipt>.Success(new DownloadReceipt(request.DestinationPath, length, Resumed: true));
    }

    private async Task<TubeForgeError?> EnsureVideoTranscodeSourceAsync(
        QueuedDownloadWork work,
        string sourcePath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (work.Selection.AudioFormat is not StreamFormat audioFormat)
        {
            var result = await EnsureTrackDownloadedAsync(
                TrackRequest(work.Metadata, work.Selection.Format, sourcePath),
                progress,
                cancellationToken);
            return result.Error;
        }

        var adaptive = await _adaptiveDownloader.DownloadAsync(new AdaptiveDownloadRequest
        {
            Video = TrackRequest(
                work.Metadata,
                work.Selection.Format,
                IntermediateTrackPath(sourcePath, "video", work.Selection.Format)),
            Audio = TrackRequest(
                work.Metadata,
                audioFormat,
                IntermediateTrackPath(sourcePath, "audio", audioFormat)),
            DestinationPath = sourcePath,
            OutputContainer = work.Selection.OutputContainer,
            AllowExistingValidatedOutput = true
        }, progress, cancellationToken);
        return adaptive.Error;
    }

    private async Task<TubeForgeError?> SplitChaptersAsync(
        QueuedDownloadWork work,
        CancellationToken cancellationToken)
    {
        if (!work.SplitChapters || work.Metadata.Duration is not { } duration)
        {
            return new TubeForgeError(
                "Queue.InvalidChapterSplit",
                "The queued chapter split selection is invalid.");
        }

        var timeline = TimelineChapters(work);
        if (timeline.Count == 0)
        {
            return new TubeForgeError(
                "Queue.InvalidChapterSplit",
                "No chapter overlaps the selected trim range.");
        }

        var outputDuration = work.Trim?.Duration ?? duration;
        var format = work.Selection.Format;
        var quality = work.Output.IsAudioTranscode
            ? work.Output.BitrateKbps > 0 ? $"{work.Output.BitrateKbps}kbps" : "lossless"
            : format.HasVideo
                ? format.Height is > 0 ? $"{format.Height}p" : "video"
                : format.Bitrate is > 0 ? $"{Math.Round(format.Bitrate.Value / 1000d):0}kbps" : "audio";
        var extension = OutputExtension(work.Selection, work.Output);
        StatusMessage = $"Creating {timeline.Count} lossless chapter files";
        var result = await _chapterSplitter.SplitAsync(new ChapterSplitRequest
        {
            SourcePath = work.Destination,
            DestinationDirectory = ChapterSplitDirectoryPath(work.Destination),
            OutputContainer = FinalMediaContainer(work.Selection, work.Output),
            Chapters = timeline,
            Duration = outputDuration,
            FileNameContext = new FileNameTemplateContext
            {
                Title = work.Metadata.Title,
                Channel = work.Metadata.Channel,
                VideoId = work.Metadata.Id.Value,
                Quality = quality,
                Container = extension.TrimStart('.'),
                IndexWidth = Math.Max(2, timeline.Count.ToString().Length)
            },
            AllowExistingValidatedOutput = true
        }, cancellationToken);
        return result.Error;
    }

    private async Task<(TubeForgeError? Error, long BytesWritten)> FinalizeNativeTrimAsync(
        QueuedDownloadWork work,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!work.RequiresNativeTrim || work.Trim is not { } trim)
        {
            return (new TubeForgeError(
                "Queue.InvalidTrim",
                "The queued trim selection is invalid."), 0);
        }

        StatusMessage = "Trimming with lossless stream copy · start may align to a keyframe";
        var result = await _mediaProcessor.TrimStreamCopyAsync(
            sourcePath,
            destinationPath,
            FinalMediaContainer(work.Selection, work.Output),
            trim,
            cancellationToken,
            allowExistingValidatedOutput: true);
        if (!result.IsSuccess)
        {
            return (result.Error, CompletedOrPartialLength(sourcePath));
        }

        TryDeleteIntermediate(sourcePath);
        return (null, result.Value.BytesWritten);
    }

    private async Task<(TubeForgeError? Error, long BytesWritten)> FinalizeMetadataAsync(
        QueuedDownloadWork work,
        string mediaPath,
        CancellationToken cancellationToken)
    {
        if (!work.RequiresMetadataFinalization)
        {
            return (new TubeForgeError(
                "Queue.InvalidMetadataSelection",
                "The queued media metadata selection is invalid."), 0);
        }

        var container = FinalMediaContainer(work.Selection, work.Output);
        var captionPaths = work.Captions?.Selections
            .Select((_, index) => CaptionSubtitlePath(work.Destination, index))
            .ToArray() ?? [];
        if (File.Exists(work.Destination))
        {
            var recovered = await FinalizeWithMetadataProcessorAsync(
                work,
                mediaPath,
                captionPaths,
                container,
                cancellationToken);
            if (!recovered.IsSuccess)
            {
                return (recovered.Error, CompletedOrPartialLength(work.Destination));
            }

            TryDeleteIntermediate(mediaPath);
            foreach (var captionPath in captionPaths)
            {
                TryDeleteIntermediate(captionPath);
            }
            return (null, recovered.Value.BytesWritten);
        }

        if (work.Captions is { } captions)
        {
            var selections = captions.Selections;
            for (var index = 0; index < selections.Count; index++)
            {
                var caption = selections[index];
                var captionPath = captionPaths[index];
                var track = work.Metadata.CaptionTracks.FirstOrDefault(candidate => CaptionMatches(candidate, caption));
                if (track is null)
                {
                    return (new TubeForgeError(
                        "Queue.CaptionUnavailable",
                        "A selected caption track is no longer available. Analyze the video again."),
                        CompletedOrPartialLength(mediaPath));
                }

                try
                {
                    File.Delete(captionPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return (new TubeForgeError(
                        "Caption.WriteFailed",
                        "TubeForge could not prepare an embedded caption file.",
                        exception.GetType().Name),
                        CompletedOrPartialLength(mediaPath));
                }

                StatusMessage = $"Downloading caption {index + 1} of {selections.Count} · {track.LanguageCode.ToUpperInvariant()}";
                var downloaded = await _captionDownloader.DownloadAsync(new CaptionDownloadRequest
                {
                    SourceUrl = track.Url,
                    DestinationPath = captionPath,
                    OutputFormat = CaptionOutputFormat.SubRip
                }, cancellationToken);
                if (!downloaded.IsSuccess)
                {
                    return (downloaded.Error, CompletedOrPartialLength(mediaPath));
                }

                if (work.Trim is not null || work.RemovedSponsorSegments.Count > 0)
                {
                    try
                    {
                        var captionContent = await File.ReadAllTextAsync(captionPath, cancellationToken)
                            .ConfigureAwait(false);
                        if (work.Trim is { } trim)
                        {
                            var trimmedCaption = SubRipTimelineTrimmer.Trim(captionContent, trim);
                            if (!trimmedCaption.IsSuccess)
                            {
                                return (trimmedCaption.Error, CompletedOrPartialLength(mediaPath));
                            }

                            captionContent = trimmedCaption.Value;
                        }

                        if (work.RemovedSponsorSegments.Count > 0)
                        {
                            var editedCaption = SubRipTimelineTrimmer.RemoveSegments(
                                captionContent,
                                work.RemovedSponsorSegments);
                            if (!editedCaption.IsSuccess)
                            {
                                return (editedCaption.Error, CompletedOrPartialLength(mediaPath));
                            }

                            captionContent = editedCaption.Value;
                        }

                        await File.WriteAllTextAsync(
                            captionPath,
                            captionContent,
                            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        return (new TubeForgeError(
                            "Caption.WriteFailed",
                            "TubeForge could not align an embedded subtitle track to the edited media timeline.",
                            exception.GetType().Name), CompletedOrPartialLength(mediaPath));
                    }
                }
            }
        }

        StatusMessage = work.Captions is not null && work.EmbedChapters
            ? "Embedding soft subtitles and chapters"
            : work.EmbedChapters
                ? "Embedding chapters"
                : work.RequiresSponsorChapters
                    ? "Writing SponsorBlock timeline chapters"
                    : $"Embedding {captionPaths.Length} soft-subtitle track(s)";
        var embedded = await FinalizeWithMetadataProcessorAsync(
            work,
            mediaPath,
            captionPaths,
            container,
            cancellationToken);
        if (!embedded.IsSuccess)
        {
            return (embedded.Error, CompletedOrPartialLength(mediaPath));
        }

        TryDeleteIntermediate(mediaPath);
        foreach (var captionPath in captionPaths)
        {
            TryDeleteIntermediate(captionPath);
        }
        return (null, embedded.Value.BytesWritten);
    }

    private Task<Result<MediaProcessReceipt>> FinalizeWithMetadataProcessorAsync(
        QueuedDownloadWork work,
        string mediaPath,
        IReadOnlyList<string> captionPaths,
        MediaContainer container,
        CancellationToken cancellationToken)
    {
        if (!work.EmbedChapters && !work.RequiresSponsorChapters &&
            work.Captions is { } captions)
        {
            return _mediaProcessor.EmbedSubtitlesAsync(
                mediaPath,
                captionPaths,
                work.Destination,
                container,
                captions,
                cancellationToken,
                allowExistingValidatedOutput: true);
        }

        var chapters = TimelineChapters(work);
        return _mediaProcessor.EmbedMetadataTracksAsync(
            mediaPath,
            work.Destination,
            container,
            chapters,
            work.Trim?.Duration ?? work.Metadata.Duration ?? TimeSpan.Zero,
            captionPaths,
            work.Captions,
            cancellationToken,
            allowExistingValidatedOutput: true);
    }

    private static IReadOnlyList<VideoChapter> TimelineChapters(QueuedDownloadWork work)
    {
        var duration = work.Trim?.Duration ?? work.Metadata.Duration ?? TimeSpan.Zero;
        IReadOnlyList<VideoChapter> chapters;
        if (work.Trim is not { } trim || work.Metadata.Duration is not { } sourceDuration)
        {
            chapters = work.Metadata.Chapters;
        }
        else
        {
            chapters = MediaTimelineEditor.TrimChapters(
                work.Metadata.Chapters,
                sourceDuration,
                trim);
        }

        return work.RequiresSponsorChapters
            ? MediaTimelineEditor.AddSponsorBlockChapters(
                chapters,
                work.SafeSponsorSegments,
                duration)
            : chapters;
    }

    private static void TryDeleteIntermediate(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task RunLiveCaptureAsync(
        Guid itemId,
        QueuedDownloadWork work,
        LiveCaptureOptions options,
        CancellationToken cancellationToken)
    {
        var sourcePath = LiveCaptureSourcePath(work.Destination);
        var reservedBytes = HlsReservedLength(sourcePath);
        var diskForecast = DiskSpacePolicy.Check(
            work.Destination,
            options.MaximumBytes,
            reservedBytes,
            requiresMuxing: true);
        if (!diskForecast.IsSuccess)
        {
            await CompleteQueueRunAsync(
                itemId,
                DownloadQueueStatus.Failed,
                diskForecast.Error,
                reservedBytes);
            return;
        }

        TubeForgeError? error = null;
        long completedBytes = reservedBytes;
        if (!File.Exists(sourcePath))
        {
            var manifest = await ResolveLiveManifestAsync(work, options, cancellationToken);
            if (!manifest.IsSuccess)
            {
                var status = manifest.Error?.Code == "Operation.Cancelled"
                    ? CancellationStatus(itemId)
                    : DownloadQueueStatus.Failed;
                await CompleteQueueRunAsync(itemId, status, manifest.Error, reservedBytes);
                return;
            }

            StatusMessage = "Recording public HLS stream · pause preserves downloaded segments";
            var progress = new Progress<DownloadProgress>(value => UpdateQueueProgress(itemId, value));
            var capture = await new HlsCaptureEngine(_httpClient, _hostRequestGate).CaptureAsync(
                new HlsCaptureRequest
                {
                    ManifestUri = manifest.Value.Url,
                    DestinationPath = sourcePath,
                    Options = options,
                    HttpUserAgent = manifest.Value.HttpUserAgent
                },
                progress,
                cancellationToken);
            error = capture.Error;
            completedBytes = capture.IsSuccess ? capture.Value.BytesWritten : HlsReservedLength(sourcePath);
        }

        if (error is null)
        {
            StatusMessage = "Finalizing live capture to MKV · stream copy";
            var remux = await _mediaProcessor.RemuxHlsCaptureAsync(
                sourcePath,
                work.Destination,
                cancellationToken,
                allowExistingValidatedOutput: true);
            error = remux.Error;
            completedBytes = remux.IsSuccess ? remux.Value.BytesWritten : HlsReservedLength(sourcePath);
            if (remux.IsSuccess)
            {
                File.Delete(sourcePath);
                HlsCaptureEngine.Cleanup(sourcePath);
            }
        }

        if (error is not null)
        {
            var status = error.Code == "Operation.Cancelled"
                ? CancellationStatus(itemId)
                : DownloadQueueStatus.Failed;
            await CompleteQueueRunAsync(itemId, status, error, completedBytes);
            return;
        }

        await CompleteQueueRunAsync(
            itemId,
            DownloadQueueStatus.Completed,
            error: null,
            completedBytes);
        await RecordHistoryAsync(work, completedBytes, CancellationToken.None);
        StatusMessage = "Completed: " + Path.GetFileName(work.Destination);
    }

    private async Task<Result<StreamFormat>> ResolveLiveManifestAsync(
        QueuedDownloadWork work,
        LiveCaptureOptions options,
        CancellationToken cancellationToken)
    {
        var current = work.Selection.Format;
        if (!current.IsLiveManifestPending)
        {
            return Result<StreamFormat>.Success(current);
        }

        var deadline = DateTimeOffset.UtcNow + options.MaximumWaitForStart;
        while (DateTimeOffset.UtcNow < deadline)
        {
            StatusMessage = $"Waiting for public live stream · up to {FormatDuration(options.MaximumWaitForStart)}";
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Result<StreamFormat>.Failure(new TubeForgeError(
                    "Operation.Cancelled",
                    "Waiting for the live stream was paused or cancelled."));
            }

            var resolved = await _rateLimitedRequests.ExecuteAsync(
                YouTubeOrigin,
                token => _resolver.ResolveAsync(work.Metadata.Id, token),
                cancellationToken: cancellationToken);
            if (!resolved.IsSuccess)
            {
                return Result<StreamFormat>.Failure(resolved.Error!);
            }

            var live = resolved.Value.Metadata.Formats.FirstOrDefault(format =>
                format.IsLiveHls && !format.IsLiveManifestPending);
            if (live is not null)
            {
                return Result<StreamFormat>.Success(live);
            }
        }

        return Result<StreamFormat>.Failure(new TubeForgeError(
            "Hls.StartWaitExpired",
            "The upcoming public stream did not start before the configured wait limit."));
    }

    private static string IntermediateTrackPath(
        string destination,
        string role,
        StreamFormat format) =>
        destination + $".{role}-track" + FormatDisplay.OutputExtension(format);

    private static string AudioSourcePath(string destination, StreamFormat format) =>
        destination + ".source" + FormatDisplay.OutputExtension(format);

    private static string VideoSourcePath(string destination, FormatItemViewModel selection) =>
        destination + ".source" + NativeOutputExtension(selection);

    private static string PostProcessMediaSourcePath(string destination, bool embedChapters) =>
        destination + (embedChapters ? ".chapter-source" : ".caption-source") + Path.GetExtension(destination);

    private static string TrimSourcePath(string destination) =>
        destination + ".trim-source" + Path.GetExtension(destination);

    private static string LiveCaptureSourcePath(string destination) =>
        destination + ".hls-source";

    private static long HlsReservedLength(string sourcePath)
    {
        if (File.Exists(sourcePath))
        {
            return new FileInfo(sourcePath).Length;
        }

        var partsDirectory = sourcePath + ".hls.parts";
        if (!Directory.Exists(partsDirectory))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(partsDirectory, "*.bin", SearchOption.TopDirectoryOnly)
                .Sum(path => new FileInfo(path).Length);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static string CaptionSubtitlePath(string destination, int index) =>
        index == 0
            ? destination + ".caption-source.srt"
            : destination + $".caption-{index + 1}-source.srt";

    private static string ChapterSplitDirectoryPath(string destination) =>
        Path.Combine(
            Path.GetDirectoryName(destination)!,
            Path.GetFileNameWithoutExtension(destination) + " - chapters");

    private static string NativeOutputExtension(FormatItemViewModel selection) =>
        selection.RequiresMuxing
            ? FormatDisplay.Extension(selection.OutputContainer)
            : FormatDisplay.OutputExtension(selection.Format);

    private static bool RequiresMp4Normalization(FormatItemViewModel selection) =>
        selection.AudioFormat is null &&
        selection.Format.Container == MediaContainer.Mp4 &&
        selection.Format.Kind != StreamKind.AudioOnly;

    private static MediaContainer FinalMediaContainer(
        FormatItemViewModel selection,
        OutputProfile output) => output.Kind switch
        {
            OutputProfileKind.H264AacMp4 or OutputProfileKind.H265AacMp4 => MediaContainer.Mp4,
            OutputProfileKind.Vp9OpusWebM => MediaContainer.WebM,
            _ => selection.RequiresMuxing ? selection.OutputContainer : selection.Format.Container
        };

    private static bool IsCaptionContainerSupported(MediaContainer container) =>
        container is MediaContainer.Mp4 or MediaContainer.Mkv or MediaContainer.WebM;

    private static string SelectionIdentity(
        VideoMetadata metadata,
        FormatItemViewModel selection,
        OutputProfile output = default,
        CaptionEmbedSelectionSet? caption = null,
        bool embedChapters = false,
        bool splitChapters = false,
        MediaTrimRange? trim = null,
        SponsorBlockSelection? sponsorBlock = null,
        LiveCaptureOptions? liveCapture = null) =>
        DownloadSourceIdentity.Create(
            metadata.Id,
            selection.Format.FormatId,
            selection.AudioFormat?.FormatId,
            output,
            caption,
            embedChapters,
            splitChapters,
            trim,
            sponsorBlock,
            liveCapture);

    private Result<string> RenderFileName(
        VideoMetadata metadata,
        FormatItemViewModel selection,
        int? index,
        int indexWidth,
        OutputProfile output = default) =>
        RenderCollectionFileName(
            metadata,
            selection,
            index,
            indexWidth,
            output,
            FileNameTemplateText,
            IncludeQualityInFileName);

    private static Result<string> RenderCollectionFileName(
        VideoMetadata metadata,
        FormatItemViewModel selection,
        int? index,
        int indexWidth,
        OutputProfile output,
        string fileNameTemplate,
        bool includeQualityInFileName)
    {
        var format = selection.Format;
        var quality = output.IsAudioTranscode
            ? output.BitrateKbps > 0 ? $"{output.BitrateKbps}kbps" : "lossless"
            : format.HasVideo
                ? format.Height is > 0 ? $"{format.Height}p" : "video"
                : format.Bitrate is > 0 ? $"{Math.Round(format.Bitrate.Value / 1000d):0}kbps" : "audio";
        var extension = OutputExtension(selection, output);
        var template = index is not null && fileNameTemplate == FileNameTemplate.Default
            ? "{index} - {title}"
            : fileNameTemplate;
        var rendered = FileNameTemplate.Render(template, new FileNameTemplateContext
        {
            Title = metadata.Title,
            Channel = metadata.Channel,
            VideoId = metadata.Id.Value,
            Quality = quality,
            Container = extension.TrimStart('.'),
            Index = index,
            IndexWidth = indexWidth
        });
        if (!rendered.IsSuccess)
        {
            return rendered;
        }

        var stem = rendered.Value.TrimEnd();
        if (stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^extension.Length].TrimEnd();
        }

        return includeQualityInFileName
            ? Result<string>.Success($"{stem} {quality}")
            : Result<string>.Success(stem);
    }

    private static string DuplicateDescription(DownloadDuplicateKind kind) => kind switch
    {
        DownloadDuplicateKind.QueuedOutput => "same output already queued",
        DownloadDuplicateKind.CompletedOutput => "same output already in Library",
        DownloadDuplicateKind.QueueDestination => "destination already queued",
        DownloadDuplicateKind.HistoryDestination => "destination already in Library",
        _ => "duplicate output"
    };

    private OutputProfile OutputProfileFor(FormatItemViewModel selection) =>
        SelectedDownloadMode.Value switch
        {
            DownloadMode.AudioOnly when selection.Format.Kind == StreamKind.AudioOnly =>
                SelectedAudioProcessing.Value,
            DownloadMode.AudioVideo when selection.Format.Kind is StreamKind.Progressive or StreamKind.VideoOnly =>
                SelectedVideoProcessing.Value.ForVideoHeight(selection.Format.Height),
            _ => OutputProfile.Native
        };

    private CaptionEmbedSelectionSet? SelectedCaptionEmbedSelections()
    {
        var selections = CaptionTracks
            .Where(track => track.IsSelectedForEmbedding)
            .Select(track => new CaptionEmbedSelection(
                track.Track.LanguageCode,
                track.Track.IsAutoGenerated))
            .ToArray();
        if (!CaptionEmbedSelectionSet.TryCreate(selections, out var set))
        {
            return null;
        }

        return set;
    }

    private void ClearEmbeddedCaptionSelections()
    {
        foreach (var track in CaptionTracks)
        {
            track.IsSelectedForEmbedding = false;
        }
        if (SelectedCaptionTrack is not null)
        {
            SelectedCaptionTrack.IsSelectedForEmbedding = false;
        }

        OnPropertyChanged(nameof(EmbedSelectedCaption));
        OnPropertyChanged(nameof(HasSelectedCaptionEmbeds));
    }

    private static bool CaptionMatches(CaptionTrack track, CaptionEmbedSelection selection) =>
        track.IsAutoGenerated == selection.IsAutoGenerated &&
        track.LanguageCode.Equals(selection.LanguageCode, StringComparison.OrdinalIgnoreCase);

    private static string OutputExtension(
        FormatItemViewModel selection,
        OutputProfile output) =>
        output.RequiresTranscode
            ? output.Extension
            : selection.RequiresMuxing
                ? FormatDisplay.Extension(selection.OutputContainer)
                : FormatDisplay.OutputExtension(selection.Format);

    private static long? ForecastLength(QueuedDownloadWork work)
    {
        var sourceLength = CombinedLength(work.Selection);
        if (!work.Output.RequiresTranscode || work.Metadata.Duration is null)
        {
            return sourceLength;
        }

        var convertedLength = work.Output.EstimateTranscodedBytes(work.Metadata.Duration);
        if (convertedLength is null)
        {
            return null;
        }

        return sourceLength is null ? convertedLength : Math.Max(sourceLength.Value, convertedLength.Value);
    }

    private static long? CombinedLength(FormatItemViewModel selection)
    {
        if (selection.Format.ContentLength is null || selection.AudioFormat?.ContentLength is null)
        {
            return selection.AudioFormat is null ? selection.Format.ContentLength : null;
        }

        try
        {
            return checked(selection.Format.ContentLength.Value + selection.AudioFormat.ContentLength.Value);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static long SelectionPartialLength(
        string destination,
        FormatItemViewModel selection,
        OutputProfile output = default)
    {
        if (File.Exists(destination))
        {
            return CompletedOrReservedLength(destination);
        }

        if (output.IsAudioTranscode)
        {
            return CompletedOrPartialLength(AudioSourcePath(destination, selection.Format));
        }

        if (output.IsVideoTranscode)
        {
            return SelectionPartialLength(
                VideoSourcePath(destination, selection),
                selection,
                OutputProfile.Native);
        }

        if (selection.AudioFormat is null && !RequiresMp4Normalization(selection))
        {
            return PartialLength(destination);
        }

        if (selection.AudioFormat is null)
        {
            return CompletedOrPartialLength(
                IntermediateTrackPath(destination, "video", selection.Format));
        }

        var videoPath = IntermediateTrackPath(destination, "video", selection.Format);
        var audioPath = IntermediateTrackPath(destination, "audio", selection.AudioFormat);
        try
        {
            return checked(CompletedOrPartialLength(videoPath) + CompletedOrPartialLength(audioPath));
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static long SelectionReservedLength(
        string destination,
        FormatItemViewModel selection,
        OutputProfile output = default)
    {
        if (File.Exists(destination))
        {
            return CompletedOrReservedLength(destination);
        }

        if (output.IsAudioTranscode)
        {
            return CompletedOrReservedLength(AudioSourcePath(destination, selection.Format));
        }

        if (output.IsVideoTranscode)
        {
            return SelectionReservedLength(
                VideoSourcePath(destination, selection),
                selection,
                OutputProfile.Native);
        }

        if (selection.AudioFormat is null && !RequiresMp4Normalization(selection))
        {
            return CompletedOrReservedLength(destination);
        }

        if (selection.AudioFormat is null)
        {
            return CompletedOrReservedLength(
                IntermediateTrackPath(destination, "video", selection.Format));
        }

        var videoPath = IntermediateTrackPath(destination, "video", selection.Format);
        var audioPath = IntermediateTrackPath(destination, "audio", selection.AudioFormat);
        try
        {
            return checked(CompletedOrReservedLength(videoPath) + CompletedOrReservedLength(audioPath));
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static long CompletedOrPartialLength(string destination)
    {
        try
        {
            if (File.Exists(destination))
            {
                return new FileInfo(destination).Length;
            }

            return PartialLength(destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private async Task<TubeForgeError?> UpsertQueueItemAsync(
        DownloadQueueItem item,
        CancellationToken cancellationToken = default)
    {
        if (_queueUnavailable && !await TryRecoverQueueAsync(cancellationToken))
        {
            return new TubeForgeError(
                "Queue.Unavailable",
                "The saved queue must be repaired before starting another download.");
        }

        await _queueMutationLock.WaitAsync(cancellationToken);
        try
        {
            var nextSnapshot = _queueSnapshot with
            {
                Items = _queueSnapshot.Items
                    .Where(existing => existing.Id != item.Id)
                    .Append(item)
                    .OrderBy(existing => existing.CreatedAtUtc)
                    .ToArray()
            };
            var result = await _queueStore.SaveAsync(nextSnapshot, cancellationToken);
            if (!result.IsSuccess)
            {
                return result.Error;
            }

            _queueSnapshot = nextSnapshot;
            RefreshQueueItem(item);
            return null;
        }
        finally
        {
            _queueMutationLock.Release();
        }
    }

    /// <summary>
    /// Puts an item back on the queue after the media server rejected its stream link, so the
    /// next run re-resolves the video and continues from the bytes already on disk. Applied at
    /// most once per item so an item that keeps being rejected still reaches a Failed state.
    /// </summary>
    private async Task<bool> TryRequeueWithFreshLinksAsync(Guid itemId)
    {
        if (!_relinkedQueueItems.Add(itemId))
        {
            return false;
        }

        _preparedQueueWork.Remove(itemId);
        var error = await UpdateQueueItemAsync(itemId, DownloadQueueStatus.Queued, failureCode: null);
        if (error is not null)
        {
            _relinkedQueueItems.Remove(itemId);
            return false;
        }

        StatusMessage = "Stream link expired; refreshing and continuing";
        return true;
    }

    private static bool HasUsableMediaUrls(QueuedDownloadWork work)
    {
        if (work.LiveCapture is not null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        return SignedMediaUrl.IsUsable(work.Selection.Format.Url, now) &&
               (work.Selection.AudioFormat is null ||
                SignedMediaUrl.IsUsable(work.Selection.AudioFormat.Url, now));
    }

    /// <summary>
    /// Applies a terminal status to the in-memory queue when persistence failed, so the row stops
    /// showing an active transfer that no longer exists. Disk state is repaired on the next
    /// successful save or on the next load.
    /// </summary>
    private void ApplyUnsavedQueueStatus(
        Guid itemId,
        DownloadQueueStatus status,
        string? failureCode,
        long? completedBytes)
    {
        var item = _queueSnapshot.Items.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is null)
        {
            return;
        }

        var updated = item with
        {
            Status = status,
            FailureCode = failureCode,
            BytesReceived = completedBytes ?? item.BytesReceived,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        _queueSnapshot = _queueSnapshot with
        {
            Items = _queueSnapshot.Items
                .Select(existing => existing.Id == itemId ? updated : existing)
                .ToArray()
        };
        RefreshQueueItem(updated);
        NotifyQueueProperties();
    }

    /// <summary>
    /// Retries the queue load after a startup failure. The original cause is usually transient
    /// (antivirus or backup holding the file, a full disk), so latching the failure for the whole
    /// session would block every later download for no reason.
    /// </summary>
    private async Task<bool> TryRecoverQueueAsync(CancellationToken cancellationToken)
    {
        var reloaded = await _queueStore.LoadAsync(cancellationToken);
        if (!reloaded.IsSuccess)
        {
            return false;
        }

        _queueUnavailable = false;
        _undispatchableQueueItems.Clear();
        _queueSnapshot = reloaded.Value;
        RebuildQueueItems();
        NotifyQueueProperties();
        return true;
    }

    private async Task<TubeForgeError?> UpdateQueueItemAsync(
        Guid itemId,
        DownloadQueueStatus status,
        string? failureCode,
        long? completedBytes = null,
        CancellationToken cancellationToken = default)
    {
        var item = _queueSnapshot.Items.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is null)
        {
            return new TubeForgeError("Queue.ItemMissing", "The queued download no longer exists.");
        }

        var expectedLength = item.ExpectedLength;
        if (status == DownloadQueueStatus.Completed && completedBytes is > 0)
        {
            expectedLength = completedBytes;
        }

        return await UpsertQueueItemAsync(item with
        {
            ExpectedLength = expectedLength,
            BytesReceived = completedBytes ?? item.BytesReceived,
            AttemptCount = status == DownloadQueueStatus.Downloading && item.Status != DownloadQueueStatus.Downloading
                ? checked(item.AttemptCount + 1)
                : item.AttemptCount,
            Status = status,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            FailureCode = failureCode
        }, cancellationToken);
    }

    private static long PartialLength(string destination)
    {
        try
        {
            var segmentedBytes = SegmentedTransferProgress.GetCompletedBytes(destination);
            if (segmentedBytes is not null)
            {
                return segmentedBytes.Value;
            }

            var partialPath = destination + ".part";
            return File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long CompletedOrReservedLength(string destination)
    {
        try
        {
            var path = File.Exists(destination) ? destination : destination + ".part";
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void ClearAnalysis()
    {
        _metadata = null;
        _collection = null;
        CollectionItems.Clear();
        CaptionTracks = [];
        SelectedCaptionTrack = null;
        EmbedChapters = false;
        SplitChapters = false;
        EnableTrim = false;
        EnableSponsorBlock = false;
        _trimStartText = "00:00:00";
        _trimEndText = "00:00:00";
        OnPropertyChanged(nameof(TrimStartText));
        OnPropertyChanged(nameof(TrimEndText));
        _videoTitle = string.Empty;
        _videoChannel = string.Empty;
        _videoDuration = null;
        _thumbnailUrl = null;
        _allFormats = [];
        _downloadModes = BaseModeChoices;
        OnPropertyChanged(nameof(DownloadModes));
        SelectedDownloadMode = BaseModeChoices[0];
        ExtractionStatus = string.Empty;
        ErrorMessage = string.Empty;
        ProgressDetail = string.Empty;
        Formats.Clear();
        SelectedFormat = null;
        DownloadActionLabel = "Add to queue";
        RebuildFormatFilters(resetSelections: true);
        NotifyVideoProperties();
        OnPropertyChanged(nameof(HasCollection));
        OnPropertyChanged(nameof(CollectionTitle));
        OnPropertyChanged(nameof(CollectionSummary));
        OnPropertyChanged(nameof(CollectionSelectionSummary));
    }

    private void SetCollectionSelection(bool isSelected)
    {
        foreach (var item in CollectionItems)
        {
            item.IsSelected = isSelected;
        }

        CollectionSelectionChanged();
    }

    private void CollectionSelectionChanged()
    {
        OnPropertyChanged(nameof(CollectionSelectionSummary));
        SelectAllCollectionCommand.RaiseCanExecuteChanged();
        SelectNoneCollectionCommand.RaiseCanExecuteChanged();
        QueueCollectionCommand.RaiseCanExecuteChanged();
        SelectMissingCollectionCommand.RaiseCanExecuteChanged();
        SaveArchiveProfileCommand.RaiseCanExecuteChanged();
        CheckArchiveProfilesCommand.RaiseCanExecuteChanged();
        RemoveArchiveProfileCommand.RaiseCanExecuteChanged();
    }

    private void RebuildFormatFilters(bool resetSelections)
    {
        if (_updatingFormatFilters)
        {
            return;
        }

        _updatingFormatFilters = true;
        try
        {
            var modeFormats = IsAudioVideo
                ? AudioVideoVideoFormats()
                : FormatFilter.Apply(_allFormats, new FormatSelectionCriteria
                {
                    Mode = SelectedDownloadMode.Value
                });

            if (IsAudioOnly)
            {
                ResolutionOptions = [];
                SelectedResolution = null;
                VideoCodecOptions = [];
                SelectedVideoCodec = null;
                FrameRateOptions = [];
                SelectedFrameRate = null;
                DynamicRangeOptions = [];
                SelectedDynamicRange = null;

                BitrateOptions = WithAny(
                    "Best available",
                    modeFormats
                        .Where(format => format.Bitrate is > 0)
                        .Select(format => format.Bitrate!.Value)
                        .Distinct()
                        .OrderByDescending(value => value)
                        .Select(value => new FilterOption<long>($"{Math.Round(value / 1000d):0} kbps", value)));
                SelectedBitrate = Choose(
                    BitrateOptions,
                    resetSelections ? null : SelectedBitrate?.Value);

                var bitrateFormats = FilterBy(modeFormats, bitrate: SelectedBitrate?.Value);
                ContainerOptions = WithAny(
                    "Any native format",
                    bitrateFormats
                        .Select(format => format.Container)
                        .Distinct()
                        .OrderBy(ContainerOrder)
                        .Select(value => new FilterOption<MediaContainer>(AudioContainerLabel(value), value)));
                SelectedContainer = Choose(
                    ContainerOptions,
                    resetSelections ? null : SelectedContainer?.Value);

                var containerFormats = FilterBy(
                    bitrateFormats,
                    container: SelectedContainer?.Value);
                AudioCodecOptions = WithAny(
                    "Any audio codec",
                    containerFormats
                        .Select(format => format.AudioCodec)
                        .Distinct()
                        .OrderBy(AudioCodecOrder)
                        .Select(value => new FilterOption<AudioCodec>(AudioCodecLabel(value), value)));
                SelectedAudioCodec = Choose(
                    AudioCodecOptions,
                    resetSelections ? null : SelectedAudioCodec?.Value);
            }
            else
            {
                ResolutionOptions = WithAny(
                    "Best available",
                    modeFormats
                        .Where(format => format.Height is > 0)
                        .Select(format => format.Height!.Value)
                        .Distinct()
                        .OrderByDescending(value => value)
                        .Select(value => new FilterOption<int>(ResolutionLabel(value), value)));
                SelectedResolution = Choose(
                    ResolutionOptions,
                    resetSelections ? null : SelectedResolution?.Value);

                var resolutionFormats = FilterBy(modeFormats, height: SelectedResolution?.Value);
                ContainerOptions = WithAny(
                    "Any container · MP4 preferred",
                    resolutionFormats
                        .Select(format => format.Container)
                        .Distinct()
                        .OrderBy(ContainerOrder)
                        .Select(value => new FilterOption<MediaContainer>(ContainerLabel(value), value)));
                SelectedContainer = Choose(
                    ContainerOptions,
                    resetSelections ? null : SelectedContainer?.Value);

                var containerFormats = FilterBy(
                    resolutionFormats,
                    container: SelectedContainer?.Value);
                VideoCodecOptions = WithAny(
                    "Any video codec",
                    containerFormats
                        .Select(format => format.VideoCodec)
                        .Distinct()
                        .OrderBy(VideoCodecOrder)
                        .Select(value => new FilterOption<VideoCodec>(VideoCodecLabel(value), value)));
                SelectedVideoCodec = Choose(
                    VideoCodecOptions,
                    resetSelections ? null : SelectedVideoCodec?.Value);

                var codecFormats = FilterBy(
                    containerFormats,
                    videoCodec: SelectedVideoCodec?.Value);
                FrameRateOptions = WithAny(
                    "Any frame rate",
                    codecFormats
                        .Where(format => format.FramesPerSecond is > 0)
                        .Select(format => format.FramesPerSecond!.Value)
                        .Distinct()
                        .OrderByDescending(value => value)
                        .Select(value => new FilterOption<int>($"{value} FPS", value)));
                SelectedFrameRate = Choose(
                    FrameRateOptions,
                    resetSelections ? null : SelectedFrameRate?.Value);

                var frameRateFormats = FilterBy(
                    codecFormats,
                    framesPerSecond: SelectedFrameRate?.Value);
                DynamicRangeOptions = WithAny(
                    "Any dynamic range",
                    frameRateFormats
                        .Select(format => format.IsHdr)
                        .Distinct()
                        .OrderByDescending(value => value)
                        .Select(value => new FilterOption<bool>(value ? "HDR" : "SDR", value)));
                SelectedDynamicRange = Choose(
                    DynamicRangeOptions,
                    resetSelections ? null : SelectedDynamicRange?.Value);

                if (IsAudioVideo)
                {
                    var selectedVideoFormats = frameRateFormats
                        .Where(format => SelectedDynamicRange?.Value is null ||
                                         format.IsHdr == SelectedDynamicRange.Value.Value)
                        .ToArray();
                    var audioCandidates = CompatibleAudioFormats(selectedVideoFormats);
                    BitrateOptions = WithAny(
                        "Best available",
                        audioCandidates
                            .Where(format => format.Bitrate is > 0)
                            .Select(format => format.Bitrate!.Value)
                            .Distinct()
                            .OrderByDescending(value => value)
                            .Select(value => new FilterOption<long>($"{Math.Round(value / 1000d):0} kbps", value)));
                    SelectedBitrate = Choose(
                        BitrateOptions,
                        resetSelections ? null : SelectedBitrate?.Value);

                    var bitrateFormats = FilterBy(audioCandidates, bitrate: SelectedBitrate?.Value);
                    AudioCodecOptions = WithAny(
                        "Best compatible codec",
                        bitrateFormats
                            .Select(format => format.AudioCodec)
                            .Distinct()
                            .OrderBy(AudioCodecOrder)
                            .Select(value => new FilterOption<AudioCodec>(AudioCodecLabel(value), value)));
                    SelectedAudioCodec = Choose(
                        AudioCodecOptions,
                        resetSelections ? null : SelectedAudioCodec?.Value);
                }
                else
                {
                    BitrateOptions = [];
                    SelectedBitrate = null;
                    AudioCodecOptions = [];
                    SelectedAudioCodec = null;
                }
            }

            RefreshMatchingFormats();
        }
        finally
        {
            _updatingFormatFilters = false;
            RevalidateSelectedFormatCapabilities();
        }
    }

    private void RevalidateSelectedFormatCapabilities()
    {
        if (!CanEmbedSelectedCaption)
        {
            ClearEmbeddedCaptionSelections();
        }
        if (!CanEmbedChapters)
        {
            EmbedChapters = false;
        }
        if (!CanSplitChapters)
        {
            SplitChapters = false;
        }
        if (!CanTrim)
        {
            EnableTrim = false;
        }
        if (!CanUseSponsorBlock)
        {
            EnableSponsorBlock = false;
        }
    }

    private void RefreshDownloadModes()
    {
        var combined = AudioVideoVideoFormats();
        var audio = FormatsForMode(DownloadMode.AudioOnly);
        var video = FormatsForMode(DownloadMode.VideoOnly);

        _downloadModes =
        [
            new DownloadModeOption(
                DownloadMode.AudioVideo,
                "Audio + video",
                $"{OptionCount(combined.Count)} · up to {MaximumVideoQuality(combined)} · highest-quality audio"),
            new DownloadModeOption(
                DownloadMode.AudioOnly,
                "Audio only",
                $"{OptionCount(audio.Count)} · up to {MaximumAudioBitrate(audio)} · M4A/WebM"),
            new DownloadModeOption(
                DownloadMode.VideoOnly,
                "Video only",
                $"{OptionCount(video.Count)} · up to {MaximumVideoQuality(video)} · no audio")
        ];
        OnPropertyChanged(nameof(DownloadModes));
    }

    private IReadOnlyList<StreamFormat> FormatsForMode(DownloadMode mode) =>
        FormatFilter.Apply(_allFormats, new FormatSelectionCriteria { Mode = mode });

    private bool HasFormatsForMode(DownloadMode mode) => mode == DownloadMode.AudioVideo
        ? AudioVideoVideoFormats().Count > 0
        : FormatsForMode(mode).Count > 0;

    private IReadOnlyList<StreamFormat> AudioVideoVideoFormats()
    {
        var audio = _allFormats.Where(format => format.Kind == StreamKind.AudioOnly).ToArray();
        return _allFormats
            .Where(format => format.Kind == StreamKind.Progressive ||
                             format.Kind == StreamKind.VideoOnly &&
                             audio.Any(candidate => AdaptiveFormatSelector.AreMkvMuxCompatible(format, candidate)))
            .OrderByDescending(format => format.Kind == StreamKind.VideoOnly)
            .ThenByDescending(format => format.Height ?? 0)
            .ThenByDescending(format => format.FramesPerSecond ?? 0)
            .ThenByDescending(format => format.IsHdr)
            .ThenByDescending(format => format.Container == MediaContainer.Mp4)
            .ThenByDescending(format => format.Bitrate ?? 0)
            .ThenBy(format => format.FormatId)
            .ToArray();
    }

    private IReadOnlyList<StreamFormat> CompatibleAudioFormats(IEnumerable<StreamFormat> videos)
    {
        var materializedVideos = videos.Where(format => format.Kind == StreamKind.VideoOnly).ToArray();
        return _allFormats
            .Where(audio => audio.Kind == StreamKind.AudioOnly &&
                            materializedVideos.Any(video => AdaptiveFormatSelector.AreMkvMuxCompatible(video, audio)))
            .OrderByDescending(audio => audio.Bitrate ?? 0)
            .ThenByDescending(audio => audio.AudioSampleRate ?? 0)
            .ThenBy(audio => audio.FormatId)
            .ToArray();
    }

    private string CombinedModeNotice()
    {
        var combined = AudioVideoVideoFormats();
        return $"Up to {MaximumVideoQuality(combined)} with audio. TubeForge downloads the selected video and highest-quality compatible audio separately, then muxes both locally without re-encoding.";
    }

    private static string AudioProcessingNotice(OutputProfile output) =>
        output.BitrateKbps > 0
            ? $"{output.DisplayName} {output.BitrateKbps} kbps: bundled FFmpeg decodes and re-encodes locally."
            : $"{output.DisplayName}: bundled FFmpeg decodes and re-encodes locally.";

    private static string VideoProcessingNotice(OutputProfile output) =>
        $"{output.DisplayName}: selected source is downloaded first, then video and audio are re-encoded locally. " +
        "Slower than Original quality; requires additional temporary disk space.";

    private string VideoOnlyModeNotice() =>
        $"Up to {MaximumVideoQuality(FormatsForMode(DownloadMode.VideoOnly))}. " +
        "Video track only: no audio. Choose Audio + video for a locally muxed file with both tracks.";

    private void RefreshMatchingFormats()
    {
        if (IsAudioVideo)
        {
            var videos = AudioVideoVideoFormats()
                .Where(format =>
                    (SelectedResolution?.Value is null || format.Height == SelectedResolution.Value.Value) &&
                    (SelectedContainer?.Value is null || format.Container == SelectedContainer.Value.Value) &&
                    (SelectedVideoCodec?.Value is null || format.VideoCodec == SelectedVideoCodec.Value.Value) &&
                    (SelectedFrameRate?.Value is null || format.FramesPerSecond == SelectedFrameRate.Value.Value) &&
                    (SelectedDynamicRange?.Value is null || format.IsHdr == SelectedDynamicRange.Value.Value))
                .ToArray();
            var audio = _allFormats
                .Where(format => format.Kind == StreamKind.AudioOnly &&
                                 (SelectedBitrate?.Value is null || format.Bitrate == SelectedBitrate.Value.Value) &&
                                 (SelectedAudioCodec?.Value is null || format.AudioCodec == SelectedAudioCodec.Value.Value))
                .OrderByDescending(format => format.Bitrate ?? 0)
                .ThenByDescending(format => format.AudioSampleRate ?? 0)
                .ThenBy(format => format.FormatId)
                .ToArray();

            Formats.Clear();
            foreach (var video in videos)
            {
                if (video.Kind == StreamKind.Progressive)
                {
                    Formats.Add(new FormatItemViewModel(video));
                    continue;
                }

                var companion = AdaptiveFormatSelector.SelectCompanionAudio(video, audio);
                if (companion is not null)
                {
                    Formats.Add(new FormatItemViewModel(video, companion));
                }
            }

            CompleteFormatRefresh();
            return;
        }

        var criteria = new FormatSelectionCriteria
        {
            Mode = SelectedDownloadMode.Value,
            Height = HasVideoFilters ? SelectedResolution?.Value : null,
            Container = SelectedContainer?.Value,
            VideoCodec = HasVideoFilters ? SelectedVideoCodec?.Value : null,
            AudioCodec = IsAudioOnly ? SelectedAudioCodec?.Value : null,
            FramesPerSecond = HasVideoFilters ? SelectedFrameRate?.Value : null,
            Bitrate = IsAudioOnly ? SelectedBitrate?.Value : null,
            IsHdr = HasVideoFilters ? SelectedDynamicRange?.Value : null
        };

        Formats.Clear();
        foreach (var format in FormatFilter.Apply(_allFormats, criteria))
        {
            Formats.Add(new FormatItemViewModel(format));
        }

        CompleteFormatRefresh();
    }

    private void CompleteFormatRefresh()
    {
        SelectedFormat = Formats.FirstOrDefault();
        OnPropertyChanged(nameof(FormatCountLabel));
        OnPropertyChanged(nameof(DiagnosticsFormatSummary));
        if (_metadata is not null && !IsBusy)
        {
            StatusMessage = Formats.Count > 0
                ? "Choose exact output, then download"
                : "No output matches these filters";
        }
    }

    private void SetFilterSelection<T>(
        ref FilterOption<T>? field,
        FilterOption<T>? value,
        [CallerMemberName] string? propertyName = null) where T : struct
    {
        if (!Set(ref field, value, propertyName) || _updatingFormatFilters)
        {
            return;
        }

        MarkPresetCustom();
        RebuildFormatFilters(resetSelections: false);
    }

    private void ApplyDownloadPreset(DownloadPresetOption preset)
    {
        if (preset.Value == DownloadPresetKind.Custom)
        {
            return;
        }

        var targetMode = preset.Value == DownloadPresetKind.Mp3_320
            ? DownloadMode.AudioOnly
            : DownloadMode.AudioVideo;
        var mode = DownloadModes.FirstOrDefault(option => option.Value == targetMode);
        if (mode is null)
        {
            MarkPresetCustom(force: true);
            return;
        }

        var restoreTrim = EnableTrim;
        _applyingDownloadPreset = true;
        try
        {
            SelectedDownloadMode = mode;
            RebuildFormatFilters(resetSelections: true);
            switch (preset.Value)
            {
                case DownloadPresetKind.BestOriginal:
                    SelectedVideoProcessing = VideoProcessingChoices[0];
                    break;
                case DownloadPresetKind.WindowsCompatibleMp4:
                    SelectedVideoProcessing = VideoProcessingChoices.First(option =>
                        option.Value.Kind == OutputProfileKind.H264AacMp4);
                    break;
                case DownloadPresetKind.SmallFile:
                    SelectedVideoProcessing = VideoProcessingChoices.First(option =>
                        option.Value.Kind == OutputProfileKind.H265AacMp4);
                    SelectedResolution = ResolutionOptions
                        .Where(option => option.Value is > 0 and <= 720)
                        .OrderByDescending(option => option.Value)
                        .FirstOrDefault() ?? ResolutionOptions.FirstOrDefault();
                    break;
                case DownloadPresetKind.Mp3_320:
                    SelectedAudioProcessing = AudioProcessingChoices.First(option =>
                        option.Value == OutputProfile.Mp3(320));
                    break;
            }
        }
        finally
        {
            _applyingDownloadPreset = false;
            if (restoreTrim && CanTrim)
            {
                EnableTrim = true;
            }
        }
    }

    private static DownloadPresetKind DownloadPresetFromSettings(PreferredDownloadPreset preset) => preset switch
    {
        PreferredDownloadPreset.WindowsCompatibleMp4 => DownloadPresetKind.WindowsCompatibleMp4,
        PreferredDownloadPreset.SmallFile => DownloadPresetKind.SmallFile,
        PreferredDownloadPreset.Mp3_320 => DownloadPresetKind.Mp3_320,
        _ => DownloadPresetKind.BestOriginal
    };

    private static PreferredDownloadPreset DownloadPresetToSettings(DownloadPresetKind preset) => preset switch
    {
        DownloadPresetKind.WindowsCompatibleMp4 => PreferredDownloadPreset.WindowsCompatibleMp4,
        DownloadPresetKind.SmallFile => PreferredDownloadPreset.SmallFile,
        DownloadPresetKind.Mp3_320 => PreferredDownloadPreset.Mp3_320,
        _ => PreferredDownloadPreset.BestOriginal
    };

    private void MarkPresetCustom(bool force = false)
    {
        if ((!force && (_applyingDownloadPreset || _updatingFormatFilters)) ||
            _selectedDownloadPreset.Value == DownloadPresetKind.Custom)
        {
            return;
        }

        _selectedDownloadPreset = DownloadPresetChoices.First(option =>
            option.Value == DownloadPresetKind.Custom);
        OnPropertyChanged(nameof(SelectedDownloadPreset));
    }

    private void NotifyVideoProperties()
    {
        OnPropertyChanged(nameof(HasVideo));
        OnPropertyChanged(nameof(VideoTitle));
        OnPropertyChanged(nameof(VideoMetaLine));
        OnPropertyChanged(nameof(ThumbnailUrl));
        OnPropertyChanged(nameof(HasCaptions));
        OnPropertyChanged(nameof(HasChapters));
        OnPropertyChanged(nameof(CanEmbedSelectedCaption));
        OnPropertyChanged(nameof(CanEmbedChapters));
        OnPropertyChanged(nameof(CanSplitChapters));
        OnPropertyChanged(nameof(CanTrim));
        OnPropertyChanged(nameof(CanUseSponsorBlock));
        OnPropertyChanged(nameof(IsLiveCapture));
        OnPropertyChanged(nameof(IsUpcomingLiveCapture));
        OnPropertyChanged(nameof(LiveCaptureModeNotice));
        OnPropertyChanged(nameof(FormatCountLabel));
        RefreshCommands();
    }

    private void BusyStateChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        AnalyzeCommand.RaiseCanExecuteChanged();
        DownloadCommand.RaiseCanExecuteChanged();
        DownloadCaptionCommand.RaiseCanExecuteChanged();
        SaveThumbnailCommand.RaiseCanExecuteChanged();
        SaveMetadataCommand.RaiseCanExecuteChanged();
        SaveSettingsCommand.RaiseCanExecuteChanged();
        SelectAllCollectionCommand.RaiseCanExecuteChanged();
        SelectNoneCollectionCommand.RaiseCanExecuteChanged();
        QueueCollectionCommand.RaiseCanExecuteChanged();
        SelectMissingCollectionCommand.RaiseCanExecuteChanged();
        SaveArchiveProfileCommand.RaiseCanExecuteChanged();
        CheckArchiveProfilesCommand.RaiseCanExecuteChanged();
        RemoveArchiveProfileCommand.RaiseCanExecuteChanged();
        CancelAnalysisCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        CheckForUpdatesCommand.RaiseCanExecuteChanged();
        UpdateNowCommand.RaiseCanExecuteChanged();
    }

    private void CancelAnalysis()
    {
        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _analysisCancellation = null;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool TryGetLiveCaptureOptions(
        FormatItemViewModel selection,
        OutputProfile output,
        CaptionEmbedSelectionSet? caption,
        bool embedChapters,
        bool splitChapters,
        out LiveCaptureOptions? options,
        out TubeForgeError? error)
    {
        options = null;
        error = null;
        if (!selection.Format.IsLiveHls)
        {
            return true;
        }

        if (output != OutputProfile.Native || caption is not null || embedChapters || splitChapters ||
            EnableTrim || EnableSponsorBlock)
        {
            error = new TubeForgeError(
                "Hls.IncompatibleProcessing",
                "Live capture currently requires original-quality MKV without trim, captions, chapters, SponsorBlock, or conversion.");
            return false;
        }

        if (!int.TryParse(LiveDurationMinutesText, NumberStyles.None, CultureInfo.InvariantCulture,
                out var durationMinutes) || durationMinutes is < 1 or > 1_440 ||
            !int.TryParse(LiveMaximumSizeGiBText, NumberStyles.None, CultureInfo.InvariantCulture,
                out var maximumGiB) || maximumGiB is < 1 or > 100 ||
            !int.TryParse(LiveMaximumWaitMinutesText, NumberStyles.None, CultureInfo.InvariantCulture,
                out var waitMinutes) || waitMinutes is < 0 or > 1_440 ||
            selection.Format.IsLiveManifestPending && waitMinutes == 0)
        {
            error = new TubeForgeError(
                "Hls.InvalidLimits",
                "Choose 1–1440 capture minutes, 1–100 GiB, and up to 1440 wait minutes (at least 1 for an upcoming stream).");
            return false;
        }

        var selected = new LiveCaptureOptions(
            TimeSpan.FromMinutes(durationMinutes),
            maximumGiB * 1024L * 1024 * 1024,
            selection.Format.IsLiveManifestPending ? TimeSpan.FromMinutes(waitMinutes) : TimeSpan.Zero);
        if (!selected.IsValid)
        {
            error = new TubeForgeError("Hls.InvalidLimits", "The live capture limits are invalid.");
            return false;
        }

        options = selected;
        return true;
    }

    private bool TryGetSelectedTrim(
        out MediaTrimRange? trim,
        out TubeForgeError? error)
    {
        trim = null;
        error = null;
        if (!EnableTrim)
        {
            return true;
        }

        if (_metadata?.Duration is not { } duration || duration <= TimeSpan.Zero ||
            !TimeSpan.TryParse(TrimStartText.Trim(), CultureInfo.InvariantCulture, out var start) ||
            !TimeSpan.TryParse(TrimEndText.Trim(), CultureInfo.InvariantCulture, out var end))
        {
            error = new TubeForgeError(
                "Timeline.InvalidTrim",
                "Enter trim times as HH:MM:SS, for example 00:01:30.");
            return false;
        }

        start = TimeSpan.FromMilliseconds(Math.Floor(start.TotalMilliseconds));
        end = TimeSpan.FromMilliseconds(Math.Floor(end.TotalMilliseconds));
        if (!MediaTrimRange.TryCreate(start, end, out var selected) || end > duration)
        {
            error = new TubeForgeError(
                "Timeline.InvalidTrim",
                "Choose a trim end after its start and within the video duration.");
            return false;
        }

        trim = selected;
        return true;
    }

    private bool TryGetSponsorBlockSelection(
        OutputProfile output,
        out SponsorBlockSelection? selection,
        out TubeForgeError? error)
    {
        selection = null;
        error = null;
        if (!EnableSponsorBlock)
        {
            return true;
        }

        var categories = SponsorBlockCategories.None;
        categories |= SponsorCategory ? SponsorBlockCategories.Sponsor : SponsorBlockCategories.None;
        categories |= IntroCategory ? SponsorBlockCategories.Intro : SponsorBlockCategories.None;
        categories |= OutroCategory ? SponsorBlockCategories.Outro : SponsorBlockCategories.None;
        categories |= SelfPromotionCategory ? SponsorBlockCategories.SelfPromotion : SponsorBlockCategories.None;
        categories |= InteractionCategory ? SponsorBlockCategories.Interaction : SponsorBlockCategories.None;
        categories |= PreviewCategory ? SponsorBlockCategories.Preview : SponsorBlockCategories.None;
        categories |= FillerCategory ? SponsorBlockCategories.Filler : SponsorBlockCategories.None;
        if (categories == SponsorBlockCategories.None)
        {
            error = new TubeForgeError(
                "SponsorBlock.InvalidSelection",
                "Choose at least one SponsorBlock category.");
            return false;
        }

        var mode = SelectedSponsorBlockMode.Value ?? SponsorBlockMode.Chapters;
        if (mode == SponsorBlockMode.Remove && !output.RequiresTranscode)
        {
            error = new TubeForgeError(
                "SponsorBlock.TranscodeRequired",
                "Select an audio or video conversion preset before removing SponsorBlock segments.");
            return false;
        }

        if (mode == SponsorBlockMode.Remove && (EmbedChapters || SplitChapters))
        {
            error = new TubeForgeError(
                "SponsorBlock.IncompatibleTimelineMetadata",
                "SponsorBlock removal cannot combine with chapter workflows.");
            return false;
        }

        if (mode == SponsorBlockMode.Chapters &&
            (SelectedFormat is null || IsAudioOnly ||
             !IsCaptionContainerSupported(FinalMediaContainer(SelectedFormat, output))))
        {
            error = new TubeForgeError(
                "SponsorBlock.VideoRequired",
                "SponsorBlock chapter markers require an MP4, MKV, or WebM video output.");
            return false;
        }

        selection = new SponsorBlockSelection(mode, categories);
        return true;
    }

    private static string FormatTimelineInput(TimeSpan value) =>
        value.ToString("c", CultureInfo.InvariantCulture);

    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"h\:mm\:ss")
        : value.ToString(@"m\:ss");

    private static string FormatBytes(long bytes)
    {
        var megabytes = bytes / 1024d / 1024d;
        return megabytes >= 1024 ? $"{megabytes / 1024:0.00} GB" : $"{megabytes:0.0} MB";
    }

    private static string OptionCount(int count) => count == 1 ? "1 option" : $"{count} options";

    private static string MaximumVideoQuality(IEnumerable<StreamFormat> formats)
    {
        var height = formats.Select(format => format.Height ?? 0).DefaultIfEmpty(0).Max();
        return height > 0 ? ResolutionLabel(height) : "unavailable";
    }

    private static string MaximumAudioBitrate(IEnumerable<StreamFormat> formats)
    {
        var bitrate = formats.Select(format => format.Bitrate ?? 0).DefaultIfEmpty(0).Max();
        return bitrate > 0 ? $"{Math.Round(bitrate / 1000d):0} kbps" : "unknown bitrate";
    }

    private static IReadOnlyList<FilterOption<T>> WithAny<T>(
        string label,
        IEnumerable<FilterOption<T>> options) where T : struct =>
        [new FilterOption<T>(label, null), .. options];

    private static FilterOption<T>? Choose<T>(
        IReadOnlyList<FilterOption<T>> options,
        T? desired) where T : struct =>
        desired is null
            ? options.FirstOrDefault()
            : options.FirstOrDefault(option => EqualityComparer<T?>.Default.Equals(option.Value, desired)) ??
              options.FirstOrDefault();

    private static IReadOnlyList<StreamFormat> FilterBy(
        IEnumerable<StreamFormat> formats,
        int? height = null,
        MediaContainer? container = null,
        VideoCodec? videoCodec = null,
        int? framesPerSecond = null,
        long? bitrate = null) =>
        formats.Where(format =>
            (height is null || format.Height == height) &&
            (container is null || format.Container == container) &&
            (videoCodec is null || format.VideoCodec == videoCodec) &&
            (framesPerSecond is null || format.FramesPerSecond == framesPerSecond) &&
            (bitrate is null || format.Bitrate == bitrate))
        .ToArray();

    private static string ResolutionLabel(int height) => height switch
    {
        >= 4320 => $"{height}p · 8K",
        >= 2160 => $"{height}p · 4K",
        >= 1440 => $"{height}p · 2K",
        >= 1080 => $"{height}p · Full HD",
        >= 720 => $"{height}p · HD",
        _ => $"{height}p"
    };

    private static string ContainerLabel(MediaContainer container) => container switch
    {
        MediaContainer.Mp4 => "MP4",
        MediaContainer.WebM => "WebM",
        MediaContainer.ThreeGp => "3GP",
        _ => "Unknown container"
    };

    private static string AudioContainerLabel(MediaContainer container) => container switch
    {
        MediaContainer.Mp4 => "M4A / MP4",
        MediaContainer.WebM => "WebM",
        MediaContainer.ThreeGp => "3GP",
        _ => "Unknown format"
    };

    private static string VideoCodecLabel(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H.264 · broad compatibility",
        VideoCodec.Vp9 => "VP9 · efficient",
        VideoCodec.Av1 => "AV1 · most efficient",
        VideoCodec.Unknown => "Unknown codec",
        _ => "No video codec"
    };

    private static string AudioCodecLabel(AudioCodec codec) => codec switch
    {
        AudioCodec.Aac => "AAC · broad compatibility",
        AudioCodec.Opus => "Opus · efficient",
        AudioCodec.Vorbis => "Vorbis",
        AudioCodec.Unknown => "Unknown codec",
        _ => "No audio codec"
    };

    private static int ContainerOrder(MediaContainer container) => container switch
    {
        MediaContainer.Mp4 => 0,
        MediaContainer.WebM => 1,
        MediaContainer.ThreeGp => 2,
        _ => 3
    };

    private static int VideoCodecOrder(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => 0,
        VideoCodec.Vp9 => 1,
        VideoCodec.Av1 => 2,
        _ => 3
    };

    private static int AudioCodecOrder(AudioCodec codec) => codec switch
    {
        AudioCodec.Aac => 0,
        AudioCodec.Opus => 1,
        AudioCodec.Vorbis => 2,
        _ => 3
    };

    private enum AppPage
    {
        Download,
        Queue,
        Library,
        Settings,
        Diagnostics
    }

    private sealed record CollectionQueueConfiguration(
        string DestinationPath,
        string FileNameTemplate,
        bool IncludeQualityInFileName,
        ArchiveOutputPreset OutputPreset,
        ArchiveCaptionPreference CaptionPreference,
        bool EmbedChapters);

    private sealed record QueuedDownloadWork(
        VideoMetadata Metadata,
        FormatItemViewModel Selection,
        string Destination,
        OutputProfile Output = default,
        CaptionEmbedSelectionSet? Captions = null,
        bool EmbedChapters = false,
        bool SplitChapters = false,
        MediaTrimRange? Trim = null,
        SponsorBlockSelection? SponsorBlock = null,
        IReadOnlyList<SponsorBlockSegment>? SponsorSegments = null,
        LiveCaptureOptions? LiveCapture = null)
    {
        public IReadOnlyList<SponsorBlockSegment> SafeSponsorSegments => SponsorSegments ?? [];

        public bool RequiresSponsorChapters =>
            SponsorBlock is { Mode: SponsorBlockMode.Chapters } && SafeSponsorSegments.Count > 0;

        public bool RequiresMetadataFinalization => Captions is not null || EmbedChapters || RequiresSponsorChapters;

        public bool RequiresNativeTrim => Trim is not null && !Output.RequiresTranscode;

        public IReadOnlyList<MediaTrimRange> RemovedSponsorSegments =>
            SponsorBlock is { Mode: SponsorBlockMode.Remove }
                ? MediaTimelineEditor.MergeRemovalRanges(SafeSponsorSegments)
                : [];
    }
}

public sealed class UpdateAvailableEventArgs(Version version) : EventArgs
{
    public Version Version { get; } = version ?? throw new ArgumentNullException(nameof(version));
}

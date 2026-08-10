using System.Reflection;
using TubeForge.App.ViewModels;
using TubeForge.Core.Files;
using TubeForge.Core.Media;
using TubeForge.Core.Results;
using TubeForge.Core.YouTube;
using TubeForge.Downloads.Queue;
using TubeForge.Downloads.Archives;
using TubeForge.Tests.Framework;

namespace TubeForge.Tests.App;

public static class MainViewModelSelectionTests
{
    [Test]
    public static void GeneralCommandRefreshInvalidatesCollectionArchiveActions()
    {
        using var viewModel = new MainViewModel();
        var selectMissingInvalidations = 0;
        var saveArchiveInvalidations = 0;
        var checkArchivesInvalidations = 0;
        var removeArchiveInvalidations = 0;
        viewModel.SelectMissingCollectionCommand.CanExecuteChanged += (_, _) => selectMissingInvalidations++;
        viewModel.SaveArchiveProfileCommand.CanExecuteChanged += (_, _) => saveArchiveInvalidations++;
        viewModel.CheckArchiveProfilesCommand.CanExecuteChanged += (_, _) => checkArchivesInvalidations++;
        viewModel.RemoveArchiveProfileCommand.CanExecuteChanged += (_, _) => removeArchiveInvalidations++;
        var refresh = typeof(MainViewModel).GetMethod(
            "RefreshCommands",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainViewModel).FullName, "RefreshCommands");

        _ = refresh.Invoke(viewModel, null);

        Assert.Equal(1, selectMissingInvalidations);
        Assert.Equal(1, saveArchiveInvalidations);
        Assert.Equal(1, checkArchivesInvalidations);
        Assert.Equal(1, removeArchiveInvalidations);
    }

    [Test]
    public static void EveryDependentFilterAndProcessingCombinationKeepsAValidExactOutput()
    {
        using var viewModel = new MainViewModel();
        SetFormats(viewModel, BuildCompleteCatalog());

        var terminalCombinations = 0;
        foreach (var mode in viewModel.DownloadModes.ToArray())
        {
            viewModel.SelectedDownloadMode = mode;
            switch (mode.Value)
            {
                case DownloadMode.AudioOnly:
                    ExerciseAudioOnly(viewModel, ref terminalCombinations);
                    break;
                case DownloadMode.AudioVideo:
                    ExerciseVideo(viewModel, includeAudioFilters: true, ref terminalCombinations);
                    break;
                case DownloadMode.VideoOnly:
                    ExerciseVideo(viewModel, includeAudioFilters: false, ref terminalCombinations);
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected mode {mode.Value}.");
            }
        }

        Assert.True(terminalCombinations >= 500,
            $"Expected broad dependent-filter coverage; exercised {terminalCombinations} terminal combinations.");
    }

    [Test]
    public static void EveryDecodedVideoAudioCodecAndContainerPairHasTruthfulLosslessOutput()
    {
        var id = 1;
        foreach (var videoContainer in new[] { MediaContainer.Mp4, MediaContainer.WebM })
            foreach (var videoCodec in new[] { VideoCodec.H264, VideoCodec.Vp9, VideoCodec.Av1 })
                foreach (var audioContainer in new[] { MediaContainer.Mp4, MediaContainer.WebM })
                    foreach (var audioCodec in new[] { AudioCodec.Aac, AudioCodec.Opus, AudioCodec.Vorbis })
                    {
                        var video = Video(id++, videoContainer, videoCodec, 1080, 60, isHdr: false);
                        var audio = Audio(id++, audioContainer, audioCodec, 192_000, 48_000);
                        var native = AdaptiveFormatSelector.AreMuxCompatible(video, audio);
                        var output = AdaptiveFormatSelector.ResolveOutputContainer(video, audio);

                        Assert.Equal(native ? videoContainer : MediaContainer.Mkv, output);
                        var display = new FormatItemViewModel(video, audio);
                        Assert.Equal(output, display.OutputContainer);
                        Assert.True(display.TechnicalLabel.Contains("no re-encoding", StringComparison.Ordinal));
                    }
    }

    [Test]
    public static void Mp3FilenameQualityUsesTargetBitrateInsteadOfSourceBitrate()
    {
        using var viewModel = new MainViewModel
        {
            FileNameTemplateText = FileNameTemplate.Default,
            IncludeQualityInFileName = true
        };
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));
        var metadata = new VideoMetadata
        {
            Id = videoId,
            Title = "Fixture",
            Formats = []
        };
        var source = Audio(140, MediaContainer.WebM, AudioCodec.Opus, 143_000, 48_000);
        var render = typeof(MainViewModel).GetMethod(
            "RenderFileName",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainViewModel).FullName, "RenderFileName");

        var result = (Result<string>)render.Invoke(viewModel,
            [metadata, new FormatItemViewModel(source), null, 2, OutputProfile.Mp3(192)])!;

        Assert.True(result.IsSuccess);
        Assert.Equal("Fixture 192kbps", result.Value);

        viewModel.IncludeQualityInFileName = false;
        var cleanResult = (Result<string>)render.Invoke(viewModel,
            [metadata, new FormatItemViewModel(source), null, 2, OutputProfile.Mp3(192)])!;
        Assert.True(cleanResult.IsSuccess);
        Assert.Equal("Fixture", cleanResult.Value);
    }

    [Test]
    public static void AudioFilenamesUseSelectedQualityWithoutContainerSuffix()
    {
        using var viewModel = new MainViewModel
        {
            FileNameTemplateText = FileNameTemplate.Default,
            IncludeQualityInFileName = true
        };
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));
        var metadata = new VideoMetadata { Id = videoId, Title = "Fixture", Formats = [] };
        var source = new FormatItemViewModel(Audio(140, MediaContainer.WebM, AudioCodec.Opus, 143_000, 48_000));
        var render = typeof(MainViewModel).GetMethod(
            "RenderFileName",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainViewModel).FullName, "RenderFileName");

        foreach (var testCase in new[]
                 {
                     (OutputProfile.Native, "Fixture 143kbps"),
                     (OutputProfile.Aac(256), "Fixture 256kbps"),
                     (OutputProfile.Opus(160), "Fixture 160kbps"),
                     (OutputProfile.Wav, "Fixture lossless"),
                     (OutputProfile.Flac, "Fixture lossless")
                 })
        {
            var result = (Result<string>)render.Invoke(
                viewModel,
                [metadata, source, null, 2, testCase.Item1])!;
            Assert.True(result.IsSuccess);
            Assert.Equal(testCase.Item2, result.Value);
        }
    }

    [Test]
    public static void VideoPresetSelectionUsesPersistableProfileAndTruthfulFilename()
    {
        using var viewModel = new MainViewModel
        {
            FileNameTemplateText = FileNameTemplate.Default,
            IncludeQualityInFileName = true
        };
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));
        var metadata = new VideoMetadata { Id = videoId, Title = "Fixture", Formats = [] };
        var selection = new FormatItemViewModel(Progressive(18, 1080, 30));
        var profileFor = typeof(MainViewModel).GetMethod(
            "OutputProfileFor",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainViewModel).FullName, "OutputProfileFor");
        var render = typeof(MainViewModel).GetMethod(
            "RenderFileName",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainViewModel).FullName, "RenderFileName");

        foreach (var option in viewModel.VideoProcessingOptions)
        {
            viewModel.SelectedVideoProcessing = option;
            var selected = (OutputProfile)profileFor.Invoke(viewModel, [selection])!;
            Assert.Equal(option.Value.ForVideoHeight(1080), selected);
            Assert.True(OutputProfile.TryParseIdentity(selected.Identity, out var parsed));
            Assert.Equal(selected, parsed);
        }

        var filename = (Result<string>)render.Invoke(
            viewModel,
            [metadata, selection, null, 2, OutputProfile.H264AacMp4])!;
        Assert.True(filename.IsSuccess);
        Assert.Equal("Fixture 1080p", filename.Value);
    }

    [Test]
    public static void VideoFilenameAppliesOutputExtensionOnceBeforeQualitySuffix()
    {
        using var viewModel = new MainViewModel
        {
            FileNameTemplateText = "{title}.MP4",
            IncludeQualityInFileName = true
        };
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));
        var metadata = new VideoMetadata { Id = videoId, Title = "Fixture", Formats = [] };
        var selection = new FormatItemViewModel(Progressive(18, 1080, 30));
        var render = typeof(MainViewModel).GetMethod(
            "RenderFileName",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainViewModel).FullName, "RenderFileName");

        var withQuality = (Result<string>)render.Invoke(
            viewModel,
            [metadata, selection, null, 2, OutputProfile.H264AacMp4])!;
        Assert.True(withQuality.IsSuccess);
        Assert.Equal("Fixture 1080p", withQuality.Value);

        viewModel.IncludeQualityInFileName = false;
        var withoutQuality = (Result<string>)render.Invoke(
            viewModel,
            [metadata, selection, null, 2, OutputProfile.H264AacMp4])!;
        Assert.True(withoutQuality.IsSuccess);
        Assert.Equal("Fixture", withoutQuality.Value);
    }

    [Test]
    public static void QuickPresetsApplyTruthfulStateAndManualChangesBecomeCustom()
    {
        using var viewModel = new MainViewModel();
        SetFormats(viewModel, BuildCompleteCatalog());

        Assert.Equal(5, viewModel.DownloadPresets.Count);
        foreach (var preset in viewModel.DownloadPresets.Where(option =>
                     option.Value != DownloadPresetKind.Custom))
        {
            viewModel.SelectedDownloadPreset = preset;

            Assert.Equal(preset, viewModel.SelectedDownloadPreset);
            Assert.True(viewModel.SelectedFormat is not null);
            switch (preset.Value)
            {
                case DownloadPresetKind.BestOriginal:
                    Assert.Equal(DownloadMode.AudioVideo, viewModel.SelectedDownloadMode.Value);
                    Assert.Equal(OutputProfileKind.Native, viewModel.SelectedVideoProcessing.Value.Kind);
                    break;
                case DownloadPresetKind.WindowsCompatibleMp4:
                    Assert.Equal(DownloadMode.AudioVideo, viewModel.SelectedDownloadMode.Value);
                    Assert.Equal(OutputProfileKind.H264AacMp4, viewModel.SelectedVideoProcessing.Value.Kind);
                    break;
                case DownloadPresetKind.SmallFile:
                    Assert.Equal(DownloadMode.AudioVideo, viewModel.SelectedDownloadMode.Value);
                    Assert.Equal(OutputProfileKind.H265AacMp4, viewModel.SelectedVideoProcessing.Value.Kind);
                    Assert.Equal(720, viewModel.SelectedResolution?.Value);
                    break;
                case DownloadPresetKind.Mp3_320:
                    Assert.Equal(DownloadMode.AudioOnly, viewModel.SelectedDownloadMode.Value);
                    Assert.Equal(OutputProfile.Mp3(320), viewModel.SelectedAudioProcessing.Value);
                    break;
            }
        }

        viewModel.SelectedDownloadPreset = viewModel.DownloadPresets.First(option =>
            option.Value == DownloadPresetKind.WindowsCompatibleMp4);
        viewModel.SelectedVideoProcessing = viewModel.VideoProcessingOptions.First(option =>
            option.Value.Kind == OutputProfileKind.Native);
        Assert.Equal(DownloadPresetKind.Custom, viewModel.SelectedDownloadPreset.Value);

        viewModel.ShowAdvancedDownloadOptions = false;
        viewModel.SelectedDownloadPreset = viewModel.DownloadPresets.First(option =>
            option.Value == DownloadPresetKind.Custom);
        Assert.True(viewModel.ShowAdvancedDownloadOptions);
    }

    [Test]
    public static void QuickPresetsPreserveEnabledTrim()
    {
        using var viewModel = new MainViewModel();
        SetFormats(viewModel, BuildCompleteCatalog());
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));
        SetMetadata(viewModel, new VideoMetadata
        {
            Id = videoId,
            Title = "Fixture",
            Duration = TimeSpan.FromMinutes(2),
            Formats = BuildCompleteCatalog(),
            Chapters =
            [
                new VideoChapter { Title = "Intro", StartTime = TimeSpan.Zero },
                new VideoChapter { Title = "Main", StartTime = TimeSpan.FromMinutes(1) }
            ]
        });
        viewModel.EnableTrim = true;

        foreach (var preset in viewModel.DownloadPresets.Where(option =>
                     option.Value is not DownloadPresetKind.Custom))
        {
            viewModel.SelectedDownloadPreset = preset;

            Assert.True(viewModel.CanTrim);
            Assert.True(viewModel.EnableTrim, preset.Label);
        }

        viewModel.SelectedDownloadMode = viewModel.DownloadModes.First(option =>
            option.Value == DownloadMode.AudioVideo);
        var caption = new CaptionTrackOption(new CaptionTrack
        {
            Url = new Uri("https://www.youtube.com/api/timedtext?v=Fixture123_&lang=en"),
            LanguageCode = "en",
            Name = "English"
        });
        SetCaptionTracks(viewModel, [caption]);
        viewModel.SelectedCaptionTrack = caption;
        viewModel.EmbedSelectedCaption = true;
        viewModel.EmbedChapters = true;
        viewModel.SplitChapters = true;
        viewModel.SelectedContainer = viewModel.ContainerOptions.First(option =>
            option.Value == MediaContainer.WebM);

        Assert.True(viewModel.CanTrim);
        Assert.True(viewModel.EnableTrim, "Advanced container filter");
        Assert.True(viewModel.EmbedSelectedCaption, "Advanced container filter");
        Assert.True(viewModel.EmbedChapters, "Advanced container filter");
        Assert.True(viewModel.SplitChapters, "Advanced container filter");
        Assert.True(viewModel.SelectedFormat is not null);
    }

    [Test]
    public static void QueueCardShowsExplicitLocalProcessingPhase()
    {
        var now = new DateTimeOffset(2026, 8, 2, 2, 0, 0, TimeSpan.Zero);
        var item = new DownloadQueueItem
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            VideoId = "Fixture123_",
            FormatId = 22,
            SourceIdentity = "Fixture123_:22",
            DisplayTitle = "Fixture",
            DestinationPath = Path.Combine(Path.GetTempPath(), "fixture.mp4"),
            ExpectedLength = 1024,
            BytesReceived = 1024,
            AttemptCount = 1,
            Status = DownloadQueueStatus.Downloading,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var card = new QueueItemViewModel(
            item,
            _ => Task.CompletedTask,
            _ => { },
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => { });
        var updateProcessing = typeof(QueueItemViewModel).GetMethod(
            "UpdateProcessing",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.True(updateProcessing is not null);
        updateProcessing!.Invoke(card, ["Transcoding to H.265/AAC MP4 · local FFmpeg"]);

        Assert.Equal("PROCESSING", card.StatusLabel);
        Assert.Equal("Transcoding to H.265/AAC MP4 · local FFmpeg", card.ProgressDetail);
    }

    [Test]
    public static void ArchivePresetsChoosePersistableOutputsAndBoundSmallVideo()
    {
        var select = typeof(MainViewModel).GetMethod(
            "CollectionPresetSelection",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainViewModel).FullName, "CollectionPresetSelection");
        var formats = BuildCompleteCatalog();

        foreach (var testCase in new[]
                 {
                     (ArchiveOutputPreset.BestOriginal, OutputProfileKind.Native),
                     (ArchiveOutputPreset.WindowsCompatibleMp4, OutputProfileKind.H264AacMp4),
                     (ArchiveOutputPreset.SmallFile, OutputProfileKind.H265AacMp4),
                     (ArchiveOutputPreset.Mp3_320, OutputProfileKind.Mp3)
                 })
        {
            var value = ((FormatItemViewModel Selection, OutputProfile Output))select.Invoke(
                null,
                [formats, testCase.Item1])!;
            Assert.Equal(testCase.Item2, value.Output.Kind);
            Assert.True(value.Output.ForVideoHeight(value.Selection.Format.Height).IsValid);
            if (testCase.Item1 == ArchiveOutputPreset.SmallFile)
            {
                Assert.True(value.Selection.Format.Height is > 0 and <= 720);
            }
            if (testCase.Item1 == ArchiveOutputPreset.Mp3_320)
            {
                Assert.Equal(StreamKind.AudioOnly, value.Selection.Format.Kind);
                Assert.Equal(320, value.Output.BitrateKbps);
            }
        }
    }

    [Test]
    public static void SoftSubtitleSelectionRequiresSupportedVideoOutputAndResetsForAudioOnly()
    {
        using var viewModel = new MainViewModel();
        SetFormats(viewModel, BuildCompleteCatalog());
        viewModel.SelectedCaptionTrack = new CaptionTrackOption(new CaptionTrack
        {
            Url = new Uri("https://www.youtube.com/api/timedtext?v=Fixture123_&lang=en"),
            LanguageCode = "en",
            Name = "English"
        });

        Assert.True(viewModel.CanEmbedSelectedCaption);
        viewModel.EmbedSelectedCaption = true;
        Assert.True(viewModel.EmbedSelectedCaption);

        viewModel.SelectedDownloadMode = viewModel.DownloadModes.First(option =>
            option.Value == DownloadMode.AudioOnly);

        Assert.False(viewModel.CanEmbedSelectedCaption);
        Assert.False(viewModel.EmbedSelectedCaption);
    }

    [Test]
    public static void BuildsOrderedMultiLanguageCaptionSelectionFromCheckedTracks()
    {
        using var viewModel = new MainViewModel();
        var options = new[]
        {
            new CaptionTrackOption(new CaptionTrack
            {
                Url = new Uri("https://www.youtube.com/api/timedtext?v=Fixture123_&lang=en"),
                LanguageCode = "en",
                Name = "English"
            }),
            new CaptionTrackOption(new CaptionTrack
            {
                Url = new Uri("https://www.youtube.com/api/timedtext?v=Fixture123_&lang=de"),
                LanguageCode = "de",
                Name = "German",
                IsAutoGenerated = true
            })
        };
        options[0].IsSelectedForEmbedding = true;
        options[1].IsSelectedForEmbedding = true;
        var property = typeof(MainViewModel).GetProperty(nameof(MainViewModel.CaptionTracks))
            ?? throw new MissingMemberException(typeof(MainViewModel).FullName, nameof(MainViewModel.CaptionTracks));
        property.GetSetMethod(nonPublic: true)!.Invoke(viewModel, [options]);
        var method = typeof(MainViewModel).GetMethod(
            "SelectedCaptionEmbedSelections",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainViewModel).FullName, "SelectedCaptionEmbedSelections");

        var selected = (CaptionEmbedSelectionSet?)method.Invoke(viewModel, null);

        Assert.True(selected is not null);
        Assert.Equal("m.en,a.de", selected!.Value.Identity);
    }

    [Test]
    public static void ChapterEmbeddingRequiresTimedChaptersAndResetsForAudioOnly()
    {
        using var viewModel = new MainViewModel();
        SetFormats(viewModel, BuildCompleteCatalog());
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));
        SetMetadata(viewModel, new VideoMetadata
        {
            Id = videoId,
            Title = "Fixture",
            Duration = TimeSpan.FromMinutes(2),
            Formats = BuildCompleteCatalog(),
            Chapters =
            [
                new VideoChapter { Title = "Intro", StartTime = TimeSpan.Zero },
                new VideoChapter { Title = "Main", StartTime = TimeSpan.FromMinutes(1) }
            ]
        });

        Assert.True(viewModel.HasChapters);
        Assert.True(viewModel.CanEmbedChapters);
        Assert.True(viewModel.CanSplitChapters);
        viewModel.EmbedChapters = true;
        viewModel.SplitChapters = true;
        Assert.True(viewModel.EmbedChapters);
        Assert.True(viewModel.SplitChapters);

        viewModel.SelectedDownloadMode = viewModel.DownloadModes.First(option =>
            option.Value == DownloadMode.VideoOnly);
        Assert.True(viewModel.CanEmbedChapters);
        Assert.True(viewModel.CanSplitChapters);
        Assert.True(viewModel.EmbedChapters);
        Assert.True(viewModel.SplitChapters);

        viewModel.SelectedDownloadMode = viewModel.DownloadModes.First(option =>
            option.Value == DownloadMode.AudioOnly);

        Assert.False(viewModel.CanEmbedChapters);
        Assert.False(viewModel.CanSplitChapters);
        Assert.False(viewModel.EmbedChapters);
        Assert.False(viewModel.SplitChapters);
    }

    [Test]
    public static void TrimControlsValidateAgainstAnalyzedDuration()
    {
        using var viewModel = new MainViewModel();
        SetFormats(viewModel, BuildCompleteCatalog());
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));
        SetMetadata(viewModel, new VideoMetadata
        {
            Id = videoId,
            Title = "Fixture",
            Duration = TimeSpan.FromMinutes(2),
            Formats = BuildCompleteCatalog()
        });

        Assert.True(viewModel.CanTrim);
        viewModel.EnableTrim = true;
        viewModel.TrimStartText = "00:00:05.500";
        viewModel.TrimEndText = "00:01:00";
        var valid = InvokeSelectedTrim(viewModel);
        Assert.True(valid.Success);
        Assert.Equal(new MediaTrimRange(
            TimeSpan.FromSeconds(5.5),
            TimeSpan.FromMinutes(1)), valid.Range);

        viewModel.TrimEndText = "00:03:00";
        var invalid = InvokeSelectedTrim(viewModel);
        Assert.False(invalid.Success);
        Assert.Equal("Timeline.InvalidTrim", invalid.Error?.Code);
    }

    [Test]
    public static void SponsorBlockRemovalRequiresExplicitTranscodeProfile()
    {
        using var viewModel = new MainViewModel();
        SetFormats(viewModel, BuildCompleteCatalog());
        Assert.True(YouTubeVideoId.TryCreate("Fixture123_", out var videoId));
        SetMetadata(viewModel, new VideoMetadata
        {
            Id = videoId,
            Title = "Fixture",
            Duration = TimeSpan.FromMinutes(2),
            Formats = BuildCompleteCatalog(),
            Chapters = [new VideoChapter { Title = "Start", StartTime = TimeSpan.Zero }]
        });
        viewModel.EnableSponsorBlock = true;
        viewModel.SelectedSponsorBlockMode = viewModel.SponsorBlockModeOptions.First(option =>
            option.Value == SponsorBlockMode.Remove);

        var native = InvokeSponsorBlockSelection(viewModel, OutputProfile.Native);
        Assert.False(native.Success);
        Assert.Equal("SponsorBlock.TranscodeRequired", native.Error?.Code);

        var converted = InvokeSponsorBlockSelection(viewModel, OutputProfile.H264AacMp4);
        Assert.True(converted.Success);
        Assert.Equal(SponsorBlockMode.Remove, converted.Selection?.Mode);
        Assert.Equal(SponsorBlockCategories.Sponsor, converted.Selection?.Categories);

        var caption = new CaptionTrackOption(new CaptionTrack
        {
            Url = new Uri("https://www.youtube.com/api/timedtext?v=Fixture123_&lang=en"),
            LanguageCode = "en",
            Name = "English"
        })
        {
            IsSelectedForEmbedding = true
        };
        SetCaptionTracks(viewModel, [caption]);
        viewModel.SelectedCaptionTrack = caption;

        var captioned = InvokeSponsorBlockSelection(viewModel, OutputProfile.H264AacMp4);
        Assert.True(captioned.Success);
        Assert.True(viewModel.SponsorBlockModeNotice.Contains("captions are rebased", StringComparison.OrdinalIgnoreCase));

        viewModel.EmbedChapters = true;
        Assert.True(viewModel.EmbedChapters);
        var chaptered = InvokeSponsorBlockSelection(viewModel, OutputProfile.H264AacMp4);
        Assert.False(chaptered.Success);
        Assert.Equal("SponsorBlock.IncompatibleTimelineMetadata", chaptered.Error?.Code);
    }

    [Test]
    public static void LiveCaptureUsesBoundedOriginalMkvSettings()
    {
        using var viewModel = new MainViewModel();
        var live = Progressive(1_000_001, 1080, 30) with
        {
            Url = new Uri("https://manifest.googlevideo.com/live/master.m3u8"),
            Container = MediaContainer.Mkv,
            IsLiveHls = true,
            IsLiveManifestPending = true
        };
        SetFormats(viewModel, [live]);

        Assert.True(viewModel.IsLiveCapture);
        Assert.True(viewModel.IsUpcomingLiveCapture);
        viewModel.SelectedVideoProcessing = viewModel.VideoProcessingOptions.First(option =>
            option.Value.Kind == OutputProfileKind.H264AacMp4);
        Assert.Equal(OutputProfile.Native, viewModel.SelectedVideoProcessing.Value);
        viewModel.SelectedDownloadPreset = viewModel.DownloadPresets.First(option =>
            option.Value == DownloadPresetKind.WindowsCompatibleMp4);
        Assert.Equal(DownloadPresetKind.BestOriginal, viewModel.SelectedDownloadPreset.Value);

        var selected = InvokeLiveCapture(viewModel, viewModel.SelectedFormat!, OutputProfile.Native);
        Assert.True(selected.Success, selected.Error?.Message);
        Assert.Equal(TimeSpan.FromMinutes(60), selected.Options?.MaximumDuration);
        Assert.Equal(4L * 1024 * 1024 * 1024, selected.Options?.MaximumBytes);
        Assert.Equal(TimeSpan.FromMinutes(360), selected.Options?.MaximumWaitForStart);

        viewModel.LiveMaximumWaitMinutesText = "0";
        var invalid = InvokeLiveCapture(viewModel, viewModel.SelectedFormat!, OutputProfile.Native);
        Assert.False(invalid.Success);
        Assert.Equal("Hls.InvalidLimits", invalid.Error?.Code);
    }

    private static void ExerciseAudioOnly(MainViewModel viewModel, ref int terminalCombinations)
    {
        foreach (var bitrate in viewModel.BitrateOptions.ToArray())
        {
            viewModel.SelectedBitrate = bitrate;
            foreach (var container in viewModel.ContainerOptions.ToArray())
            {
                viewModel.SelectedContainer = container;
                foreach (var codec in viewModel.AudioCodecOptions.ToArray())
                {
                    viewModel.SelectedAudioCodec = codec;
                    foreach (var processing in viewModel.AudioProcessingOptions)
                    {
                        viewModel.SelectedAudioProcessing = processing;
                        AssertTerminalOutputs(viewModel, DownloadMode.AudioOnly);
                        terminalCombinations++;
                    }
                }
            }
        }
    }

    private static void ExerciseVideo(
        MainViewModel viewModel,
        bool includeAudioFilters,
        ref int terminalCombinations)
    {
        foreach (var resolution in viewModel.ResolutionOptions.ToArray())
        {
            viewModel.SelectedResolution = resolution;
            foreach (var container in viewModel.ContainerOptions.ToArray())
            {
                viewModel.SelectedContainer = container;
                foreach (var codec in viewModel.VideoCodecOptions.ToArray())
                {
                    viewModel.SelectedVideoCodec = codec;
                    foreach (var frameRate in viewModel.FrameRateOptions.ToArray())
                    {
                        viewModel.SelectedFrameRate = frameRate;
                        foreach (var dynamicRange in viewModel.DynamicRangeOptions.ToArray())
                        {
                            viewModel.SelectedDynamicRange = dynamicRange;
                            if (includeAudioFilters)
                            {
                                ExerciseMuxedAudioFilters(viewModel, ref terminalCombinations);
                            }
                            else
                            {
                                AssertTerminalOutputs(viewModel, DownloadMode.VideoOnly);
                                terminalCombinations++;
                            }
                        }
                    }
                }
            }
        }
    }

    private static void ExerciseMuxedAudioFilters(MainViewModel viewModel, ref int terminalCombinations)
    {
        foreach (var bitrate in viewModel.BitrateOptions.ToArray())
        {
            viewModel.SelectedBitrate = bitrate;
            foreach (var codec in viewModel.AudioCodecOptions.ToArray())
            {
                viewModel.SelectedAudioCodec = codec;
                foreach (var processing in viewModel.VideoProcessingOptions)
                {
                    viewModel.SelectedVideoProcessing = processing;
                    AssertTerminalOutputs(viewModel, DownloadMode.AudioVideo);
                    terminalCombinations++;
                }
            }
        }
    }

    private static void AssertTerminalOutputs(MainViewModel viewModel, DownloadMode mode)
    {
        Assert.True(viewModel.Formats.Count > 0,
            $"Mode {mode} produced an empty terminal option path.");
        Assert.True(viewModel.SelectedFormat is not null);
        foreach (var output in viewModel.Formats)
        {
            switch (mode)
            {
                case DownloadMode.AudioOnly:
                    Assert.Equal(StreamKind.AudioOnly, output.Format.Kind);
                    break;
                case DownloadMode.VideoOnly:
                    Assert.Equal(StreamKind.VideoOnly, output.Format.Kind);
                    Assert.True(output.AudioFormat is null);
                    break;
                case DownloadMode.AudioVideo:
                    Assert.True(output.Format.Kind is StreamKind.Progressive or StreamKind.VideoOnly);
                    if (output.Format.Kind == StreamKind.VideoOnly)
                    {
                        Assert.True(output.AudioFormat is not null);
                        Assert.True(AdaptiveFormatSelector.AreMkvMuxCompatible(output.Format, output.AudioFormat!));
                    }

                    break;
            }
        }
    }

    private static void SetFormats(MainViewModel viewModel, IReadOnlyList<StreamFormat> formats)
    {
        var field = typeof(MainViewModel).GetField("_allFormats", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(MainViewModel).FullName, "_allFormats");
        field.SetValue(viewModel, formats);
        var refresh = typeof(MainViewModel).GetMethod(
            "RefreshDownloadModes",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainViewModel).FullName, "RefreshDownloadModes");
        refresh.Invoke(viewModel, null);
        viewModel.SelectedDownloadMode = viewModel.DownloadModes[1];
        viewModel.SelectedDownloadMode = viewModel.DownloadModes[0];
    }

    private static void SetCaptionTracks(
        MainViewModel viewModel,
        IReadOnlyList<CaptionTrackOption> tracks)
    {
        var property = typeof(MainViewModel).GetProperty(nameof(MainViewModel.CaptionTracks))
            ?? throw new MissingMemberException(typeof(MainViewModel).FullName, nameof(MainViewModel.CaptionTracks));
        property.GetSetMethod(nonPublic: true)!.Invoke(viewModel, [tracks]);
    }

    private static void SetMetadata(MainViewModel viewModel, VideoMetadata metadata)
    {
        var field = typeof(MainViewModel).GetField("_metadata", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(MainViewModel).FullName, "_metadata");
        field.SetValue(viewModel, metadata);
    }

    private static (bool Success, MediaTrimRange? Range, TubeForge.Core.Errors.TubeForgeError? Error)
        InvokeSelectedTrim(MainViewModel viewModel)
    {
        var method = typeof(MainViewModel).GetMethod(
            "TryGetSelectedTrim",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainViewModel).FullName, "TryGetSelectedTrim");
        object?[] arguments = [null, null];
        var success = (bool)method.Invoke(viewModel, arguments)!;
        return (
            success,
            (MediaTrimRange?)arguments[0],
            (TubeForge.Core.Errors.TubeForgeError?)arguments[1]);
    }

    private static (bool Success, LiveCaptureOptions? Options, TubeForge.Core.Errors.TubeForgeError? Error)
        InvokeLiveCapture(
            MainViewModel viewModel,
            FormatItemViewModel selection,
            OutputProfile output)
    {
        var method = typeof(MainViewModel).GetMethod(
            "TryGetLiveCaptureOptions",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainViewModel).FullName, "TryGetLiveCaptureOptions");
        object?[] arguments = [selection, output, null, false, false, null, null];
        var success = (bool)method.Invoke(viewModel, arguments)!;
        return (
            success,
            (LiveCaptureOptions?)arguments[5],
            (TubeForge.Core.Errors.TubeForgeError?)arguments[6]);
    }

    private static (
        bool Success,
        SponsorBlockSelection? Selection,
        TubeForge.Core.Errors.TubeForgeError? Error) InvokeSponsorBlockSelection(
            MainViewModel viewModel,
            OutputProfile output)
    {
        var method = typeof(MainViewModel).GetMethod(
            "TryGetSponsorBlockSelection",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(MainViewModel).FullName,
                "TryGetSponsorBlockSelection");
        object?[] arguments = [output, null, null];
        var success = (bool)method.Invoke(viewModel, arguments)!;
        return (
            success,
            (SponsorBlockSelection?)arguments[1],
            (TubeForge.Core.Errors.TubeForgeError?)arguments[2]);
    }

    private static IReadOnlyList<StreamFormat> BuildCompleteCatalog()
    {
        var formats = new List<StreamFormat>();
        var id = 1;
        foreach (var height in new[] { 360, 720 })
            foreach (var frameRate in new[] { 30, 60 })
            {
                formats.Add(Progressive(id++, height, frameRate));
            }

        foreach (var container in new[] { MediaContainer.Mp4, MediaContainer.WebM })
            foreach (var codec in new[] { VideoCodec.H264, VideoCodec.Vp9, VideoCodec.Av1 })
                foreach (var height in new[] { 720, 1080, 2160 })
                    foreach (var frameRate in new[] { 30, 60 })
                        foreach (var isHdr in new[] { false, true })
                        {
                            formats.Add(Video(id++, container, codec, height, frameRate, isHdr));
                        }

        foreach (var container in new[] { MediaContainer.Mp4, MediaContainer.WebM })
            foreach (var codec in new[] { AudioCodec.Aac, AudioCodec.Opus, AudioCodec.Vorbis })
                foreach (var bitrate in new[] { 128_000L, 192_000L, 256_000L })
                    foreach (var sampleRate in new[] { 44_100, 48_000 })
                    {
                        formats.Add(Audio(id++, container, codec, bitrate, sampleRate));
                    }

        return formats;
    }

    private static StreamFormat Progressive(int id, int height, int frameRate) => new()
    {
        FormatId = id,
        Url = new Uri($"https://example.test/{id}"),
        Kind = StreamKind.Progressive,
        Container = MediaContainer.Mp4,
        Height = height,
        FramesPerSecond = frameRate,
        Bitrate = height * 10_000L,
        VideoCodec = VideoCodec.H264,
        AudioCodec = AudioCodec.Aac,
        ContentLength = height * 100_000L
    };

    private static StreamFormat Video(
        int id,
        MediaContainer container,
        VideoCodec codec,
        int height,
        int frameRate,
        bool isHdr) => new()
        {
            FormatId = id,
            Url = new Uri($"https://example.test/{id}"),
            Kind = StreamKind.VideoOnly,
            Container = container,
            Height = height,
            FramesPerSecond = frameRate,
            IsHdr = isHdr,
            Bitrate = height * frameRate * 100L,
            VideoCodec = codec,
            AudioCodec = AudioCodec.None,
            ContentLength = height * frameRate * 1_000L
        };

    private static StreamFormat Audio(
        int id,
        MediaContainer container,
        AudioCodec codec,
        long bitrate,
        int sampleRate) => new()
        {
            FormatId = id,
            Url = new Uri($"https://example.test/{id}"),
            Kind = StreamKind.AudioOnly,
            Container = container,
            Bitrate = bitrate,
            VideoCodec = VideoCodec.None,
            AudioCodec = codec,
            AudioSampleRate = sampleRate,
            ContentLength = bitrate
        };
}

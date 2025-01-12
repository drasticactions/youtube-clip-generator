// <copyright file="Program.cs" company="Drastic Actions">
// Copyright (c) Drastic Actions. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CliWrap;
using ConsoleAppFramework;
using FFMpegCore;
using YouTubeClipGenerator;
using YoutubeExplode;
using YoutubeExplode.Channels;
using YoutubeExplode.Common;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

var app = ConsoleApp.Create();
app.Add<AppCommands>();
app.Run(args);

/// <summary>
/// App Commands.
/// </summary>
#pragma warning disable SA1649 // File name should match first type name
public class AppCommands
#pragma warning restore SA1649 // File name should match first type name
{
    private ConsoleLog log;
    private YoutubeClient youtubeClient;
    private Random random = new();

    public AppCommands()
    {
#if DEBUG
        this.log = new ConsoleLog(true);
#else
        this.log = new ConsoleLog(false);
#endif

        this.youtubeClient = new YoutubeClient(new HttpClient() { Timeout = TimeSpan.FromSeconds(3) });
    }
    
    /// <summary>
    /// Generate a clip from a YouTube video from the top of the heatmap if available.
    /// </summary>
    /// <param name="videoUri">The video uri.</param>
    /// <param name="startTimeBefore">-sb, length of time to start before the hit.</param>
    /// <param name="startTimeAfter">-sa, length of time after to continue with the clip.</param>
    /// <param name="outputPath">-o, Output path.</param>
    /// <param name="videoResolution">-q, Video resolution. Defaults to the highest available.</param>
    /// <returns></returns>
    [Command("heatmap")]
    public async Task GetClipFromHeadmapAsync([Argument] string videoUri, int startTimeBefore = 2, int startTimeAfter = 5, string? outputPath = default, Resolution? videoResolution = default)
    {
        var videoId = VideoId.TryParse(videoUri);
        if (videoId == null)
        {
            this.log.LogError($"Invalid video URL: {videoUri}");
            return;
        }

        var client = new YoutubeClient();
        var video = await client.Videos.GetAsync(videoId.Value);
        if (video.Heatmap is null)
        {
            this.log.LogError("No heatmap available for this video.");
            return;
        }

        var heatmap = video.Heatmap;
        
        var test = heatmap.OrderByDescending(n => n.HeatMarkerIntensityScoreNormalized).ToList();
        var maxIndex = test[0].MarkerStart;

        var seekTime = maxIndex.TotalSeconds - startTimeBefore;
        var length = startTimeBefore + startTimeAfter;
        await ProcessVideoAsync(videoId.Value, (int)seekTime, length, false, outputPath, videoResolution);
    }

    /// <summary>
    /// Generate a clip from a YouTube video from the top of the heatmap if available.
    /// </summary>
    /// <param name="channelId">The video uri.</param>
    /// <param name="startTimeBefore">-sb, length of time to start before the hit.</param>
    /// <param name="startTimeAfter">-sa, length of time after to continue with the clip.</param>
    /// <param name="outputPath">-o, Output path.</param>
    /// <param name="videoResolution">-q, Video resolution. Defaults to the highest available.</param>
    /// <returns></returns>
    [Command("heatmap channel-id")]
    public async Task GetClipFromHeadmapChannelAsync([Argument] string channelId, int startTimeBefore = 4, int startTimeAfter = 4, string? outputPath = default, Resolution? videoResolution = default)
    {
        var channelIdParsed = ChannelId.TryParse(channelId);
        if (channelIdParsed == null)
        {
            this.log.LogError($"Invalid channel ID: {channelId}");
            return;
        }

        var client = new YoutubeClient();
        var channel = await client.Channels.GetAsync(channelIdParsed.Value);
        if (channel == null)
        {
            this.log.LogError($"Failed to get channel {channelId}");
            return;
        }

        var videos = await client.Channels.GetUploadsAsync(channelIdParsed.Value);
        var videoIds = new List<VideoId>();
        foreach (var video in videos)
        {
            videoIds.Add(video.Id);
        }

        foreach(var videoId in videoIds)
        {
            var video = await client.Videos.GetAsync(videoId);
            if (video.Heatmap is null)
            {
                this.log.LogError("No heatmap available for this video.");
                continue;
            }

            var heatmap = video.Heatmap;
            
            var test = heatmap.Where(n => n.MarkerStart.TotalSeconds > 2).OrderByDescending(n => n.HeatMarkerIntensityScoreNormalized).ToList();
            var maxIndex = test[0].MarkerStart;

            var seekTime = maxIndex.TotalSeconds - startTimeBefore;
            if (seekTime < 0)
            {
                seekTime = 0;
            }
            var length = startTimeBefore + startTimeAfter;
            await ProcessVideoAsync(videoId, (int)seekTime, length, false, outputPath, videoResolution);
        }
    }

    /// <summary>
    /// Generate a clip from a YouTube video chapters.
    /// </summary>
    /// <param name="videoUri">The YouTube Video URL or ID.</param>
    /// <param name="outputPath">-o, Output path for the video. Defaults to the current directory.</param>
    /// <returns>Task.</returns>
    [Command("chapter")]
    public async Task SplitVideoIntoChapters([Argument] string videoUri, string? outputPath = default)
    {
        var videoId = VideoId.TryParse(videoUri);
        if (videoId == null)
        {
            this.log.LogError($"Invalid video URL: {videoUri}");
            return;
        }

        var video = await this.youtubeClient.Videos.GetAsync(videoId.Value);
        var chapterPath = Path.Combine(outputPath ?? Environment.CurrentDirectory);

        var chapterJson = JsonSerializer.Serialize(video.Chapters, SourceGenerationContext.Default.ChapterDescriptionArray);
        var chapterJsonPath = Path.Combine(chapterPath, $"{videoId}_chapters.json");
        await File.WriteAllTextAsync(chapterJsonPath, chapterJson);

        for (int i = 0; i < video.Chapters.Count; i++)
        {
            ChapterDescription? chapter = video.Chapters[i];
            var chapterStart = chapter.TimeRangeStartMillis;
            var chapterEnd = video.Chapters.Count > i + 1 ? video.Chapters[i + 1].TimeRangeStartMillis : video.Duration!.Value.TotalMilliseconds;
            var chapterLength = chapterEnd - chapterStart;
            var chapterTitle = chapter.Title;
            var chapterFilename = ConvertToValidFilename($"{videoId}_{chapterTitle}.mp4");
            if (File.Exists(Path.Combine(chapterPath, chapterFilename)))
            {
                this.log.Log($"Chapter {chapterTitle} already exists.");
                continue;
            }
            this.log.Log($"Generating chapter {chapterTitle} from {TimeSpan.FromMilliseconds(chapterStart)} to {TimeSpan.FromMilliseconds(chapterEnd)}.");

            await ProcessVideoAsync(videoId.Value, (int)TimeSpan.FromMilliseconds(chapterStart).TotalSeconds, (int)TimeSpan.FromMilliseconds(chapterLength).TotalSeconds, false, chapterPath, outputName: chapterFilename);
        }
        
    }

    /// <summary>
    /// Generate a clip from a YouTube video.
    /// </summary>
    /// <param name="videoUri">The YouTube Video URL or ID.</param>
    /// <param name="startTimeInSeconds">-ss, the seek time in seconds for the start of the clip. When using Random Clip, sets the start time for the seed.</param>
    /// <param name="lengthInSeconds">-l, Length of the clip, defaults to 5.</param>
    /// <param name="randomClip">-r, Make random clip.</param>
    /// <param name="outputPath">-o, Output path for the video. Defaults to the current directory.</param>
    /// <param name="videoResolution">-q, Video resolution. Defaults to the highest available.</param>
    /// <returns></returns>
    [Command("video")]
    public async Task GetVideoAsync([Argument] string videoUri, int startTimeInSeconds = 0, int lengthInSeconds = 5, bool randomClip = false, string? outputPath = default, Resolution? videoResolution = default)
    {
        var videoId = VideoId.TryParse(videoUri);
        if (videoId == null)
        {
            this.log.LogError($"Invalid video URL: {videoUri}");
            return;
        }

        await ProcessVideoAsync(videoId.Value, startTimeInSeconds, lengthInSeconds, randomClip, outputPath, videoResolution);
    }

    /// <summary>
    /// Generate clips from a YouTube account channel id.
    /// </summary>
    /// <param name="channelId">The YouTube Channel ID.</param>
    /// <param name="clipsToGenerate">-c, Number of clips to generate. Defaults to 5.</param>
    /// <param name="startTimeInSeconds">-ss, the seek time in seconds for the start of the clip. When using Random Clip, sets the start time for the seed.</param>
    /// <param name="lengthInSeconds">-l, Length of the clip, defaults to 5.</param>
    /// <param name="randomClip">-r, Make random clip.</param>
    /// <param name="outputPath">-o, Output path for the video. Defaults to the current directory.</param>
    /// <param name="videoResolution">-q, Video resolution. Defaults to the highest available.</param>
    /// <returns></returns>
    [Command("channel-id")]
    public async Task GetVideosFromChannelId([Argument] string channelId, int clipsToGenerate = 5, int startTimeInSeconds = 0, int lengthInSeconds = 5, bool randomClip = false, string? outputPath = default, Resolution? videoResolution = default)
    {
        var videoIds = await GetVideoIdsFromChannelIdAsync(channelId, clipsToGenerate);
        if (videoIds == null)
        {
            this.log.LogError($"Failed to get video IDs for {channelId}");
            return;
        }

        videoIds = await PickRandomVideos(videoIds, clipsToGenerate);

        foreach (var videoId in videoIds)
        {
            await ProcessVideoAsync(videoId, startTimeInSeconds, lengthInSeconds, randomClip, outputPath, videoResolution);
        }
    }

    /// <summary>
    /// Generate clips from a YouTube account user.
    /// </summary>
    /// <param name="user">The YouTube username.</param>
    /// <param name="clipsToGenerate">-c, Number of clips to generate. Defaults to 5.</param>
    /// <param name="startTimeInSeconds">-ss, the seek time in seconds for the start of the clip. When using Random Clip, sets the start time for the seed.</param>
    /// <param name="lengthInSeconds">-l, Length of the clip, defaults to 5.</param>
    /// <param name="randomClip">-r, Make random clip.</param>
    /// <param name="outputPath">-o, Output path for the video. Defaults to the current directory.</param>
    /// <param name="videoResolution">-q, Video resolution. Defaults to the highest available.</param>
    /// <returns></returns>
    [Command("user")]
    public async Task GetVideosFromUser([Argument] string user, int clipsToGenerate = 5, int startTimeInSeconds = 0, int lengthInSeconds = 5, bool randomClip = false, string? outputPath = default, Resolution? videoResolution = default)
    {
        var videoIds = await GetVideoIdsFromUserAsync(user, clipsToGenerate);
        if (videoIds == null)
        {
            this.log.LogError($"Failed to get video IDs for {user}");
            return;
        }

        videoIds = await PickRandomVideos(videoIds, clipsToGenerate);

        foreach (var videoId in videoIds)
        {
            await ProcessVideoAsync(videoId, startTimeInSeconds, lengthInSeconds, randomClip, outputPath, videoResolution);
        }
    }

    /// <summary>
    /// Generate clips from a YouTube account slug.
    /// </summary>
    /// <param name="slug">The YouTube slug of the user.</param>
    /// <param name="clipsToGenerate">-c, Number of clips to generate. Defaults to 5.</param>
    /// <param name="startTimeInSeconds">-ss, the seek time in seconds for the start of the clip. When using Random Clip, sets the start time for the seed.</param>
    /// <param name="lengthInSeconds">-l, Length of the clip, defaults to 5.</param>
    /// <param name="randomClip">-r, Make random clip.</param>
    /// <param name="outputPath">-o, Output path for the video. Defaults to the current directory.</param>
    /// <param name="videoResolution">-q, Video resolution. Defaults to the highest available.</param>
    /// <returns></returns>
    [Command("slug")]
    public async Task GetVideosFromSlug([Argument] string slug, int clipsToGenerate = 5, int startTimeInSeconds = 0, int lengthInSeconds = 5, bool randomClip = false, string? outputPath = default, Resolution? videoResolution = default)
    {
        var videoIds = await GetVideoIdsFromSlugAsync(slug, clipsToGenerate);
        if (videoIds == null)
        {
            this.log.LogError($"Failed to get video IDs for {slug}");
            return;
        }

        videoIds = await PickRandomVideos(videoIds, clipsToGenerate);

        foreach (var videoId in videoIds)
        {
            await ProcessVideoAsync(videoId, startTimeInSeconds, lengthInSeconds, randomClip, outputPath, videoResolution);
        }
    }

    /// <summary>
    /// Generate clips from a YouTube account handle.
    /// </summary>
    /// <param name="handle">The YouTube Handle of the user.</param>
    /// <param name="clipsToGenerate">-c, Number of clips to generate. Defaults to 5.</param>
    /// <param name="startTimeInSeconds">-ss, the seek time in seconds for the start of the clip. When using Random Clip, sets the start time for the seed.</param>
    /// <param name="lengthInSeconds">-l, Length of the clip, defaults to 5.</param>
    /// <param name="randomClip">-r, Make random clip.</param>
    /// <param name="outputPath">-o, Output path for the video. Defaults to the current directory.</param>
    /// <param name="videoResolution">-q, Video resolution. Defaults to the highest available.</param>
    /// <returns></returns>
    [Command("handle")]
    public async Task GetVideosFromHandle([Argument] string handle, int clipsToGenerate = 5, int startTimeInSeconds = 0, int lengthInSeconds = 5, bool randomClip = false, string? outputPath = default, Resolution? videoResolution = default)
    {
        var videoIds = await GetVideoIdsFromHandleAsync(handle, clipsToGenerate);
        if (videoIds == null)
        {
            this.log.LogError($"Failed to get video IDs for {handle}");
            return;
        }

        videoIds = await PickRandomVideos(videoIds, clipsToGenerate);

        foreach (var videoId in videoIds)
        {
            await ProcessVideoAsync(videoId, startTimeInSeconds, lengthInSeconds, randomClip, outputPath, videoResolution);
        }
    }

    /// <summary>
    /// Generate clips from a YouTube playlist.
    /// </summary>
    /// <param name="playlistId">The YouTube Playlist.</param>
    /// <param name="clipsToGenerate">-c, Number of clips to generate. Defaults to 5.</param>
    /// <param name="startTimeInSeconds">-ss, the seek time in seconds for the start of the clip. When using Random Clip, sets the start time for the seed.</param>
    /// <param name="lengthInSeconds">-l, Length of the clip, defaults to 5.</param>
    /// <param name="randomClip">-r, Make random clip.</param>
    /// <param name="outputPath">-o, Output path for the video. Defaults to the current directory.</param>
    /// <param name="videoResolution">-q, Video resolution. Defaults to the highest available.</param>
    /// <returns></returns>
    [Command("playlist")]
    public async Task GetVideosFromPlaylistId([Argument] string playlistId, int clipsToGenerate = 5, int startTimeInSeconds = 0, int lengthInSeconds = 5, bool randomClip = false, string? outputPath = default, Resolution? videoResolution = default)
    {
        var videoIds = await GetVideoIdsFromPlaylistIdAsync(playlistId, clipsToGenerate);
        if (videoIds == null)
        {
            this.log.LogError($"Failed to get video IDs for {playlistId}");
            return;
        }

        videoIds = await PickRandomVideos(videoIds, clipsToGenerate);

        foreach (var videoId in videoIds)
        {
            await ProcessVideoAsync(videoId, startTimeInSeconds, lengthInSeconds, randomClip, outputPath, videoResolution);
        }
    }

    private Task<List<VideoId>> PickRandomVideos(List<VideoId> videoIds, int clipsToGenerate)
    {
        var randomVideos = new List<VideoId>();
        for (int i = 0; i < clipsToGenerate; i++)
        {
            var randomIndex = this.random.Next(0, videoIds.Count);
            randomVideos.Add(videoIds[randomIndex]);
        }
        return Task.FromResult(randomVideos);
    }

    private async Task<List<VideoId>?> GetVideoIdsFromPlaylistIdAsync(string playlistId, int clipsToGenerate)
    {
        try
        {
            var id = PlaylistId.TryParse(playlistId);
            if (id == null)
            {
                this.log.LogError($"Invalid playlist ID: {playlistId}");
                return null;
            }
            var playlist = await this.youtubeClient.Playlists.GetAsync(id.Value);
            if (playlist == null)
            {
                this.log.LogError($"Failed to get playlist {playlistId}");
                return null;
            }

            var videos = await this.youtubeClient.Playlists.GetVideosAsync(id.Value);
            var fullVideoList = new List<VideoId>();
            foreach (var video in videos)
            {
                fullVideoList.Add(video.Id);
            }
            this.log.Log($"Found {fullVideoList.Count} videos.");
            return fullVideoList.ToList();
        }
        catch (Exception ex)
        {
            this.log.LogError(ex.Message);
            return null;
        }
    }

    private async Task<List<VideoId>?> GetVideoIdsFromSlugAsync(string slug, int clipsToGenerate)
    {
        try
        {
            var channel = await this.youtubeClient.Channels.GetBySlugAsync(slug);
            return await GetVideoIdsFromChannelAsync(channel, clipsToGenerate);
        }
        catch (Exception ex)
        {
            this.log.LogError(ex.Message);
            return null;
        }
    }

    private async Task<List<VideoId>?> GetVideoIdsFromUserAsync(string user, int clipsToGenerate)
    {
        try
        {
            var channel = await this.youtubeClient.Channels.GetByUserAsync(user);
            return await GetVideoIdsFromChannelAsync(channel, clipsToGenerate);
        }
        catch (Exception ex)
        {
            this.log.LogError(ex.Message);
            return null;
        }
    }

    private async Task<List<VideoId>?> GetVideoIdsFromHandleAsync(string handle, int clipsToGenerate)
    {
        try
        {
            var channel = await this.youtubeClient.Channels.GetByHandleAsync(handle);
            return await GetVideoIdsFromChannelAsync(channel, clipsToGenerate);
        }
        catch (Exception ex)
        {
            this.log.LogError(ex.Message);
            return null;
        }
    }

    private async Task<List<VideoId>?> GetVideoIdsFromChannelIdAsync(ChannelId channelId, int clipsToGenerate)
    {
        try
        {
            var channel = await this.youtubeClient.Channels.GetAsync(channelId);
            return await GetVideoIdsFromChannelAsync(channel, clipsToGenerate);
        }
        catch (Exception ex)
        {
            this.log.LogError(ex.Message);
            return null;
        }
    }

    private async Task<List<VideoId>?> GetVideoIdsFromChannelAsync(Channel channel, int clipsToGenerate)
    {
        try
        {
            this.log.Log($"Getting videos from {channel.Title}");
            var videos = await this.youtubeClient.Channels.GetUploadsAsync(channel.Id);
            var fullVideoList = new List<VideoId>();
            foreach (var video in videos)
            {
                fullVideoList.Add(video.Id);
            }
            this.log.Log($"Found {fullVideoList.Count} videos.");
            return fullVideoList.ToList();
        }
        catch (Exception ex)
        {
            this.log.LogError(ex.Message);
            return null;
        }
    }

    private async Task ProcessVideoAsync(VideoId videoId, int seekTime, int length, bool randomClip, string? outputPath = default, Resolution? videoResolution = default, string? outputName = default)
    {
        var duration = await GetLengthOfVideoAsync(videoId);
        if (duration == null)
        {
            this.log.LogError("Failed to get video duration.");
            return;
        }

        try
        {
            if (randomClip)
            {
                var totalTime = (int)duration.Value.TotalSeconds - length;
                if (totalTime <= 0)
                {
                    this.log.LogError($"Video {videoId} is too short for a random clip.");
                    return;
                }

                seekTime = this.random.Next(seekTime, (int)duration.Value.TotalSeconds - length);
            }
        }
        catch (Exception ex)
        {
            this.log.LogError(ex.Message);
            return;
        }

        this.log.Log($"Generating clip from {videoId} at {TimeSpan.FromSeconds(seekTime)} for {length} seconds.");

        (var videoUri, var audioUri, var scaleSize) = await GetManifestUrisAsync(videoId, videoResolution);

        if (videoUri == null)
        {
            this.log.LogError("Failed to get video manifest URI.");
            return;
        }

        if (audioUri == null)
        {
            this.log.LogError("Failed to get audio manifest URI.");
            return;
        }

        await ProcessClipAsync(new Uri(videoUri), new Uri(audioUri), videoId.Value, seekTime, length, outputPath, videoResolution, scaleSize, outputName);
    }

    private async Task<TimeSpan?> GetLengthOfVideoAsync(VideoId videoId)
    {
        try
        {
            var video = await this.youtubeClient.Videos.GetAsync(videoId);
            return video.Duration;
        }
        catch (Exception ex)
        {
            this.log.LogError(ex.Message);
            return null;
        }
    }

    private async Task<(string?, string?, bool)> GetManifestUrisAsync(VideoId videoId, Resolution? videoResolution = default)
    {
        try
        {
            var manifest = await this.youtubeClient.Videos.Streams.GetManifestAsync(videoId);
            var streams = manifest.GetVideoOnlyStreams();
            var scaleSize = true;
            if (videoResolution is not null)
            {
                // if resolution starts with p, substring it. Otherwise, parse it.
                var resolution = videoResolution.Value.ToString();
                if (resolution.StartsWith("p"))
                {
                    resolution = resolution.Substring(1);
                }
                var videoInt = Int32.Parse(resolution);
                streams = streams.Where(n => n.VideoQuality.MaxHeight <= videoInt);
                scaleSize = false;
            }
            var videoInfo = streams.Where(n => n.Container == Container.Mp4).TryGetWithHighestVideoQuality();
            if (videoInfo == null)
            {
                this.log.LogError("No stream info found.");
                return (null, null, scaleSize);
            }

            var audioInfo = manifest.GetAudioOnlyStreams().Where(n => n.Container == Container.Mp4).TryGetWithHighestBitrate();
            if (audioInfo == null)
            {
                this.log.LogError("No audio stream info found.");
                return (null, null, scaleSize);
            }

            return (videoInfo.Url, audioInfo.Url, scaleSize);
        }
        catch (Exception ex)
        {
            this.log.LogError(ex.Message);
            return (null, null, false);
        }
    }

    private async Task<string> ProcessVideoClipAsync(Uri videoUri, string videoId, int seekTime, int length, string? defaultPath = default, Resolution? videoResolution = default, bool scaleSize = false, string? outputName = default)
    {
        defaultPath ??= Environment.CurrentDirectory;
        Directory.CreateDirectory(defaultPath);
        var videoFilename = outputName ?? ConvertToValidFilename($"{videoId}_video_{Guid.NewGuid()}.mp4");
        var videoPath = Path.Combine(defaultPath, videoFilename);

        var arguments = FFMpegArguments.FromUrlInput(videoUri, options =>
        {
            options.Seek(TimeSpan.FromSeconds(seekTime));
        }).OutputToFile(videoPath, true, options =>
        {
            options.EndSeek(TimeSpan.FromSeconds(seekTime + length));
        }).ProcessAsynchronously();

        var result = await arguments;
        if (!result)
        {
            this.log.LogError("Failed to generate clip.");
            return string.Empty;
        }
        return videoPath;
    }

    private async Task ProcessClipAsync(Uri videoUri, Uri audioUri, string videoId, int seekTime, int length, string? defaultPath = default, Resolution? videoResolution = default, bool scaleSize = false, string? outputName = default)
    {
        try
        {
            var stopWatch = new Stopwatch();
            defaultPath ??= Environment.CurrentDirectory;
            Directory.CreateDirectory(defaultPath);
            var videoFilename = outputName ?? ConvertToValidFilename($"{videoId}_{Guid.NewGuid()}.mp4");
            var videoPath = Path.Combine(defaultPath, videoFilename);

            var arguments = FFMpegArguments
                .FromUrlInput(videoUri, options =>
                {
                    options.Seek(TimeSpan.FromSeconds(seekTime));
                })
                .AddUrlInput(audioUri, options =>
                {
                    options.Seek(TimeSpan.FromSeconds(seekTime));
                })
                .OutputToFile(videoPath, true, options =>
                {
                    options.WithVideoCodec(FFMpegCore.Enums.VideoCodec.LibX264).WithAudioCodec(FFMpegCore.Enums.AudioCodec.Aac)
                    .WithFastStart().WithDuration(TimeSpan.FromSeconds(length));
                }
            );

            var list = arguments.Arguments;
            stopWatch.Start();
            var result = await arguments.ProcessAsynchronously();
            stopWatch.Stop();

            if (!result)
            {
                this.log.LogError("Failed to generate clip.");
                return;
            }

            this.log.Log($"Clip saved to {videoPath}");
            this.log.Log($"Clip took {stopWatch.ElapsedMilliseconds}ms to generate.");
        }
        catch (Exception ex)
        {
            this.log.LogError(ex.Message);
        }
    }

    static string ConvertToValidFilename(string input)
    {
        // Remove invalid characters
        string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        string invalidReStr = string.Format(@"[{0}]+", invalidChars);
        string sanitizedInput = Regex.Replace(input, invalidReStr, "_");

        // Trim leading/trailing whitespaces and dots
        sanitizedInput = sanitizedInput.Trim().Trim('.');

        // Replace - with underlines
        sanitizedInput = sanitizedInput.Replace("-", "_");

        // Ensure filename is not empty
        if (string.IsNullOrEmpty(sanitizedInput))
        {
            return "_";
        }

        // Ensure filename is not too long
        int maxFilenameLength = 255;
        if (sanitizedInput.Length > maxFilenameLength)
        {
            sanitizedInput = sanitizedInput.Substring(0, maxFilenameLength);
        }

        return sanitizedInput;
    }

    static string TimeSpanToFFmpeg(TimeSpan ts)
    {
        var milliseconds = ts.Milliseconds;
        var seconds = ts.Seconds;
        var minutes = ts.Minutes;
        var hours = (int)ts.TotalHours;

        return $"{hours:D}:{minutes:D2}:{seconds:D2}.{milliseconds:D3}";
    }

    string ResolutionToFFMpegScale(Resolution resolution)
    {
        return resolution switch
        {
            Resolution.p144 => "256:144",
            Resolution.p240 => "426:240",
            Resolution.p360 => "640:360",
            Resolution.p480 => "854:480",
            Resolution.p720 => "1280:720",
            Resolution.p1080 => "1920:1080",
            Resolution.p1440 => "2560:1440",
            Resolution.p2160 => "3840:2160",
            _ => "1920:1080",
        };
    }

    public enum Resolution
    {
        p144,
        p240,
        p360,
        p480,
        p720,
        p1080,
        p1440,
        p2160
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Internal;
using YoutubeExplode.Internal.Parsers;
using YoutubeExplode.Models;
using YoutubeExplode.Models.ClosedCaptions;
using YoutubeExplode.Models.MediaStreams;

namespace YoutubeExplode
{
    public partial class YoutubeClient
    {
        private async Task<VideoEmbedPageParser> GetVideoEmbedPageParserAsync(string videoId)
        {
            var url = $"https://www.youtube.com/embed/{videoId}?disable_polymer=true&hl=en";
            var raw = await _httpClient.GetStringAsync(url).ConfigureAwait(false);

            return VideoEmbedPageParser.Initialize(raw);
        }

        private async Task<VideoWatchPageParser> GetVideoWatchPageParserAsync(string videoId)
        {
            var url = $"https://www.youtube.com/watch?v={videoId}&disable_polymer=true&hl=en";
            var raw = await _httpClient.GetStringAsync(url).ConfigureAwait(false);

            return VideoWatchPageParser.Initialize(raw);
        }

        private async Task<VideoInfoParser> GetVideoInfoParserAsync(string videoId, string el, string sts)
        {
            // This parameter does magic and a lot of videos don't work without it
            var eurl = $"https://youtube.googleapis.com/v/{videoId}".UrlEncode();

            var url = $"https://www.youtube.com/get_video_info?video_id={videoId}&el={el}&sts={sts}&eurl={eurl}&hl=en";
            var raw = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            
            return VideoInfoParser.Initialize(raw);
        }

        private async Task<VideoInfoParser> GetVideoInfoParserAsync(string videoId, string sts = "")
        {
            // Get parser for 'el=embedded'
            var parser = await GetVideoInfoParserAsync(videoId, "embedded", sts).ConfigureAwait(false);

            // Check if video exists by verifying that video ID property is not empty
            if (parser.ParseId().IsBlank())
            {
                // Get native error code and error reason
                var errorCode = parser.ParseErrorCode();
                var errorReason = parser.ParseErrorReason();

                throw new VideoUnavailableException(videoId, errorCode, errorReason);
            }

            // If requested with "sts" parameter, it means that the calling code is interested in getting video info with streams.
            // For that we also need to make sure the video is fully available by checking for errors.
            if (sts.IsNotBlank() && parser.ParseErrorCode() != 0)
            {
                // Retry for "el=detailpage"
                parser = await GetVideoInfoParserAsync(videoId, "detailpage", sts).ConfigureAwait(false);

                // If there are still errors - throw
                if (parser.ParseErrorCode() != 0)
                {
                    // Get native error code and error reason
                    var errorCode = parser.ParseErrorCode();
                    var errorReason = parser.ParseErrorReason();

                    throw new VideoUnavailableException(videoId, errorCode, errorReason);
                }
            }

            return parser;
        }

        private async Task<PlayerSourceParser> GetPlayerSourceParserAsync(string sourceUrl)
        {
            var raw = await _httpClient.GetStringAsync(sourceUrl).ConfigureAwait(false);
            return PlayerSourceParser.Initialize(raw);
        }

        private async Task<DashManifestParser> GetDashManifestParserAsync(string dashManifestUrl)
        {
            var raw = await _httpClient.GetStringAsync(dashManifestUrl).ConfigureAwait(false);
            return DashManifestParser.Initialize(raw);
        }

        private async Task<PlayerContext> GetVideoPlayerContextAsync(string videoId)
        {
            // Get embed page config
            var configJson = await GetVideoEmbedPageConfigAsync(videoId).ConfigureAwait(false);

            // Extract values
            var sourceUrl = configJson["assets"]["js"].Value<string>();
            var sts = configJson["sts"].Value<string>();

            // Check if successful
            if (sourceUrl.IsBlank() || sts.IsBlank())
                throw new ParseException("Could not parse player context.");

            // Append host to source url
            sourceUrl = "https://www.youtube.com" + sourceUrl;

            return new PlayerContext(sourceUrl, sts);
        }

        private async Task<PlayerSource> GetVideoPlayerSourceAsync(string sourceUrl)
        {
            // Original code credit:
            // https://github.com/flagbug/YoutubeExtractor/blob/3106efa1063994fd19c0e967793315f6962b2d3c/YoutubeExtractor/YoutubeExtractor/Decipherer.cs
            // No copyright, MIT license

            // Try to resolve from cache first
            var playerSource = _playerSourceCache.GetOrDefault(sourceUrl);
            if (playerSource != null)
                return playerSource;

            // Get parser
            var parser = await GetPlayerSourceParserAsync(sourceUrl).ConfigureAwait(false);
            
            // Extract cipher operations
            var operations = parser.ParseCipherOperations();

            return _playerSourceCache[sourceUrl] = new PlayerSource(operations);
        }

        /// <inheritdoc />
        public async Task<Video> GetVideoAsync(string videoId)
        {
            videoId.GuardNotNull(nameof(videoId));

            if (!ValidateVideoId(videoId))
                throw new ArgumentException($"Invalid YouTube video ID [{videoId}].", nameof(videoId));

            // Get video info parser
            var videoInfoParser = await GetVideoInfoParserAsync(videoId).ConfigureAwait(false);

            // Extract info
            var author = videoInfoParser.ParseAuthor();
            var title = videoInfoParser.ParseTitle();
            var duration = videoInfoParser.ParseDuration();
            var keywords = videoInfoParser.ParseKeywords();
            var viewCount = videoInfoParser.ParseViewCount();
            var loudness = videoInfoParser.ParseLoudness();

            // Get video watch page parser
            var videoWatchPageParser = await GetVideoWatchPageParserAsync(videoId).ConfigureAwait(false);

            // Extract info
            var uploadDate = videoWatchPageParser.ParseUploadDate();
            var description = videoWatchPageParser.ParseDescription();
            var likeCount = videoWatchPageParser.ParseLikeCount();
            var dislikeCount = videoWatchPageParser.ParseDislikeCount();

            var statistics = new Statistics(viewCount, likeCount, dislikeCount);
            var thumbnails = new ThumbnailSet(videoId);

            return new Video(videoId, author, uploadDate, title, description, thumbnails, duration, keywords,
                statistics, loudness);
        }

        /// <inheritdoc />
        public async Task<Channel> GetVideoAuthorChannelAsync(string videoId)
        {
            videoId.GuardNotNull(nameof(videoId));

            if (!ValidateVideoId(videoId))
                throw new ArgumentException($"Invalid YouTube video ID [{videoId}].", nameof(videoId));

            // Get video info parser just to assert that the video exists
            await GetVideoInfoParserAsync(videoId).ConfigureAwait(false);

            // Get parser
            var parser = await GetVideoEmbedPageParserAsync(videoId).ConfigureAwait(false);

            // Extract info
            var id = parser.ParseChannelId();
            var title = parser.ParseChannelTitle();
            var logoUrl = parser.ParseChannelLogoUrl();

            return new Channel(id, title, logoUrl);
        }

        /// <inheritdoc />
        public async Task<MediaStreamInfoSet> GetVideoMediaStreamInfosAsync(string videoId)
        {
            videoId.GuardNotNull(nameof(videoId));

            if (!ValidateVideoId(videoId))
                throw new ArgumentException($"Invalid YouTube video ID [{videoId}].", nameof(videoId));

            // Get player context
            var playerContext = await GetVideoPlayerContextAsync(videoId).ConfigureAwait(false);

            // Get video info
            var videoInfo = await GetVideoInfoAsync(videoId, playerContext.Sts).ConfigureAwait(false);

            // Check if requires purchase
            if (videoInfo.ContainsKey("ypc_vid"))
            {
                var previewVideoId = videoInfo["ypc_vid"];
                throw new VideoRequiresPurchaseException(videoId, previewVideoId);
            }

            // Prepare stream info collections
            var muxedStreamInfoMap = new Dictionary<int, MuxedStreamInfo>();
            var audioStreamInfoMap = new Dictionary<int, AudioStreamInfo>();
            var videoStreamInfoMap = new Dictionary<int, VideoStreamInfo>();

            // Resolve muxed streams
            var muxedStreamInfosEncoded = videoInfo.GetOrDefault("url_encoded_fmt_stream_map");
            if (muxedStreamInfosEncoded.IsNotBlank())
            {
                foreach (var streamEncoded in muxedStreamInfosEncoded.Split(","))
                {
                    var streamInfoDic = UrlEx.SplitQuery(streamEncoded);

                    // Extract values
                    var itag = streamInfoDic["itag"].ParseInt();
                    var url = streamInfoDic["url"];
                    var sig = streamInfoDic.GetOrDefault("s");

#if RELEASE
                    if (!MediaStreamInfo.IsKnown(itag))
                        continue;
#endif

                    // Decipher signature if needed
                    if (sig.IsNotBlank())
                    {
                        var playerSource =
                            await GetVideoPlayerSourceAsync(playerContext.SourceUrl).ConfigureAwait(false);
                        sig = playerSource.Decipher(sig);
                        url = UrlEx.SetQueryParameter(url, "signature", sig);
                    }

                    // Probe stream and get content length
                    long contentLength;
                    using (var response = await _httpClient.HeadAsync(url).ConfigureAwait(false))
                    {
                        // Some muxed streams can be gone
                        if (response.StatusCode == HttpStatusCode.NotFound ||
                            response.StatusCode == HttpStatusCode.Gone)
                            continue;

                        // Ensure success
                        response.EnsureSuccessStatusCode();

                        // Extract content length
                        contentLength = response.Content.Headers.ContentLength ??
                                        throw new ParseException("Could not extract content length of muxed stream.");
                    }

                    var streamInfo = new MuxedStreamInfo(itag, url, contentLength);
                    muxedStreamInfoMap[itag] = streamInfo;
                }
            }

            // Resolve adaptive streams
            var adaptiveStreamInfosEncoded = videoInfo.GetOrDefault("adaptive_fmts");
            if (adaptiveStreamInfosEncoded.IsNotBlank())
            {
                foreach (var streamEncoded in adaptiveStreamInfosEncoded.Split(","))
                {
                    var streamInfoDic = UrlEx.SplitQuery(streamEncoded);

                    // Extract values
                    var itag = streamInfoDic["itag"].ParseInt();
                    var url = streamInfoDic["url"];
                    var sig = streamInfoDic.GetOrDefault("s");
                    var contentLength = streamInfoDic["clen"].ParseLong();
                    var bitrate = streamInfoDic["bitrate"].ParseLong();

#if RELEASE
                    if (!MediaStreamInfo.IsKnown(itag))
                        continue;
#endif

                    // Decipher signature if needed
                    if (sig.IsNotBlank())
                    {
                        var playerSource =
                            await GetVideoPlayerSourceAsync(playerContext.SourceUrl).ConfigureAwait(false);
                        sig = playerSource.Decipher(sig);
                        url = UrlEx.SetQueryParameter(url, "signature", sig);
                    }

                    // Check if audio
                    var isAudio = streamInfoDic["type"].Contains("audio/");

                    // If audio stream
                    if (isAudio)
                    {
                        var streamInfo = new AudioStreamInfo(itag, url, contentLength, bitrate);
                        audioStreamInfoMap[itag] = streamInfo;
                    }
                    // If video stream
                    else
                    {
                        // Parse additional data
                        var size = streamInfoDic["size"];
                        var width = size.SubstringUntil("x").ParseInt();
                        var height = size.SubstringAfter("x").ParseInt();
                        var resolution = new VideoResolution(width, height);
                        var framerate = streamInfoDic["fps"].ParseInt();
                        var qualityLabel = streamInfoDic["quality_label"];

                        var streamInfo = new VideoStreamInfo(itag, url, contentLength, bitrate, resolution, framerate,
                            qualityLabel);
                        videoStreamInfoMap[itag] = streamInfo;
                    }
                }
            }

            // Resolve dash streams
            var dashManifestUrl = videoInfo.GetOrDefault("dashmpd");
            if (dashManifestUrl.IsNotBlank())
            {
                // Parse signature
                var sig = Regex.Match(dashManifestUrl, @"/s/(.*?)(?:/|$)").Groups[1].Value;

                // Decipher signature if needed
                if (sig.IsNotBlank())
                {
                    var playerSource = await GetVideoPlayerSourceAsync(playerContext.SourceUrl).ConfigureAwait(false);
                    sig = playerSource.Decipher(sig);
                    dashManifestUrl = UrlEx.SetRouteParameter(dashManifestUrl, "signature", sig);
                }

                // Get the manifest
                var response = await _httpClient.GetStringAsync(dashManifestUrl).ConfigureAwait(false);
                var dashManifestXml = XElement.Parse(response).StripNamespaces();
                var streamsXml = dashManifestXml.Descendants("Representation");

                // Parse streams
                foreach (var streamXml in streamsXml)
                {
                    // Skip partial streams
                    if (streamXml.Descendants("Initialization").FirstOrDefault()?.Attribute("sourceURL")?.Value
                            .Contains("sq/") == true)
                        continue;

                    // Extract values
                    var itag = (int) streamXml.Attribute("id");
                    var url = (string) streamXml.Element("BaseURL");
                    var bitrate = (long) streamXml.Attribute("bandwidth");

#if RELEASE
                    if (!MediaStreamInfo.IsKnown(itag))
                        continue;
#endif

                    // Parse content length
                    var contentLength = Regex.Match(url, @"clen[/=](\d+)").Groups[1].Value.ParseLong();

                    // Check if audio stream
                    var isAudio = streamXml.Element("AudioChannelConfiguration") != null;

                    // If audio stream
                    if (isAudio)
                    {
                        var streamInfo = new AudioStreamInfo(itag, url, contentLength, bitrate);
                        audioStreamInfoMap[itag] = streamInfo;
                    }
                    // If video stream
                    else
                    {
                        // Parse additional data
                        var width = (int) streamXml.Attribute("width");
                        var height = (int) streamXml.Attribute("height");
                        var resolution = new VideoResolution(width, height);
                        var framerate = (int) streamXml.Attribute("frameRate");

                        var streamInfo = new VideoStreamInfo(itag, url, contentLength, bitrate, resolution, framerate);
                        videoStreamInfoMap[itag] = streamInfo;
                    }
                }
            }

            // Get the raw HLS stream playlist (*.m3u8)
            var hlsLiveStreamUrl = videoInfo.GetOrDefault("hlsvp");

            // Finalize stream info collections
            var muxedStreamInfos = muxedStreamInfoMap.Values.OrderByDescending(s => s.VideoQuality).ToArray();
            var audioStreamInfos = audioStreamInfoMap.Values.OrderByDescending(s => s.Bitrate).ToArray();
            var videoStreamInfos = videoStreamInfoMap.Values.OrderByDescending(s => s.VideoQuality).ToArray();

            return new MediaStreamInfoSet(muxedStreamInfos, audioStreamInfos, videoStreamInfos, hlsLiveStreamUrl);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<ClosedCaptionTrackInfo>> GetVideoClosedCaptionTrackInfosAsync(string videoId)
        {
            videoId.GuardNotNull(nameof(videoId));

            if (!ValidateVideoId(videoId))
                throw new ArgumentException($"Invalid YouTube video ID [{videoId}].", nameof(videoId));

            // Get video info
            var videoInfo = await GetVideoInfoAsync(videoId).ConfigureAwait(false);

            // Extract captions metadata
            var playerResponseRaw = videoInfo["player_response"];
            var playerResponseJson = JToken.Parse(playerResponseRaw);
            var captionTracksJson = playerResponseJson.SelectToken("$..captionTracks").EmptyIfNull();

            // Parse closed caption tracks
            var closedCaptionTrackInfos = new List<ClosedCaptionTrackInfo>();
            foreach (var captionTrackJson in captionTracksJson)
            {
                // Extract values
                var code = captionTrackJson["languageCode"].Value<string>();
                var name = captionTrackJson["name"]["simpleText"].Value<string>();
                var language = new Language(code, name);
                var isAuto = captionTrackJson["vssId"].Value<string>()
                    .StartsWith("a.", StringComparison.OrdinalIgnoreCase);
                var url = captionTrackJson["baseUrl"].Value<string>();

                // Enforce format
                url = UrlEx.SetQueryParameter(url, "format", "3");

                var closedCaptionTrackInfo = new ClosedCaptionTrackInfo(url, language, isAuto);
                closedCaptionTrackInfos.Add(closedCaptionTrackInfo);
            }

            return closedCaptionTrackInfos;
        }
    }
}
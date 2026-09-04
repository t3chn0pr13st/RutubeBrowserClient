using System.Net;
using System.Text;
using System.Text.Json;

namespace RutubeBrowserClient.Tests;

public sealed class VodAndMetadataTests
{
    [Fact]
    public async Task Identity_uses_current_public_visitor_endpoint_and_categories_parse_studio_fixture()
    {
        var handler = new RecordingHandler((request, _) => Task.FromResult(TestData.Json(
            request.Uri.AbsolutePath.EndsWith("/v2/accounts/visitor/", StringComparison.Ordinal)
                ? TestData.Fixture("identity.json")
                : TestData.Fixture("categories.json"))));
        await using var client = TestData.Client(handler);

        var identity = await client.Identity.GetAsync();
        var categories = await client.Categories.GetAllAsync();

        Assert.Equal("owner-42", identity.AccountId);
        Assert.True(identity.PhoneVerified);
        Assert.Equal("channel-77", identity.ChannelId);
        Assert.Equal("public.test", handler.Requests[0].Uri.Host);
        Assert.Equal("/api/v2/accounts/visitor/", handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal("vulp", System.Web.HttpUtility.ParseQueryString(handler.Requests[0].Uri.Query)["client"]);
        Assert.Collection(categories,
            x => { Assert.Equal("8", x.Id); Assert.True(x.IsActive); },
            x => { Assert.Equal("9", x.Id); Assert.False(x.IsActive); });
    }

    [Fact]
    public async Task Vod_upload_uses_studio_session_hidden_metadata_and_tus_without_browser_credentials()
    {
        var api = new RecordingHandler((request, _) => Task.FromResult(
            request.Method == HttpMethod.Post
                ? TestData.Json("{\"video\":\"video-20\",\"sid\":\"session-30\"}")
                : request.Method == HttpMethod.Patch
                    ? TestData.Json("{}")
                    : TestData.Json("{\"id\":\"video-20\",\"owner_id\":\"owner-42\",\"title\":\"Uploaded lesson\",\"description\":\"Description\",\"category\":{\"id\":63},\"is_hidden\":true,\"action_reason\":{\"id\":0,\"name\":\"\"},\"source_url\":\"https://rutube.test/video/private/video-20/?p=private-key\",\"embed_url\":\"https://rutube.test/play/embed/video-20/?p=private-key\"}")));
        var offset = 0L;
        var upload = new RecordingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Created);
                response.Headers.Location = new Uri("https://upload.test/files/upload-40");
                return Task.FromResult(response);
            }
            if (request.Method == HttpMethod.Head)
            {
                var response = new HttpResponseMessage(HttpStatusCode.NoContent);
                response.Headers.TryAddWithoutValidation("Upload-Offset", offset.ToString());
                return Task.FromResult(response);
            }
            offset += Encoding.UTF8.GetByteCount(request.Body);
            var patched = new HttpResponseMessage(HttpStatusCode.NoContent);
            patched.Headers.TryAddWithoutValidation("Upload-Offset", offset.ToString());
            return Task.FromResult(patched);
        });
        await using var client = TestData.Client(api, upload);
        var bytes = Encoding.UTF8.GetBytes("fake-video-data");
        var opens = 0;
        var source = RutubeUploadSource.Create("lesson.mp4", "video/mp4", bytes.Length, _ =>
        {
            opens++;
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        });

        var result = await client.Videos.UploadAsync(source, new RutubeVideoCreateRequest
        {
            Title = "Uploaded lesson",
            Description = "Description",
            CategoryId = "63",
            IsHidden = true,
            ClientReference = "vod-job-1"
        });

        Assert.Equal("video-20", result.Id);
        Assert.True(result.IsHidden);
        Assert.Equal("ready", result.Status);
        Assert.Equal(1, opens);

        Assert.Collection(api.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/uploader/upload_session/", request.Uri.AbsolutePath);
                Assert.Equal("vulp", System.Web.HttpUtility.ParseQueryString(request.Uri.Query)["client"]);
                Assert.NotNull(System.Web.HttpUtility.ParseQueryString(request.Uri.Query)["batch_id"]);
                Assert.Equal("vod-job-1", request.Headers["Idempotency-Key"]);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Patch, request.Method);
                Assert.Equal("/api/v2/video/video-20/", request.Uri.AbsolutePath);
                using var metadata = JsonDocument.Parse(request.Body);
                Assert.True(metadata.RootElement.GetProperty("is_hidden").GetBoolean());
                Assert.Equal("63", metadata.RootElement.GetProperty("category").GetString());
            },
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/api/v2/video/private/video-20/", request.Uri.AbsolutePath);
            });

        Assert.Equal([HttpMethod.Post, HttpMethod.Head, HttpMethod.Patch], upload.Requests.Select(x => x.Method));
        var tusCreate = upload.Requests[0];
        Assert.Equal("/upload/session-30", tusCreate.Uri.AbsolutePath);
        Assert.Equal(bytes.Length.ToString(), tusCreate.Headers["Upload-Length"]);
        Assert.Contains("sessionId ", tusCreate.Headers["Upload-Metadata"]);
        Assert.False(tusCreate.Headers.ContainsKey("Authorization"));
        Assert.False(tusCreate.Headers.ContainsKey("Cookie"));
        Assert.False(tusCreate.Headers.ContainsKey("X-CSRFToken"));
        Assert.Equal("application/offset+octet-stream", upload.Requests[2].ContentType);
        Assert.Equal(bytes.Length.ToString(), upload.Requests[2].Headers["Content-Length"]);
    }

    [Fact]
    public async Task Tus_resume_reads_remote_offset_and_does_not_upload_the_prefix_again()
    {
        var api = new RecordingHandler((_, _) => throw new InvalidOperationException("API not expected"));
        var upload = new RecordingHandler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.NoContent);
            response.Headers.TryAddWithoutValidation("Upload-Offset", request.Method == HttpMethod.Head ? "5" : "10");
            return Task.FromResult(response);
        });
        await using var client = TestData.Client(api, upload);
        var bytes = Encoding.UTF8.GetBytes("0123456789");
        var source = RutubeUploadSource.Create("lesson.mp4", "video/mp4", bytes.Length, _ =>
            ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)));
        var session = new RutubeVideoUploadSession
        {
            VideoId = "video-20",
            SessionId = "session-30",
            BatchId = "batch-1",
            UserId = "owner-42",
            Length = bytes.Length,
            Stage = RutubeVideoUploadStage.TransferReady,
            UploadUrl = "https://upload.test/files/upload-40"
        };

        var completed = await client.Videos.UploadAsync(session, source);

        Assert.Equal(RutubeVideoUploadStage.Uploaded, completed.Stage);
        Assert.Equal(10, completed.Offset);
        Assert.Equal("56789", upload.Requests.Single(x => x.Method == HttpMethod.Patch).Body);
        Assert.Equal("5", upload.Requests.Single(x => x.Method == HttpMethod.Patch).Headers["Upload-Offset"]);
    }

    [Fact]
    public async Task Private_video_readback_preserves_link_only_playback_and_embed_urls()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(TestData.Json(
            "{\"id\":\"video-20\",\"is_hidden\":true,\"action_reason\":{\"id\":0,\"name\":\"\"},\"source_url\":\"https://rutube.ru/video/private/video-20/?p=private-key\"}")));
        await using var client = TestData.Client(handler);

        var video = await client.Videos.GetPrivateAsync("video-20");

        Assert.True(video.IsHidden);
        Assert.Equal("ready", video.Status);
        Assert.Equal("https://rutube.ru/video/private/video-20/?p=private-key", video.PlaybackUrl!.ToString());
        Assert.Equal("https://rutube.ru/play/embed/video-20/?p=private-key", video.EmbedUrl!.ToString());
    }

    [Fact]
    public async Task Vod_get_update_delete_use_public_api_and_do_not_reupload()
    {
        var handler = new RecordingHandler((request, _) => Task.FromResult(
            request.Method == HttpMethod.Delete ? TestData.Json("{}") : TestData.Json(TestData.Fixture("video.json"))));
        var uploads = new RecordingHandler((_, _) => throw new InvalidOperationException("No upload expected"));
        await using var client = TestData.Client(handler, uploads);

        var current = await client.Videos.GetAsync("video-20");
        var updated = await client.Videos.UpdateAsync("video-20", new RutubeVideoUpdateRequest
        {
            Title = "Updated",
            Description = "Description",
            CategoryId = "8",
            IsHidden = true
        });
        await client.Videos.DeleteAsync("video-20");

        Assert.Equal("video-20", current.Id);
        Assert.Equal("video-20", updated.Id);
        Assert.Equal([HttpMethod.Get, HttpMethod.Patch, HttpMethod.Delete], handler.Requests.Select(x => x.Method));
        using var patch = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.True(patch.RootElement.GetProperty("is_hidden").GetBoolean());
    }

    [Fact]
    public async Task Thumbnail_enforces_provider_limits_before_network_and_uses_studio_endpoint()
    {
        var api = new RecordingHandler((_, _) => throw new InvalidOperationException("API not expected"));
        var upload = new RecordingHandler((_, _) => Task.FromResult(TestData.Json("{\"thumbnail_url\":\"https://rutube.test/cover.jpg\"}")));
        await using var client = TestData.Client(api, upload);
        var image = RutubeUploadSource.Create("cover.jpg", "image/jpeg", 100, _ =>
            ValueTask.FromResult<Stream>(new MemoryStream(new byte[100], false)));

        var url = await client.Live.UploadThumbnailAsync("live-100", image);

        Assert.Equal("https://rutube.test/cover.jpg", url!.ToString());
        Assert.Equal("/api/video/live-100/thumbnail/", upload.Requests.Single().Uri.AbsolutePath);
        var tooLarge = RutubeUploadSource.Create("cover.png", "image/png", 1024 * 1024 + 1, _ =>
            ValueTask.FromResult<Stream>(new MemoryStream()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.Live.UploadThumbnailAsync("live-100", tooLarge));
        Assert.Single(upload.Requests);
    }

    [Fact]
    public void Metadata_validation_matches_rutube_documented_limits()
    {
        var title = new string('x', 101);
        var request = new RutubeLiveCreateRequest { Title = title, Description = "", CategoryId = "8", ClientReference = "job" };
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => request.Validate());
        Assert.Contains("100", exception.Message);
        var longDescription = new RutubeLiveCreateRequest
        {
            Title = "ok",
            Description = new string('x', 5001),
            CategoryId = "8",
            ClientReference = "job"
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => longDescription.Validate());
    }
}

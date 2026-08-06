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
    public async Task Vod_upload_streams_multipart_and_returns_stable_video_identity()
    {
        var api = new RecordingHandler((_, _) => throw new InvalidOperationException("API handler not expected"));
        var upload = new RecordingHandler((request, _) => Task.FromResult(TestData.Json(TestData.Fixture("video.json"))));
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
            CategoryId = "8",
            ClientReference = "vod-job-1"
        });

        Assert.Equal("video-20", result.Id);
        Assert.Equal(1, opens);
        var request = upload.Requests.Single();
        Assert.Equal("/api/video/", request.Uri.AbsolutePath);
        Assert.StartsWith("multipart/form-data", request.ContentType);
        Assert.Contains("name=\"video_file\"; filename=\"lesson.mp4\"", request.Body);
        Assert.Contains("name=\"client_reference\"", request.Body);
        Assert.Equal("vod-job-1", request.Headers["Idempotency-Key"]);
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

using Amazon.S3;
using Amazon.S3.Model;
using Guild.Application.Services;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Guild.Tests.Services;

/// <summary>
/// Covers GuildEmojiService's S3 upload/delete/presigned-url logic. IAmazonS3 is a huge SDK
/// interface (unlike IMessageBus/IHubContext elsewhere in this suite) so it's faked with
/// NSubstitute rather than hand-rolled, mirroring RedisTestFactory's approach for
/// IConnectionMultiplexer.
/// </summary>
[TestFixture]
public class GuildEmojiServiceTests
{
    private IAmazonS3 _s3 = null!;
    private GuildEmojiService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _s3 = Substitute.For<IAmazonS3>();
        _service = new GuildEmojiService(_s3);
    }

    [TearDown]
    public void TearDown() => _s3.Dispose();

    private static IFormFile MakeFile(string contentType = "image/png", string content = "fake-image-bytes")
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var file = Substitute.For<IFormFile>();
        file.OpenReadStream().Returns(stream);
        file.ContentType.Returns(contentType);
        file.Length.Returns(stream.Length);
        return file;
    }

    [Test]
    public async Task UploadEmojiAsync_SendsPutObjectRequest_WithGuildScopedKey()
    {
        var file = MakeFile();

        await _service.UploadEmojiAsync(file, "guild-1", "emoji-1");

        await _s3.Received(1).PutObjectAsync(
            Arg.Is<PutObjectRequest>(r => r.Key == "emojis/guild-1/emoji-1" && r.ContentType == "image/png"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UploadEmojiAsync_DifferentGuildsOrEmojis_ProduceDifferentKeys()
    {
        var file1 = MakeFile();
        var file2 = MakeFile();

        await _service.UploadEmojiAsync(file1, "guild-1", "emoji-1");
        await _service.UploadEmojiAsync(file2, "guild-2", "emoji-1");

        await _s3.Received(1).PutObjectAsync(Arg.Is<PutObjectRequest>(r => r.Key == "emojis/guild-1/emoji-1"), Arg.Any<CancellationToken>());
        await _s3.Received(1).PutObjectAsync(Arg.Is<PutObjectRequest>(r => r.Key == "emojis/guild-2/emoji-1"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteEmojiAsync_SendsDeleteObjectRequest_WithMatchingKey()
    {
        await _service.DeleteEmojiAsync("guild-1", "emoji-1");

        await _s3.Received(1).DeleteObjectAsync(
            Arg.Is<DeleteObjectRequest>(r => r.Key == "emojis/guild-1/emoji-1"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void DeleteEmojiAsync_S3Throws_IsSwallowed_BestEffort()
    {
        _s3.DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonS3Exception("boom"));

        Assert.DoesNotThrowAsync(() => _service.DeleteEmojiAsync("guild-1", "emoji-1"));
    }

    [Test]
    public void GetPresignedUrl_ReturnsUrlFromS3Client()
    {
        _s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>()).Returns("https://cdn.example/emojis/guild-1/emoji-1");

        var url = _service.GetPresignedUrl("guild-1", "emoji-1");

        Assert.That(url, Is.EqualTo("https://cdn.example/emojis/guild-1/emoji-1"));
    }

    [Test]
    public void GetPresignedUrl_RequestsGetVerb_WithMatchingKey()
    {
        _service.GetPresignedUrl("guild-1", "emoji-1");

        _s3.Received(1).GetPreSignedURL(Arg.Is<GetPreSignedUrlRequest>(
            r => r.Key == "emojis/guild-1/emoji-1" && r.Verb == HttpVerb.GET));
    }
}

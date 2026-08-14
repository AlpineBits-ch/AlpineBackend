using System.Security.Claims;
using System.Text.Json.Nodes;
using AppEnvironment;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Echo.Entitlements.Wire;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Messaging.Application.Controllers;
using Messaging.Application.Dtos.Response;
using Messaging.Application.Services;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Messaging.Tests.Controllers;

/// <summary>
/// The upload endpoint's half of storage enforcement: which entitlement key a request resolves
/// against, what a refusal looks like on the wire, and what a reduction looks like.
/// </summary>
[TestFixture]
public class AttachmentUploadEntitlementTests
{
    private const long Mb = 1024L * 1024L;
    private const string GuildId = "guild-1";
    private const string UserId = "user-1";

    private TestMessagingContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private StubLedger _ledger = null!;
    private StubObjectStore _objects = null!;
    private string _licenseMode = null!;
    private string _billingUrl = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestMessagingContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _ledger = new StubLedger();
        _objects = new StubObjectStore();
        _licenseMode = Env.License.Mode;
        _billingUrl = Env.License.BillingServiceUrl;
    }

    [TearDown]
    public async Task TearDown()
    {
        Env.License.Mode = _licenseMode;
        Env.License.BillingServiceUrl = _billingUrl;
        await _context.DisposeAsync();
    }

    [Test]
    public async Task Upload_WithNoGuild_ResolvesTheUserKeyAndStores()
    {
        var controller = Controller(UserCeiling(25 * Mb));

        var result = await controller.UploadFileAsync([Sized("a.png", 1 * Mb)]);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            Assert.That(_ledger.Recorded, Is.Empty, "an upload with no guild consumes no guild's quota");
        });
    }

    [Test]
    public async Task Upload_OverTheCeiling_Is403_WithTheSharedRefusalShape()
    {
        var controller = Controller(UserCeiling(25 * Mb));

        var result = (ObjectResult)await controller.UploadFileAsync([Sized("raw.mov", 400 * Mb)]);
        var denial = (EntitlementDenialDto)result.Value!;

        Assert.Multiple(() =>
        {
            Assert.That(result.StatusCode, Is.EqualTo(EntitlementDenialDto.StatusCode),
                "one status for every entitlement refusal; 402 would be a lie for an operator ceiling, "
                + "which is the one code a self-hosted instance emits");
            Assert.That(denial.Code, Is.EqualTo("user_plan_limit"));
            Assert.That(denial.Code, Is.EqualTo(denial.Reason), "a denial's code is its reason code");
            Assert.That(denial.Key, Is.EqualTo("user.upload_max_bytes"));
            Assert.That(denial.BoundBy, Is.EqualTo(EntitlementBoundBy.User));
            Assert.That(denial.Requested!.Value, Is.EqualTo(400 * Mb));
            Assert.That(denial.Granted!.Value, Is.EqualTo(25 * Mb));
            Assert.That(denial.Retryable, Is.False);
        });
    }

    [Test]
    public async Task Upload_IntoAFullGuild_NamesTheQuotaKeyRatherThanTheUploadCeiling()
    {
        _ledger.Seed(GuildId, 100 * Mb);
        var controller = Controller(GuildQuota(100 * Mb), allowGuild: true);

        var result = (ObjectResult)await controller.UploadFileAsync([Sized("a.png", 1 * Mb)], GuildId);
        var denial = (EntitlementDenialDto)result.Value!;

        Assert.Multiple(() =>
        {
            Assert.That(result.StatusCode, Is.EqualTo(EntitlementDenialDto.StatusCode));
            Assert.That(denial.Key, Is.EqualTo("storage.guild_quota_bytes"),
                "the key is how the client tells 'your file is too big' from 'the guild is full'");
            Assert.That(denial.Reason, Is.EqualTo("guild_plan_limit"));
            Assert.That(denial.Subject.Kind, Is.EqualTo("guild"));
            Assert.That(denial.Subject.Id, Is.EqualTo(GuildId));
            Assert.That(_ledger.Used(GuildId), Is.EqualTo(100 * Mb),
                "a full guild keeps everything it has; only growth is frozen");
        });
    }

    [Test]
    public async Task Upload_OnAnInstanceThatSellsNothing_OffersNoRemedy()
    {
        var controller = Controller(UserCeiling(25 * Mb));

        var result = (ObjectResult)await controller.UploadFileAsync([Sized("raw.mov", 400 * Mb)]);
        var denial = (EntitlementDenialDto)result.Value!;

        Assert.Multiple(() =>
        {
            Assert.That(denial.Remedy, Is.EqualTo(EntitlementRemedyCodes.None),
                "the shipped license mode is selfhost, where an upgrade link points at a service that "
                + "is not deployed");
            Assert.That(denial.ActorCanRemedy, Is.False);
        });
    }

    [Test]
    public async Task Upload_OnAHostedInstance_OffersTheUpgradeForTheSideThatBound()
    {
        Env.License.Mode = "hosted";
        Env.License.BillingServiceUrl = "http://billing";

        var controller = Controller(UserCeiling(25 * Mb));

        var result = (ObjectResult)await controller.UploadFileAsync([Sized("raw.mov", 400 * Mb)]);
        var denial = (EntitlementDenialDto)result.Value!;

        Assert.Multiple(() =>
        {
            Assert.That(denial.Remedy, Is.EqualTo(EntitlementRemedyCodes.UpgradeUser));
            Assert.That(denial.ActorCanRemedy, Is.True, "the caller is the person who would upgrade");
        });
    }

    [Test]
    public async Task Upload_PartialBatch_Is200_AndCarriesADegradationPerRejectedFile()
    {
        var controller = Controller(UserCeiling(25 * Mb));

        var result = (OkObjectResult)await controller.UploadFileAsync(
            [Sized("small.png", 1 * Mb), Sized("huge.mov", 400 * Mb)]);

        var body = (JsonObject)result.Value!;
        var degradations = (JsonArray)body[EntitlementResponses.PropertyName]!;

        Assert.Multiple(() =>
        {
            Assert.That(body["attachments"]!.AsArray(), Has.Count.EqualTo(1),
                "one oversized file no longer refuses the whole batch");
            Assert.That(degradations, Has.Count.EqualTo(1));
            Assert.That(degradations[0]!["key"]!.GetValue<string>(), Is.EqualTo("user.upload_max_bytes"));
            Assert.That(degradations[0]!["reason"]!.GetValue<string>(), Is.EqualTo("user_plan_limit"));
            Assert.That(degradations[0]!["requested"]!["value"]!.GetValue<long>(), Is.EqualTo(400 * Mb));
            Assert.That(_context.Attachments.Count(), Is.EqualTo(1), "only the stored file gets a row");
        });
    }

    [Test]
    public async Task Upload_ThatFitsEntirely_ReturnsTheV1ArrayUntouched()
    {
        var controller = Controller(UserCeiling(25 * Mb));

        var result = (OkObjectResult)await controller.UploadFileAsync([Sized("small.png", 1 * Mb)]);

        Assert.That(result.Value, Is.InstanceOf<IEnumerable<CreateAttachmentResponse>>(),
            "nothing was reduced, so the response has to be byte-identical to what v1 sends today");
    }

    [Test]
    public async Task Usage_ReturnsTheGuildsConsumedBytes()
    {
        _ledger.Seed(GuildId, 42 * Mb);
        var controller = Controller(GuildQuota(100 * Mb), allowGuild: true);

        var result = (OkObjectResult)await controller.GetStorageUsageAsync(GuildId);
        var usage = (EntitlementUsageDto)result.Value!;

        Assert.That(usage.Used["storage.guild_quota_bytes"], Is.EqualTo(42 * Mb));
    }

    [Test]
    public async Task Usage_IsGatedOnTheSamePermissionAsUploading()
    {
        var controller = Controller(GuildQuota(100 * Mb), allowGuild: false);

        var result = await controller.GetStorageUsageAsync(GuildId);

        Assert.That(result, Is.InstanceOf<ForbidResult>());
    }

    [Test]
    public async Task Upload_NamingAGuildTheCallerCannotAttachFilesIn_IsForbidden()
    {
        var controller = Controller(GuildQuota(100 * Mb), allowGuild: false);

        var result = await controller.UploadFileAsync([Sized("a.png", 1 * Mb)], GuildId);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidResult>(),
                "without this the new parameter is a way to fill up a guild you are not in");
            Assert.That(_ledger.Recorded, Is.Empty);
        });
    }

    [Test]
    public async Task Upload_Unauthenticated_StillReturnsBadRequest()
    {
        var controller = Controller(UserCeiling(25 * Mb), user: TestPrincipal.Anonymous());

        var result = await controller.UploadFileAsync([]);

        Assert.That(result, Is.InstanceOf<BadRequestResult>());
    }

    // ══════════════════════════════════════════════════════════════════════════ Fixtures
    // ══════════════════════════════════════════════════════════════════════════

    private AttachmentController Controller(
        EntitlementResolver resolver, bool allowGuild = true, ClaimsPrincipal? user = null)
    {
        var bus = new FakeMessageBus(message => message switch
        {
            HasUserPermissionToGuildRequest request => new HasUserPermissionToGuildResponse
            {
                GuildId = request.GuildId,
                UserId = request.UserId,
                IsAllowed = allowGuild,
                Permission = request.Permission,
            },
            _ => throw new InvalidOperationException($"unexpected request {message.GetType().Name}"),
        });

        var controller = new AttachmentController(
            new FileService(null!, resolver, storageLedger: _ledger, objectStore: _objects),
            bus, null!, _context, _cache, new ConversationPermissionService(_context, _cache));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user ?? TestPrincipal.ForUser(UserId) },
        };

        return controller;
    }

    private static EntitlementResolver UserCeiling(long bytes) =>
        Resolver(EntitlementSet.Empty, Set((EntitlementKeys.UserUploadMaxBytes, bytes)));

    private static EntitlementResolver GuildQuota(long bytes) =>
        Resolver(
            Set((EntitlementKeys.StorageGuildQuotaBytes, bytes),
                (EntitlementKeys.StorageUploadMaxBytes, 100_000 * Mb)),
            Set((EntitlementKeys.StorageUploadMaxBytes, 100_000 * Mb)));

    private static EntitlementResolver Resolver(EntitlementSet guild, EntitlementSet user) =>
        new([new StubSource(guild, user)]);

    private static EntitlementSet Set(params (EntitlementKey Key, long Value)[] values)
    {
        var builder = new EntitlementSetBuilder(EntitlementPrecedence.PlanDefault);
        foreach (var (key, value) in values) builder.Number(key, value, "test-plan");
        return builder.Build();
    }

    private static IFormFile Sized(string name, long length) => new StubFormFile(name, length);

    private sealed class StubSource(EntitlementSet guild, EntitlementSet user) : IEntitlementSource
    {
        public EntitlementPrecedence Precedence => EntitlementPrecedence.PlanDefault;

        public Task<EntitlementSet> ResolveAsync(EntitlementSubject subject, CancellationToken cancellationToken) =>
            Task.FromResult(subject.Kind == SubjectKind.Guild ? guild : user);
    }

    private sealed class StubLedger : IGuildStorageLedger
    {
        private readonly Dictionary<string, long> _used = new(StringComparer.Ordinal);

        public List<(string GuildId, long Delta)> Recorded { get; } = [];

        public long Used(string guildId) => _used.GetValueOrDefault(guildId);

        public void Seed(string guildId, long bytes) => _used[guildId] = bytes;

        public Task<long> GetUsedBytesAsync(string guildId, CancellationToken ct) =>
            Task.FromResult(Used(guildId));

        public Task<long> AddAsync(string guildId, long deltaBytes, CancellationToken ct)
        {
            Recorded.Add((guildId, deltaBytes));
            var total = Math.Max(0, Used(guildId) + deltaBytes);
            _used[guildId] = total;
            return Task.FromResult(total);
        }
    }

    private sealed class StubObjectStore : IAttachmentObjectStore
    {
        public Task PutAsync(
            string bucketName, string key, string? contentType, Stream content, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class StubFormFile(string fileName, long length) : IFormFile
    {
        public string ContentType => "application/octet-stream";
        public string ContentDisposition => string.Empty;
        public IHeaderDictionary Headers => new HeaderDictionary();
        public long Length => length;
        public string Name => "files";
        public string FileName => fileName;

        public void CopyTo(Stream target) => OpenReadStream().CopyTo(target);

        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) =>
            OpenReadStream().CopyToAsync(target, cancellationToken);

        public Stream OpenReadStream() => new MemoryStream([1, 2, 3, 4]);
    }
}

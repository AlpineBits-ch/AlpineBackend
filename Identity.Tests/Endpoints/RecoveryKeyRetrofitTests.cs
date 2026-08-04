using System.Net;
using System.Text.Json;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Domain.Entities;
using Identity.Domain.ValueObjects;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Endpoints;

/// <summary>Adding a recovery code to an account that already has a master key.</summary>
[TestFixture]
public class RecoveryKeyRetrofitTests
{
    private const string Password = "SecurePass123!";

    private static IAlbaHost Host => AppFixture.Host;

    private static readonly byte[] PasswordCipherText = [1, 1, 1];
    private static readonly byte[] RecoveryCipherText = [4, 4, 4];

    private static async Task<(string Username, string Token)> RegisterAsync(string prefix)
    {
        var username = $"{prefix}{Guid.NewGuid():N}"[..15];

        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = $"{username}@example.com",
                Password = Password,
                Username = username,
                BirthDate = DateTime.UtcNow.AddYears(-20),
            }).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        var tokenResult = await Host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = username,
                ["password"] = Password,
                ["client_id"] = "echo",
            }).ToUrl("/connect/token");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await tokenResult.ReadAsJsonAsync<JsonElement>();
        return (username, body.GetProperty("access_token").GetString()!);
    }

    /// <summary>An account exactly as the field has them: master key wrapped under the password,
    /// no recovery-code wrapping, and a device backup blob sealed at version 1.</summary>
    private static async Task SeedLegacyAccountAsync(string username, bool withBackupBlob)
    {
        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.UserName == username);

        user.EncryptedMasterKey = new EncryptedMasterKey
        {
            CipherText = PasswordCipherText, Salt = [2], Iv = [3], Version = 1, Kdf = "argon2id",
            Argon2Iterations = 3, Argon2Memory = 65536, Argon2Parallelism = 4,
        };
        user.RecoveryCodeWrappedMasterKey = null;

        if (withBackupBlob)
        {
            var device = UserDevice.Create(new CreateUserDeviceParams
            {
                UserId = user.Id,
                ClientDeviceId = $"device-{Guid.NewGuid():N}",
                DeviceName = "Legacy desktop",
                DeviceType = Identity.Domain.Enums.DeviceType.Desktop,
                IdentityPublicKey = [1],
            });
            ctx.UserDevices.Add(device);

            ctx.UserDeviceBackups.Add(UserDeviceBackup.Create(new CreateUserDeviceBackupParams
            {
                UserId = user.Id,
                DeviceId = device.Id,
                Backup = [9, 9, 9],
                Version = 1,
                RecoveryKeyVersion = 1,
                CreatedAt = DateTimeOffset.UtcNow,
            }));
        }

        await ctx.SaveChangesAsync();
    }

    /// <summary>Defaults to no <paramref name="publicVerifier"/>, which is the shape of every account
    /// in the field and therefore the shape the retrofit path has to accept. A rotation must supply
    /// one - that is the write where key material is established, so it is the only point at which the
    /// value can be demanded at all.</summary>
    private static PutRecoveryKeyDto Envelope(int version, MasterKeyWrappingDto? recovery,
        byte[]? cipherText = null, byte[]? publicVerifier = null) => new()
    {
        Version = version,
        Kdf = "argon2id",
        Iterations = 3,
        MemoryKiB = 65536,
        Parallelism = 4,
        Salt = [2],
        Iv = [3],
        CipherText = cipherText ?? PasswordCipherText,
        Password = Password,
        PublicVerifier = publicVerifier,
        RecoveryCodeWrapping = recovery,
    };

    private static MasterKeyWrappingDto RecoveryWrapping(byte[]? cipherText = null) => new()
    {
        Kdf = "argon2id",
        Iterations = 3,
        MemoryKiB = 65536,
        Parallelism = 4,
        Salt = [5],
        Iv = [6],
        CipherText = cipherText ?? RecoveryCipherText,
    };

    private static async Task<(EncryptedMasterKey? Password, EncryptedMasterKey? Recovery)> ReadEnvelopeAsync(
        string username)
    {
        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.AsNoTracking().FirstAsync(u => u.UserName == username);
        return (user.EncryptedMasterKey, user.RecoveryCodeWrappedMasterKey);
    }

    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AddingARecoveryCodeAtTheSameVersion_PersistsTheWrappingAndOrphansNothing()
    {
        var (username, token) = await RegisterAsync("retrofit");
        await SeedLegacyAccountAsync(username, withBackupBlob: true);

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Put.Json(Envelope(version: 1, RecoveryWrapping())).ToUrl("/api/v1/backup/recovery-key");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<PutRecoveryKeyResultDto>();
        var (password, recovery) = await ReadEnvelopeAsync(username);

        Assert.Multiple(() =>
        {
            // The wrapping actually landed.
            Assert.That(recovery, Is.Not.Null);
            Assert.That(recovery!.CipherText, Is.EqualTo(RecoveryCipherText));

            // Same master key, same version - so the version must not move and the password
            // wrapping must be untouched.
            Assert.That(recovery.Version, Is.EqualTo(1));
            Assert.That(password!.Version, Is.EqualTo(1));
            Assert.That(password.CipherText, Is.EqualTo(PasswordCipherText));
            Assert.That(body!.Version, Is.EqualTo(1));

            // Nothing was rotated, so nothing can have been orphaned - the blob seeded at
            // recoveryKeyVersion 1 is still readable.
            Assert.That(body.OrphanedBlobDeviceIds, Is.Empty);
        });
    }

    [Test]
    public async Task AddingARecoveryCode_LeavesExistingBackupBlobsReadable()
    {
        var (username, token) = await RegisterAsync("retrofitblob");
        await SeedLegacyAccountAsync(username, withBackupBlob: true);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Put.Json(Envelope(version: 1, RecoveryWrapping())).ToUrl("/api/v1/backup/recovery-key");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.AsNoTracking().FirstAsync(u => u.UserName == username);
        var blob = await ctx.UserDeviceBackups.AsNoTracking().FirstAsync(b => b.UserId == user.Id);

        // A blob is stale when its recoveryKeyVersion is behind the account's current envelope.
        Assert.That(blob.RecoveryKeyVersion, Is.EqualTo(user.EncryptedMasterKey!.Version));
    }

    [Test]
    public async Task AddingARecoveryCode_MakesTheAccountReportItAsRecoverable()
    {
        var (username, token) = await RegisterAsync("retrofitget");
        await SeedLegacyAccountAsync(username, withBackupBlob: false);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Put.Json(Envelope(version: 1, RecoveryWrapping())).ToUrl("/api/v1/backup/recovery-key");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        // Alpine re-reads the envelope to confirm the wrapping landed rather than displaying a code
        // the server did not store. That read has to be able to see it.
        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url("/api/v1/backup/recovery-key");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("recoveryCodeWrapping").ValueKind, Is.Not.EqualTo(JsonValueKind.Null));
            Assert.That(body.GetProperty("encryptedHistoryRecoverable").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task RegeneratingTheRecoveryCode_ReplacesTheWrappingWithoutRotating()
    {
        var (username, token) = await RegisterAsync("retrofitregen");
        await SeedLegacyAccountAsync(username, withBackupBlob: true);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Put.Json(Envelope(version: 1, RecoveryWrapping())).ToUrl("/api/v1/backup/recovery-key");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Put.Json(Envelope(version: 1, RecoveryWrapping([7, 7, 7]))).ToUrl("/api/v1/backup/recovery-key");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<PutRecoveryKeyResultDto>();
        var (_, recovery) = await ReadEnvelopeAsync(username);

        Assert.Multiple(() =>
        {
            Assert.That(recovery!.CipherText, Is.EqualTo(new byte[] { 7, 7, 7 }));
            // Forcing a version bump to change a credential that no blob is sealed under would
            // orphan the whole account's history for nothing.
            Assert.That(body!.OrphanedBlobDeviceIds, Is.Empty);
            Assert.That(recovery.Version, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ReSubmittingTheSameEnvelope_IsStillIdempotent()
    {
        var (username, token) = await RegisterAsync("retrofitidem");
        await SeedLegacyAccountAsync(username, withBackupBlob: false);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Put.Json(Envelope(version: 1, recovery: null)).ToUrl("/api/v1/backup/recovery-key");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var (_, recovery) = await ReadEnvelopeAsync(username);
        Assert.That(recovery, Is.Null, "Submitting nothing new must not invent a wrapping");
    }

    [Test]
    public async Task ChangingThePasswordWrappingAtTheSameVersion_IsRefused()
    {
        var (username, token) = await RegisterAsync("retrofitsub");
        await SeedLegacyAccountAsync(username, withBackupBlob: false);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Put.Json(Envelope(version: 1, RecoveryWrapping(), cipherText: [9, 9, 9]))
                .ToUrl("/api/v1/backup/recovery-key");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });

        // Different bytes under an unchanged version is either a re-wrap under a new password -
        // which rewrap-password exists for - or a different master key masquerading as the same
        // one, which would make every blob at this version unopenable while claiming nothing
        // changed. The server cannot tell them apart, so it refuses.
        var (password, recovery) = await ReadEnvelopeAsync(username);
        Assert.Multiple(() =>
        {
            Assert.That(password!.CipherText, Is.EqualTo(PasswordCipherText));
            Assert.That(recovery, Is.Null, "A refused write must not partially apply");
        });
    }

    [Test]
    public async Task BumpingTheVersion_StillOrphansTheBlobsItActuallyInvalidates()
    {
        var (username, token) = await RegisterAsync("retrofitrot");
        await SeedLegacyAccountAsync(username, withBackupBlob: true);

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Put.Json(Envelope(version: 2, RecoveryWrapping(), cipherText: [8, 8, 8],
                    publicVerifier: Enumerable.Repeat((byte)0x77, 32).ToArray()))
                .ToUrl("/api/v1/backup/recovery-key");
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });

        // The additive path must not have weakened the real rotation guard: a genuine version bump
        // still names the blobs it is about to make unopenable and refuses without acknowledgement.
        var body = await result.ReadAsJsonAsync<PutRecoveryKeyResultDto>();
        Assert.That(body!.OrphanedBlobDeviceIds, Has.Count.EqualTo(1));

        var (password, _) = await ReadEnvelopeAsync(username);
        Assert.That(password!.Version, Is.EqualTo(1), "A refused rotation must not apply");
    }
}

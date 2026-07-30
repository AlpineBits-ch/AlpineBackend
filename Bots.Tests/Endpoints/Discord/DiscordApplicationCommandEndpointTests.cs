using System.Security.Claims;
using System.Text.Json;
using Bots.Application.Endpoints.Discord;
using Bots.Domain.Entity;
using Bots.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Bots.Tests.Endpoints.Discord;

[TestFixture]
public class DiscordApplicationCommandEndpointTests
{
    private TestBotsContext _context = null!;
    private DiscordApplicationCommandEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestBotsContext(Guid.NewGuid().ToString());
        _endpoint = new DiscordApplicationCommandEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ClaimsPrincipal MakeUser(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private async Task<BotApplication> AddApplicationAsync(string botUserId, bool enabled = true)
    {
        var app = new BotApplication { Id = BotApplication.GenerateId(), OwnerUserId = "usr_owner", BotUserId = botUserId, Name = "Test Bot", IsEnabled = enabled };
        _context.BotApplications.Add(app);
        await _context.SaveChangesAsync();
        return app;
    }

    private static object AsArray(IResult result) => result.GetType().GetProperty("Value")!.GetValue(result)!;

    // ── Global commands ───────────────────────────────────────────────────────

    [Test]
    public async Task BulkOverwriteGlobal_CallerIsNotTheApplication_ReturnsForbid()
    {
        await AddApplicationAsync("usr_bot1");

        var result = await _endpoint.BulkOverwriteGlobalAsync("usr_bot1", [new DiscordCommandDto { Name = "ping" }], MakeUser("usr_someone_else"), _context);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task BulkOverwriteGlobal_DisabledApplication_ReturnsForbid()
    {
        await AddApplicationAsync("usr_bot1", enabled: false);

        var result = await _endpoint.BulkOverwriteGlobalAsync("usr_bot1", [new DiscordCommandDto { Name = "ping" }], MakeUser("usr_bot1"), _context);

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task BulkOverwriteGlobal_ReplacesExistingGlobalCommandsWithNewSet()
    {
        var app = await AddApplicationAsync("usr_bot1");
        _context.BotCommands.Add(new BotCommand { Id = BotCommand.GenerateId(), BotApplicationId = app.Id, Name = "old-cmd", Description = "old", GuildId = null });
        await _context.SaveChangesAsync();

        var result = await _endpoint.BulkOverwriteGlobalAsync("usr_bot1", [new DiscordCommandDto { Name = "new-cmd", Description = "new" }], MakeUser("usr_bot1"), _context);
        await _context.SaveChangesAsync();

        var remaining = _context.BotCommands.Where(c => c.BotApplicationId == app.Id).ToList();
        Assert.That(remaining, Has.Count.EqualTo(1));
        Assert.That(remaining.Single().Name, Is.EqualTo("new-cmd"));
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task CreateGlobal_NewCommand_IsInsertedAsGlobal()
    {
        var app = await AddApplicationAsync("usr_bot1");

        var result = await _endpoint.CreateGlobalAsync("usr_bot1", new DiscordCommandDto { Name = "ping", Description = "pongs back" }, MakeUser("usr_bot1"), _context);
        await _context.SaveChangesAsync();

        var command = _context.BotCommands.Single();
        Assert.Multiple(() =>
        {
            Assert.That(command.Name, Is.EqualTo("ping"));
            Assert.That(command.GuildId, Is.Null);
            Assert.That(command.BotApplicationId, Is.EqualTo(app.Id));
        });

        var value = AsArray(result);
        var name = (string)value.GetType().GetProperty("name")!.GetValue(value)!;
        Assert.That(name, Is.EqualTo("ping"));
    }

    [Test]
    public async Task CreateGlobal_SameNameAsExisting_UpdatesInPlaceInsteadOfDuplicating()
    {
        var app = await AddApplicationAsync("usr_bot1");
        _context.BotCommands.Add(new BotCommand { Id = BotCommand.GenerateId(), BotApplicationId = app.Id, Name = "ping", Description = "old desc" });
        await _context.SaveChangesAsync();

        await _endpoint.CreateGlobalAsync("usr_bot1", new DiscordCommandDto { Name = "ping", Description = "new desc" }, MakeUser("usr_bot1"), _context);
        await _context.SaveChangesAsync();

        var commands = _context.BotCommands.Where(c => c.BotApplicationId == app.Id).ToList();
        Assert.That(commands, Has.Count.EqualTo(1));
        Assert.That(commands.Single().Description, Is.EqualTo("new desc"));
    }

    [Test]
    public async Task ListGlobal_ReturnsOnlyGlobalCommandsForThatApplication()
    {
        var app = await AddApplicationAsync("usr_bot1");
        _context.BotCommands.Add(new BotCommand { Id = BotCommand.GenerateId(), BotApplicationId = app.Id, Name = "global-cmd", Description = "", GuildId = null });
        _context.BotCommands.Add(new BotCommand { Id = BotCommand.GenerateId(), BotApplicationId = app.Id, Name = "guild-cmd", Description = "", GuildId = "gld_1" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.ListGlobalAsync("usr_bot1", MakeUser("usr_bot1"), _context);

        var names = ((System.Collections.IEnumerable)AsArray(result)).Cast<object>()
            .Select(v => (string)v.GetType().GetProperty("name")!.GetValue(v)!).ToList();
        Assert.That(names, Is.EquivalentTo(new[] { "global-cmd" }));
    }

    [Test]
    public async Task DeleteGlobal_UnknownCommand_ReturnsNotFound()
    {
        await AddApplicationAsync("usr_bot1");

        var result = await _endpoint.DeleteGlobalAsync("usr_bot1", "boco_missing", MakeUser("usr_bot1"), _context);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task DeleteGlobal_ExistingCommand_RemovesIt()
    {
        var app = await AddApplicationAsync("usr_bot1");
        var command = new BotCommand { Id = BotCommand.GenerateId(), BotApplicationId = app.Id, Name = "ping", Description = "" };
        _context.BotCommands.Add(command);
        await _context.SaveChangesAsync();

        var result = await _endpoint.DeleteGlobalAsync("usr_bot1", command.Id, MakeUser("usr_bot1"), _context);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(_context.BotCommands.Any(), Is.False);
    }

    // ── Guild-scoped commands ────────────────────────────────────────────────

    [Test]
    public async Task CreateGuild_IsScopedToThatGuildOnly()
    {
        var app = await AddApplicationAsync("usr_bot1");

        await _endpoint.CreateGuildAsync("usr_bot1", "gld_1", new DiscordCommandDto { Name = "guild-only" }, MakeUser("usr_bot1"), _context);
        await _context.SaveChangesAsync();

        var command = _context.BotCommands.Single();
        Assert.That(command.GuildId, Is.EqualTo("gld_1"));
        Assert.That(command.BotApplicationId, Is.EqualTo(app.Id));
    }

    [Test]
    public async Task ListGuild_ExcludesGlobalAndOtherGuildCommands()
    {
        var app = await AddApplicationAsync("usr_bot1");
        _context.BotCommands.Add(new BotCommand { Id = BotCommand.GenerateId(), BotApplicationId = app.Id, Name = "global-cmd", Description = "", GuildId = null });
        _context.BotCommands.Add(new BotCommand { Id = BotCommand.GenerateId(), BotApplicationId = app.Id, Name = "gld1-cmd", Description = "", GuildId = "gld_1" });
        _context.BotCommands.Add(new BotCommand { Id = BotCommand.GenerateId(), BotApplicationId = app.Id, Name = "gld2-cmd", Description = "", GuildId = "gld_2" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.ListGuildAsync("usr_bot1", "gld_1", MakeUser("usr_bot1"), _context);

        var names = ((System.Collections.IEnumerable)AsArray(result)).Cast<object>()
            .Select(v => (string)v.GetType().GetProperty("name")!.GetValue(v)!).ToList();
        Assert.That(names, Is.EquivalentTo(new[] { "gld1-cmd" }));
    }

    [Test]
    public async Task Options_RoundTripThroughOptionsJson()
    {
        var app = await AddApplicationAsync("usr_bot1");
        var optionsJson = JsonSerializer.Deserialize<JsonElement>("""[{"name":"target","type":6}]""");

        var result = await _endpoint.CreateGlobalAsync("usr_bot1", new DiscordCommandDto { Name = "ping", Options = optionsJson }, MakeUser("usr_bot1"), _context);

        var value = AsArray(result);
        var options = (JsonElement)value.GetType().GetProperty("options")!.GetValue(value)!;
        Assert.That(options[0].GetProperty("name").GetString(), Is.EqualTo("target"));

        var stored = _context.BotCommands.Local.Single(c => c.BotApplicationId == app.Id);
        Assert.That(stored.OptionsJson, Is.EqualTo(optionsJson.GetRawText()));
    }
}

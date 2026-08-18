using Guild.Domain.Entity;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Guild.Tests.Services;

/// <summary>
/// The persona schema, read off the real Npgsql model: the indexes the send path and the approval
/// queue depend on, and the delete behaviours §3.3 specifies. None of these are visible from a unit
/// test against InMemory, which ignores relational metadata.
/// </summary>
[TestFixture]
public class PersonaModelShapeTests
{
    private PostgresGuildContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new PostgresGuildContext();

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private IEntityType Entity<T>() => _context.Model.FindEntityType(typeof(T))!;

    private static IEnumerable<string> IndexColumns(IEntityType entity, bool uniqueOnly = false) =>
        entity.GetIndexes()
            .Where(i => !uniqueOnly || i.IsUnique)
            .Select(i => string.Join(",", i.Properties.Select(p => p.Name)));

    private static DeleteBehavior DeleteBehaviorFor(IEntityType entity, string foreignKeyProperty) =>
        entity.GetForeignKeys()
            .Single(fk => fk.Properties.Any(p => p.Name == foreignKeyProperty))
            .DeleteBehavior;

    // ══════════════════════════════════════════════════════════════════════════ A persona is not a member
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The decisive rule of §2, expressed where it cannot be argued with: nothing joins a
    /// persona to the member table, so no code path can turn one into a second row for the same
    /// (user, guild) pair.</summary>
    [Test]
    public void Persona_HasNoRelationshipToGuildMember()
    {
        var toMembers = Entity<Persona>().GetForeignKeys()
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(GuildMember))
            .ToList();

        var fromMembers = Entity<GuildMember>().GetForeignKeys()
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(Persona))
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(toMembers, Is.Empty);
            Assert.That(fromMembers, Is.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Indexes
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Persona_IsIndexedByGuildAndOwner() =>
        Assert.That(IndexColumns(Entity<Persona>()), Does.Contain("OwnerGuildId,OwnerUserId"));

    [Test]
    public void Persona_IsIndexedByOwningUserForTheAccountListAndThePurge() =>
        Assert.That(IndexColumns(Entity<Persona>()), Does.Contain("OwnerUserId"));

    /// <summary>Adoption is idempotent; two profiles for the same pair would make "which overrides
    /// apply here" a coin flip.</summary>
    [Test]
    public void PersonaGuildProfile_IsUniquePerPersonaAndGuild() =>
        Assert.That(IndexColumns(Entity<PersonaGuildProfile>(), uniqueOnly: true),
            Does.Contain("PersonaId,GuildId"));

    [Test]
    public void PersonaGuildProfile_IndexesTheApprovalQueueAndTheProxyPrefix()
    {
        var indexes = IndexColumns(Entity<PersonaGuildProfile>()).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(indexes, Does.Contain("GuildId,ApprovalState"));
            Assert.That(indexes, Does.Contain("GuildId,ProxyPrefix"));
        });
    }

    [Test]
    public void PersonaGrant_CannotBeIssuedTwiceToTheSameRoleOrUser()
    {
        var unique = IndexColumns(Entity<PersonaGrant>(), uniqueOnly: true).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(unique, Does.Contain("PersonaId,RoleId"));
            Assert.That(unique, Does.Contain("PersonaId,UserId"));
        });
    }

    [Test]
    public void PersonaAutoproxy_IsOneRowPerUserAndChannel() =>
        Assert.That(IndexColumns(Entity<PersonaAutoproxyState>(), uniqueOnly: true),
            Does.Contain("UserId,ChannelId"));

    /// <summary>Leaving a guild clears the autoproxy state, which is one delete only because the
    /// guild id is on the row.</summary>
    [Test]
    public void PersonaAutoproxy_IsIndexedByUserAndGuild() =>
        Assert.That(IndexColumns(Entity<PersonaAutoproxyState>()), Does.Contain("UserId,GuildId"));

    /// <summary>One character has one page in a guild, and the filter is what keeps every ordinary
    /// page's null persona from colliding with every other one.</summary>
    [Test]
    public void WikiPage_IsUniquePerGuildAndPersona()
    {
        var index = Entity<WikiPage>().GetIndexes().Single(i =>
            string.Join(",", i.Properties.Select(p => p.Name)) == "GuildId,PersonaId");

        Assert.Multiple(() =>
        {
            Assert.That(index.IsUnique, Is.True);
            Assert.That(index.GetFilter(), Does.Contain("persona_id"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Lifecycle
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void GuildDeletion_CascadesProfilesGrantsAndSharedPersonas()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DeleteBehaviorFor(Entity<PersonaGuildProfile>(), nameof(PersonaGuildProfile.GuildId)),
                Is.EqualTo(DeleteBehavior.Cascade));
            Assert.That(DeleteBehaviorFor(Entity<Persona>(), nameof(Persona.OwnerGuildId)),
                Is.EqualTo(DeleteBehavior.Cascade));
            Assert.That(DeleteBehaviorFor(Entity<PersonaGrant>(), nameof(PersonaGrant.PersonaId)),
                Is.EqualTo(DeleteBehavior.Cascade));
        });
    }

    /// <summary>The pointers that must survive what they point at: the prose outlives the persona,
    /// the adoption outlives the page, and a gone persona leaves autoproxy resolving to nothing.</summary>
    [Test]
    public void TheOptionalPointers_AreNulledRatherThanCascaded()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DeleteBehaviorFor(Entity<WikiPage>(), nameof(WikiPage.PersonaId)),
                Is.EqualTo(DeleteBehavior.SetNull));
            Assert.That(DeleteBehaviorFor(Entity<PersonaGuildProfile>(), nameof(PersonaGuildProfile.WikiPageId)),
                Is.EqualTo(DeleteBehavior.SetNull));
            Assert.That(DeleteBehaviorFor(Entity<PersonaAutoproxyState>(), nameof(PersonaAutoproxyState.PersonaId)),
                Is.EqualTo(DeleteBehavior.SetNull));
        });
    }

    /// <summary>Promotion re-points this by hand rather than by cascade, so a foreign key here
    /// would turn a product decision into a delete rule.</summary>
    [Test]
    public void Persona_HomeProfileId_CarriesNoForeignKey()
    {
        var keys = Entity<Persona>().GetForeignKeys()
            .Where(fk => fk.Properties.Any(p => p.Name == nameof(Persona.HomeProfileId)));

        Assert.That(keys, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════ Infobox storage
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Real jsonb, not text: this is Postgres-only, unlike Message.EmbedsJson which also
    /// has to survive Scylla. And the revision carries it too, or a stat change is not a diff.</summary>
    [Test]
    public void TheInfoboxColumns_AreJsonb()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Entity<WikiPage>().FindProperty(nameof(WikiPage.InfoboxJson))!.GetColumnType(),
                Is.EqualTo("jsonb"));
            Assert.That(Entity<WikiRevision>().FindProperty(nameof(WikiRevision.InfoboxJson))!.GetColumnType(),
                Is.EqualTo("jsonb"));
            Assert.That(Entity<WikiCategory>().FindProperty(nameof(WikiCategory.InfoboxTemplateJson))!.GetColumnType(),
                Is.EqualTo("jsonb"));
        });
    }
}

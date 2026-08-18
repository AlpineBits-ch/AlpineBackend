using Guild.Domain.Entity;
using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

/// <summary>
/// The persona rules that are pure: who may speak as one, how a profile moves through the approval
/// queue, and how a guild's copy of a character page reads against the reference copy.
/// </summary>
[TestFixture]
public class PersonaDomainTests
{
    private static Persona UserPersona(string ownerUserId = "user-anna") =>
        Persona.Create(new CreatePersonaParams
        {
            Scope = PersonaScope.User, OwnerUserId = ownerUserId, Name = "Mayor Cogsgrove",
        });

    private static Persona GuildPersona(string ownerGuildId = "gild-1") =>
        Persona.Create(new CreatePersonaParams
        {
            Scope = PersonaScope.Guild, OwnerGuildId = ownerGuildId, Name = "The Narrator",
        });

    private static PersonaGrant GrantToRole(Persona persona, string roleId) =>
        PersonaGrant.Create(new CreatePersonaGrantParams { PersonaId = persona.Id, RoleId = roleId });

    private static PersonaGrant GrantToUser(Persona persona, string userId) =>
        PersonaGrant.Create(new CreatePersonaGrantParams { PersonaId = persona.Id, UserId = userId });

    // ══════════════════════════════════════════════════════════════════════════ Creation
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Create_KeepsOnlyTheOwnerThatMatchesTheScope()
    {
        var user = Persona.Create(new CreatePersonaParams
        {
            Scope = PersonaScope.User,
            OwnerUserId = "user-anna",
            OwnerGuildId = "gild-1",
            Name = "Mayor Cogsgrove",
        });

        var guild = Persona.Create(new CreatePersonaParams
        {
            Scope = PersonaScope.Guild,
            OwnerUserId = "user-anna",
            OwnerGuildId = "gild-1",
            Name = "The Narrator",
        });

        Assert.Multiple(() =>
        {
            Assert.That(user.OwnerGuildId, Is.Null, "a user-scoped persona has no owning guild");
            Assert.That(user.OwnerUserId, Is.EqualTo("user-anna"));
            Assert.That(guild.OwnerUserId, Is.Null, "a guild-scoped persona has no owning user at all");
            Assert.That(guild.OwnerGuildId, Is.EqualTo("gild-1"));
        });
    }

    /// <summary>The prefix is the whole point of §2: an id shaped like a member id, in the same
    /// table, eventually ends up in AuthorId.</summary>
    [Test]
    public void Create_MintsAnIdThatCannotBeMistakenForAMemberId()
    {
        Assert.Multiple(() =>
        {
            Assert.That(UserPersona().Id, Does.StartWith("pers"));
            Assert.That(Persona.Prefix, Is.Not.EqualTo(GuildMember.Prefix));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Grant resolution
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void Create_Grant_RejectsNamingBothARoleAndAUser() =>
        Assert.Throws<ArgumentException>(() => PersonaGrant.Create(new CreatePersonaGrantParams
        {
            PersonaId = "pers-1", RoleId = "role-1", UserId = "user-1",
        }));

    [Test]
    public void Create_Grant_RejectsNamingNeither() =>
        Assert.Throws<ArgumentException>(() =>
            PersonaGrant.Create(new CreatePersonaGrantParams { PersonaId = "pers-1" }));

    [Test]
    public void UserScopedPersona_IsSpeakableOnlyByItsOwner()
    {
        var persona = UserPersona();

        Assert.Multiple(() =>
        {
            Assert.That(persona.CanBeSpokenBy("user-anna", [], []), Is.True);
            Assert.That(persona.CanBeSpokenBy("user-bob", [], []), Is.False);
        });
    }

    /// <summary>A grant on somebody else's personal character is not a thing, so one must not
    /// accidentally start working if a row shows up.</summary>
    [Test]
    public void UserScopedPersona_IgnoresGrants()
    {
        var persona = UserPersona();

        Assert.That(persona.CanBeSpokenBy("user-bob", ["role-gm"], [GrantToUser(persona, "user-bob")]),
            Is.False);
    }

    [Test]
    public void GuildScopedPersona_IsSpeakableThroughARoleGrant()
    {
        var persona = GuildPersona();
        var grants = new[] { GrantToRole(persona, "role-gm") };

        Assert.Multiple(() =>
        {
            Assert.That(persona.CanBeSpokenBy("user-anna", ["role-gm", "role-everyone"], grants), Is.True);
            Assert.That(persona.CanBeSpokenBy("user-bob", ["role-everyone"], grants), Is.False);
        });
    }

    [Test]
    public void GuildScopedPersona_IsSpeakableThroughADirectUserGrant()
    {
        var persona = GuildPersona();
        var grants = new[] { GrantToUser(persona, "user-anna") };

        Assert.Multiple(() =>
        {
            Assert.That(persona.CanBeSpokenBy("user-anna", [], grants), Is.True);
            Assert.That(persona.CanBeSpokenBy("user-bob", [], grants), Is.False);
        });
    }

    [Test]
    public void GuildScopedPersona_WithNoGrants_IsSpeakableByNobody() =>
        Assert.That(GuildPersona().CanBeSpokenBy("user-anna", ["role-gm"], []), Is.False);

    /// <summary>Grants are fetched per persona, but the resolver must not trust that: a grant on a
    /// different character reaching this one is a mis-attribution, not a permission bug.</summary>
    [Test]
    public void GuildScopedPersona_IgnoresGrantsBelongingToAnotherPersona()
    {
        var narrator = GuildPersona();
        var guard = GuildPersona();

        Assert.That(narrator.CanBeSpokenBy("user-anna", ["role-gm"], [GrantToRole(guard, "role-gm")]),
            Is.False);
    }

    [Test]
    public void RetiredPersona_IsSpeakableByNobody()
    {
        var owned = UserPersona();
        var shared = GuildPersona();
        var grants = new[] { GrantToUser(shared, "user-anna") };

        owned.Retire();
        shared.Retire();

        Assert.Multiple(() =>
        {
            Assert.That(owned.CanBeSpokenBy("user-anna", [], []), Is.False, "not even the owner");
            Assert.That(shared.CanBeSpokenBy("user-anna", [], grants), Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Approval
    // ══════════════════════════════════════════════════════════════════════════

    private static PersonaGuildProfile Profile() =>
        PersonaGuildProfile.Create(new CreatePersonaGuildProfileParams
        {
            PersonaId = "pers-1", GuildId = "gild-1",
        });

    [Test]
    public void NewProfile_StartsAsADraft() =>
        Assert.That(Profile().ApprovalState, Is.EqualTo(PersonaApprovalState.Draft));

    [Test]
    public void Submit_ThenApprove_RecordsTheReviewerAndTheRevision()
    {
        var profile = Profile();

        profile.Submit();
        profile.Approve("user-gm", 4);

        Assert.Multiple(() =>
        {
            Assert.That(profile.ApprovalState, Is.EqualTo(PersonaApprovalState.Approved));
            Assert.That(profile.ApprovedByUserId, Is.EqualTo("user-gm"));
            Assert.That(profile.ApprovedAt, Is.Not.Null);
            Assert.That(profile.LastApprovedRevisionNumber, Is.EqualTo(4));
        });
    }

    [Test]
    public void RequestChanges_KeepsTheReasonAndAllowsResubmission()
    {
        var profile = Profile();

        profile.Submit();
        profile.RequestChanges("Too many swords.");

        Assert.That(profile.ApprovalState, Is.EqualTo(PersonaApprovalState.ChangesRequested));
        Assert.That(profile.ChangesRequestedReason, Is.EqualTo("Too many swords."));

        profile.Submit();

        Assert.Multiple(() =>
        {
            Assert.That(profile.ApprovalState, Is.EqualTo(PersonaApprovalState.Submitted));
            Assert.That(profile.ChangesRequestedReason, Is.Null, "the old reason is not still standing");
        });
    }

    [Test]
    public void Submit_FromSubmitted_Throws()
    {
        var profile = Profile();
        profile.Submit();

        Assert.Throws<InvalidOperationException>(() => profile.Submit());
    }

    [Test]
    public void Submit_FromApproved_Throws()
    {
        var profile = Profile();
        profile.Submit();
        profile.Approve("user-gm", 1);

        Assert.Throws<InvalidOperationException>(() => profile.Submit());
    }

    [Test]
    public void Approve_WithNothingSubmitted_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Profile().Approve("user-gm", 1));

    [Test]
    public void RequestChanges_WithNothingSubmitted_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Profile().RequestChanges("no"));

    [Test]
    public void HasUnapprovedChanges_OnlyCountsRevisionsAboveTheApprovedOne()
    {
        var profile = Profile();
        profile.Submit();
        profile.Approve("user-gm", 4);

        Assert.Multiple(() =>
        {
            Assert.That(profile.HasUnapprovedChanges(4), Is.False);
            Assert.That(profile.HasUnapprovedChanges(5), Is.True);
        });
    }

    [Test]
    public void HasUnapprovedChanges_OnAProfileThatWasNeverApproved_IsFalse() =>
        Assert.That(Profile().HasUnapprovedChanges(9), Is.False);

    /// <summary>Blocking speech on a typo fix is how approval queues become resented.</summary>
    [Test]
    public void MaySpeak_IsUnaffectedByAPendingEdit()
    {
        var profile = Profile();
        profile.Submit();
        profile.Approve("user-gm", 4);

        Assert.Multiple(() =>
        {
            Assert.That(profile.HasUnapprovedChanges(7), Is.True);
            Assert.That(profile.MaySpeak(guildRequiresApproval: true), Is.True);
        });
    }

    [Test]
    public void MaySpeak_GatesFirstUseOnlyWhereTheGuildAsksForIt()
    {
        var profile = Profile();

        Assert.Multiple(() =>
        {
            Assert.That(profile.MaySpeak(guildRequiresApproval: false), Is.True);
            Assert.That(profile.MaySpeak(guildRequiresApproval: true), Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Autoproxy
    // ══════════════════════════════════════════════════════════════════════════

    private static PersonaAutoproxyState Autoproxy(AutoproxyMode mode, string? personaId = null) =>
        PersonaAutoproxyState.Create(new CreatePersonaAutoproxyStateParams
        {
            UserId = "user-anna", GuildId = "gild-1", ChannelId = "chan-1",
            Mode = mode, PersonaId = personaId,
        });

    [Test]
    public void Autoproxy_Off_ResolvesToNothingEvenWithAPersonaSupplied() =>
        Assert.That(Autoproxy(AutoproxyMode.Off, "pers-1").ResolvePersonaId(), Is.Null);

    [Test]
    public void Autoproxy_Pinned_ResolvesToThePinnedPersona() =>
        Assert.That(Autoproxy(AutoproxyMode.Pinned, "pers-1").ResolvePersonaId(), Is.EqualTo("pers-1"));

    [Test]
    public void Autoproxy_Set_ToPinnedWithoutAPersona_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            Autoproxy(AutoproxyMode.Off).Set(AutoproxyMode.Pinned, null));

    [Test]
    public void Autoproxy_Set_ToOff_DropsThePersona()
    {
        var state = Autoproxy(AutoproxyMode.Pinned, "pers-1");

        state.Set(AutoproxyMode.Off, "pers-1");

        Assert.Multiple(() =>
        {
            Assert.That(state.PersonaId, Is.Null);
            Assert.That(state.ResolvePersonaId(), Is.Null);
        });
    }

    /// <summary>Sticky follows whatever was last proxied here, so switching into it must not
    /// clear the persona the send path already wrote.</summary>
    [Test]
    public void Autoproxy_Set_ToSticky_KeepsWhateverWasLastProxied()
    {
        var state = Autoproxy(AutoproxyMode.Pinned, "pers-1");

        state.Set(AutoproxyMode.Sticky, null);

        Assert.That(state.ResolvePersonaId(), Is.EqualTo("pers-1"));
    }

    /// <summary>A persona going away nulls the column rather than deleting the row, so the state
    /// has to resolve to nothing rather than to a dangling id.</summary>
    [Test]
    public void Autoproxy_WithTheirPersonaGone_ResolvesToNothing()
    {
        var state = Autoproxy(AutoproxyMode.Sticky, "pers-1");

        state.PersonaId = null;

        Assert.That(state.ResolvePersonaId(), Is.Null);
    }
}

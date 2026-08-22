# Guild Languages Implementation Plan (Discovery, plan one point five)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A guild declares the language it speaks and up to four more, Discovery mirrors them, and the feed stops showing a listing to someone who cannot read it.

**Architecture:** Guild owns the fields and the settings screen. Discovery mirrors them onto `guild_profile` through the batch request that already carries name and icon, and stores the viewer's own languages beside their interests. The feed excludes on set intersection, with a visible toggle that drops the filter. `Listing.Language`, shipped in plan one, is removed: spec section 5.1 puts languages on the guild so a posting does not re-declare them.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, Wolverine 6.24.6 on RabbitMQ, Angular 21 with `@ngrx/signals`, PrimeNG.

**Spec:** `docs/specs/discovery.md`, section 5.1. Ordering rationale is section 18.

## Global Constraints

- No em dashes anywhere: code, comments, UI copy, commit messages. Use a comma, colon, semicolon or full stop.
- Conventional commit prefix, one line, lowercase, imperative. No body unless it carries what the diff cannot. No trailers, no co-authors, no emoji.
- Comments: one line stating an invariant whose violation is silent, a `TODO`/`FIXME` with an owner, or a short line naming a non-obvious symbol. Never narrative rationale, never a justification against code nobody wrote, never a hypothetical future maintainer.
- 4-space indent, single quotes, semicolons, LF. No bracket spacing in imports.
- Angular: `inject()`, `input()`/`output()`/`model()`, `ChangeDetectionStrategy.OnPush`, signals, standalone, control-flow blocks.
- Never hand-edit an EF migration. Add an EF-generated one and use `Sql()` if needed.
- Wolverine handlers must not call `SaveChangesAsync`; `AutoApplyTransactions` commits on successful return. Anything outside a handler must commit itself.
- A Wolverine handler class name must end in exactly `Handler` or `Consumer`. Plural `Handlers` is silently never scanned.
- i18n: flat dot-separated keys in `src/assets/i18n/locales/en.json`. Prefer an existing key.
- Tests: baseline is green. Useful tests, not tests for a high count. Never `readonly x = SOME_IMPORTED_CONST` as a class field; use a getter.
- Never `git stash`, `git checkout --`, or `git reset --hard`. Commit by explicit path, never `-A`.
- Another agent works on `main` concurrently. Unrelated build or test breakage is theirs; do not fix it, do not revert it.

## Interfaces produced by this plan

```csharp
// Guild.Domain/Aggregates/Guild.cs
public string PrimaryLanguage { get; set; } = "en";
public List<string> OtherLanguages { get; set; } = [];

// Guild.Domain/LanguageTag.cs
public static class LanguageTag
{
    public const int MaxOtherLanguages = 4;
    public static bool IsWellFormed(string? tag);
    public static string? Normalize(string? tag);          // null when malformed
    public static bool TryNormalizeSet(string? primary, IEnumerable<string>? others,
        out string normalizedPrimary, out List<string> normalizedOthers, out string? problem);
}

// Guild.Contracts/Bus/Request/GetGuildProfilesRequest.cs, on GuildProfileDto
public string PrimaryLanguage { get; set; } = "en";
public IReadOnlyList<string> OtherLanguages { get; set; } = [];

// Discovery.Domain/Entities/GuildProfile.cs
public string PrimaryLanguage { get; set; } = "en";
public List<string> OtherLanguages { get; set; } = [];

// Discovery.Domain/Entities/UserLanguage.cs
public class UserLanguage : BaseEntity<UserLanguage>, IPrefixedEntity  // "ulng"
{
    public string UserId { get; set; }
    public string Language { get; set; }
}
```

---

## Task 1: Language tags and the two Guild fields

**Files:**
- Create: `Guild.Domain/LanguageTag.cs`
- Modify: `Guild.Domain/Aggregates/Guild.cs`
- Modify: `Guild.Infrastructure/Persistence/MicroserviceContext.cs`
- Test: `Guild.Tests/Domain/LanguageTagTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `LanguageTag` as above; `Guild.PrimaryLanguage`, `Guild.OtherLanguages`.

- [ ] **Step 1: Write the failing test**

`Guild.Tests/Domain/LanguageTagTests.cs`:

```csharp
using Guild.Domain;

namespace Guild.Tests.Domain;

[TestFixture]
public class LanguageTagTests
{
    [TestCase("en", "en")]
    [TestCase("EN", "en")]
    [TestCase("pt-br", "pt-BR")]
    [TestCase("zh-hans", "zh-Hans")]
    [TestCase("de-CH", "de-CH")]
    public void A_well_formed_tag_normalizes_to_canonical_case(string input, string expected)
    {
        Assert.That(LanguageTag.Normalize(input), Is.EqualTo(expected));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("e")]
    [TestCase("english!")]
    [TestCase("en_US")]
    [TestCase("toolongsubtag")]
    [TestCase(null)]
    public void A_malformed_tag_is_refused(string? input)
    {
        Assert.That(LanguageTag.Normalize(input), Is.Null);
    }

    [Test]
    public void The_primary_is_dropped_from_the_others()
    {
        var ok = LanguageTag.TryNormalizeSet("en", ["EN", "de"], out var primary, out var others, out _);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(primary, Is.EqualTo("en"));
            Assert.That(others, Is.EqualTo(new[] { "de" }));
        });
    }

    [Test]
    public void Duplicates_among_the_others_collapse()
    {
        LanguageTag.TryNormalizeSet("en", ["de", "DE", "fr"], out _, out var others, out _);

        Assert.That(others, Is.EqualTo(new[] { "de", "fr" }));
    }

    [Test]
    public void More_than_four_others_is_refused_by_name()
    {
        var ok = LanguageTag.TryNormalizeSet("en", ["de", "fr", "it", "es", "pl"],
            out _, out _, out var problem);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(problem, Does.Contain("4"));
        });
    }

    [Test]
    public void A_malformed_other_names_the_offender()
    {
        var ok = LanguageTag.TryNormalizeSet("en", ["de", "nope!"], out _, out _, out var problem);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(problem, Does.Contain("nope!"));
        });
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test Guild.Tests/Guild.Tests.csproj --filter "FullyQualifiedName~LanguageTagTests"`
Expected: FAIL, `LanguageTag` does not exist.

- [ ] **Step 3: Write `Guild.Domain/LanguageTag.cs`**

```csharp
using System.Text;

namespace Guild.Domain;

/// <summary>
/// BCP-47 shape only, not validated against the subtag registry: a guild may declare any
/// well-formed tag, and the client offers a curated list (spec 5.1).
/// </summary>
public static class LanguageTag
{
    public const int MaxOtherLanguages = 4;

    private const int MaxSubtagLength = 8;

    public static bool IsWellFormed(string? tag) => Normalize(tag) is not null;

    /// <summary>Canonical case: language lowercase, script title, region upper. Null when malformed.</summary>
    public static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var parts = tag.Trim().Split('-');
        if (parts.Length == 0) return null;

        var primary = parts[0];
        if (primary.Length is < 2 or > MaxSubtagLength || !primary.All(char.IsAsciiLetter)) return null;

        var builder = new StringBuilder(primary.ToLowerInvariant());

        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > MaxSubtagLength || !part.All(char.IsAsciiLetterOrDigit)) return null;

            builder.Append('-').Append(CaseSubtag(part));
        }

        return builder.ToString();
    }

    private static string CaseSubtag(string part) => part.Length switch
    {
        4 when part.All(char.IsAsciiLetter) => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant(),
        2 when part.All(char.IsAsciiLetter) => part.ToUpperInvariant(),
        _ => part.ToLowerInvariant(),
    };

    /// <summary>
    /// Normalizes a whole declaration. The primary never appears in the others, and the others
    /// carry no duplicates, so the match set is just {primary} union others.
    /// </summary>
    public static bool TryNormalizeSet(
        string? primary,
        IEnumerable<string>? others,
        out string normalizedPrimary,
        out List<string> normalizedOthers,
        out string? problem)
    {
        normalizedPrimary = string.Empty;
        normalizedOthers = [];

        var canonicalPrimary = Normalize(primary);
        if (canonicalPrimary is null)
        {
            problem = $"'{primary}' is not a well-formed language tag.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { canonicalPrimary };
        var collected = new List<string>();

        foreach (var candidate in others ?? [])
        {
            var canonical = Normalize(candidate);
            if (canonical is null)
            {
                problem = $"'{candidate}' is not a well-formed language tag.";
                return false;
            }

            if (seen.Add(canonical)) collected.Add(canonical);
        }

        if (collected.Count > MaxOtherLanguages)
        {
            problem = $"A guild may list at most {MaxOtherLanguages} other languages.";
            return false;
        }

        normalizedPrimary = canonicalPrimary;
        normalizedOthers = collected;
        problem = null;
        return true;
    }
}
```

- [ ] **Step 4: Add the two fields to the aggregate**

In `Guild.Domain/Aggregates/Guild.cs`, beside the other scalar settings:

```csharp
    /// <summary>BCP-47. What this guild speaks, and what a Discovery card renders.</summary>
    public string PrimaryLanguage { get; set; } = "en";

    /// <summary>Up to <see cref="LanguageTag.MaxOtherLanguages"/> more it can accommodate.</summary>
    public List<string> OtherLanguages { get; set; } = [];
```

- [ ] **Step 5: Configure the columns**

In `Guild.Infrastructure/Persistence/MicroserviceContext.cs`, inside the existing `modelBuilder.Entity<Guild>` block, matching the `text[]` pattern already used for `BlockedWords` at line 879:

```csharp
            guildBuilder.Property(x => x.PrimaryLanguage).HasMaxLength(35);
            guildBuilder.Property(x => x.OtherLanguages).HasColumnType("text[]");
```

35 is the longest well-formed tag this accepts: four 8-character subtags plus separators.

- [ ] **Step 6: Generate the migration**

```
dotnet ef migrations add AddGuildLanguages --project Guild.Infrastructure --startup-project Guild.Application
```

Do not hand-edit the result. Confirm it adds `primary_language` with default `'en'` and `other_languages text[]`.

- [ ] **Step 7: Run the tests**

Run: `dotnet test Guild.Tests/Guild.Tests.csproj --filter "FullyQualifiedName~LanguageTagTests"`
Expected: PASS, 7 cases.

- [ ] **Step 8: Commit**

```
git add Guild.Domain/LanguageTag.cs Guild.Domain/Aggregates/Guild.cs Guild.Infrastructure/Persistence/MicroserviceContext.cs Guild.Infrastructure/Migrations Guild.Tests/Domain/LanguageTagTests.cs
git commit -m "feat(guild): declare the languages a guild speaks"
```

---

## Task 2: The settings endpoint accepts them

**Files:**
- Modify: `Guild.Application/Dtos/Request/UpdateGuildDto.cs`
- Modify: `Guild.Application/Endpoints/Guild/GuildEndpoint.cs` (the `UpdateGuild` handler at line 118)
- Test: `Guild.Tests/Endpoints/GuildLanguageUpdateTests.cs`

**Interfaces:**
- Consumes: `LanguageTag.TryNormalizeSet` from Task 1.
- Produces: `UpdateGuildDto.PrimaryLanguage`, `UpdateGuildDto.OtherLanguages`.

- [ ] **Step 1: Extend the DTO**

```csharp
    public string? PrimaryLanguage { get; set; }

    /// <summary>Null leaves the list alone; an empty list clears it.</summary>
    public List<string>? OtherLanguages { get; set; }
```

Null-means-unchanged, matching every other field on this DTO.

- [ ] **Step 2: Write the failing test**

`Guild.Tests/Endpoints/GuildLanguageUpdateTests.cs`. Follow the fixture style of the existing endpoint tests in `Guild.Tests/Endpoints/`. Three cases that matter:

```csharp
[Test]
public async Task Declaring_languages_normalizes_and_stores_both()
{
    // dto: PrimaryLanguage = "PT-br", OtherLanguages = ["EN", "pt-BR"]
    // expect: PrimaryLanguage "pt-BR", OtherLanguages ["en"] - the primary is not repeated
}

[Test]
public async Task A_malformed_tag_is_refused_without_touching_the_guild()
{
    // dto: PrimaryLanguage = "nope!"
    // expect: 400, and the stored guild still reads its previous language
}

[Test]
public async Task Omitting_the_fields_leaves_them_alone()
{
    // dto with only Name set
    // expect: languages unchanged
}
```

- [ ] **Step 3: Run it and watch it fail**

Run: `dotnet test Guild.Tests/Guild.Tests.csproj --filter "FullyQualifiedName~GuildLanguageUpdateTests"`

- [ ] **Step 4: Handle it in `UpdateGuild`**

Beside the other `if (dto.X is not null)` guards, and before any mutation so a refusal touches nothing:

```csharp
        if (dto.PrimaryLanguage is not null || dto.OtherLanguages is not null)
        {
            if (!LanguageTag.TryNormalizeSet(
                    dto.PrimaryLanguage ?? guild.PrimaryLanguage,
                    dto.OtherLanguages ?? guild.OtherLanguages,
                    out var primaryLanguage, out var otherLanguages, out var languageProblem))
            {
                return Results.BadRequest(languageProblem);
            }

            guild.PrimaryLanguage = primaryLanguage;
            guild.OtherLanguages = otherLanguages;
        }
```

- [ ] **Step 5: Run the tests**

Expected: PASS.

- [ ] **Step 6: Commit**

```
git add Guild.Application/Dtos/Request/UpdateGuildDto.cs Guild.Application/Endpoints/Guild/GuildEndpoint.cs Guild.Tests/Endpoints/GuildLanguageUpdateTests.cs
git commit -m "feat(guild): accept a guild's languages on the settings patch"
```

**Note for the implementer:** `GuildEndpoint` announces changes with `hub.Clients.Users(...).SendAsync("guild.GuildUpdated", new { GuildId = id })`, an untyped refetch ping rather than a real event. That is pre-existing and out of scope. Ride it; do not introduce a typed event here, and do not change the ping.

---

## Task 3: Mirror the languages into Discovery

**Files:**
- Modify: `Guild.Contracts/Bus/Request/GetGuildProfilesRequest.cs` (the `GuildProfileDto`)
- Modify: `Guild.Application/Bus/Consumers/GetGuildProfilesHandler.cs`
- Modify: `Discovery.Domain/Entities/GuildProfile.cs`
- Modify: `Discovery.Infrastructure/Persistence/MicroserviceContext.cs`
- Modify: `Discovery.Application/Services/GuildProfileMirror.cs`
- Test: `Discovery.Tests/Services/GuildProfileMirrorTests.cs` (extend)

**Interfaces:**
- Consumes: `Guild.PrimaryLanguage`, `Guild.OtherLanguages` from Task 1.
- Produces: `GuildProfile.PrimaryLanguage`, `GuildProfile.OtherLanguages` on the Discovery side.

- [ ] **Step 1: Extend the contract**

On `GuildProfileDto`:

```csharp
    public string PrimaryLanguage { get; set; } = "en";
    public IReadOnlyList<string> OtherLanguages { get; set; } = [];
```

- [ ] **Step 2: Project them in the handler**

Add both to the projection in `GetGuildProfilesHandler.Handle`.

- [ ] **Step 3: Extend the Discovery entity**

```csharp
    public string PrimaryLanguage { get; set; } = "en";
    public List<string> OtherLanguages { get; set; } = [];
```

- [ ] **Step 4: Configure and migrate**

In `Discovery.Infrastructure/Persistence/MicroserviceContext.cs`, in the `GuildProfile` block:

```csharp
            profile.Property(p => p.PrimaryLanguage).HasMaxLength(35);
            profile.Property(p => p.OtherLanguages).HasColumnType("text[]");
```

A mirror must never declare a narrower column than its source (spec 15.1). 35 matches Guild exactly.

```
dotnet ef migrations add AddGuildProfileLanguages --project Discovery.Infrastructure --startup-project Discovery.Application
```

- [ ] **Step 5: Copy them in the mirror**

In `GuildProfileMirror.EnsureFreshAsync`, in the copy-back block, beside `Name` and `IconUrl`:

```csharp
                row.PrimaryLanguage = dto.PrimaryLanguage;
                row.OtherLanguages = [.. dto.OtherLanguages];
```

- [ ] **Step 6: Test that the mirror carries them**

Extend the existing mirror test with one case: a profile answered with `PrimaryLanguage = "de"` and `OtherLanguages = ["en"]` persists both, and a refresh that answers with a shorter list shrinks the stored one rather than merging.

- [ ] **Step 7: Run**

Run: `dotnet test Discovery.Tests/Discovery.Tests.csproj` and `dotnet test Guild.Tests/Guild.Tests.csproj`
Expected: PASS, no regression.

- [ ] **Step 8: Commit**

```
git add Guild.Contracts Guild.Application/Bus/Consumers/GetGuildProfilesHandler.cs Discovery.Domain/Entities/GuildProfile.cs Discovery.Infrastructure Discovery.Application/Services/GuildProfileMirror.cs Discovery.Tests
git commit -m "feat(discovery): mirror a guild's languages onto its profile"
```

---

## Task 4: The viewer's own languages

**Files:**
- Create: `Discovery.Domain/Entities/UserLanguage.cs`
- Modify: `Discovery.Infrastructure/Persistence/MicroserviceContext.cs`
- Modify: `Discovery.Application/Endpoints/InterestEndpoint.cs`
- Modify: `Discovery.Application/Dtos/Request/SaveInterestsDto.cs`
- Modify: `Discovery.Application/Dtos/Response/InterestsDto.cs`
- Modify: `Discovery.Application/Services/` whichever service owns interest writes
- Test: `Discovery.Tests/Endpoints/InterestEndpointTests.cs` (extend)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `SaveInterestsDto.Languages`, `InterestsDto.Languages`, table `user_languages`.

- [ ] **Step 1: The entity**

```csharp
using Persistence;

namespace Discovery.Domain.Entities;

/// <summary>
/// A language the viewer reads. A private feed preference, not a profile fact, which is why it
/// lives here beside their interests rather than on Identity (spec 5.1).
/// </summary>
public class UserLanguage : BaseEntity<UserLanguage>, IPrefixedEntity
{
    public static string Prefix { get; } = "ulng";

    public string UserId { get; set; } = null!;

    /// <summary>BCP-47, already normalized on write.</summary>
    public string Language { get; set; } = null!;
}
```

Configure a unique index on `(UserId, Language)` and `HasMaxLength(35)` on `Language`, then generate `AddUserLanguages`.

- [ ] **Step 2: Extend both DTOs**

`SaveInterestsDto` gains `public List<string> Languages { get; set; } = [];`
`InterestsDto` gains `public IReadOnlyList<string> Languages { get; set; } = [];`

Cap at 10, refused by name if exceeded. Normalize with the Discovery-side equivalent of `LanguageTag`. Discovery cannot reference `Guild.Domain`, so add `Discovery.Domain/Topics/LanguageTag.cs` with the same `Normalize` body. Duplicating a 40-line pure function across a service boundary is correct here; sharing it would mean a new shared package for one function.

- [ ] **Step 3: Write the failing test**

Extend `InterestEndpointTests`:

```csharp
[Test]
public async Task Saving_languages_normalizes_and_dedupes_them()
{
    // ["EN", "en", "pt-br"] stores ["en", "pt-BR"]
}

[Test]
public async Task A_malformed_language_refuses_the_whole_save()
{
    // ["en", "nope!"] returns 400 and stores neither, and leaves existing interests untouched
}

[Test]
public async Task Clearing_languages_is_allowed_and_means_no_filter()
{
    // [] stores nothing, and the read-back reports an empty list rather than a default
}
```

- [ ] **Step 4: Implement the read and the write**

The write replaces the whole set, matching how interests already work. The read returns them sorted, so the client renders a stable order.

- [ ] **Step 5: Run**

Run: `dotnet test Discovery.Tests/Discovery.Tests.csproj`
Expected: PASS.

- [ ] **Step 6: Commit**

```
git add Discovery.Domain Discovery.Infrastructure Discovery.Application Discovery.Tests
git commit -m "feat(discovery): let a viewer say which languages they read"
```

---

## Task 5: A real Postgres harness for Discovery.Tests

All 117 Discovery tests run on EF InMemory. `Discovery.Tests/Helpers/PostgresDiscoveryContext.cs`
renders SQL for `ToQueryString()` assertions and never executes anything. Task 6 filters on a
`text[]` with a nested `Any(...)`, which InMemory refuses to translate, so those tests cannot run
without this. It also pays for itself beyond this plan: the plan-one outage whose root cause was
`GameTopic.Name` declared `varchar(200)` against a 256-wide source was invisible to every InMemory
test and would have failed instantly against a real database.

**Files:**
- Create: `Discovery.Tests/Helpers/PostgresTestDatabase.cs`
- Modify: `Discovery.Tests/Discovery.Tests.csproj`

**Interfaces:**
- Consumes: nothing.
- Produces: `PostgresTestDatabase.EnsureStartedAsync()`, `.ResetToEmptyAsync()`, `.CreateContext()`.

- [ ] **Step 1: Copy the shape that already works**

`Billing.Tests/Helpers/PostgresTestDatabase.cs` is the reference. Match its API exactly so a reader
moving between the two suites finds the same three methods. Note its `PostgreSqlBuilder()`
parameterless constructor is obsolete and warns; use the constructor with the image parameter.

- [ ] **Step 2: Add the package references**

Match the versions `Billing.Tests.csproj` already pins. Do not upgrade them here.

- [ ] **Step 3: Prove it runs a real query**

One test, in a new `Discovery.Tests/Helpers/PostgresHarnessTests.cs`, that InMemory could not pass:

```csharp
[Test]
public async Task A_text_array_column_round_trips_and_is_queryable()
{
    await using var db = PostgresTestDatabase.CreateContext();
    await db.Database.MigrateAsync();

    db.GuildProfiles.Add(new GuildProfile
    {
        Id = GuildProfile.GenerateId(),
        GuildId = "gild_test",
        OtherLanguages = ["en", "de"],
    });
    await db.SaveChangesAsync();

    var found = await db.GuildProfiles
        .Where(p => p.OtherLanguages.Any(o => o == "de"))
        .ToListAsync();

    Assert.That(found, Has.Count.EqualTo(1));
}
```

This depends on Task 3 having added `OtherLanguages`, so Task 5 runs after Task 3.

- [ ] **Step 4: Run**

Run: `dotnet test Discovery.Tests/Discovery.Tests.csproj`
Docker must be running. If it is not, say so rather than claiming it passed.
Expected: PASS, and the existing 117 InMemory tests still pass unchanged. Do not migrate them.

- [ ] **Step 5: Commit**

```
git add Discovery.Tests/Helpers Discovery.Tests/Discovery.Tests.csproj
git commit -m "test(discovery): run against a real postgres where inmemory cannot"
```

---

## Task 6: The feed excludes what the viewer cannot read

**Files:**
- Modify: `Discovery.Application/Services/DiscoveryFeedQuery.cs`
- Modify: `Discovery.Application/Endpoints/FeedEndpoint.cs`
- Modify: `Discovery.Domain/Entities/Listing.cs` (remove `Language`)
- Modify: `Discovery.Application/Dtos/Request/UpsertListingDraftDto.cs` (remove `Language`)
- Modify: `Discovery.Application/Dtos/Response/ListingDto.cs`, `DiscoveryCardDto.cs` (remove `Language`)
- Modify: `Discovery.Application/Services/ListingWriteService.cs` (remove the `Bcp47` check and the field)
- Modify: `Discovery.Infrastructure/Persistence/MicroserviceContext.cs`
- Test: `Discovery.Tests/Services/DiscoveryFeedLanguageTests.cs`

**Interfaces:**
- Consumes: `GuildProfile.PrimaryLanguage`/`OtherLanguages` (Task 3), `UserLanguage` (Task 4).
- Produces: `DiscoveryFeedRequest.IgnoreLanguage`.

- [ ] **Step 1: Write the failing tests first, they are the whole point of this task**

`Discovery.Tests/Services/DiscoveryFeedLanguageTests.cs`:

```csharp
[Test]
public async Task A_viewer_with_no_languages_sees_everything()
{
    // The rule that keeps the feature from being hostile (spec 5.1). Two listings, one German
    // one Japanese, viewer declares nothing: both come back.
}

[Test]
public async Task A_listing_the_viewer_cannot_read_is_excluded()
{
    // viewer ["en"], guild primary "ja" with no others: excluded.
}

[Test]
public async Task A_secondary_language_is_enough_to_match()
{
    // viewer ["en"], guild primary "ja" others ["en"]: included. The match set is
    // {primary} union others, not the primary alone.
}

[Test]
public async Task The_toggle_drops_the_filter()
{
    // viewer ["en"], guild "ja", IgnoreLanguage = true: included.
}
```

- [ ] **Step 2: Run them and watch them fail**

- [ ] **Step 3: Remove `Listing.Language`**

Delete the property, its column configuration, its DTO fields, and the `Bcp47` validation in `ListingWriteService`. Generate `RemoveListingLanguage`. Spec 5.1: languages belong to the guild, so a listing carrying its own is a second source of truth.

- [ ] **Step 4: Filter in `DiscoveryFeedQuery`**

`PublishedCandidatesQuery` loses its `language` parameter. The exclusion joins `guild_profile` and tests intersection:

```csharp
        // Absence of a preference is absence of a filter, never an empty screen (spec 5.1).
        if (!request.IgnoreLanguage && viewerLanguages.Count > 0)
        {
            listingsQuery = listingsQuery.Where(l => ctx.GuildProfiles.Any(p =>
                p.GuildId == l.GuildId
                && (viewerLanguages.Contains(p.PrimaryLanguage)
                    || p.OtherLanguages.Any(o => viewerLanguages.Contains(o)))));
        }
```

`OtherLanguages.Any(...)` over a `text[]` translates on Npgsql. It does not translate on EF InMemory, so any test asserting this path must run against the Postgres test context, not InMemory. State that in the test file.

- [ ] **Step 5: Carry the toggle on the request**

`FeedEndpoint` gains `bool ignoreLanguage = false`, passed through to the query.

- [ ] **Step 6: Run**

Run: `dotnet test Discovery.Tests/Discovery.Tests.csproj`
Expected: PASS, including the four new cases.

- [ ] **Step 7: Commit**

```
git commit -m "feat(discovery): stop showing a listing to someone who cannot read it"
```

---

## Task 7: The client

**Files:**
- Modify: `src/app/features/guild/components/guild-settings-modal/pages/overview-settings/overview-settings.component.{ts,html}`
- Modify: `src/app/services/guild.service.ts` (`UpdateGuildDto`)
- Create: `src/app/components/language-picker/language-picker.component.{ts,html}`
- Modify: `src/app/features/discovery/listing-editor/listing-editor.component.{ts,html}` (remove the language field)
- Modify: `src/app/features/discovery/interest-onboarding/interest-onboarding.component.{ts,html}`
- Modify: `src/app/features/discovery/discover-page/discover-page.component.{ts,html}` (the toggle)
- Modify: `src/app/dtos/{request,response}/discovery.dto.ts`
- Modify: `src/assets/i18n/locales/en.json`

**Interfaces:**
- Consumes: everything above. `CONTENT_LANGUAGES` and `contentLanguageLabel` already exist in `src/app/models/language.model.ts` and are the vocabulary for every picker here.

- [ ] **Step 1: Extract the picker**

The listing editor already has a working single-language `p-select` over `CONTENT_LANGUAGES` with `filterBy="label,english"`. Lift it into `app-language-picker` with `value = model<string>()` and a `multiple = input(false)`, so guild settings uses one instance for the primary and one multi-select capped at 4 for the others. Do not write a second language list.

- [ ] **Step 2: Guild settings**

Two controls under a `LANGUAGES` heading. The others multi-select disables at 4 selected and never offers the primary. Diff against `baseline` like `verificationLevel` does, and send only when changed.

- [ ] **Step 3: Remove the listing editor's language field**

Delete the control, the `language` signal, its DTO field and `LANGUAGE_HINT`. Replace with one line pointing at guild settings, keyed `DISCOVERY.LISTING.LANGUAGE_MOVED`: "Set in your server's settings."

- [ ] **Step 4: Interests screen gains languages**

Beside the topic picker. Seed the first value from the current UI locale via `LanguageService.current()` so the common case needs no interaction, per spec 5.1.

- [ ] **Step 5: The toggle on the feed**

A checkbox labelled `DISCOVERY.FEED.IGNORE_LANGUAGE`: "Show all languages". Off by default. Visible whenever the viewer has declared at least one language, so the exclusion is discoverable rather than a silently shorter feed.

- [ ] **Step 6: Run**

```
bun run test
bun run ng build --configuration development
bun run lint
bunx prettier --write src/app/features/discovery src/app/components/language-picker src/app/features/guild/components/guild-settings-modal/pages/overview-settings src/app/models/language.model.ts src/assets/i18n/locales/en.json
```

- [ ] **Step 7: Commit**

```
git commit -m "feat(discovery): pick a guild's languages, and filter the feed by them"
```

---

## Task 8: Full verification

- [ ] **Step 1: Backend**

Run: `dotnet build Echo.sln`, then `Discovery.Tests`, `Guild.Tests`, `Social.Tests`, `Echo.Tests`, `Billing.Tests`. Docker must be running for `Guild.Tests` and `Billing.Tests`; if it is not, say so rather than claiming they passed.

- [ ] **Step 2: Client**

Run: `bun run test`, `bun run ng build --configuration development`, `bun run lint`.

- [ ] **Step 3: State what is unverified**

The two migrations cannot be applied locally against production data. Report them as unverified and pushed.

- [ ] **Step 4: Check the deployment actually moved**

After CI, confirm `guild/values.yaml` and `discovery/values.yaml` in alpine-infra carry the new SHA. A chart still on the old SHA means the pods are running the old code whatever the commit says.

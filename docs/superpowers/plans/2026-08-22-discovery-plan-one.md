# Discovery, Plan One Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship public community listings: a new Discovery microservice, a shared topic vocabulary, per-user interests, plan-gated guild listings, a ranked feed, and the Discover destination in the Angular client.

**Architecture:** A new `Discovery.{Domain,Contracts,Application,Infrastructure,Tests}` slice with its own Postgres database, behind the existing YARP gateway. Discovery owns listings, tags and interests; it mirrors the game catalog from Social over the bus and guild identity from Guild on a TTL. Ranking and slug normalization are pure functions with no database and no clock. The client adds one store on the two existing store features and one new `MainView`.

**Tech Stack:** .NET 10, Wolverine 6.24.6 on RabbitMQ, EF Core 10 with Npgsql and snake_case naming, NUnit 4 with EF InMemory and hand-rolled fakes, SignalR over a Redis backplane, Angular 21 with `@ngrx/signals`, Helm and Argo CD, Terraform.

**Spec:** `docs/specs/discovery.md`. Read it before task 1. This plan implements sections 1 through 5, 9, 12 and 13 of it, plus the parts of 15 and 16 that plan one needs. Postings, applications, moderation and reports are plans two and three.

## Global Constraints

Every task's requirements implicitly include this section.

**The gateway strips the service prefix.** `discovery-route` matches `/api/v1/discovery/{**catch-all}` and rewrites to `/api/v1/{**catch-all}`. Declare endpoints as `[WolverineGet("/api/v1/discover")]`, never `/api/v1/discovery/discover`. Getting this wrong 404s silently and reads as a cluster routing fault.

**The health endpoint is not prefixed and not rewritten.** `app.MapHealthChecks("/discovery/health")`. Both the Helm probes and YARP's active health check hit it directly on the pod, bypassing the route above.

**`Discovery.Application`'s RootNamespace is `Discovery.Api`,** matching `Social.Application`'s `Social.Api`. The project name and the namespace deliberately differ.

**Never call `SaveChangesAsync` in a Wolverine endpoint or handler.** `opts.Policies.AutoApplyTransactions()` commits on successful return. This is a house rule in `CLAUDE.md`, not a convention.

**An entitlement key absent from `EntitlementKeys.All` silently never resolves.** `EntitlementCatalogueTests` enforces it by reflection. Declaring the `static readonly` field is half the job.

**Enums are stored as strings, not Postgres enums.** Use `.HasConversion<string>()` in `OnModelCreating`. Do not call `options.MapEnum<T>()` for Discovery's own enums. Adding a member to a `MapEnum` enum without a migration crashes the service at startup and no unit test can catch it; Discovery's enums grow in plan two.

**Never hand-edit a migration.** The Designer snapshot desyncs. Add a new EF-generated empty migration plus `Sql()`.

**Tests are NUnit with EF InMemory and hand-written fakes.** There is no mocking framework anywhere in this repo. Do not add one. Handler and endpoint tests call the static method directly with hand-built fakes and assert on the returned `IResult`'s concrete type.

**Write meaningful tests, not a count.** Each test in this plan pins one rule that a careless edit would break. Do not pad with variations that exercise the same branch.

**Another agent is working on `main` concurrently.** Stage every commit by explicit path. Never `git add -A`. Unrelated build or test failures are their in-flight work; report and continue.

**House style.** Plain hyphens only, no em dash or en dash. No emoji in comments, logs, docs or commit messages. Comments record a constraint, a trap, or why an obvious alternative was rejected, never narration. Commits are a conventional prefix and a subject line only, no body, no trailers.

**Angular.** `inject()` not constructor params, `input()`/`output()`/`model()` not decorators, `ChangeDetectionStrategy.OnPush` on every new component, standalone, control-flow blocks (`@if`/`@for`/`@switch`), signals for component state. Four-space indent, single quotes, no bracket spacing in imports.

**UI copy is short.** A label is a label. No paragraph explaining why a control exists, no narrated rationale in an empty state, no help text restating what the button already says. A screen that needs an essay to be usable is a screen with the wrong shape. The longest string in this feature is one sentence.

**Locale strings go in `src/assets/i18n/locales/en.json` only.** `de.json` and `fr.json` lag deliberately (2045 keys against en's 4237). Do not backfill them.

**No god objects.** Spec section 16 is binding: the feed query, listing writes, and the mirrors each get their own class. A single `DiscoveryService` holding all of them is the failure this plan exists to avoid.

---

## File Structure

**New backend projects** (`C:\Users\Domin\RiderProjects\Echo`)

| Path | Responsibility |
|---|---|
| `Discovery.Domain/Topics/TagSlug.cs` | Free text to slug. Pure. |
| `Discovery.Domain/Topics/TopicRef.cs` | The `(kind, id)` pair and its parsing. Pure. |
| `Discovery.Domain/Ranking/ListingRank.cs` | Score inputs to score. Pure, no clock, no database. |
| `Discovery.Domain/Entities/Tag.cs` | Free-form topic row, with alias merging. |
| `Discovery.Domain/Entities/GameTopic.cs` | Mirror of one Social catalog row. |
| `Discovery.Domain/Entities/UserInterest.cs` | One user's pick of one topic. |
| `Discovery.Domain/Entities/GuildProfile.cs` | Mirror of guild identity, with `ProjectedAt`. |
| `Discovery.Domain/Entities/Listing.cs` | The listing aggregate and its state machine. |
| `Discovery.Contracts/Bus/Events/ListingStateChanged.cs` | What Discovery publishes. |
| `Discovery.Infrastructure/Persistence/MicroserviceContext.cs` | DbSets, fluent config, timestamps. |
| `Discovery.Infrastructure/Persistence/DesignTimeFactory.cs` | So `dotnet ef` can construct the context. |
| `Discovery.Infrastructure/DiscoveryInfrastructure.cs` | `AddInfrastructure` / `UseInfrastructure`. |
| `Discovery.Application/Program.cs` | Host wiring. |
| `Discovery.Application/Services/TopicResolver.cs` | The one seam where a `TopicRef` becomes a row. |
| `Discovery.Application/Services/ListingWriteService.cs` | Draft, publish, unlist, bump. |
| `Discovery.Application/Services/DiscoveryFeedQuery.cs` | The ranked read. Nothing else. |
| `Discovery.Application/Services/GuildProfileMirror.cs` | Pull-with-TTL refresh of guild identity. |
| `Discovery.Application/Services/ListingRealtime.cs` | The only place this service pushes SignalR. |
| `Discovery.Application/Endpoints/TopicEndpoint.cs` | `GET /api/v1/topics/search` |
| `Discovery.Application/Endpoints/InterestEndpoint.cs` | `GET`/`PUT /api/v1/me/interests` |
| `Discovery.Application/Endpoints/ListingEndpoint.cs` | The five listing routes. |
| `Discovery.Application/Endpoints/FeedEndpoint.cs` | `GET /api/v1/discover` |
| `Discovery.Application/Bus/GameCatalogSyncService.cs` | Hosted service, pages the mirror in. |
| `Discovery.Application/Bus/GameCatalogChangedHandler.cs` | Triggers an out-of-band resync. |
| `Discovery.Application/Bus/EntitlementsChangedHandler.cs` | Suspends listings on plan loss. |
| `Discovery.Tests/**` | NUnit. |

**Modified backend files**

| Path | Change |
|---|---|
| `Echo.sln` | Five project entries. |
| `Echo.Entitlements/Keys/EntitlementKeys.cs` | Two flags, plus both in `All`. |
| `Billing.Application/appsettings.json` | Both keys across free, plus, pro. |
| `Echo/Proxy/ProxyConfig.cs` | Route, cluster, `Services__Discovery`. |
| `.github/workflows/docker-build.yml` | One matrix row. |
| `Social.Contracts/Bus/Integration/Request/ListGameTopicsRequest.cs` | New paged request and response. |
| `Social.Application/Integration/GameCatalog/ListGameTopicsHandler.cs` | Serves it. |
| `Social.Contracts/Bus/Integration/Events/GameCatalogChanged.cs` | New event. |
| `Social.Application/Services/GameCatalogSeedService.cs` | Publishes it after a seed apply. |
| `Guild.Contracts/Bus/Request/GetGuildProfilesRequest.cs` | New batch request and response. |
| `Guild.Application/Bus/Consumers/GetGuildProfilesHandler.cs` | Serves it. |
| `docs/specs/discovery.md` | Record the two deviations this plan makes. |

**Client files** (`C:\Users\Domin\WebstormProjects\Alpine`)

| Path | Responsibility |
|---|---|
| `src/app/dtos/response/discovery.dto.ts` | Wire types in. |
| `src/app/dtos/request/discovery.dto.ts` | Wire types out. |
| `src/app/services/discovery-api.service.ts` | HTTP only. |
| `src/app/stores/discovery.store.ts` | Feed, listing, interests. |
| `src/app/features/discovery/discover-page/` | The destination. |
| `src/app/features/discovery/topic-picker/` | Shared by interests and the editor. |
| `src/app/features/discovery/interest-onboarding/` | The empty-interests first screen. |
| `src/app/features/discovery/listing-editor/` | Editor plus paywall preview. |
| `src/app/services/realtime-events.ts` | Three payload entries. |
| `src/app/services/realtime-listeners.ts` | One array entry. |
| `src/app/features/main-page/navigation.service.ts` | `discover` and `listing-editor` views. |
| `src/app/features/main-page/main-page.component.html` | Two `@case` blocks. |
| `src/app/features/guild/components/server-taskbar/` | The rail entry. |
| `src/assets/i18n/locales/en.json` | The `DISCOVERY.*` keys. |

**Infra files**

| Repo | Path | Change |
|---|---|---|
| alpine-infra | `discovery/` | Seven files, copied from `social/`. |
| infrastructure | `variables.tf` | `"discovery"` in `db_names`. |
| infrastructure | `modules/argocd/templates/argocd-apps.yaml` | One `Application`, appended after `billing`. |

---

## Task 1: The service boots and answers its health check

Nothing here is worth a unit test. A skeleton's only meaningful assertion is that the solution builds and the host starts, and both are commands, not test methods. Do not add a placeholder test; `Social.Tests/UnitTest1.cs` is a dead `dotnet new` stub and reproducing it would be copying a mistake.

**Files:**
- Create: `Discovery.Domain/Discovery.Domain.csproj`, `Discovery.Contracts/Discovery.Contracts.csproj`, `Discovery.Infrastructure/Discovery.Infrastructure.csproj`, `Discovery.Application/Discovery.Application.csproj`, `Discovery.Tests/Discovery.Tests.csproj`
- Create: `Discovery.Infrastructure/Persistence/MicroserviceContext.cs`, `Discovery.Infrastructure/Persistence/DesignTimeFactory.cs`, `Discovery.Infrastructure/DiscoveryInfrastructure.cs`
- Create: `Discovery.Application/Program.cs`, `Discovery.Application/Dockerfile`
- Modify: `Echo.sln`

**Interfaces:**
- Produces: `Discovery.Infrastructure.Persistence.MicroserviceContext`, `DiscoveryInfrastructure.AddInfrastructure(IServiceCollection)`, `DiscoveryInfrastructure.UseInfrastructure(IApplicationBuilder)`. Every later task consumes the context.

- [ ] **Step 1: Create the four non-test csproj files**

`Discovery.Domain/Discovery.Domain.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Domain\Domain.csproj" />
    <ProjectReference Include="..\Persistence\Persistence.csproj" />
  </ItemGroup>
</Project>
```

`Discovery.Contracts/Discovery.Contracts.csproj` carries no project references on purpose, so other services can reference it without pulling Discovery's internals:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="WolverineFx" Version="6.24.6" />
  </ItemGroup>
</Project>
```

`Discovery.Infrastructure/Discovery.Infrastructure.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Discovery.Tests" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="EFCore.NamingConventions" Version="10.0.1" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.10">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Tools" Version="10.0.10">
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\AppEnvironment\AppEnvironment.csproj" />
    <ProjectReference Include="..\Discovery.Domain\Discovery.Domain.csproj" />
  </ItemGroup>
</Project>
```

`Discovery.Application/Discovery.Application.csproj`. Note `RootNamespace`, and that the package versions are single entries: `Social.Application` carries duplicate `Microsoft.AspNetCore.OpenApi` and `JwtBearer` references at two versions, which is pre-existing sloppiness, not a pattern.

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Discovery.Api</RootNamespace>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Content Include="..\.dockerignore" Link=".dockerignore" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="JasperFx.RuntimeCompiler" Version="5.0.0" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.10" />
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.10" />
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Common" Version="10.0.10" />
    <PackageReference Include="Microsoft.AspNetCore.SignalR.StackExchangeRedis" Version="10.0.10" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.10" />
    <PackageReference Include="Microsoft.Extensions.Caching.StackExchangeRedis" Version="10.0.10" />
    <PackageReference Include="WolverineFx" Version="6.24.6" />
    <PackageReference Include="WolverineFx.EntityFrameworkCore" Version="6.24.6" />
    <PackageReference Include="WolverineFx.Http" Version="6.24.6" />
    <PackageReference Include="WolverineFx.Postgresql" Version="6.24.6" />
    <PackageReference Include="WolverineFx.RabbitMQ" Version="6.24.6" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Messaging\Messaging.csproj" />
    <ProjectReference Include="..\Echo.Realtime\Echo.Realtime.csproj" />
    <ProjectReference Include="..\Echo.Auth\Echo.Auth.csproj" />
    <ProjectReference Include="..\Echo.Entitlements\Echo.Entitlements.csproj" />
    <ProjectReference Include="..\Discovery.Contracts\Discovery.Contracts.csproj" />
    <ProjectReference Include="..\Discovery.Infrastructure\Discovery.Infrastructure.csproj" />
    <ProjectReference Include="..\Guild.Contracts\Guild.Contracts.csproj" />
    <ProjectReference Include="..\Social.Contracts\Social.Contracts.csproj" />
    <ProjectReference Include="..\Billing.Contracts\Billing.Contracts.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the test project**

`Discovery.Tests/Discovery.Tests.csproj`. The `Compile Include` link follows `Guild.Tests`; `Social.Tests` omits it and is the inconsistent one.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="NUnit.Framework" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="10.0.10" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.8.1" />
    <PackageReference Include="NUnit" Version="4.6.1" />
    <PackageReference Include="NUnit.Analyzers" Version="4.14.0" />
    <PackageReference Include="NUnit3TestAdapter" Version="6.2.0" />
    <PackageReference Include="coverlet.collector" Version="10.0.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Discovery.Application\Discovery.Application.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="..\TestConventions\AsyncVoidTestConventionTests.cs"
             Link="TestConventions\AsyncVoidTestConventionTests.cs" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create the DbContext, empty of entities for now**

`Discovery.Infrastructure/Persistence/MicroserviceContext.cs`:

```csharp
using AppEnvironment;
using Microsoft.EntityFrameworkCore;
using Persistence;

namespace Discovery.Infrastructure.Persistence;

public class MicroserviceContext : DbContext
{
    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseNpgsql(Env.Database.ConnectionString()).UseSnakeCaseNamingConvention();
    }

    public override int SaveChanges()
    {
        ChangeTracker.UpdateTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = new())
    {
        ChangeTracker.UpdateTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = new())
    {
        ChangeTracker.UpdateTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
```

`Discovery.Infrastructure/Persistence/DesignTimeFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Discovery.Infrastructure.Persistence;

public class DesignTimeFactory : IDesignTimeDbContextFactory<MicroserviceContext>
{
    public MicroserviceContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MicroserviceContext>().Options);
}
```

`Discovery.Infrastructure/DiscoveryInfrastructure.cs`:

```csharp
using Discovery.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Discovery.Infrastructure;

public static class DiscoveryInfrastructure
{
    public static void AddInfrastructure(this IServiceCollection services) { }

    public static void UseInfrastructure(this IApplicationBuilder builder)
    {
        using var scope = builder.ApplicationServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<MicroserviceContext>().Database.Migrate();
    }
}
```

- [ ] **Step 4: Create Program.cs**

The `codegen` early-exit branch is required. Omitting it breaks the Dockerfile's `dotnet run -- codegen write` step, and the container build fails rather than the app.

```csharp
using AppEnvironment;
using Discovery.Infrastructure;
using Discovery.Infrastructure.Persistence;
using Echo.Auth;
using JasperFx;
using JasperFx.RuntimeCompiler;
using Messaging;
using System.Text.Json.Serialization;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);
builder.AddErrorReporting();

builder.Services.AddOpenApi();
builder.Services.AddGracefulShutdownHealthCheck();
builder.Services.AddInfrastructure();
builder.Services.AddMemoryCache();

var redis = Env.Redis;
builder.Services.AddStackExchangeRedisCache(config =>
{
    config.Configuration = $"{redis.Host}:{redis.Port},password={redis.Password}";
});

builder.Services.AddSignalR(config => { config.EnableDetailedErrors = true; })
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    })
    .AddStackExchangeRedis($"{redis.Host}:{redis.Port},password={redis.Password}");

builder.Services.AddWolverineHttp();
builder.Services.AddVentaJwtBearer();

builder.UseWolverine(opts =>
{
    opts.Services.AddDbContextWithWolverineIntegration<MicroserviceContext>(_ => { });
    opts.ConfigureWolverine();

    if (builder.Environment.IsDevelopment())
    {
        opts.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Dynamic;
        opts.Services.AddRuntimeCompilation();
    }
});

if (args.Contains("codegen") || args.Contains("describe"))
{
    var codeGenApp = builder.Build();
    codeGenApp.MapWolverineEndpoints();
    return await codeGenApp.RunJasperFxCommands(args);
}

var app = builder.Build();

app.MapOpenApi("/internal/openapi/{documentName}.json");
app.UseHttpsRedirection();
app.MapWolverineEndpoints();
app.UseGracefulShutdownHealthCheck();
app.MapHealthChecks("/discovery/health");
app.UseInfrastructure();

return await app.RunJasperFxCommands(args);
```

- [ ] **Step 5: Create the Dockerfile**

`Discovery.Application/Dockerfile`, identical to Social's with the name swapped:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY **/*.csproj ./
RUN for file in $(ls *.csproj); do mkdir -p ${file%.*}/ && mv $file ${file%.*}/; done

RUN dotnet restore "Discovery.Application/Discovery.Application.csproj"

COPY . .
WORKDIR "/src/Discovery.Application"

RUN dotnet run -c $BUILD_CONFIGURATION -- codegen write

RUN dotnet build "./Discovery.Application.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
RUN dotnet publish "./Discovery.Application.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
USER $APP_UID
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Discovery.Application.dll"]
```

- [ ] **Step 6: Add the five projects to the solution**

```bash
dotnet sln Echo.sln add Discovery.Domain/Discovery.Domain.csproj Discovery.Contracts/Discovery.Contracts.csproj Discovery.Infrastructure/Discovery.Infrastructure.csproj Discovery.Application/Discovery.Application.csproj Discovery.Tests/Discovery.Tests.csproj
```

- [ ] **Step 7: Verify the solution builds**

Run: `dotnet build Echo.sln`
Expected: success. Pre-existing errors in projects you did not touch are the other agent's in-flight work; confirm the five `Discovery.*` projects themselves compile before moving on.

- [ ] **Step 8: Commit**

```bash
git add Echo.sln Discovery.Domain Discovery.Contracts Discovery.Infrastructure Discovery.Application Discovery.Tests
git commit -m "feat(discovery): scaffold the discovery service"
```

---

## Task 2: The two entitlement flags

**Files:**
- Modify: `Echo.Entitlements/Keys/EntitlementKeys.cs`
- Modify: `Billing.Application/appsettings.json`
- Test: `Echo.Entitlements.Tests/DiscoveryEntitlementTests.cs`

**Interfaces:**
- Produces: `EntitlementKeys.GuildPublicListing`, `EntitlementKeys.GuildRecruitment`. Task 10 consumes the first.

- [ ] **Step 1: Write the failing test**

`guild.vanity_url` is granted at pro only. These two are granted at plus, which is a deliberate product decision and the reason this test exists: the nearest precedent in the file says something different, and a future reader copying it would get the tier wrong.

`Echo.Entitlements.Tests/DiscoveryEntitlementTests.cs`:

```csharp
using Echo.Entitlements.Keys;
using Microsoft.Extensions.DependencyInjection;

namespace Echo.Entitlements.Tests;

[TestFixture]
public class DiscoveryEntitlementTests
{
    [Test]
    public void Both_discovery_keys_are_listed_in_the_catalogue()
    {
        Assert.That(EntitlementKeys.All, Does.Contain(EntitlementKeys.GuildPublicListing));
        Assert.That(EntitlementKeys.All, Does.Contain(EntitlementKeys.GuildRecruitment));
    }

    [Test]
    public void Free_withholds_both_and_plus_grants_both()
    {
        var free = Resolve(TierFixtures.FreeGuild);
        var plus = Resolve(TierFixtures.PlusGuild);

        Assert.Multiple(() =>
        {
            Assert.That(free.Flag(EntitlementKeys.GuildPublicListing), Is.False);
            Assert.That(free.Flag(EntitlementKeys.GuildRecruitment), Is.False);
            Assert.That(plus.Flag(EntitlementKeys.GuildPublicListing), Is.True);
            Assert.That(plus.Flag(EntitlementKeys.GuildRecruitment), Is.True);
        });
    }

    private static ResolvedEntitlements Resolve(string plan)
    {
        var services = new ServiceCollection();
        services.AddEntitlements(options =>
        {
            options.DefaultGuildPlan = plan;
            options.Plans = TierFixtures.Options().Plans;
        });
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<EntitlementResolver>()
            .ResolveAsync(Subjects.Guild).GetAwaiter().GetResult();
    }
}
```

Check the exact names of `TierFixtures.FreeGuild`, `TierFixtures.PlusGuild`, `TierFixtures.Options()`, `Subjects.Guild` and `ResolvedEntitlements` in `Echo.Entitlements.Tests/EntitlementTestFixtures.cs` before writing, and match them. If `PlusGuild` does not exist, add the plus tier to the fixture alongside the free and pro ones already there.

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test Echo.Entitlements.Tests/Echo.Entitlements.Tests.csproj --filter DiscoveryEntitlementTests`
Expected: compile failure, `GuildPublicListing` does not exist.

- [ ] **Step 3: Declare the keys**

In `Echo.Entitlements/Keys/EntitlementKeys.cs`, beside `GuildVanityUrl`:

```csharp
public static readonly EntitlementKey GuildPublicListing =
    EntitlementKey.Flag("guild.public_listing", EntitlementScope.Guild, false);

public static readonly EntitlementKey GuildRecruitment =
    EntitlementKey.Flag("guild.recruitment", EntitlementScope.Guild, false);
```

Add both to the `All` array in the same file. A key missing from `All` never resolves, and only the reflection test in `EntitlementCatalogueTests` catches it.

- [ ] **Step 4: Grant them per plan**

In `Billing.Application/appsettings.json`, under `Entitlements:Plans`, add to each tier:

```json
"free": { "guild.public_listing": "false", "guild.recruitment": "false" },
"plus": { "guild.public_listing": "true",  "guild.recruitment": "true"  },
"pro":  { "guild.public_listing": "true",  "guild.recruitment": "true"  }
```

Merge these into the existing per-tier objects; do not replace them. A key present in the catalogue but missing from a plan resolves to the catalogue default, which is `false` here, so a missed tier fails closed rather than open.

- [ ] **Step 5: Run the tests**

Run: `dotnet test Echo.Entitlements.Tests/Echo.Entitlements.Tests.csproj`
Expected: PASS, including `EntitlementCatalogueTests`.

- [ ] **Step 6: Commit**

```bash
git add Echo.Entitlements/Keys/EntitlementKeys.cs Billing.Application/appsettings.json Echo.Entitlements.Tests/DiscoveryEntitlementTests.cs
git commit -m "feat(entitlements): add the public listing and recruitment flags"
```

---

## Task 3: Slug normalization

**Files:**
- Create: `Discovery.Domain/Topics/TagSlug.cs`, `Discovery.Domain/Topics/TopicRef.cs`
- Test: `Discovery.Tests/Topics/TagSlugTests.cs`, `Discovery.Tests/Topics/TopicRefTests.cs`

**Interfaces:**
- Produces: `TagSlug.Normalize(string?) -> string?`, `TagSlug.MaxLength`, `TopicRef` record with `Kind`, `Id`, `TopicRef.Parse(string)`, `TopicRef.TryParse(string, out TopicRef)`, `TopicKind` enum with `Game` and `Tag`. Tasks 5, 7, 8 and 10 all consume these.

- [ ] **Step 1: Write the failing tests**

`Discovery.Tests/Topics/TagSlugTests.cs`. Six cases, each a distinct rule. More accented letters or more punctuation marks would exercise the same two branches and are not worth writing.

```csharp
using Discovery.Domain.Topics;

namespace Discovery.Tests.Topics;

[TestFixture]
public class TagSlugTests
{
    [Test]
    public void Punctuation_drops_without_leaving_a_separator() =>
        Assert.That(TagSlug.Normalize("D&D 5e"), Is.EqualTo("dd-5e"));

    [Test]
    public void Runs_of_whitespace_collapse_to_one_hyphen() =>
        Assert.That(TagSlug.Normalize("  Play  By   Post "), Is.EqualTo("play-by-post"));

    [Test]
    public void A_hyphen_separates_rather_than_dropping() =>
        Assert.That(TagSlug.Normalize("Sci-Fi Play-By-Post"), Is.EqualTo("sci-fi-play-by-post"));

    [Test]
    public void Combining_marks_are_stripped() =>
        Assert.That(TagSlug.Normalize("Pokemon"), Is.EqualTo("pokemon"));

    [Test]
    public void Nothing_surviving_is_null_and_not_an_empty_string() =>
        Assert.That(TagSlug.Normalize("---"), Is.Null);

    [Test]
    public void Trailing_punctuation_leaves_no_trailing_hyphen() =>
        Assert.That(TagSlug.Normalize("West Marches!!!"), Is.EqualTo("west-marches"));

    [Test]
    public void Truncation_does_not_leave_a_trailing_hyphen()
    {
        var slug = TagSlug.Normalize(new string('a', TagSlug.MaxLength) + " tail");
        Assert.Multiple(() =>
        {
            Assert.That(slug!.Length, Is.LessThanOrEqualTo(TagSlug.MaxLength));
            Assert.That(slug, Does.Not.EndWith("-"));
        });
    }
}
```

For the combining-mark test, write the input as the decomposed form so the test proves the NFKD path rather than passing trivially: use `"Pokemo\u0301n"` and expect `"pokemon"`.

`Discovery.Tests/Topics/TopicRefTests.cs`:

```csharp
using Discovery.Domain.Topics;

namespace Discovery.Tests.Topics;

[TestFixture]
public class TopicRefTests
{
    [Test]
    public void Parses_a_game_reference()
    {
        var topic = TopicRef.Parse("game:gapp_01ABC");
        Assert.Multiple(() =>
        {
            Assert.That(topic.Kind, Is.EqualTo(TopicKind.Game));
            Assert.That(topic.Id, Is.EqualTo("gapp_01ABC"));
        });
    }

    [Test]
    public void A_tag_reference_normalizes_its_id()
    {
        var topic = TopicRef.Parse("tag:D&D 5e");
        Assert.That(topic.Id, Is.EqualTo("dd-5e"));
    }

    [Test]
    public void An_unknown_kind_does_not_parse() =>
        Assert.That(TopicRef.TryParse("guild:g1", out _), Is.False);

    [Test]
    public void A_tag_that_normalizes_to_nothing_does_not_parse() =>
        Assert.That(TopicRef.TryParse("tag:---", out _), Is.False);
}
```

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test Discovery.Tests/Discovery.Tests.csproj`
Expected: compile failure, `TagSlug` does not exist.

- [ ] **Step 3: Implement**

`Discovery.Domain/Topics/TagSlug.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace Discovery.Domain.Topics;

/// <summary>Folds free text into a tag slug.</summary>
public static class TagSlug
{
    public const int MaxLength = 48;

    /// <summary>The slug, or null when nothing survives normalization.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var builder = new StringBuilder(raw.Length);
        var pendingSeparator = false;

        foreach (var rune in raw.Normalize(NormalizationForm.FormKD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark) continue;

            if (Rune.IsLetterOrDigit(rune))
            {
                if (pendingSeparator && builder.Length > 0) builder.Append('-');
                pendingSeparator = false;
                builder.Append(Rune.ToLowerInvariant(rune));
                continue;
            }

            // Only whitespace and hyphens separate. Everything else drops silently, so "D&D" is
            // one word while "sci-fi" stays two.
            if (Rune.IsWhiteSpace(rune) || rune.Value == '-') pendingSeparator = true;
        }

        var slug = builder.ToString();
        if (slug.Length > MaxLength) slug = slug[..MaxLength].TrimEnd('-');
        return slug.Length == 0 ? null : slug;
    }
}
```

`Discovery.Domain/Topics/TopicRef.cs`:

```csharp
namespace Discovery.Domain.Topics;

public enum TopicKind
{
    Game,
    Tag,
}

/// <summary>A topic on a listing or a profile. Games resolve against the mirrored catalog.</summary>
public readonly record struct TopicRef(TopicKind Kind, string Id)
{
    public static TopicRef Parse(string raw) =>
        TryParse(raw, out var topic) ? topic : throw new FormatException($"Not a topic reference: {raw}");

    public static bool TryParse(string? raw, out TopicRef topic)
    {
        topic = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var separator = raw.IndexOf(':');
        if (separator <= 0 || separator == raw.Length - 1) return false;

        var kind = raw[..separator];
        var id = raw[(separator + 1)..];

        if (kind.Equals("game", StringComparison.OrdinalIgnoreCase))
        {
            topic = new TopicRef(TopicKind.Game, id);
            return true;
        }

        if (!kind.Equals("tag", StringComparison.OrdinalIgnoreCase)) return false;

        var slug = TagSlug.Normalize(id);
        if (slug is null) return false;

        topic = new TopicRef(TopicKind.Tag, slug);
        return true;
    }

    public override string ToString() => $"{(Kind == TopicKind.Game ? "game" : "tag")}:{Id}";
}
```

- [ ] **Step 4: Run and watch them pass**

Run: `dotnet test Discovery.Tests/Discovery.Tests.csproj`
Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add Discovery.Domain/Topics Discovery.Tests/Topics
git commit -m "feat(discovery): normalize tag slugs and parse topic references"
```

---

## Task 4: The ranking function

**Files:**
- Create: `Discovery.Domain/Ranking/ListingRank.cs`
- Test: `Discovery.Tests/Ranking/ListingRankTests.cs`

**Interfaces:**
- Produces: `RankInputs(int MatchedTopics, int ListingTopics, TimeSpan SinceBump, int ActiveMembers)` and `ListingRank.Score(RankInputs) -> double`. Task 12 consumes both.

- [ ] **Step 1: Write the failing tests**

Every test here pins a property rather than a number. Asserting a score to six decimals would only pin the implementation to itself and would fail on any weight change, including a correct one.

```csharp
using Discovery.Domain.Ranking;

namespace Discovery.Tests.Ranking;

[TestFixture]
public class ListingRankTests
{
    private static readonly TimeSpan Now = TimeSpan.Zero;
    private static readonly TimeSpan AWeek = TimeSpan.FromDays(7);

    [Test]
    public void Matching_every_topic_beats_matching_none()
    {
        var all = ListingRank.Score(new RankInputs(4, 4, Now, 100));
        var none = ListingRank.Score(new RankInputs(0, 4, Now, 100));
        Assert.That(all, Is.GreaterThan(none));
    }

    [Test]
    public void At_equal_matches_the_broader_listing_ranks_lower()
    {
        var focused = ListingRank.Score(new RankInputs(2, 2, Now, 100));
        var broad = ListingRank.Score(new RankInputs(2, 8, Now, 100));
        Assert.That(broad, Is.LessThan(focused));
    }

    [Test]
    public void A_week_old_bump_is_worth_half_a_fresh_one()
    {
        var fresh = ListingRank.Score(new RankInputs(0, 4, Now, 0));
        var week = ListingRank.Score(new RankInputs(0, 4, AWeek, 0));
        Assert.That(week, Is.EqualTo(fresh / 2).Within(0.0001));
    }

    [Test]
    public void A_dead_guild_bumping_now_loses_to_a_healthy_one_from_last_week()
    {
        var dead = ListingRank.Score(new RankInputs(0, 4, Now, 0));
        var healthy = ListingRank.Score(new RankInputs(0, 4, AWeek, 5_000));
        Assert.That(healthy, Is.GreaterThan(dead));
    }

    [Test]
    public void With_no_interests_the_interest_term_is_equal_for_everyone()
    {
        var a = ListingRank.Score(new RankInputs(0, 2, Now, 100));
        var b = ListingRank.Score(new RankInputs(0, 8, Now, 100));
        Assert.That(a, Is.EqualTo(b).Within(0.0001));
    }

    [Test]
    public void Extreme_inputs_stay_in_range()
    {
        var scores = new[]
        {
            ListingRank.Score(new RankInputs(0, 0, TimeSpan.FromDays(-5), -10)),
            ListingRank.Score(new RankInputs(99, 1, TimeSpan.FromDays(9999), int.MaxValue)),
        };
        Assert.That(scores, Is.All.InRange(0d, 1d));
    }
}
```

The fourth test is the one that justifies the weights existing at all. It is the claim spec section 9 makes in prose, and it is a property of the three terms together rather than of any one of them.

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test Discovery.Tests/Discovery.Tests.csproj --filter ListingRankTests`
Expected: compile failure.

- [ ] **Step 3: Implement**

```csharp
namespace Discovery.Domain.Ranking;

/// <summary>The rank inputs for one listing, gathered by the feed query.</summary>
public readonly record struct RankInputs(
    int MatchedTopics,
    int ListingTopics,
    TimeSpan SinceBump,
    int ActiveMembers);

public static class ListingRank
{
    private const double InterestWeight = 0.55;
    private const double FreshnessWeight = 0.25;
    private const double HealthWeight = 0.20;

    private const double HalfLifeDays = 7;

    /// <summary>Caps the health term so one very large guild cannot own the feed.</summary>
    private const int HealthCeiling = 10_000;

    public static double Score(RankInputs inputs) =>
        InterestWeight * Interest(inputs)
        + FreshnessWeight * Freshness(inputs.SinceBump)
        + HealthWeight * Health(inputs.ActiveMembers);

    // Divided by the listing's topic count, not the match count: otherwise a listing that fills all
    // eight topic slots outranks a focused one by breadth alone.
    private static double Interest(RankInputs inputs) =>
        inputs.ListingTopics <= 0
            ? 0
            : Math.Clamp((double)inputs.MatchedTopics / inputs.ListingTopics, 0, 1);

    private static double Freshness(TimeSpan sinceBump) =>
        sinceBump <= TimeSpan.Zero ? 1 : Math.Pow(0.5, sinceBump.TotalDays / HalfLifeDays);

    private static double Health(int activeMembers) =>
        activeMembers <= 0
            ? 0
            : Math.Log(1 + Math.Min(activeMembers, HealthCeiling)) / Math.Log(1 + HealthCeiling);
}
```

- [ ] **Step 4: Run and watch them pass**

Run: `dotnet test Discovery.Tests/Discovery.Tests.csproj --filter ListingRankTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add Discovery.Domain/Ranking Discovery.Tests/Ranking
git commit -m "feat(discovery): score a listing from interest overlap, freshness and health"
```

---

## Task 5: Entities and the first migration

**Files:**
- Create: `Discovery.Domain/Entities/{Tag,GameTopic,UserInterest,GuildProfile,Listing,ListingTopic}.cs`
- Modify: `Discovery.Infrastructure/Persistence/MicroserviceContext.cs`
- Create: `Discovery.Infrastructure/Migrations/*` (EF-generated)
- Test: `Discovery.Tests/Entities/ListingStateTests.cs`

**Interfaces:**
- Produces: the six entity types and their DbSets (`Tags`, `GameTopics`, `UserInterests`, `GuildProfiles`, `Listings`, `ListingTopics`); `Listing.Create`, `Listing.Publish`, `Listing.Unlist`, `Listing.Suspend`, `Listing.Bump`, `Listing.BumpAvailableAt`; `ListingState`, `JoinPolicy`, `SuspensionReason`, `InterestSource` enums.

- [ ] **Step 1: Write the failing test**

Only the listing has behaviour worth testing. The other five are data. Testing that a property setter sets a property is the padding this plan's constraints forbid.

```csharp
using Discovery.Domain.Entities;

namespace Discovery.Tests.Entities;

[TestFixture]
public class ListingStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void A_new_listing_is_a_draft_and_has_never_published()
    {
        var listing = Listing.Create("gld_1");
        Assert.Multiple(() =>
        {
            Assert.That(listing.State, Is.EqualTo(ListingState.Draft));
            Assert.That(listing.PublishedAt, Is.Null);
        });
    }

    [Test]
    public void Publishing_stamps_the_first_publish_and_bumps()
    {
        var listing = Listing.Create("gld_1");
        listing.Publish(T0);
        Assert.Multiple(() =>
        {
            Assert.That(listing.State, Is.EqualTo(ListingState.Published));
            Assert.That(listing.PublishedAt, Is.EqualTo(T0));
            Assert.That(listing.LastBumpedAt, Is.EqualTo(T0));
        });
    }

    [Test]
    public void Republishing_after_an_unlist_keeps_the_original_publish_date()
    {
        var listing = Listing.Create("gld_1");
        listing.Publish(T0);
        listing.Unlist();
        listing.Publish(T0.AddDays(30));
        Assert.That(listing.PublishedAt, Is.EqualTo(T0));
    }

    [Test]
    public void Bumping_inside_the_cooldown_is_refused()
    {
        var listing = Listing.Create("gld_1");
        listing.Publish(T0);
        Assert.Multiple(() =>
        {
            Assert.That(listing.Bump(T0.AddHours(71)), Is.False);
            Assert.That(listing.Bump(T0.AddHours(73)), Is.True);
        });
    }

    [Test]
    public void Suspension_records_why_and_unlisting_does_not()
    {
        var suspended = Listing.Create("gld_1");
        suspended.Publish(T0);
        suspended.Suspend(SuspensionReason.PlanLapsed);

        var unlisted = Listing.Create("gld_2");
        unlisted.Publish(T0);
        unlisted.Unlist();

        Assert.Multiple(() =>
        {
            Assert.That(suspended.State, Is.EqualTo(ListingState.Suspended));
            Assert.That(suspended.SuspendedReason, Is.EqualTo(SuspensionReason.PlanLapsed));
            Assert.That(unlisted.State, Is.EqualTo(ListingState.Unlisted));
            Assert.That(unlisted.SuspendedReason, Is.Null);
        });
    }

    [Test]
    public void A_draft_cannot_be_suspended()
    {
        var listing = Listing.Create("gld_1");
        listing.Suspend(SuspensionReason.PlanLapsed);
        Assert.That(listing.State, Is.EqualTo(ListingState.Draft));
    }
}
```

The last two matter most. `Unlisted` and `Suspended` render identically to a stranger and differently to the owner, which is the entire reason they are separate states, and the plan-lapse handler in task 11 depends on a draft being left alone.

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test Discovery.Tests/Discovery.Tests.csproj --filter ListingStateTests`
Expected: compile failure.

- [ ] **Step 3: Write the entities**

`Discovery.Domain/Entities/Listing.cs`:

```csharp
using Discovery.Domain.Topics;
using Persistence;

namespace Discovery.Domain.Entities;

public enum ListingState { Draft, Published, Suspended, Unlisted }

public enum JoinPolicy { Open, Application }

public enum SuspensionReason { PlanLapsed, StaffAction }

public class Listing : BaseEntity<Listing>, IPrefixedEntity
{
    public static string Prefix { get; } = "disc";

    /// <summary>Cooldown between bumps. Spec section 9.1.</summary>
    public static readonly TimeSpan BumpCooldown = TimeSpan.FromHours(72);

    public string GuildId { get; set; } = null!;
    public string Headline { get; set; } = string.Empty;
    public string Pitch { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public JoinPolicy JoinPolicy { get; set; } = JoinPolicy.Open;
    public List<string> Links { get; set; } = [];
    public ListingState State { get; set; } = ListingState.Draft;
    public SuspensionReason? SuspendedReason { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? LastBumpedAt { get; set; }

    public virtual ICollection<ListingTopic> Topics { get; set; } = new List<ListingTopic>();

    public static Listing Create(string guildId) =>
        new() { Id = GenerateId(), GuildId = guildId };

    public DateTimeOffset? BumpAvailableAt =>
        LastBumpedAt is null ? null : LastBumpedAt.Value + BumpCooldown;

    public void Publish(DateTimeOffset now)
    {
        State = ListingState.Published;
        SuspendedReason = null;
        // Keeps the original date: a guild that unlists for a month and comes back is not new.
        PublishedAt ??= now;
        LastBumpedAt = now;
    }

    public void Unlist()
    {
        if (State != ListingState.Published) return;
        State = ListingState.Unlisted;
        SuspendedReason = null;
    }

    public void Suspend(SuspensionReason reason)
    {
        if (State != ListingState.Published) return;
        State = ListingState.Suspended;
        SuspendedReason = reason;
    }

    public bool Bump(DateTimeOffset now)
    {
        if (State != ListingState.Published) return false;
        if (BumpAvailableAt is { } available && now < available) return false;
        LastBumpedAt = now;
        return true;
    }
}
```

`Discovery.Domain/Entities/ListingTopic.cs`, the join row that makes topic overlap a database join rather than a JSON scan:

```csharp
using Discovery.Domain.Topics;
using Persistence;

namespace Discovery.Domain.Entities;

public class ListingTopic : BaseEntity<ListingTopic>, IPrefixedEntity
{
    public static string Prefix { get; } = "lstt";

    public string ListingId { get; set; } = null!;
    public virtual Listing Listing { get; set; } = null!;

    public TopicKind Kind { get; set; }

    /// <summary>A `gapp_` id for a game, a slug for a tag.</summary>
    public string TopicId { get; set; } = null!;

    public static ListingTopic For(string listingId, TopicRef topic) =>
        new() { Id = GenerateId(), ListingId = listingId, Kind = topic.Kind, TopicId = topic.Id };
}
```

`Discovery.Domain/Entities/Tag.cs`:

```csharp
using Persistence;

namespace Discovery.Domain.Entities;

public class Tag : BaseEntity<Tag>, IPrefixedEntity
{
    public static string Prefix { get; } = "tag";

    public string Slug { get; set; } = null!;
    public string DisplayName { get; set; } = null!;

    /// <summary>Set when staff merge this tag into another. Reads resolve through it.</summary>
    public string? AliasOf { get; set; }

    public int UsageCount { get; set; }
}
```

`Discovery.Domain/Entities/GameTopic.cs`, the mirror of one Social row:

```csharp
using Persistence;

namespace Discovery.Domain.Entities;

public class GameTopic : BaseEntity<GameTopic>, IPrefixedEntity
{
    public static string Prefix { get; } = "gmtp";

    /// <summary>Social's `gapp_` id. The topic id, not this row's id.</summary>
    public string GameApplicationId { get; set; } = null!;

    public string Name { get; set; } = null!;
    public string[] Aliases { get; set; } = [];

    /// <summary>Mirrored for the cross-instance topic key federation will need. Unread in v1.</summary>
    public string? SteamAppId { get; set; }

    public bool IsEnabled { get; set; } = true;
}
```

`Discovery.Domain/Entities/UserInterest.cs`:

```csharp
using Discovery.Domain.Topics;
using Persistence;

namespace Discovery.Domain.Entities;

/// <summary>Where an interest came from. Suggested exists so the activity-detection prompt in spec
/// section 3.4 needs no migration.</summary>
public enum InterestSource { Manual, Suggested, Imported }

public class UserInterest : BaseEntity<UserInterest>, IPrefixedEntity
{
    public static string Prefix { get; } = "intr";

    public string UserId { get; set; } = null!;
    public TopicKind Kind { get; set; }
    public string TopicId { get; set; } = null!;
    public InterestSource Source { get; set; } = InterestSource.Manual;
}
```

`Discovery.Domain/Entities/GuildProfile.cs`:

```csharp
using Persistence;

namespace Discovery.Domain.Entities;

/// <summary>Guild identity for a card. Never authoritative: refreshed on a TTL, so a rename shows
/// late. Display only.</summary>
public class GuildProfile : BaseEntity<GuildProfile>, IPrefixedEntity
{
    public static string Prefix { get; } = "gpfl";

    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? IconUrl { get; set; }
    public string? BannerUrl { get; set; }
    public int MemberCount { get; set; }
    public int ActiveMemberCount { get; set; }
    public string Features { get; set; } = string.Empty;
    public DateTimeOffset ProjectedAt { get; set; }
}
```

A user's interest visibility is one boolean per account, not per row, so it belongs on its own tiny table rather than repeated on every interest:

`Discovery.Domain/Entities/InterestVisibility.cs`:

```csharp
using Persistence;

namespace Discovery.Domain.Entities;

public class InterestVisibility : BaseEntity<InterestVisibility>, IPrefixedEntity
{
    public static string Prefix { get; } = "invs";

    public string UserId { get; set; } = null!;
    public bool Visible { get; set; } = true;
}
```

- [ ] **Step 4: Wire the DbContext**

Add to `MicroserviceContext`:

```csharp
public DbSet<Listing> Listings { get; set; }
public DbSet<ListingTopic> ListingTopics { get; set; }
public DbSet<Tag> Tags { get; set; }
public DbSet<GameTopic> GameTopics { get; set; }
public DbSet<UserInterest> UserInterests { get; set; }
public DbSet<InterestVisibility> InterestVisibilities { get; set; }
public DbSet<GuildProfile> GuildProfiles { get; set; }
```

and `OnModelCreating`. Every enum is a string conversion, never `MapEnum`; see Global Constraints.

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.Entity<Listing>(listing =>
    {
        listing.HasIndex(l => l.GuildId).IsUnique();
        listing.HasIndex(l => new { l.State, l.LastBumpedAt });
        listing.Property(l => l.State).HasConversion<string>();
        listing.Property(l => l.JoinPolicy).HasConversion<string>();
        listing.Property(l => l.SuspendedReason).HasConversion<string>();
        listing.Property(l => l.Headline).HasMaxLength(80);
        listing.Property(l => l.Pitch).HasMaxLength(600);
        listing.Property(l => l.Language).HasMaxLength(16);
        listing.HasMany(l => l.Topics)
            .WithOne(t => t.Listing)
            .HasForeignKey(t => t.ListingId)
            .OnDelete(DeleteBehavior.Cascade);
    });

    modelBuilder.Entity<ListingTopic>(topic =>
    {
        topic.Property(t => t.Kind).HasConversion<string>();
        topic.HasIndex(t => new { t.Kind, t.TopicId });
        topic.HasIndex(t => new { t.ListingId, t.Kind, t.TopicId }).IsUnique();
    });

    modelBuilder.Entity<Tag>(tag =>
    {
        tag.HasIndex(t => t.Slug).IsUnique();
        tag.Property(t => t.Slug).HasMaxLength(TagSlug.MaxLength);
        tag.Property(t => t.DisplayName).HasMaxLength(80);
    });

    modelBuilder.Entity<GameTopic>(game =>
    {
        game.HasIndex(g => g.GameApplicationId).IsUnique();
        game.Property(g => g.Name).HasMaxLength(200);
    });

    modelBuilder.Entity<UserInterest>(interest =>
    {
        interest.Property(i => i.Kind).HasConversion<string>();
        interest.Property(i => i.Source).HasConversion<string>();
        interest.HasIndex(i => i.UserId);
        interest.HasIndex(i => new { i.UserId, i.Kind, i.TopicId }).IsUnique();
    });

    modelBuilder.Entity<InterestVisibility>(v => v.HasIndex(x => x.UserId).IsUnique());

    modelBuilder.Entity<GuildProfile>(profile => profile.HasIndex(p => p.GuildId).IsUnique());
}
```

- [ ] **Step 5: Run the entity tests**

Run: `dotnet test Discovery.Tests/Discovery.Tests.csproj --filter ListingStateTests`
Expected: PASS, 6 tests.

- [ ] **Step 6: Generate the migration**

On this machine `dotnet ef` needs `$env:Path` refreshed from Machine and User plus `%USERPROFILE%\.dotnet\tools`, which is on neither.

```bash
dotnet ef migrations add InitialDiscoverySchema --project Discovery.Infrastructure --startup-project Discovery.Application
```

Do not hand-edit the generated files. If the shape is wrong, delete both the migration and its Designer file, fix the model, and regenerate.

- [ ] **Step 7: Commit**

```bash
git add Discovery.Domain/Entities Discovery.Infrastructure/Persistence Discovery.Infrastructure/Migrations Discovery.Tests/Entities
git commit -m "feat(discovery): add listing, topic, interest and guild profile schema"
```

---

## Task 6: Mirror the game catalog from Social

The mirror has to be locally joinable or every ranked query becomes a cross-service fan-out. It arrives over the bus rather than over HTTP: a typed `HttpClient` injected into a Wolverine handler parameter breaks the whole generated handler chain while the service stays green, which is a trap this repo has already been bitten by.

**Files:**
- Create: `Social.Contracts/Bus/Integration/Request/ListGameTopicsRequest.cs`
- Create: `Social.Contracts/Bus/Integration/Events/GameCatalogChanged.cs`
- Create: `Social.Application/Integration/GameCatalog/ListGameTopicsHandler.cs`
- Modify: `Social.Application/Services/GameCatalogSeedService.cs`
- Create: `Discovery.Application/Bus/GameCatalogSyncService.cs`, `Discovery.Application/Bus/GameCatalogChangedHandler.cs`
- Test: `Discovery.Tests/Bus/GameCatalogSyncTests.cs`, `Social.Tests/Handlers/ListGameTopicsHandlerTests.cs`

**Interfaces:**
- Produces: `ListGameTopicsRequest { int Limit; string? After; }`, `ListGameTopicsResponse { IReadOnlyList<GameTopicDto> Topics; string? NextCursor; }`, `GameTopicDto { string Id; string Name; string[] Aliases; string? SteamAppId; bool IsEnabled; }`, `GameCatalogChanged { string Version; }`, and `GameCatalogSync.RunAsync(MicroserviceContext, IMessageBus, CancellationToken)`. Task 7 reads `GameTopics`.

- [ ] **Step 1: Write the failing sync test**

The rule worth pinning is that a resync is a reconciliation, not an append: a game disabled or renamed upstream must change here, and one that vanished must not linger in the picker.

```csharp
using Discovery.Api.Bus;
using Discovery.Domain.Entities;
using Discovery.Tests.Helpers;
using Social.Contracts.Bus.Integration.Request;

namespace Discovery.Tests.Bus;

[TestFixture]
public class GameCatalogSyncTests
{
    [Test]
    public async Task A_first_sync_writes_every_page()
    {
        await using var ctx = TestDiscoveryContext.New();
        var bus = new FakeMessageBus();
        bus.RespondWith<ListGameTopicsRequest, ListGameTopicsResponse>(request =>
            request.After is null
                ? Page(next: "gapp_2", Game("gapp_1", "The Isle"))
                : Page(next: null, Game("gapp_2", "MSFS 2024")));

        await GameCatalogSync.RunAsync(ctx, bus, CancellationToken.None);

        Assert.That(ctx.GameTopics.Select(g => g.Name), Is.EquivalentTo(new[] {"The Isle", "MSFS 2024"}));
    }

    [Test]
    public async Task A_resync_updates_a_renamed_game_rather_than_duplicating_it()
    {
        await using var ctx = TestDiscoveryContext.New();
        ctx.GameTopics.Add(new GameTopic {Id = "gmtp_1", GameApplicationId = "gapp_1", Name = "Old"});
        await ctx.SaveChangesAsync();

        var bus = new FakeMessageBus();
        bus.RespondWith<ListGameTopicsRequest, ListGameTopicsResponse>(_ =>
            Page(next: null, Game("gapp_1", "New")));

        await GameCatalogSync.RunAsync(ctx, bus, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.GameTopics.Count(), Is.EqualTo(1));
            Assert.That(ctx.GameTopics.Single().Name, Is.EqualTo("New"));
        });
    }

    [Test]
    public async Task A_game_that_left_the_catalogue_stops_being_offered()
    {
        await using var ctx = TestDiscoveryContext.New();
        ctx.GameTopics.Add(new GameTopic {Id = "gmtp_1", GameApplicationId = "gapp_gone", Name = "Gone", IsEnabled = true});
        await ctx.SaveChangesAsync();

        var bus = new FakeMessageBus();
        bus.RespondWith<ListGameTopicsRequest, ListGameTopicsResponse>(_ =>
            Page(next: null, Game("gapp_1", "Here")));

        await GameCatalogSync.RunAsync(ctx, bus, CancellationToken.None);

        var gone = ctx.GameTopics.Single(g => g.GameApplicationId == "gapp_gone");
        Assert.That(gone.IsEnabled, Is.False);
    }
}
```

The third test is why the sync disables rather than deletes: a listing already tagged with a game that later left the catalog must keep rendering its chip, and a delete would orphan it.

Write `Discovery.Tests/Helpers/TestDiscoveryContext.cs` copying `Social.Tests/Helpers/TestSocialContext.cs`, including the empty `OnConfiguring` override and its comment, and `Discovery.Tests/Helpers/FakeMessageBus.cs` copying `Social.Tests/Helpers/FakeMessageBus.cs`.

- [ ] **Step 2: Run and watch it fail**

Run: `dotnet test Discovery.Tests/Discovery.Tests.csproj --filter GameCatalogSyncTests`
Expected: compile failure.

- [ ] **Step 3: Add the contracts in Social**

`Social.Contracts/Bus/Integration/Request/ListGameTopicsRequest.cs`:

```csharp
namespace Social.Contracts.Bus.Integration.Request;

/// <summary>One page of the catalog, as topics: names and aliases, no executable rules.</summary>
public class ListGameTopicsRequest
{
    public int Limit { get; set; } = 500;

    /// <summary>The last id of the previous page. Null starts over.</summary>
    public string? After { get; set; }
}

public class ListGameTopicsResponse
{
    public IReadOnlyList<GameTopicDto> Topics { get; set; } = [];
    public string? NextCursor { get; set; }
}

public class GameTopicDto
{
    public string Id { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string[] Aliases { get; set; } = [];
    public string? SteamAppId { get; set; }
    public bool IsEnabled { get; set; }
}
```

`Social.Contracts/Bus/Integration/Events/GameCatalogChanged.cs`:

```csharp
namespace Social.Contracts.Bus.Integration.Events;

public class GameCatalogChanged
{
    public string Version { get; set; } = string.Empty;
}
```

- [ ] **Step 4: Serve the request from Social**

`Social.Application/Integration/GameCatalog/ListGameTopicsHandler.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Social.Infrastructure.Persistence;

namespace Social.Api.Integration.GameCatalog;

public class ListGameTopicsHandler
{
    private const int MaxLimit = 1000;

    public static async Task<ListGameTopicsResponse> Handle(
        ListGameTopicsRequest request,
        MicroserviceContext ctx,
        CancellationToken ct)
    {
        var limit = Math.Clamp(request.Limit, 1, MaxLimit);

        var query = ctx.GameApplications.AsNoTracking().OrderBy(g => g.Id).AsQueryable();
        if (!string.IsNullOrEmpty(request.After))
            query = query.Where(g => string.Compare(g.Id, request.After) > 0);

        var rows = await query.Take(limit + 1).Select(g => new GameTopicDto
        {
            Id = g.Id,
            Name = g.Name,
            Aliases = g.Aliases,
            SteamAppId = g.SteamAppId,
            IsEnabled = g.IsEnabled,
        }).ToListAsync(ct);

        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);

        return new ListGameTopicsResponse
        {
            Topics = rows,
            NextCursor = hasMore ? rows[^1].Id : null,
        };
    }
}
```

`string.Compare` rather than `>` on the string: `GameApplication.Id` ordering is ordinal, matching how `GameCatalogEndpoint` already orders with `StringComparer.Ordinal`.

- [ ] **Step 5: Publish the change event from the seeder**

In `Social.Application/Services/GameCatalogSeedService.cs`, after a seed apply completes, publish `new GameCatalogChanged { Version = <the seed version> }` through the injected `IMessageBus`. Read the file first: if the service already resolves a bus, reuse it; if it does not, add `IMessageBus` to its constructor rather than resolving it from a scope by hand.

- [ ] **Step 6: Write the Discovery sync**

`Discovery.Application/Bus/GameCatalogSyncService.cs`:

```csharp
using Discovery.Domain.Entities;
using Discovery.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Social.Contracts.Bus.Integration.Request;
using Wolverine;

namespace Discovery.Api.Bus;

public static class GameCatalogSync
{
    public static async Task RunAsync(MicroserviceContext ctx, IMessageBus bus, CancellationToken ct)
    {
        var existing = await ctx.GameTopics.ToDictionaryAsync(g => g.GameApplicationId, ct);
        var seen = new HashSet<string>();

        string? cursor = null;
        do
        {
            var page = await bus.InvokeAsync<ListGameTopicsResponse>(
                new ListGameTopicsRequest { After = cursor }, ct);

            foreach (var dto in page.Topics)
            {
                seen.Add(dto.Id);
                if (!existing.TryGetValue(dto.Id, out var row))
                {
                    row = new GameTopic { Id = GameTopic.GenerateId(), GameApplicationId = dto.Id };
                    ctx.GameTopics.Add(row);
                    existing[dto.Id] = row;
                }

                row.Name = dto.Name;
                row.Aliases = dto.Aliases;
                row.SteamAppId = dto.SteamAppId;
                row.IsEnabled = dto.IsEnabled;
            }

            cursor = page.NextCursor;
        } while (cursor is not null);

        // Disabled, not deleted: a listing already tagged with a game that left the catalogue must
        // keep rendering its chip.
        foreach (var row in existing.Values.Where(r => !seen.Contains(r.GameApplicationId)))
            row.IsEnabled = false;

        await ctx.SaveChangesAsync(ct);
    }
}

/// <summary>Syncs at startup and daily. The event handler covers everything in between.</summary>
public class GameCatalogSyncService(IServiceProvider services, ILogger<GameCatalogSyncService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = services.CreateScope();
                await GameCatalogSync.RunAsync(
                    scope.ServiceProvider.GetRequiredService<MicroserviceContext>(),
                    scope.ServiceProvider.GetRequiredService<IMessageBus>(),
                    stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Game catalog sync failed, retrying on the next tick");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
```

`Discovery.Application/Bus/GameCatalogChangedHandler.cs`:

```csharp
using Discovery.Infrastructure.Persistence;
using Social.Contracts.Bus.Integration.Events;
using Wolverine;

namespace Discovery.Api.Bus;

public class GameCatalogChangedHandler
{
    public static Task Handle(GameCatalogChanged message, MicroserviceContext ctx, IMessageBus bus, CancellationToken ct)
        => GameCatalogSync.RunAsync(ctx, bus, ct);
}
```

Register the hosted service in `Program.cs`: `builder.Services.AddHostedService<GameCatalogSyncService>();`

- [ ] **Step 7: Run the tests**

Run: `dotnet test Discovery.Tests/Discovery.Tests.csproj --filter GameCatalogSyncTests`
Expected: PASS, 3 tests.

Then run `dotnet test Social.Tests/Social.Tests.csproj` and confirm the handler test you added passes and nothing else regressed.

- [ ] **Step 8: Commit**

```bash
git add Social.Contracts/Bus/Integration Social.Application/Integration/GameCatalog Social.Application/Services/GameCatalogSeedService.cs Discovery.Application/Bus Discovery.Tests/Bus Discovery.Tests/Helpers Social.Tests/Handlers/ListGameTopicsHandlerTests.cs
git commit -m "feat(discovery): mirror the game catalogue from social over the bus"
```

---

## Task 7: Topic search

**Files:**
- Create: `Discovery.Application/Services/TopicResolver.cs`, `Discovery.Application/Endpoints/TopicEndpoint.cs`, `Discovery.Application/Dtos/Response/TopicDto.cs`
- Create: a migration enabling `pg_trgm` and its indexes
- Test: `Discovery.Tests/Services/TopicResolverTests.cs`

**Interfaces:**
- Produces: `TopicResolver.SearchAsync(string query, int limit, CancellationToken) -> IReadOnlyList<TopicDto>`, `TopicResolver.ResolveAsync(IEnumerable<TopicRef>, CancellationToken) -> IReadOnlyList<TopicDto>`, `TopicResolver.EnsureTagsAsync(IEnumerable<TopicRef>, CancellationToken)`, and `TopicDto { string Kind; string Id; string Name; string? SteamAppId; }`. Tasks 8, 10 and 12 all resolve topics through this one seam, per spec section 16.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestFixture]
public class TopicResolverTests
{
    [Test]
    public async Task Games_rank_above_tags_for_the_same_query() { }

    [Test]
    public async Task An_alias_finds_the_game_under_its_canonical_name() { }

    [Test]
    public async Task A_disabled_game_is_never_offered() { }

    [Test]
    public async Task A_tag_merged_into_another_resolves_to_its_target() { }

    [Test]
    public async Task Ensuring_an_unknown_tag_mints_it_once_and_reuses_it_after() { }
}
```

Fill each body following the fixture style in step 1 of task 6. The second and fourth are the ones that justify the design: aliases are the reason games reuse Social's catalog instead of a new tag table, and merge-through is the reason a free-form vocabulary stays clean.

Because these exercise trigram ranking, which EF InMemory cannot evaluate, write `SearchAsync` so the ordering decision is a separate pure method (`TopicResolver.RankOrder(candidates, query)`) that the tests call directly, and keep the database call to a plain filter. This keeps the tests meaningful rather than testing the InMemory provider.

- [ ] **Step 2: Run and watch them fail**

Run: `dotnet test Discovery.Tests/Discovery.Tests.csproj --filter TopicResolverTests`

- [ ] **Step 3: Implement `TopicResolver`**

One class, three responsibilities that are genuinely the same one: turning text or a `TopicRef` into a `TopicDto`. Games come from `GameTopics`, tags from `Tags` with `AliasOf` followed once. `EnsureTagsAsync` mints a `Tag` for any `TopicRef` of kind `Tag` with no row, setting `DisplayName` from the caller's raw text and `Slug` from `TagSlug.Normalize`.

- [ ] **Step 4: Add the endpoint**

```csharp
[Authorize]
public static class TopicEndpoint
{
    [WolverineGet("/api/v1/topics/search")]
    public static async Task<IResult> SearchAsync(
        [NotBody] TopicResolver topics,
        string? q = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q)) return Results.Ok(new { topics = Array.Empty<TopicDto>() });
        return Results.Ok(new { topics = await topics.SearchAsync(q, Math.Clamp(limit, 1, 50), ct) });
    }
}
```

Route is `/api/v1/topics/search`, with no `discovery` segment. See Global Constraints.

- [ ] **Step 5: Add the trigram migration**

```bash
dotnet ef migrations add TopicSearchIndexes --project Discovery.Infrastructure --startup-project Discovery.Application
```

The migration is empty of model changes, so add the extension and indexes with `Sql()` inside the generated `Up`, which is the sanctioned way to put raw SQL in a migration here:

```csharp
migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
migrationBuilder.Sql("CREATE INDEX ix_game_topics_name_trgm ON game_topics USING gin (name gin_trgm_ops);");
migrationBuilder.Sql("CREATE INDEX ix_tags_display_name_trgm ON tags USING gin (display_name gin_trgm_ops);");
```

- [ ] **Step 6: Run the tests, then commit**

```bash
git add Discovery.Application/Services/TopicResolver.cs Discovery.Application/Endpoints/TopicEndpoint.cs Discovery.Application/Dtos Discovery.Infrastructure/Migrations Discovery.Tests/Services/TopicResolverTests.cs
git commit -m "feat(discovery): search games and tags through one topic seam"
```

---

## Task 8: User interests

**Files:**
- Create: `Discovery.Application/Endpoints/InterestEndpoint.cs`, `Discovery.Application/Services/InterestService.cs`
- Modify: `Discovery.Application/Services/ListingRealtime.cs` (created here, extended in task 10)
- Test: `Discovery.Tests/Services/InterestServiceTests.cs`

**Interfaces:**
- Produces: `InterestService.GetAsync(userId, ct)`, `InterestService.ReplaceAsync(userId, IReadOnlyList<TopicRef>, bool visible, ct)`, and `InterestsDto { IReadOnlyList<TopicDto> Topics; bool Visible; }`. Task 12 reads interests for ranking.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestFixture]
public class InterestServiceTests
{
    [Test] public async Task Replacing_removes_what_is_no_longer_listed() { }
    [Test] public async Task More_than_the_cap_is_refused_and_nothing_is_written() { }
    [Test] public async Task An_unknown_tag_is_minted_on_the_way_in() { }
    [Test] public async Task Duplicates_in_one_request_collapse_to_one_row() { }
    [Test] public async Task Hiding_interests_does_not_remove_them() { }
}
```

The second matters because a partial write on a rejected request leaves a user with half their interests and no error they can act on. The fifth pins the privacy contract from the spec: `visible` is about other people's view, never about ranking your own feed.

- [ ] **Step 2: Run and watch them fail**

- [ ] **Step 3: Implement `InterestService`**

Cap is 25. Validate the whole request before writing anything. `ReplaceAsync` calls `TopicResolver.EnsureTagsAsync` first, then diffs the existing rows against the requested set, deleting and inserting only the difference so unchanged rows keep their `Source`.

- [ ] **Step 4: Add the endpoints**

```csharp
[Authorize]
public static class InterestEndpoint
{
    [WolverineGet("/api/v1/me/interests")]
    public static async Task<IResult> GetAsync(
        [NotBody] InterestService interests,
        [NotBody] ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();
        return Results.Ok(await interests.GetAsync(userId, ct));
    }

    [WolverinePut("/api/v1/me/interests")]
    public static async Task<IResult> PutAsync(
        UpdateInterestsDto dto,
        [NotBody] InterestService interests,
        [NotBody] ListingRealtime realtime,
        [NotBody] ClaimsPrincipal user,
        CancellationToken ct = default)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var refs = new List<TopicRef>();
        foreach (var raw in dto.Topics)
        {
            if (!TopicRef.TryParse(raw, out var topic)) return Results.BadRequest($"Not a topic: {raw}");
            refs.Add(topic);
        }

        if (refs.Count > InterestService.MaxInterests)
            return Results.BadRequest($"At most {InterestService.MaxInterests} interests.");

        var result = await interests.ReplaceAsync(userId, refs, dto.Visible, ct);
        await realtime.InterestsChangedAsync(userId, ct);
        return Results.Ok(result);
    }
}
```

- [ ] **Step 5: Create `ListingRealtime` with its first method**

This is the only class in the service allowed to touch `IHubContext`. The hub has exactly one group convention, `device:{userId}:{deviceId}`, and no guild groups at all, so user targeting is `Clients.User(id)` and guild fan-out resolves an id list.

```csharp
namespace Discovery.Api.Services;

/// <summary>Every SignalR push this service makes. Nothing else injects IHubContext.</summary>
public class ListingRealtime(IHubContext<EchoRealtimeHub> hub, IMessageBus bus)
{
    public Task InterestsChangedAsync(string userId, CancellationToken ct) =>
        hub.Clients.User(userId).SendAsync("discovery.InterestsChanged", new { userId }, ct);
}
```

- [ ] **Step 6: Run the tests, then commit**

```bash
git add Discovery.Application/Services/InterestService.cs Discovery.Application/Services/ListingRealtime.cs Discovery.Application/Endpoints/InterestEndpoint.cs Discovery.Tests/Services/InterestServiceTests.cs
git commit -m "feat(discovery): let a user keep a set of interests"
```

---

## Task 9: The guild profile mirror

Deviation from spec section 2, recorded in step 5. Guild publishes no guild-lifecycle events today, only the `...ForBots` family. Adding five to feed a card is a larger change to Guild than this feature earns, so the mirror is pull-with-TTL. The spec already carries `ProjectedAt` and calls a stale projection a reconcile trigger, which is exactly this.

**Files:**
- Create: `Guild.Contracts/Bus/Request/GetGuildProfilesRequest.cs`
- Create: `Guild.Application/Bus/Consumers/GetGuildProfilesHandler.cs`
- Create: `Discovery.Application/Services/GuildProfileMirror.cs`
- Test: `Discovery.Tests/Services/GuildProfileMirrorTests.cs`

**Interfaces:**
- Produces: `GuildProfileMirror.EnsureFreshAsync(IReadOnlyCollection<string> guildIds, CancellationToken) -> IReadOnlyDictionary<string, GuildProfile>`, and `GuildProfileMirror.Ttl`. Task 12 calls it for every card page.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestFixture]
public class GuildProfileMirrorTests
{
    [Test] public async Task A_missing_profile_is_fetched() { }
    [Test] public async Task A_fresh_profile_is_not_refetched() { }
    [Test] public async Task A_stale_profile_is_refetched_and_overwritten() { }
    [Test] public async Task A_guild_the_request_could_not_answer_keeps_its_stale_copy() { }
}
```

The fourth is the important one. Guild being briefly unreachable must degrade to an out-of-date guild name on a card, never to a blank card or a failed feed request.

- [ ] **Step 2: Run and watch them fail**

- [ ] **Step 3: Add the Guild contract and handler**

`GetGuildProfilesRequest { IReadOnlyList<string> GuildIds }` and `GetGuildProfilesResponse { IReadOnlyList<GuildProfileDto> Profiles }` with `GuildProfileDto { string GuildId; string Name; string? IconUrl; string? BannerUrl; int MemberCount; int ActiveMemberCount; string Features; }`. Both in the same file, matching `CreateBotGuildMemberCommand`.

The handler is a static `Handle(GetGuildProfilesRequest, MicroserviceContext, CancellationToken)` in `Guild.Application`. Cap the id list at 200 per request and ignore the excess rather than throwing, since the caller is a page of cards.

- [ ] **Step 4: Implement the mirror**

TTL is 6 hours. `EnsureFreshAsync` loads local rows, selects the ids whose `ProjectedAt` is older than the TTL or absent, asks Guild for exactly those, writes what came back, and returns everything it has including rows it failed to refresh.

- [ ] **Step 5: Record the deviation in the spec**

In `docs/specs/discovery.md` section 2, change the `guild_profile` row's justification to say the refresh is pull-with-TTL rather than event-projected, and why. One or two sentences. Do not add a changelog or a companion document; house style forbids it and the spec is edited in place.

- [ ] **Step 6: Run the tests, then commit**

```bash
git add Guild.Contracts/Bus/Request/GetGuildProfilesRequest.cs Guild.Application/Bus/Consumers/GetGuildProfilesHandler.cs Discovery.Application/Services/GuildProfileMirror.cs Discovery.Tests/Services/GuildProfileMirrorTests.cs docs/specs/discovery.md
git commit -m "feat(discovery): mirror guild identity on a ttl for card rendering"
```

---

## Task 10: Listing writes and the plan gate

**Files:**
- Create: `Discovery.Application/Services/ListingWriteService.cs`, `Discovery.Application/Endpoints/ListingEndpoint.cs`
- Modify: `Discovery.Application/Services/ListingRealtime.cs`
- Create: `Discovery.Contracts/Bus/Events/ListingStateChanged.cs`
- Test: `Discovery.Tests/Endpoints/ListingEndpointTests.cs`

**Interfaces:**
- Produces: `ListingWriteService.UpsertDraftAsync`, `.PublishAsync`, `.UnlistAsync`, `.BumpAsync`, each returning a small result record carrying the listing and a refusal reason; `ListingDto`. Task 12 reads `Listings`, the client consumes `ListingDto`.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestFixture]
public class ListingEndpointTests
{
    [Test] public async Task Publishing_without_the_entitlement_answers_the_documented_error_code() { }
    [Test] public async Task Publishing_with_the_entitlement_publishes_and_pushes_one_event() { }
    [Test] public async Task Saving_a_draft_never_checks_the_entitlement() { }
    [Test] public async Task Bumping_inside_the_cooldown_answers_409_with_the_next_available_time() { }
    [Test] public async Task A_user_without_ManageGuild_cannot_write_the_listing() { }
    [Test] public async Task More_than_eight_topics_is_refused() { }
    [Test] public async Task Links_outside_the_allowlist_are_refused() { }
}
```

Test three is load-bearing: publish is a separate route from the draft write precisely so the plan check does not run on every autosave, and folding them back together later would silently reintroduce that cost.

Test four checks the body carries `bumpAvailableAt`, because the client renders a countdown from it. A bare 409 would leave the button lying.

- [ ] **Step 2: Run and watch them fail**

- [ ] **Step 3: Implement `ListingWriteService`**

Injects `MicroserviceContext`, `TopicResolver`, and an optional `EntitlementResolver? entitlements = null`. The optional resolver follows `VanityUrlService`: null means self-host or a test with no billing wired, which is always entitled.

The entitlement read is strict, like `SetVanityUrlAsync` and unlike the display path: a published listing is a persistent artifact, so an unreadable Billing service must fail closed.

```csharp
private async Task<bool> IsEntitledAsync(string guildId, CancellationToken ct)
{
    if (entitlements is null) return true;
    try
    {
        var set = await entitlements.ResolveAsync(EntitlementSubject.ForGuild(guildId), ct);
        return set.Flag(EntitlementKeys.GuildPublicListing);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not resolve the public listing entitlement for {GuildId}", guildId);
        return false;
    }
}
```

Validation on the draft write: headline at most 80, pitch at most 600, 1 to 8 topics, at most 3 links each matching the configured host allowlist, language a well-formed BCP-47 tag. Reject the whole request before writing anything.

- [ ] **Step 4: Implement the endpoints**

Five routes, all under `/api/v1/guilds/{guildId}/listing`. Permission first via a bus call to Guild's existing `HasUserPermissionToGuildResponse` request with `Permissions.ManageGuild`, answering a bare `Results.Forbid()`. Entitlement second, answering the JSON body:

```csharp
return Results.Json(
    new { error = "public_listing_not_entitled", message = "This guild's plan does not include a public listing." },
    statusCode: StatusCodes.Status403Forbidden);
```

Two distinct 403 shapes, matching `VanityUrlEndpoint`: bare `Forbid()` for a permission failure, a JSON error body for an entitlement failure. The client distinguishes them.

- [ ] **Step 5: Push the realtime events**

Add to `ListingRealtime`. Guild fan-out has no SignalR group, so resolve the audience first:

```csharp
public async Task ListingChangedAsync(string eventName, Listing listing, CancellationToken ct)
{
    var members = await bus.InvokeAsync<ListGuildMembersResponse>(
        new ListGuildMembersRequest { GuildId = listing.GuildId, Limit = 500 }, ct);

    var audience = members.Members.Where(m => !m.IsBot).Select(m => m.UserId).ToList();
    if (audience.Count == 0) return;

    await hub.Clients.Users(audience).SendAsync(
        eventName,
        new { listingId = listing.Id, guildId = listing.GuildId, state = listing.State.ToString() },
        ct);
}
```

Call it with `discovery.ListingPublished`, `discovery.ListingUpdated` and `discovery.ListingUnlisted`.

- [ ] **Step 6: Run the tests, then commit**

```bash
git add Discovery.Application/Services/ListingWriteService.cs Discovery.Application/Services/ListingRealtime.cs Discovery.Application/Endpoints/ListingEndpoint.cs Discovery.Contracts/Bus/Events Discovery.Tests/Endpoints/ListingEndpointTests.cs
git commit -m "feat(discovery): draft, publish, unlist and bump a guild listing"
```

---

## Task 11: A lapsed plan suspends the listing

**Files:**
- Create: `Discovery.Application/Bus/EntitlementsChangedHandler.cs`
- Test: `Discovery.Tests/Bus/EntitlementsChangedHandlerTests.cs`

**Interfaces:**
- Consumes: `Billing.Contracts.Bus.Events.EntitlementsChanged`, `EntitlementKeys.GuildPublicListing`, `Listing.Suspend`.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestFixture]
public class EntitlementsChangedHandlerTests
{
    [Test] public async Task Losing_the_flag_suspends_a_published_listing() { }
    [Test] public async Task Losing_the_flag_leaves_a_draft_alone() { }
    [Test] public async Task An_event_for_an_unrelated_key_changes_nothing() { }
    [Test] public async Task Regaining_the_flag_does_not_republish_by_itself() { }
    [Test] public async Task A_user_subject_event_is_ignored() { }
}
```

The fourth encodes a product decision: republishing is the owner's action. Auto-republishing would put a community back in a public feed without anyone deciding to, which is not a thing to do on a billing webhook.

The third avoids a whole class of waste: `EntitlementsChanged` fires for every key change on every plan, so a handler that does not filter on `ChangedKeys` rewrites listings on unrelated billing traffic.

- [ ] **Step 2: Run and watch them fail**

- [ ] **Step 3: Implement**

Filter on `message.SubjectKind == SubjectKind.Guild` and `message.ChangedKeys` containing `guild.public_listing`. Resolve the current value; if it is false, suspend the guild's `Published` listing with `SuspensionReason.PlanLapsed` and push `discovery.ListingSuspended` carrying `reason: "plan_lapsed"`.

Also register `AddEntitlementCache()` in `Program.cs` and add the standard per-service `EntitlementCacheHandler`, copying `Guild.Application/Bus/Events/Monetization/EntitlementCacheHandler.cs`. Without it a stale local cache re-answers a decision the version already invalidated.

- [ ] **Step 4: Run the tests, then commit**

```bash
git add Discovery.Application/Bus/EntitlementsChangedHandler.cs Discovery.Application/Bus/EntitlementCacheHandler.cs Discovery.Tests/Bus/EntitlementsChangedHandlerTests.cs
git commit -m "feat(discovery): suspend a listing when its guild loses the plan"
```

---

## Task 12: The ranked feed

**Files:**
- Create: `Discovery.Application/Services/DiscoveryFeedQuery.cs`, `Discovery.Application/Services/FeedCursor.cs`, `Discovery.Application/Endpoints/FeedEndpoint.cs`
- Test: `Discovery.Tests/Services/FeedCursorTests.cs`, `Discovery.Tests/Services/DiscoveryFeedQueryTests.cs`

**Interfaces:**
- Produces: `DiscoveryFeedQuery.RunAsync(FeedRequest, CancellationToken) -> FeedPage`, `FeedCursor.Encode(double score, string listingId)`, `FeedCursor.TryDecode(string?, out double, out string)`, `DiscoveryCardDto`, `DiscoveryFeedDto`.

- [ ] **Step 1: Write the failing cursor tests**

```csharp
[TestFixture]
public class FeedCursorTests
{
    [Test] public void A_cursor_round_trips() { }
    [Test] public void A_malformed_cursor_decodes_to_nothing_rather_than_throwing() { }
    [Test] public void The_id_breaks_ties_so_equal_scores_page_without_repeating() { }
}
```

The third is why the cursor carries the id at all. Score alone repeats or skips rows whenever two listings tie, which they will, constantly, at zero interest overlap.

- [ ] **Step 2: Write the failing query tests**

```csharp
[TestFixture]
public class DiscoveryFeedQueryTests
{
    [Test] public async Task Only_published_listings_appear() { }
    [Test] public async Task Every_card_names_the_topics_it_matched() { }
    [Test] public async Task With_no_interests_the_feed_is_still_ordered_and_not_empty() { }
    [Test] public async Task A_topic_filter_excludes_listings_without_it() { }
    [Test] public async Task A_card_carries_guild_identity_from_the_mirror() { }
}
```

Test one covers drafts, unlisted and suspended in a single assertion, which is the honest scope of the rule; three separate tests for one `WHERE` clause would be padding.

Test three is the empty-interests path, which is the first thing a new user sees and the easiest to leave returning nothing.

- [ ] **Step 3: Run and watch them fail**

- [ ] **Step 4: Implement `FeedCursor`**

Base64url over `{score:R}|{listingId}`. `TryDecode` returns false on anything malformed rather than throwing, because a cursor arrives from a client and a bad one must answer the first page, not a 500.

- [ ] **Step 5: Implement `DiscoveryFeedQuery`**

Read-only, this class does no writing. Steps:

1. Load the caller's interests as a `HashSet<(TopicKind, string)>`.
2. Query published listings with their topics, applying the topic and language filters and, when `q` is present, a text match over headline and pitch.
3. For each candidate, compute `matchedTopics` by set intersection and build `RankInputs`. `SinceBump` is `now - LastBumpedAt`, with `now` passed in rather than read, so the query stays testable.
4. Score with `ListingRank.Score`, order by score descending then id, apply the cursor, take the page.
5. Call `GuildProfileMirror.EnsureFreshAsync` for the page's guild ids and project into `DiscoveryCardDto`.

The mirror call happens after paging, not before, so a page of 24 cards refreshes at most 24 profiles.

For v1 the scoring runs in memory over the filtered candidate set rather than in SQL. Say so in a comment with the threshold at which that stops being acceptable: it is fine while published listings number in the thousands, and the fix when it is not is a materialized score column refreshed on write and on bump, not a cleverer query.

- [ ] **Step 6: Implement the endpoint**

```csharp
[Authorize]
public static class FeedEndpoint
{
    [WolverineGet("/api/v1/discover")]
    public static async Task<IResult> DiscoverAsync(
        [NotBody] DiscoveryFeedQuery feed,
        [NotBody] ClaimsPrincipal user,
        string? q = null,
        string? topics = null,
        string? language = null,
        string? cursor = null,
        int limit = 24,
        CancellationToken ct = default)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Results.Unauthorized();

        var filters = (topics ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(raw => TopicRef.TryParse(raw, out var topic) ? topic : (TopicRef?)null)
            .Where(topic => topic is not null)
            .Select(topic => topic!.Value)
            .ToList();

        return Results.Ok(await feed.RunAsync(new FeedRequest(userId, q, filters, language, cursor,
            Math.Clamp(limit, 1, 50)), ct));
    }
}
```

An unparseable topic in the filter is dropped rather than refused. The filter comes from a URL a user can edit, and a 400 on a hand-mangled query string helps nobody.

- [ ] **Step 7: Run the tests, then commit**

```bash
git add Discovery.Application/Services/DiscoveryFeedQuery.cs Discovery.Application/Services/FeedCursor.cs Discovery.Application/Endpoints/FeedEndpoint.cs Discovery.Tests/Services/FeedCursorTests.cs Discovery.Tests/Services/DiscoveryFeedQueryTests.cs
git commit -m "feat(discovery): serve a ranked, cursor-paged discovery feed"
```

---

## Task 13: Gateway route and CI

**Files:**
- Modify: `Echo/Proxy/ProxyConfig.cs`, `.github/workflows/docker-build.yml`

- [ ] **Step 1: Add the route**

In `GetRoutes()`, beside the social route:

```csharp
new RouteConfig
{
    RouteId = "discovery-route",
    ClusterId = "discovery-cluster",
    Match = new RouteMatch { Path = "/api/v1/discovery/{**catch-all}" }
}.WithTransformPathRouteValues(pattern: new PathString("/api/v1/{**catch-all}")),
```

- [ ] **Step 2: Add the destination and cluster**

In `GetClusters()`, beside the `social` line:

```csharp
var discovery = Environment.GetEnvironmentVariable("Services__Discovery") ?? "http://discovery.default.svc.cluster.local";
```

and the cluster, copying the social block with `Active.Path = "discovery/health"`.

- [ ] **Step 3: Add the CI matrix row**

After the `billing` entry:

```yaml
          - project: discovery
            context: .
            dockerfile: Discovery.Application/Dockerfile
            image: discovery-application
```

- [ ] **Step 4: Verify and commit**

Run: `dotnet build Echo/Echo.csproj`
Expected: success.

```bash
git add Echo/Proxy/ProxyConfig.cs .github/workflows/docker-build.yml
git commit -m "feat(discovery): route the gateway at the discovery service and build its image"
```

---

## Task 14: Helm chart and Terraform

Nothing here can be verified locally. The deliverable is a reviewable diff; the Argo sync and the Terraform run are approved in the cloud. Say plainly in the handoff that this is unverified.

**Files:**
- Create in alpine-infra: `discovery/{Chart.yaml,values.yaml,.helmignore,templates/{configmap,deployment,service,hpa}.yaml}`
- Modify in infrastructure: `variables.tf`, `modules/argocd/templates/argocd-apps.yaml`

- [ ] **Step 1: Copy the social chart**

Copy all seven files from `alpine-infra/social/` to `alpine-infra/discovery/`.

`templates/configmap.yaml` and `templates/deployment.yaml` carry a UTF-8 BOM. Preserve it. The other five have none.

- [ ] **Step 2: Edit the copies**

`Chart.yaml`: `name: discovery`.

`values.yaml`: `name: discovery`. Leave `replicaCount` as it is. It is dead config in every chart in this repo, because `deployment.yaml` reads `.Values.replicas` which no `values.yaml` defines, and the HPA governs the replica count anyway. Do not fix it here; a one-line correction across five charts is a separate, deliberate change.

`templates/configmap.yaml`: `name: discovery-configmap`, `DATABASE_NAME: "discovery"`. Delete the whole `TELEMETRY_CONSENT_*` block and its comment. Only Guild, Messaging and Social host that gate, and Discovery does not.

`templates/deployment.yaml`: four hardcoded literals to change, none of them templated. `containers[0].name` to `discovery`, the image to `ghcr.io/alpinebits-ch/discovery-application:{{ .Values.version }}`, both probe paths to `/discovery/health`, and `configMapRef.name` to `discovery-configmap`.

`templates/service.yaml` and `templates/hpa.yaml`: no edits, both fully templated off `.Values.name`.

- [ ] **Step 3: Add the database**

In `infrastructure/variables.tf`, add `"discovery"` to the `db_names` default list. The comment there is load-bearing: a service whose database is missing crash-loops on startup, and the name must match `DATABASE_NAME` in the chart's configmap.

- [ ] **Step 4: Add the Argo application**

Append to the end of `modules/argocd/templates/argocd-apps.yaml`, after the `billing` block. The last several services were appended rather than alphabetized; follow that.

```yaml
---
apiVersion: argoproj.io/v1alpha1
kind: Application
metadata:
  name: discovery
  namespace: argocd
spec:
  project: echo
  destination:
    namespace: default
    server: https://kubernetes.default.svc
  source:
    repoURL: git@github.com:AlpineBits-ch/alpine-infra.git
    path: discovery
    targetRevision: HEAD
  syncPolicy:
    automated:
      prune: true
      selfHeal: true
```

No `helm.parameters` block. The `useScyllaDB` parameter on social exists because its chart has that key; ours does not.

- [ ] **Step 5: Commit both repos separately**

```bash
git -C ../../WebstormProjects/alpine-infra add discovery
git -C ../../WebstormProjects/alpine-infra commit -m "feat: add the discovery service chart"

git -C ../../WebstormProjects/infrastructure add variables.tf modules/argocd/templates/argocd-apps.yaml
git -C ../../WebstormProjects/infrastructure commit -m "feat: give discovery a database and an argo application"
```

Use absolute paths if the relative ones do not resolve from the working directory.

---

## Task 15: Client wire types, API service and store

**Files:**
- Create: `src/app/dtos/response/discovery.dto.ts`, `src/app/dtos/request/discovery.dto.ts`, `src/app/services/discovery-api.service.ts`, `src/app/stores/discovery.store.ts`
- Modify: `src/app/services/realtime-events.ts`, `src/app/services/realtime-listeners.ts`
- Test: `src/app/stores/discovery.store.spec.ts`

**Interfaces:**
- Produces: `DiscoveryStore` with `feedFor(key)`, `loadFeed(key, {arg})`, `loadMoreFeed(key)`, `listingFor(guildId)`, `loadListing(guildId)`, `saveDraft`, `publish`, `unlist`, `bump`, `interests`, `saveInterests`. Tasks 16 and 17 consume it.

- [ ] **Step 1: Write the DTOs**

Mirror the server shapes. `DiscoveryCardDto` is deliberately not `ListingDto`: the feed never carries drafts, links or suspension reasons, and one type for both would invite a component to read a field that is always undefined there.

- [ ] **Step 2: Write the API service**

Follow `pantry-api.service.ts` exactly: `apiConfig` and `http` injected as fields, `base` a getter reading `this.apiConfig.baseUrl() + '/api/v1/discovery'` live on each call, every method returning a typed `Observable`. No caching, no state, no realtime in this file.

Note the base includes `/discovery` here even though the server does not see it. The gateway consumes that segment; the client must send it.

- [ ] **Step 3: Write the failing store spec**

```ts
describe('DiscoveryStore', () => {
    it('issues one request for back-to-back loads of the same feed key', () => {});
    it('applies the queued refetch when an interests change races an in-flight feed request', () => {});
    it('drops a listing event for a guild nobody has loaded', () => {});
    it('rolls a failed publish back to the previous state', () => {});
    it('keeps the draft when publish is refused for entitlement', () => {});
});
```

Copy the harness from `scheduled-event.store.spec.ts`, not `relationship.store.spec.ts`: the latter predates `withKeyedIndex` and fakes an older websocket service.

The last two matter most. A refused publish must leave the composed draft exactly where it was, because losing someone's typing to a paywall is the worst possible moment to lose it.

- [ ] **Step 4: Run and watch it fail**

Run: `bun run ng test --watch=false --include="**/discovery.store.spec.ts"`

- [ ] **Step 5: Implement the store**

Compose in this order, matching `list.store.ts`: `withEntities<DiscoveryCardDto>()`, then `withKeyedIndex` for the feed with `paging` wired to the cursor, then `withOptimisticEntities`, then `withMethods`, then `withHooks`.

The feed key is a hash of the query, filters and language, so two different filter sets do not share a cache slot.

Realtime subscriptions go in `withHooks({onInit})` with no `takeUntilDestroyed`. Root-provided stores live for the process; the pantry and list stores both do this deliberately.

Guard every realtime handler with the keyed-index `...Held` check before applying, so an event for a guild nobody has loaded is dropped rather than materializing a phantom row.

- [ ] **Step 6: Register the events and the listener**

Add to `realtime-events.ts` in a new comment-delimited section:

```ts
// Discovery.
'discovery.ListingPublished': WsListingChanged;
'discovery.ListingUpdated': WsListingChanged;
'discovery.ListingUnlisted': WsListingChanged;
'discovery.ListingSuspended': WsListingSuspended;
'discovery.InterestsChanged': WsInterestsChanged;
```

Add `DiscoveryStore` to the `LISTENERS` array in `realtime-listeners.ts`. A store left off that array starts listening only when the first view that injects it opens, and every event before that lands nowhere.

- [ ] **Step 7: Run the spec, then commit**

```bash
git add src/app/dtos/response/discovery.dto.ts src/app/dtos/request/discovery.dto.ts src/app/services/discovery-api.service.ts src/app/services/realtime-events.ts src/app/services/realtime-listeners.ts src/app/stores/discovery.store.ts src/app/stores/discovery.store.spec.ts
git commit -m "feat(discovery): add the discovery store, api service and realtime events"
```

---

## Task 16: The Discover destination

**Files:**
- Create: `src/app/features/discovery/discover-page/`, `src/app/features/discovery/topic-picker/`, `src/app/features/discovery/interest-onboarding/`
- Modify: `src/app/features/main-page/navigation.service.ts`, `src/app/features/main-page/main-page.component.html`, `src/app/features/guild/components/server-taskbar/server-taskbar.component.html`, `src/assets/i18n/locales/en.json`

- [ ] **Step 1: Add the navigation view**

`MainView` gains `{type: 'discover'}`. It carries no `guildId`: Discover is global, unlike `wiki`, `house` and `personas`.

Four other places list the guild-scoped views in parallel and each needs an entry: `PersistedNav['kind']`, `NavigationService.keyOf`, the `saveNav()` chain, and `tryRestoreGuildNav`. Miss one and the view restores to home on relaunch, which presents as the app forgetting where you were.

Add `openDiscover(): void` following `openHouse` in shape, minus the guild comparison.

- [ ] **Step 2: Add the rail entry**

In `server-taskbar.component.html`, add a button between the scrollable server list and the add-server button, as a sibling in the same outer flex column. Match the add button's sizing (`w-11 h-11 rounded-icon`) and use a solid border rather than the dashed one, so it reads as a destination and not as another create action.

- [ ] **Step 3: Add the template case**

In `main-page.component.html`: `@case ('discover') { <app-discover-page /> }`

- [ ] **Step 4: Build the topic picker**

Standalone, `OnPush`, `input.required` for the current selection, `output` for changes. Debounced search against `topics/search`, chips for the selection, a cap passed in as an input so the interest picker (25) and the listing editor (8) share it. Games render with their name; tags render with their display name.

- [ ] **Step 5: Build the feed**

Two tabs over one search box, per spec section 13.2. Cards render `matchedTopics` as chips labelled from `DISCOVERY.FEED.MATCHED`. The postings tab renders an empty state pointing at plan two rather than a broken query.

Make the postings tab's presence a single computed boolean rather than a hardcoded second tab. Plan two gates that tab on the age floor in spec section 8.3, and a tab wired straight into the template is the version of this that gets missed.

Use `<app-avatar>` for the guild icon rather than inlining an image and an initial.

With no search term and no interests, render `<app-interest-onboarding />` instead of an empty grid. That screen is the acquisition path for interest data, not a consolation message.

- [ ] **Step 6: Add the strings**

Add the `DISCOVERY.*` keys to `en.json` only. Flat, dot-separated, two-space indent. No em dashes in any copy.

- [ ] **Step 7: Verify and commit**

Run: `bun run ng build --configuration development` and `bun run lint`
Expected: build succeeds; compare the lint count against the baseline rather than expecting zero.

```bash
git add src/app/features/discovery src/app/features/main-page/navigation.service.ts src/app/features/main-page/main-page.component.html src/app/features/guild/components/server-taskbar src/assets/i18n/locales/en.json
git commit -m "feat(discovery): add the discover destination and interest picker"
```

---

## Task 17: The listing editor and its paywall

**Files:**
- Create: `src/app/features/discovery/listing-editor/`
- Modify: `src/app/features/main-page/navigation.service.ts`, `src/app/features/main-page/main-page.component.html`, `src/assets/i18n/locales/en.json`

- [ ] **Step 1: Add the view**

`MainView` gains `{type: 'listing-editor'; guildId: string}`, this one guild-scoped, with the same four parallel places updated as in task 16.

- [ ] **Step 2: Build the editor**

Headline, pitch, the topic picker capped at 8, language, join policy, links. Every field autosaves the draft through `saveDraft`, which never touches the entitlement.

- [ ] **Step 3: Build the preview and the paywall**

The right half renders the real `DiscoveryCardDto` the feed would show, built from the draft, not a mockup of one. Beneath it, for a guild without `guild.public_listing`, a single upgrade bar.

Read entitlement standing from the existing `EntitlementStore`, which already caches guild-scoped snapshots for the server-stated TTL and invalidates on `entitlements.Changed`. Do not add a second path for this.

The publish button stays enabled for an unentitled guild and surfaces the server's `public_listing_not_entitled` refusal, rather than being disabled client-side. The server is the authority, and a disabled button teaches nothing about why.

- [ ] **Step 4: Handle the suspended state**

When `state` is `Suspended`, render the reason above the editor from `DISCOVERY.LISTING.SUSPENDED.PLAN_LAPSED` or `.STAFF_ACTION`, keyed off the enum rather than interpolated, so a new reason is a missing key rather than a blank line.

- [ ] **Step 5: Verify and commit**

```bash
git add src/app/features/discovery/listing-editor src/app/features/main-page/navigation.service.ts src/app/features/main-page/main-page.component.html src/assets/i18n/locales/en.json
git commit -m "feat(discovery): add the listing editor with a live card preview"
```

---

## Task 18: Full verification

- [ ] **Step 1: Backend**

Run: `dotnet build Echo.sln`, then `dotnet test Discovery.Tests/Discovery.Tests.csproj`, `dotnet test Echo.Entitlements.Tests/Echo.Entitlements.Tests.csproj`, `dotnet test Social.Tests/Social.Tests.csproj`.

`Guild.Tests` needs Docker. Run it if Docker is up; if not, say so rather than claiming it passed.

- [ ] **Step 2: Client**

Run: `bun run test`, `bun run ng build --configuration development`, `bun run lint`.

A new failure in a component you never touched is usually Vitest re-batching files across workers after a spec was added. Check whether your change touches that file before digging.

- [ ] **Step 3: Format only your own files**

```bash
bunx prettier --write src/app/features/discovery src/app/stores/discovery.store.ts src/app/services/discovery-api.service.ts src/app/dtos/response/discovery.dto.ts src/app/dtos/request/discovery.dto.ts
```

Never `bun run format`. It is `prettier --write .` and rewrites the whole repository, which would collide with the other agent working on main.

- [ ] **Step 4: State what is unverified**

The Helm chart, the Argo application and the Terraform database entry cannot be exercised locally. Report them as unverified and pushed, per the repository's own working rules.

---

## Task 19: Ban a guild out of discovery, server side

Implements spec section 8.3. The ban lives on the GUILD, not the listing, because `Listing.Publish` clears `SuspendedReason` from any state, so a listing-level ban would be defeated by one click of publish.

**Files:**
- Create: `Discovery.Domain/Entities/DiscoveryBan.cs`, `Discovery.Application/Services/DiscoveryBanService.cs`, `Discovery.Contracts/Bus/Admin/DiscoveryBanContracts.cs`, `Discovery.Application/Bus/Admin/DiscoveryBanHandlers.cs`
- Modify: `Discovery.Infrastructure/Persistence/MicroserviceContext.cs`, `Discovery.Application/Services/ListingWriteService.cs`, `Discovery.Application/Dtos/Response/ListingDto.cs`, `Discovery.Application/Program.cs`
- Test: `Discovery.Tests/Services/DiscoveryBanServiceTests.cs`

**Interfaces:**
- Produces: `DiscoveryBanService.IsBannedAsync(guildId, now, ct)`, `.BanAsync(...)`, `.LiftAsync(...)`, `.ListAsync(...)`; bus contracts `BanGuildFromDiscoveryRequest/Response`, `LiftDiscoveryBanRequest/Response`, `ListDiscoveryBansRequest/Response`, `SearchDiscoveryListingsRequest/Response`. Task 20 consumes the contracts.

- [ ] **Step 1: The entity**

```csharp
public class DiscoveryBan : BaseEntity<DiscoveryBan>, IPrefixedEntity
{
    public static string Prefix { get; } = "dban";

    public string GuildId { get; set; } = null!;

    /// <summary>Written to be read by the owner.</summary>
    public string Reason { get; set; } = null!;

    /// <summary>Never leaves the console.</summary>
    public string? StaffNote { get; set; }

    public string BannedByUserId { get; set; } = null!;
    public DateTimeOffset BannedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LiftedAt { get; set; }
    public string? LiftedByUserId { get; set; }

    public bool IsActiveAt(DateTimeOffset now) =>
        LiftedAt is null && (ExpiresAt is null || ExpiresAt > now);

    public static DiscoveryBan Create(
        string guildId, string reason, string? note, string byUserId,
        DateTimeOffset now, DateTimeOffset? expiresAt) =>
        new()
        {
            Id = GenerateId(), GuildId = guildId, Reason = reason, StaffNote = note,
            BannedByUserId = byUserId, BannedAt = now, ExpiresAt = expiresAt,
        };
}
```

A UNIQUE index on `GuildId` would be wrong: a lifted ban keeps its row and a guild can be banned again. Index `GuildId` non-uniquely.

- [ ] **Step 2: Write the failing tests**

Seven, each pinning one rule:

```csharp
[Test] public async Task A_ban_with_no_expiry_stays_active() { }
[Test] public async Task An_expired_ban_is_not_active() { }
[Test] public async Task A_lifted_ban_is_not_active() { }
[Test] public async Task Lifting_keeps_the_row_and_records_who() { }
[Test] public async Task A_guild_can_be_banned_again_after_a_lift() { }
[Test] public async Task Banning_suspends_a_published_listing_with_staff_action() { }
[Test] public async Task Lifting_does_not_republish() { }
```

The last two matter most. `SuspensionReason.StaffAction` has been unreachable in this codebase until now, and this is what finally produces it. And lifting must never republish, for exactly the reason a regained plan does not: returning a community to a public feed is the owner's decision, not a side effect of a staff action.

Take the clock as a parameter, as `GuildProfileMirror` does, or the expiry tests are not deterministic.

- [ ] **Step 3: The service and the publish gate**

`IsBannedAsync` evaluates on read via `IsActiveAt`. No sweeper: a temporary ban that needs a background job to expire is a temporary ban that outlives a failed job.

In `ListingWriteService.PublishAsync`, check the ban BEFORE the entitlement. A banned guild being told to upgrade its plan is worse than useless. Refuse with a 403 carrying `error: "discovery_banned"` and the owner-facing `Reason`, never `StaffNote`.

Add `SuspendedMessage` to `ListingDto`, populated from the ban's `Reason` when the state is `Suspended` and the reason is `StaffAction`. This is what gives the client's currently-inert suspended banner something real to show.

- [ ] **Step 4: Realtime and bus contracts**

`BanAsync` suspends a `Published` listing via `Listing.Suspend(SuspensionReason.StaffAction)` and calls the existing `ListingRealtime.ListingSuspendedAsync` with reason `"staff_action"`. Do not write a new realtime method; that one already exists and takes the reason.

Bus contracts go in `Discovery.Contracts/Bus/Admin/`, request and response in the same file, matching `CreateBotGuildMemberCommand`. `SearchDiscoveryListingsRequest` takes a query and a cursor and returns enough to identify a guild: guild id, guild name, headline, state, published-at.

- [ ] **Step 5: Migration, then verify**

`dotnet ef migrations add DiscoveryBans --project Discovery.Infrastructure --startup-project Discovery.Application`

Then `dotnet build Echo.sln`, `dotnet test Discovery.Tests/Discovery.Tests.csproj`, and `dotnet test Echo.Tests/Echo.Tests.csproj`.

---

## Task 20: The admin endpoints in the gateway

**Files:**
- Create: `Echo/Controllers/Admin/AdminDiscoveryController.cs`
- Test: `Echo.Tests/Controllers/AdminDiscoveryControllerTests.cs`

Copy `Echo/Controllers/Admin/AdminWikiController.cs` as the shape: routed at `api/v1/admin/discovery`, injecting `StaffAccess` and `IMessageBus`, resolving the caller's tier on every request and reaching Discovery over the bus. Discovery stays free of any notion of staff.

```
GET    /api/v1/admin/discovery/listings          browse and search, for finding the guild
GET    /api/v1/admin/discovery/bans              active by default, all with includeLifted=true
POST   /api/v1/admin/discovery/bans              guildId, reason, staffNote, expiresAt
DELETE /api/v1/admin/discovery/bans/{guildId}    lift
```

Moderator and Admin both pass. Every ban and lift records the acting staff user id from the RESOLVED PRINCIPAL, never from the request body.

`StaffAccess` sets `UnavailableItemKey` on the context when the check could not be completed, which is a different thing from completing and saying no. Handle that the way the existing admin controllers do rather than collapsing it into a plain 403.

---

## Task 21: The admin dashboard page

**Files:**
- Create: `src/app/features/admin/admin-modal/pages/discovery/`
- Modify: `src/app/features/admin/admin-modal/admin-modal.component.ts` and its spec, `src/assets/i18n/locales/en.json`

The admin modal is table-driven and its spec asserts the page list, so this is one table entry, one component, and the matching spec entry.

Two panes: a search over published listings to find the guild, and the ban list with lift actions. Banning opens a small form for reason, optional internal note, optional expiry.

The reason field's label must say it is shown to the guild owner, and the note's must say it is not. That distinction is the whole point of having two fields, and a mislabelled form defeats it.

UI copy stays short, one sentence maximum, per the standing constraint.

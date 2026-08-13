using System.Security.Claims;
using Isle.Api.Services.Progression;
using Isle.Domain.Entity;
using Isle.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Isle.Api.Endpoints;

/// <summary>The isle-scoped settings, in both directions.</summary>
public sealed class PlayerPreferencesDto
{
    public bool NotifyServerStatus { get; init; } = true;
    public bool NotifyQuestComplete { get; init; } = true;
    public bool NotifyDinoDeath { get; init; }
    public bool ShowOnLeaderboard { get; init; } = true;
    public bool PublicProfile { get; init; }
}

/// <summary>Reads and writes the settings that belong to this service.</summary>
public static class PlayerPreferencesEndpoints
{
    [Authorize]
    [WolverineGet("/api/v1/preferences")]
    public static async Task<PlayerPreferencesDto> Get(
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext db,
        [NotBody] PlayerPreferencesService preferences,
        [NotBody] CancellationToken ct)
    {
        var caller = await CallerPlayer.ResolveAsync(user, db, ct);

        // An account with no linked player still gets an answer - the defaults - rather than an
        // error.
        if (caller is null)
            return Project(PlayerPreferences.For(string.Empty));

        return Project(await preferences.GetAsync(caller.PlayerId, ct));
    }

    /// <summary>Saves the caller's settings, creating the row on first use.</summary>
    [Authorize]
    [WolverinePost("/api/v1/preferences")]
    public static async Task<IResult> Save(
        PlayerPreferencesDto body,
        [NotBody] ClaimsPrincipal user,
        [NotBody] MicroserviceContext db,
        [NotBody] PlayerPreferencesService preferences,
        [NotBody] CancellationToken ct)
    {
        var caller = await CallerPlayer.ResolveAsync(user, db, ct);
        if (caller is null)
            return Results.BadRequest("Link a Steam account and join the server before changing isle settings.");

        var saved = await preferences.SaveAsync(caller.PlayerId, new PlayerPreferencesUpdate(
            body.NotifyServerStatus,
            body.NotifyQuestComplete,
            body.NotifyDinoDeath,
            body.ShowOnLeaderboard,
            body.PublicProfile), ct);

        return Results.Ok(Project(saved));
    }

    private static PlayerPreferencesDto Project(PlayerPreferences preferences) => new()
    {
        NotifyServerStatus = preferences.NotifyServerStatus,
        NotifyQuestComplete = preferences.NotifyQuestComplete,
        NotifyDinoDeath = preferences.NotifyDinoDeath,
        ShowOnLeaderboard = preferences.ShowOnLeaderboard,
        PublicProfile = preferences.PublicProfile,
    };
}

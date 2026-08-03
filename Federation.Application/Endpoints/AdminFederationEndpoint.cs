using Facet.Extensions.EFCore;
using Federation.Application.Dtos.Requests;
using Federation.Application.Dtos.Response;
using Federation.Application.Messages;
using Federation.Application.Security;
using Federation.Application.Services;
using Federation.Domain.Aggregates;
using Federation.Domain.Events;
using Federation.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Federation.Application.Endpoints;

/// <summary>
/// Instance-administration surface for federation. Requires a platform administrator, not merely
/// an authenticated user: approving/initiating a peer marks it Active, and Active is the only gate
/// on inbound federation event injection (see FederationEndpoint), so a bare [Authorize] here let
/// any registered account admit itself as a trusted peer or defederate every legitimate one.
/// </summary>
[Authorize(Policy = FederationPolicies.InstanceAdmin)]
public class AdminFederationEndpoint
{
    [WolverineGet("/api/v1/admin/federation/instances")]
    public static async Task<IResult> ListInstancesAsync(
        [NotBody] MicroserviceContext db,
        string? status,
        CancellationToken cancellationToken)
    {
        var query = db.FederationInstances.AsQueryable();

        if (Enum.TryParse<FederationStatus>(status, ignoreCase: true, out var parsed))
            query = query.Where(i => i.Status == parsed);

        var instances = await query
            .OrderByDescending(i => i.CreatedAt)
            .ToFacetsAsync<FederationInstance, FederationInstanceDto>(cancellationToken);

        return Results.Ok(instances);
    }

    [WolverinePost("/api/v1/admin/federation/{instanceId}/approve")]
    public static async Task<(IResult, FederationInstanceActivated?)> ApproveAsync(
        string instanceId,
        [NotBody] MicroserviceContext db,
        [NotBody] ILogger<AdminFederationEndpoint> logger,
        CancellationToken cancellationToken)
    {
        var instance = await db.FederationInstances.FindAsync([instanceId], cancellationToken);
        if (instance is null) return (Results.NotFound(), null);
        if (instance.Status != FederationStatus.Pending) return (Results.BadRequest("Instance is not pending"), null);

        instance.Status = FederationStatus.Active;
        instance.UpdatedAt = DateTimeOffset.UtcNow;

        logger.LogInformation("Approved federation instance {Host} ({Id})", instance.Host, instance.Id);
        return (Results.Ok(), new FederationInstanceActivated(instance.Id, instance.Host));
    }

    [WolverinePost("/api/v1/admin/federation/{instanceId}/deny")]
    public static async Task<(IResult, FederationInstanceBlocked?)> DenyAsync(
        string instanceId,
        [NotBody] MicroserviceContext db,
        [NotBody] ILogger<AdminFederationEndpoint> logger,
        CancellationToken cancellationToken)
    {
        var instance = await db.FederationInstances.FindAsync([instanceId], cancellationToken);
        if (instance is null) return (Results.NotFound(), null);
        if (instance.Status != FederationStatus.Pending) return (Results.BadRequest("Instance is not pending"), null);

        instance.Status = FederationStatus.Blocked;
        instance.UpdatedAt = DateTimeOffset.UtcNow;

        logger.LogInformation("Denied federation instance {Host} ({Id})", instance.Host, instance.Id);
        return (Results.Ok(), new FederationInstanceBlocked(instance.Id, instance.Host));
    }

    [WolverinePost("/api/v1/admin/federation/{instanceId}/defederate")]
    public static async Task<(IResult, FederationInstanceDefederated?)> DefederateAsync(
        string instanceId,
        DefederateRequest request,
        [NotBody] MicroserviceContext db,
        [NotBody] ILogger<AdminFederationEndpoint> logger,
        CancellationToken cancellationToken)
    {
        var instance = await db.FederationInstances.FindAsync([instanceId], cancellationToken);
        if (instance is null) return (Results.NotFound(), null);
        if (instance.Status == FederationStatus.Defederated) return (Results.BadRequest("Already defederated"), null);

        instance.Status = FederationStatus.Defederated;
        instance.DefederationReason = request.Reason;
        instance.UpdatedAt = DateTimeOffset.UtcNow;

        logger.LogInformation("Defederated instance {Host} ({Id}): {Reason}", instance.Host, instance.Id, request.Reason);
        return (Results.Ok(), new FederationInstanceDefederated(instance.Id, instance.Host, request.Reason));
    }

    [WolverineGet("/api/v1/admin/federation/settings")]
    public static async Task<IResult> GetSettingsAsync(
        [NotBody] MicroserviceContext db,
        CancellationToken cancellationToken)
    {
        var settings = await db.FederationSettings.FindAsync([FederationSettings.SingletonId], cancellationToken)
                       ?? new FederationSettings();
        return Results.Ok(new { settings.AcceptancePolicy });
    }

    [WolverinePatch("/api/v1/admin/federation/settings")]
    public static async Task<IResult> UpdateSettingsAsync(
        UpdateFederationSettingsRequest request,
        [NotBody] MicroserviceContext db,
        CancellationToken cancellationToken)
    {
        var settings = await db.FederationSettings.FindAsync([FederationSettings.SingletonId], cancellationToken);
        if (settings is null)
        {
            settings = new FederationSettings { Id = FederationSettings.SingletonId };
            db.FederationSettings.Add(settings);
        }

        settings.AcceptancePolicy = request.AcceptancePolicy;
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        return Results.Ok(new { settings.AcceptancePolicy });
    }

    [WolverinePost("/api/v1/admin/federation/initiate")]
    public static async Task<IResult> InitiateHandshakeAsync(
        InitiateHandshakeRequest request,
        [NotBody] MicroserviceContext db,
        [NotBody] FederationHandshakeService handshakeService,
        [NotBody] ILogger<AdminFederationEndpoint> logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Initiating handshake with {TargetHost}", request.TargetHost);

        HandshakeResponse response;
        try
        {
            response = await handshakeService.InitiateHandshakeAsync(request.TargetHost, cancellationToken);
        }
        catch (FederationHandshakeService.InvalidTargetException ex)
        {
            return Results.BadRequest(ex.Message);
        }

        // The remote controls its own response body, so it must not be able to name a *different*
        // host than the one we dialled and have that written in as Active - that would let one
        // peer register itself under another instance's identity.
        if (!FederationTargetGuard.TryNormalizeTarget(request.TargetHost, allowPrivateTargets: true, out var dialled, out _)
            || !FederationTargetGuard.TryNormalizeTarget(response.Host, allowPrivateTargets: true, out var claimed, out _)
            || !string.Equals(dialled!.Host, claimed!.Host, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Handshake with {TargetHost} returned a mismatched host {ClaimedHost}; refusing to register.",
                request.TargetHost, response.Host);
            return Results.BadRequest("Remote returned a host that does not match the handshake target.");
        }

        var existing = await db.FederationInstances
            .FirstOrDefaultAsync(i => i.Host == response.Host, cancellationToken);

        if (existing is null)
        {
            db.FederationInstances.Add(new FederationInstance
            {
                Id = FederationInstance.GenerateId(),
                Host = response.Host,
                Name = response.Name,
                PublicKey = response.PublicKey,
                Status = response.Status == "accepted" ? FederationStatus.Active : FederationStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        return Results.Ok(response);
    }
}

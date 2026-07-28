using System.Text.Json;
using Federation.Application.Dtos.Events;
using Federation.Domain.Aggregates;
using Federation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Federation.Application.Services;

public class FederationDagService
{
    private readonly MicroserviceContext _db;

    public FederationDagService(MicroserviceContext db)
    {
        _db = db;
    }

    // Returns EventIds of applied events that no other applied event references as parent.
    public async Task<string[]> GetTipsAsync(string scopeKey, CancellationToken ct = default)
    {
        var applied = await _db.FederatedEvents
            .Where(e => e.ScopeKey == scopeKey && e.Applied)
            .ToListAsync(ct);

        if (applied.Count == 0) return [];

        var referenced = applied.SelectMany(e => e.PreviousEventIds).ToHashSet();
        return applied
            .Where(e => !referenced.Contains(e.EventId))
            .Select(e => e.EventId)
            .ToArray();
    }

    // Sets PreviousEventIds and Depth on the outbound event, then persists it as Applied=true,
    // Delivered=false. Delivered flips true once VentaFederationProvider confirms the POST to
    // targetHost actually succeeded - see MarkDeliveredAsync/MarkDeliveryFailedAsync.
    public async Task StampAndRecordAsync(FederationEvent @event, string scopeKey, string targetHost = "", CancellationToken ct = default)
    {
        var tips = await GetTipsAsync(scopeKey, ct);
        @event.PreviousEventIds = tips;

        long depth = 0;
        if (tips.Length > 0)
        {
            var parents = await _db.FederatedEvents
                .Where(e => tips.Contains(e.EventId))
                .ToListAsync(ct);
            if (parents.Count > 0)
                depth = parents.Max(e => e.Depth) + 1;
        }
        @event.Depth = depth;

        _db.FederatedEvents.Add(new FederatedEventRecord
        {
            EventId = @event.EventId,
            Host = @event.Host,
            ScopeKey = scopeKey,
            Depth = @event.Depth,
            PreviousEventIds = @event.PreviousEventIds,
            PayloadJson = JsonSerializer.Serialize(@event),
            Applied = true,
            ReceivedAt = DateTimeOffset.UtcNow,
            TargetHost = targetHost,
            Delivered = false,
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task MarkDeliveredAsync(string eventId, CancellationToken ct = default)
    {
        var record = await _db.FederatedEvents.FirstOrDefaultAsync(e => e.EventId == eventId, ct);
        if (record is null) return;
        record.Delivered = true;
        await _db.SaveChangesAsync(ct);
    }

    public async Task MarkDeliveryFailedAsync(string eventId, CancellationToken ct = default)
    {
        var record = await _db.FederatedEvents.FirstOrDefaultAsync(e => e.EventId == eventId, ct);
        if (record is null) return;
        record.Attempts++;
        await _db.SaveChangesAsync(ct);
    }

    // Outbound records only ever get Delivered=false at creation (see StampAndRecordAsync);
    // inbound records default Delivered=true and are never touched here, so this is naturally
    // scoped to exactly the rows that need retrying.
    public Task<List<FederatedEventRecord>> GetUndeliveredAsync(int maxAttempts, CancellationToken ct = default) =>
        _db.FederatedEvents
            .Where(e => !e.Delivered && e.Attempts < maxAttempts)
            .OrderBy(e => e.ReceivedAt)
            .ToListAsync(ct);

    // Records an inbound event and returns all events now ready to apply in causal order.
    // Buffers events whose parents have not yet been applied.
    // Deduplicates: returns [] if EventId is already applied.
    public async Task<IReadOnlyList<FederationEvent>> RecordAndResolveAsync(FederationEvent @event, CancellationToken ct = default)
    {
        var existing = await _db.FederatedEvents.FirstOrDefaultAsync(e => e.EventId == @event.EventId, ct);
        if (existing?.Applied == true) return [];

        if (existing is null)
        {
            if (@event.PreviousEventIds.Contains(@event.EventId))
                throw new InvalidOperationException($"Event {@event.EventId} references itself as a parent.");

            var scopeKey = string.IsNullOrEmpty(@event.ChannelId) ? @event.Host : @event.ChannelId;
            _db.FederatedEvents.Add(new FederatedEventRecord
            {
                EventId = @event.EventId,
                Host = @event.Host,
                ScopeKey = scopeKey,
                Depth = @event.Depth,
                PreviousEventIds = @event.PreviousEventIds,
                PayloadJson = JsonSerializer.Serialize(@event),
                Applied = false,
                ReceivedAt = DateTimeOffset.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }

        var result = new List<FederationEvent>();
        var queue = new Queue<string>();
        var enqueued = new HashSet<string>();
        queue.Enqueue(@event.EventId);
        enqueued.Add(@event.EventId);

        while (queue.Count > 0)
        {
            var eventId = queue.Dequeue();

            var record = await _db.FederatedEvents.FirstOrDefaultAsync(e => e.EventId == eventId, ct);
            if (record is null || record.Applied) continue;

            if (record.PreviousEventIds.Length > 0)
            {
                var parentIds = record.PreviousEventIds;
                var appliedCount = await _db.FederatedEvents
                    .Where(e => parentIds.Contains(e.EventId) && e.Applied)
                    .CountAsync(ct);
                if (appliedCount < record.PreviousEventIds.Length) continue;
            }

            record.Applied = true;
            await _db.SaveChangesAsync(ct);

            result.Add(JsonSerializer.Deserialize<FederationEvent>(record.PayloadJson)!);

            var buffered = await _db.FederatedEvents
                .Where(e => !e.Applied && e.ScopeKey == record.ScopeKey)
                .ToListAsync(ct);

            foreach (var candidate in buffered.Where(b => b.PreviousEventIds.Contains(eventId)))
            {
                if (enqueued.Add(candidate.EventId))
                    queue.Enqueue(candidate.EventId);
            }
        }

        return result;
    }
}

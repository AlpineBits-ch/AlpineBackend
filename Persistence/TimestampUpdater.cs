using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Persistence;

public static class TimestampUpdater
{
    public static void UpdateTimestamps(this ChangeTracker changeTracker)
    {
        var entries = changeTracker
            .Entries()
            .Where(e =>
                e is { Entity: IBaseEntity, State: EntityState.Added or EntityState.Modified }
            );

        foreach (var entry in entries)
        {
            var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);

            if (entry.State == EntityState.Modified)
                ((IBaseEntity)entry.Entity).UpdatedAt = now;

            if (entry.State == EntityState.Added && entry.Entity is IBaseEntity baseEntity)
            {
                if (baseEntity.CreatedAt == default)
                    baseEntity.CreatedAt = now;

                if (baseEntity.UpdatedAt == default)
                    baseEntity.UpdatedAt = now;
                if (string.IsNullOrWhiteSpace(baseEntity.Id))
                    baseEntity.Id = Guid.CreateVersion7().ToString();
            }
        }

    }
}
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Consumers;

/// <summary>
/// Hands back the phone number of each of a set of accounts, for the ones that have entered one.
/// </summary>
public class GetUserPhoneNumbersHandler
{
    public static async Task<GetUserPhoneNumbersResponse> Handle(
        GetUserPhoneNumbersRequest request,
        MicroserviceContext ctx)
    {
        var requested = request.UserIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];

        if (requested.Count == 0) return new GetUserPhoneNumbersResponse();

        // Truncate, but say so.
        var userIds = requested.Take(GetUserPhoneNumbersRequest.MaxUserIds).ToList();
        var omitted = requested.Skip(GetUserPhoneNumbersRequest.MaxUserIds).ToList();

        var numbers = await ctx.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id)
                        && u.PhoneNumber != null
                        && u.Status != UserStatus.Deleted
                        // Not "== Default": Moderator and Admin are ordinary people with ordinary
                        // households, and an enum that grows a fourth human tier must not silently
                        // stop answering for them.
                        && u.UserType != UserType.Bot)
            .Select(u => new UserPhoneNumberDto
            {
                UserId = u.Id,
                PhoneNumber = u.PhoneNumber!,
                UpdatedAt = u.UpdatedAt,
            })
            .ToListAsync();

        return new GetUserPhoneNumbersResponse
        {
            // Ordered after materialising, not in the query: the StringComparer overload of OrderBy
            // has no SQL translation, and under a provider that silently evaluates it client-side
            // this reads as working right up until it is run against Postgres.
            PhoneNumbers = numbers.OrderBy(n => n.UserId, StringComparer.Ordinal).ToList(),
            OmittedUserIds = omitted,
        };
    }
}

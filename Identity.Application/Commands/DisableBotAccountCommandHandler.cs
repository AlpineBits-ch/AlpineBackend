using Identity.Contracts.Bus.Commands;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Commands;

public class DisableBotAccountCommandHandler
{
    public static async Task Handle(DisableBotAccountCommand command, MicroserviceContext ctx)
    {
        var bot = await ctx.Users.FirstOrDefaultAsync(u => u.Id == command.BotUserId && u.UserType == UserType.Bot);
        if (bot is null) return;

        bot.Status = UserStatus.Inactive;
    }
}

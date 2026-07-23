using Isle.Infrastructure.Persistence;
using IsleBridge.Sdk.Models;

namespace Isle.Api.Chat.CommandController.Commands;

public class SkinCommand(MicroserviceContext context) : ChatCommand
{
    public override async Task<string> ExecuteAsync(CommandContext context)
    {
        var verb = context.Arguments.FirstOrDefault();
        return verb switch
        {
            "create" => await CreateSkinAsync(context),
            "manage" => await ManageSkinAsync(context),
            "delete" => await DeleteSkinAsync(context),
            "apply" => await ApplySkinAsync(context),
            _ => "Usage: skin <create|manage|delete>"
        };
    }
    
    private async Task<string> CreateSkinAsync(CommandContext context)
    {
        var joinedArray = String.Join(" ", context.Arguments);
        var skin = SkinCustomizer.FromProps(joinedArray);
        throw new NotImplementedException();
    }
    private async Task<string> ManageSkinAsync(CommandContext context)
    {
        throw new NotImplementedException();
    }
    private async Task<string> DeleteSkinAsync(CommandContext context)
    {
        throw new NotImplementedException();
    }
    
    private async Task<string> ApplySkinAsync(CommandContext context)
    {
        throw new NotImplementedException();
    }

    public override string Name { get; } = "skin";
    public override string Description { get; } = "Creates and manages your skin";
    public override bool IsAdminOnly { get; set; } = true;
}
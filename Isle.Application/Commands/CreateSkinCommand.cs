using FluentValidation.Results;
using Isle.Contracts.Commands;
using Isle.Domain.Entity;
using Isle.Infrastructure.Persistence;

namespace Isle.Api.Commands;

public class CreateSkinCommandHandler(MicroserviceContext context)
{
    public async Task<CreateSkinCommandResponse> Handle(CreateSkinCommand command)
    {
        
        var player = context.Players.FirstOrDefault(x => x.Id == command.Parameter.PlayerId);
        
        if (player == null)
            return new CreateSkinCommandResponse
            {
                Errors = [new ValidationFailure("player", "not found")]
            };
        

        player.Skins.Clear();
        var skinId = player.AddSkin(command.Parameter);



        return new CreateSkinCommandResponse()
        {
            SkinId = skinId,
        };
    }
}
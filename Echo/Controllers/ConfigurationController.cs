using Echo.Domain.Entities;
using Echo.Dtos.Request;
using Echo.Dtos.Response;
using Echo.Persistence.Persistance;
using Facet.Extensions;
using Facet.Extensions.EFCore;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Echo.Controllers;

[ApiController]
[Route("api/v1/configuration")]
public class ConfigurationController(MicroserviceContext context, IMessageBus bus) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var configuration = await context.EchoConfigurations.FirstAsync();
        return Ok(configuration.ToFacet<EchoConfiguration, EchoConfigurationDto>());
    }
    
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] UpdateConfigurationDto configurationDto)
    {
        
        var isUSerAdministrativeResponse = await bus.InvokeAsync<IsUserAdministrativeResponse>(new IsUserAdministrativeRequest()
           {
               UserId = HttpContext.User.Identity?.Name ?? string.Empty,
           });
        
        if(!isUSerAdministrativeResponse.IsAdministrative)
        {
            return Unauthorized();
        }
        
        var configuration = await context.EchoConfigurations.FirstAsync();

        configuration.ApplyFacet(configurationDto);

        await context.SaveChangesAsync();

        return Ok(configuration.ToFacet<EchoConfiguration, EchoConfigurationDto>());
    }
}
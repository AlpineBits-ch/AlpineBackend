using Echo.Domain.Entities;
using Echo.Dtos.Request;
using Echo.Dtos.Response;
using Echo.Persistence.Persistance;
using Facet.Extensions;
using Facet.Extensions.EFCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Echo.Controllers;

[ApiController]
[Route("api/v1/configuration")]
public class ConfigurationController(MicroserviceContext context) : ControllerBase
{
    public async Task<IActionResult> Get()
    {
        return (Ok(context.EchoConfigurations.FirstAsync()));
    }
    
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] UpdateConfigurationDto configurationDto)
    {
        var configuration = await context.EchoConfigurations.FirstAsync();

        configuration.ApplyFacet(configurationDto);

        await context.SaveChangesAsync();

        return Ok(configuration.ToFacet<EchoConfiguration, EchoConfigurationDto>());
    }
}
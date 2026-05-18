using Identity.Application.Dtos.Response;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Identity.Application.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/mls")]
public class MlsController : ControllerBase
{
    public async Task<IActionResult> Index()
    {
        
        return Ok(new GenerateKeyPackagesDto { Count = 100 });
    }
}
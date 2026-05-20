using Federation.Application.Dtos.Response;
using Microsoft.AspNetCore.Mvc;

namespace Federation.Application.Controllers;

[ApiController]
[Route(".well-known/federation")]
public class FederationDocumentController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        return Ok(new FederationDocumentResponse());
    }
}
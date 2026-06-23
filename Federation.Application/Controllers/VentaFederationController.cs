using Federation.Application.Dtos.Response;
using Federation.Application.Providers;
using Microsoft.AspNetCore.Mvc;

namespace Federation.Application.Controllers;

[ApiController]
[Route(".well-known/federation")]
public class VentaFederationController : ControllerBase
{
    private readonly IFederationProvider _federationProvider;
    private readonly ILogger<VentaFederationController> _logger;

    public VentaFederationController(IFederationProvider federationProvider, ILogger<VentaFederationController> logger)
    {
        _federationProvider = federationProvider;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Get() => Ok(new FederationDocumentResponse());

    [HttpPut("event")]
    public IActionResult ReceiveEventAsync() => Ok();
}

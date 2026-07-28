using Federation.Application.Dtos.Response;
using Microsoft.AspNetCore.Mvc;

namespace Federation.Application.Controllers;

/// <summary>
/// Self-describing metadata document for this instance (host, name, public key, protocol
/// version) - the entry point a remote instance's admin/operator uses to verify who they're
/// about to federate with before initiating a handshake.
/// </summary>
[ApiController]
[Route(".well-known/federation")]
public class VentaFederationController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new FederationDocumentResponse());
}

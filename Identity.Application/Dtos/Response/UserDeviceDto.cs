using Facet;
using Identity.Domain.Entities;

namespace Identity.Application.Dtos.Response;

/// <summary><c>Backup</c> is excluded alongside User and KeyPackages: it is the encrypted device
/// backup blob, and as an entity it also carries Device and User back-references straight into the
/// tracked graph.</summary>
[Facet(typeof(UserDevice), nameof(UserDevice.User), nameof(UserDevice.KeyPackages), nameof(UserDevice.Backup))]
public partial class UserDeviceDto
{
    
}
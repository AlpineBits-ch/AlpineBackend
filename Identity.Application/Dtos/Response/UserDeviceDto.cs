using Facet;
using Identity.Domain.Entities;

namespace Identity.Application.Dtos.Response;

/// <summary><c>Backup</c> is excluded alongside User and KeyPackages: it is the encrypted device
/// backup blob, and as an entity it also carries Device and User back-references straight into the
/// tracked graph. <c>PushTokens</c> goes the same way - they are FCM/APNs credentials, they carry
/// the same back-references, and nothing about a device listing needs them.</summary>
[Facet(typeof(UserDevice), nameof(UserDevice.User), nameof(UserDevice.KeyPackages), nameof(UserDevice.Backup),
    nameof(UserDevice.PushTokens))]
public partial class UserDeviceDto
{
    
}
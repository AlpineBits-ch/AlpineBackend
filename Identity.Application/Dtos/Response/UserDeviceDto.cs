using Facet;
using Identity.Domain.Entities;

namespace Identity.Application.Dtos.Response;

/// <summary><c>Backups</c> is excluded alongside User and KeyPackages: they are the encrypted device
/// backup blobs, and as entities they also carry Device and User back-references straight into the
/// tracked graph. <c>PushTokens</c> goes the same way - they are FCM/APNs credentials, they carry
/// the same back-references, and nothing about a device listing needs them.</summary>
[Facet(typeof(UserDevice), nameof(UserDevice.User), nameof(UserDevice.KeyPackages), nameof(UserDevice.Backups),
    nameof(UserDevice.PushTokens))]
public partial class UserDeviceDto
{
    
}
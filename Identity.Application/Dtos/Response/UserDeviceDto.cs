using Facet;
using Identity.Domain.Entities;

namespace Identity.Application.Dtos.Response;

[Facet(typeof(UserDevice), nameof(UserDevice.User), nameof(UserDevice.KeyPackages))]
public partial class UserDeviceDto
{
    
}
using Facet;
using Identity.Domain.Entities;

namespace Identity.Application.Dtos.Response;

[Facet(typeof(UserKeyPackage), nameof(UserKeyPackage.User), nameof(UserKeyPackage.Device))]
public partial class UserKeyPackageDto
{
    
}
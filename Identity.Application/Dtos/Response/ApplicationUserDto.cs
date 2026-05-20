using Facet;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.ValueObjects;

namespace Identity.Application.Dtos.Response;

[Facet(typeof(ApplicationUser), nameof(ApplicationUser.PasswordHash), nameof(ApplicationUser.SecurityStamp), nameof(ApplicationUser.ConcurrencyStamp),
    NestedFacets = [typeof(EncryptedMasterKeyDto)])]
public partial class ApplicationUserDto
{
    
}

[Facet(typeof(EncryptedMasterKey))]
public partial class EncryptedMasterKeyDto
{
    
}
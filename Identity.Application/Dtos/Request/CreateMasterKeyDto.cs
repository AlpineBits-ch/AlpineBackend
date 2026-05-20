using Facet;
using Identity.Application.Dtos.Response;
using Identity.Domain.Entities;
using Identity.Domain.ValueObjects;

namespace Identity.Application.Dtos.Request;

[Facet(typeof(EncryptedMasterKey))]
public partial class CreateMasterKeyDto
{
    
}
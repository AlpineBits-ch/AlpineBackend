using Facet;
using Identity.Application.Dtos.Response;
using Identity.Domain.Entities;
using Identity.Domain.ValueObjects;

namespace Identity.Application.Dtos.Request;

[Facet(typeof(EncryptedMasterKey))]
public partial class CreateMasterKeyDto
{
    /// <summary>Account password, required only to <i>replace</i> an envelope that already exists.
    /// The first upload has nothing to destroy; a replacement makes every backup sealed under the
    /// old wrapped key unopenable, which is not something a stolen token should be able to do.</summary>
    public string? Password { get; set; }
}
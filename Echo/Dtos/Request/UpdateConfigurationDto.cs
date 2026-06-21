using Echo.Domain.Entities;
using Facet;

namespace Echo.Dtos.Request;


[Facet(typeof(EchoConfiguration), exclude: [nameof(EchoConfiguration.Id),
    nameof(EchoConfiguration.CreatedAt), nameof(EchoConfiguration.UpdatedAt), nameof(EchoConfiguration.Prefix), nameof(EchoConfiguration.EnforcedSingleton)])]
public partial class UpdateConfigurationDto
{
        
}
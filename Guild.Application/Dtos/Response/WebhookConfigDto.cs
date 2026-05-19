using Facet;
using Guild.Domain.Entity;

namespace Guild.Application.Dtos.Response;

[Facet(typeof(WebhookConfig), nameof(WebhookConfig.Guild))]
public partial class WebhookConfigDto
{
    
    
}
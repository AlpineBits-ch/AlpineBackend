using Facet;
using Messaging.Domain.Entities;

namespace Messaging.Application.Dtos.Response;

[Facet(typeof(Message))]
public partial class MessageDto
{
    public ICollection<Reaction> Reactions { get; set; } = new List<Reaction>();
}
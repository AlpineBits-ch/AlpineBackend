using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class CreateCategoryDto
{
    public string Name { get; set; }
    public int Position { get; set; }
}
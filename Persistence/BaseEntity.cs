using System.ComponentModel.DataAnnotations.Schema;
using Ids;

namespace Persistence;

public interface IBaseEntity
{
    public string Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static string GenerateId()
    {
        return string.Empty;
    }


}

public abstract class BaseEntity<T> :IBaseEntity where T : BaseEntity<T>, IPrefixedEntity
{
    public string Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static string GenerateId()
    {
        return Identifier.New(T.Prefix);
    }
  
}
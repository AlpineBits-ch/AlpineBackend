using Social.Contracts.Dtos;

namespace Social.Contracts.Bus.Integration.Response;

public class GetProfileByUserIdResponse
{
    public ProfileDto? Profile { get; set; }
}

public class GetProfileByUserIdsResponse
{
    public ICollection<ProfileDto> Profiles { get; set; }
}
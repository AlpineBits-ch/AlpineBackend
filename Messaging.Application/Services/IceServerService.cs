using Messaging.Application.Dtos.Response;

namespace Messaging.Application.Services;

public class IceServerService(IHttpClientFactory httpClientFactory)
{
    public async Task<IceServersDto> GetIceServersForUser(string userId)
    {
        var client = httpClientFactory.CreateClient("CloudflareRtc");

        var requestBody = new { ttl = 86400 };

        var response = await client.PostAsJsonAsync(
            "v1/turn/keys/c337f1480f16a7d3002a4fb1587595ab/credentials/generate-ice-servers", 
            requestBody
        );

        if (!response.IsSuccessStatusCode)
        {
            // Handle errors (logging, custom exceptions, etc.)
            throw new HttpRequestException($"Cloudflare API returned {response.StatusCode}");
        }

        // Deserialize the response into your DTO
        var result = await response.Content.ReadFromJsonAsync<IceServersDto>();
        
        return result ?? new IceServersDto();
    }
}
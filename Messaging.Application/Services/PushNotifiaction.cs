using FirebaseAdmin.Messaging;

namespace Messaging.Application.Services;

public class PushNotificationParams
{
    public string Title { get; set; }
    public string Body { get; set; }
    public string? ImageUrl { get; set; }
    public Dictionary<string, string> Data { get; set; } = new Dictionary<string, string>();
    public string Token { get; set; }
}

/// <summary>
/// Generic "show this title and body" push. Not the path new-message notifications take any more -
/// see <see cref="MessagePushService"/>, which sends the encrypted body along with the placeholder
/// so the device can decrypt it, and which needs APNs `mutable-content` and Android data-only
/// delivery that this shape cannot express.
/// </summary>
public class PushNotifiaction
{
    public static async Task SendPushNotification(PushNotificationParams notificationParams)
    {
        var message = new Message()
        {
            Notification = new Notification
            {
                Title = notificationParams.Title,
                Body = notificationParams.Body,
            },
            Data = notificationParams.Data,
            Token = notificationParams.Token
        };
        try
        {
            await FirebaseMessaging.DefaultInstance.SendAsync(message);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }
}

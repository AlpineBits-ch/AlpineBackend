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

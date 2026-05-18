using System.Text;
using System.Text.Json;
using dotAPNS;
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
    private static ApnsJwtOptions _options = new ApnsJwtOptions
    {
        BundleId = "com.alpinebits.echo",
        CertFilePath = "Credentials/AuthKey_U6JZ45ZLGM.p8",
        KeyId = "U6JZ45ZLGM",
        TeamId = "S33LPKH83B",
    };

    private static ApnsClient _apnsClient = ApnsClient.CreateUsingJwt(new HttpClient(), _options); 
    public static async Task SendPushNotification(PushNotificationParams notificationParams)
    {
        bool isApnToken = notificationParams.Token.Length == 64;

        if (!isApnToken)
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
        else
        {
            try
            {
                var push = new ApplePush(ApplePushType.Alert)
                    .AddToken(notificationParams.Token)
                    .AddAlert(notificationParams.Title, notificationParams.Body);

                foreach (var (key, value) in notificationParams.Data)
                {
                    push.AddCustomProperty(key, value);
                }
                var response = await _apnsClient.SendAsync(push);
                if (!response.IsSuccessful)
                {
                    var devServer = await _apnsClient.SendAsync(push.SendToDevelopmentServer());
                    if (!devServer.IsSuccessful)
                    {
                        Console.WriteLine("Failed to send push notification");
                    }
                }
                
                Console.WriteLine(JsonSerializer.Serialize(response));

            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
         

        }
        
        
       
    }
}
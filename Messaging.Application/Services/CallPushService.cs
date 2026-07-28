using AppEnvironment;
using dotAPNS;
using FirebaseAdmin.Messaging;

namespace Messaging.Application.Services;

public class CallPushPayload
{
    public required string CallId { get; init; }
    public required string ConversationId { get; init; }
    public required string CallerName { get; init; }
    public string? CallerAvatarUrl { get; init; }
}

/// <summary>
/// Push fallback for calls: Android gets a data-only FCM message over the same token used for
/// regular push (PushNotifiaction.cs), iOS gets a silent VoIP push over PushKit/APNs - the one
/// path FCM cannot cover, since CallKit requires it. See §3/§4 of the native-call-popup spec.
/// </summary>
public class CallPushService
{
    private static readonly ApnsClient VoipApnsClient = ApnsClient.CreateUsingJwt(new HttpClient(), new ApnsJwtOptions
    {
        BundleId = Env.Apns.BundleId,
        CertContent = Env.Apns.AuthKeyContent,
        KeyId = Env.Apns.KeyId,
        TeamId = Env.Apns.TeamId,
    });

    public static Task SendIncomingCallAsync(IEnumerable<string> androidTokens, IEnumerable<string> voipTokens, CallPushPayload payload)
    {
        return Task.WhenAll(
            SendAndroidAsync(androidTokens, payload, isCancel: false),
            SendVoipAsync(voipTokens, payload, isCancel: false));
    }

    public static Task SendCancelCallAsync(IEnumerable<string> androidTokens, IEnumerable<string> voipTokens, CallPushPayload payload)
    {
        return Task.WhenAll(
            SendAndroidAsync(androidTokens, payload, isCancel: true),
            SendVoipAsync(voipTokens, payload, isCancel: true));
    }

    private static async Task SendAndroidAsync(IEnumerable<string> tokens, CallPushPayload payload, bool isCancel)
    {
        foreach (var token in tokens)
        {
            var data = new Dictionary<string, string>
            {
                ["type"] = "call",
                ["callId"] = payload.CallId,
                ["conversationId"] = payload.ConversationId,
                ["callerName"] = payload.CallerName,
                ["callerAvatarUrl"] = payload.CallerAvatarUrl ?? "",
            };
            if (isCancel)
            {
                data["callSubtype"] = "end";
            }

            try
            {
                await FirebaseMessaging.DefaultInstance.SendAsync(new Message
                {
                    Token = token,
                    Data = data,
                    Android = new AndroidConfig { Priority = Priority.High },
                });
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }

    private static async Task SendVoipAsync(IEnumerable<string> tokens, CallPushPayload payload, bool isCancel)
    {
        foreach (var token in tokens)
        {
            try
            {
                var push = new ApplePush(ApplePushType.Voip).AddVoipToken(token).SetPriority(10);
                push.AddCustomProperty("callId", payload.CallId);
                push.AddCustomProperty("conversationId", payload.ConversationId);
                push.AddCustomProperty("callerName", payload.CallerName);
                push.AddCustomProperty("callerAvatarUrl", payload.CallerAvatarUrl ?? "");
                if (isCancel)
                {
                    push.AddCustomProperty("type", "end");
                }

                await VoipApnsClient.SendAsync(push);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }
}

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
        var tokenList = tokens.ToList();
        // Distinguishing "no token on file" from "sent, Apple accepted it" from "sent, Apple
        // rejected it" was previously impossible from the logs alone - the only line that ever
        // printed was the rejection case, so a silently-empty token list (stale/never-registered
        // VoIP token) looked identical to a successful, silent delivery. See venta_mobile's
        // "CallKit rings sometimes but not others" investigation.
        Console.WriteLine($"[CallPushService] SendVoipAsync: {tokenList.Count} voip token(s) for callId={payload.CallId} isCancel={isCancel}");
        foreach (var token in tokenList)
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

                // A device token is only valid against the APNs gateway that issued it — a
                // debug/Xcode-run build (Runner.entitlements: aps-environment=development)
                // registers a *sandbox* token, and dotAPNS defaults every push to the
                // production gateway unless told otherwise here. Sending a sandbox token to
                // production doesn't throw — see the response check below — Apple just
                // rejects it with BadDeviceToken.
                if (Env.Apns.UseSandbox) push.SendToDevelopmentServer();

                // SendAsync reports APNs rejections (BadDeviceToken, Unregistered, etc.) via
                // this response, not by throwing — awaiting it and discarding the result (as
                // this used to) means a rejected VoIP push looks identical to a delivered one:
                // the callee's phone just never rings, with nothing anywhere saying why. See
                // venta_mobile's "native call screen never appears on iOS" investigation.
                var response = await VoipApnsClient.SendAsync(push);
                var tokenPrefix = token[..Math.Min(8, token.Length)];
                if (!response.IsSuccessful)
                {
                    Console.WriteLine(
                        $"[CallPushService] VoIP push rejected for token {tokenPrefix}...: " +
                        $"{response.Reason} {response.ReasonString}");
                }
                else
                {
                    // Apple accepting the push (2xx from APNs) only means it queued for delivery
                    // to the device - it doesn't guarantee PushKit actually wakes the app (that
                    // can still silently fail on Apple's end). Logged so a future "rejected? no.
                    // exception? no. rang? also no." report can at least rule the server side out.
                    Console.WriteLine($"[CallPushService] VoIP push accepted by APNs for token {tokenPrefix}... (sandbox={Env.Apns.UseSandbox})");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }
}

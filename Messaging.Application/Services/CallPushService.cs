using AppEnvironment;
using dotAPNS;
using FirebaseAdmin.Messaging;

namespace Messaging.Application.Services;

/// <summary>Values for <see cref="CallPushPayload.CancelReason"/>.</summary>
public static class CallCancelReason
{
    /// <summary>Somebody answered - on another of the callee's devices, or another participant.</summary>
    public const string AcceptedElsewhere = "AcceptedElsewhere";

    /// <summary>The callee declined on one of their other devices.</summary>
    public const string DeclinedElsewhere = "DeclinedElsewhere";

    /// <summary>Nobody answered it here: the caller hung up, the ring timed out, or the call ended.</summary>
    public const string Ended = "Ended";
}

public class CallPushPayload
{
    public required string CallId { get; init; }
    public required string ConversationId { get; init; }
    public required string CallerName { get; init; }

    /// <summary>Who is calling.</summary>
    public string? CallerId { get; init; }

    public string? CallerAvatarUrl { get; init; }

    /// <summary>Cancel pushes only: the device that resolved the ring, echoed back so that device
    /// can ignore its own cancel. See <see cref="SendCancelCallAsync"/>.</summary>
    public string? ExcludeDeviceId { get; init; }

    /// <summary>Cancel pushes only: why the ring stopped - <c>AcceptedElsewhere</c>,
    /// <c>DeclinedElsewhere</c> or <c>Ended</c>. iOS maps this onto the matching
    /// <c>CXCallEndedReason</c>; without it every cancelled ring is reported as
    /// <c>.remoteEnded</c> and leaves a bogus missed call in the system call log.</summary>
    public string? CancelReason { get; init; }
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

    /// <summary>Tells every still-ringing device to stop.</summary>
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
                ["callerId"] = payload.CallerId ?? "",
                ["callerAvatarUrl"] = payload.CallerAvatarUrl ?? "",
            };
            if (isCancel)
            {
                data["callSubtype"] = "end";
                data["excludeDeviceId"] = payload.ExcludeDeviceId ?? "";
                data["cancelReason"] = payload.CancelReason ?? "";
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
        // VoIP token) looked identical to a successful, silent delivery.
        Console.WriteLine($"[CallPushService] SendVoipAsync: {tokenList.Count} voip token(s) for callId={payload.CallId} isCancel={isCancel}");
        foreach (var token in tokenList)
        {
            try
            {
                var push = new ApplePush(ApplePushType.Voip).AddVoipToken(token).SetPriority(10);
                push.AddCustomProperty("callId", payload.CallId);
                push.AddCustomProperty("conversationId", payload.ConversationId);
                push.AddCustomProperty("callerName", payload.CallerName);
                push.AddCustomProperty("callerId", payload.CallerId ?? "");
                push.AddCustomProperty("callerAvatarUrl", payload.CallerAvatarUrl ?? "");
                if (isCancel)
                {
                    push.AddCustomProperty("type", "end");
                    push.AddCustomProperty("excludeDeviceId", payload.ExcludeDeviceId ?? "");
                    push.AddCustomProperty("cancelReason", payload.CancelReason ?? "");
                }

                // A device token is only valid against the APNs gateway that issued it - a
                // debug/Xcode-run build (Runner.entitlements: aps-environment=development)
                // registers a sandbox token, and dotAPNS defaults every push to the production
                // gateway unless told otherwise here.
                if (Env.Apns.UseSandbox) push.SendToDevelopmentServer();

                // SendAsync reports APNs rejections (BadDeviceToken, Unregistered, etc.) via this
                // response, not by throwing - awaiting it and discarding the result (as this used
                // to) means a rejected VoIP push looks identical to a delivered one: the callee's
                // phone just never rings, with nothing anywhere saying why.
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
                    // Apple accepting the push (2xx from APNs) only means it queued for delivery to
                    // the device - it doesn't guarantee PushKit actually wakes the app (that can
                    // still silently fail on Apple's end).
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

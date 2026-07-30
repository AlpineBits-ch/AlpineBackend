using AppEnvironment;

namespace Messaging;

using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;

public class EmailService
{
    private readonly GraphServiceClient _graph;

    public EmailService()
    {
        var tenantId = "2d977209-9059-4da8-a971-b12c78dd9122";
        if (!Env.AuthConfiguration.RequireUserEmailVerification) return;
        var credential = new ClientSecretCredential(tenantId, Env.MicrosoftGraph.ClientId, Env.MicrosoftGraph.ClientSecret);
        _graph = new GraphServiceClient(credential);
    }

    public async Task SendEmailAsync(string toAddress, string subject, string htmlBody)
    {
        // _graph is only constructed when email verification is required (see constructor) - callers
        // like PasswordResetEndpoint.RequestPasswordReset and UserVerificationEndpoint.GenerateVerificationCode
        // unconditionally call this method (unlike UserCreatedHandler, which checks the flag itself
        // first), so without this guard they NullReferenceException on any deployment (including this
        // E2E test harness) that sets AUTH_REQUIRE_USER_EMAIL_VERIFICATION=false. The short code is
        // still generated and cached by the caller before this is invoked, so skipping the actual send
        // here doesn't break the reset/verification flow itself.
        if (!Env.AuthConfiguration.RequireUserEmailVerification) return;

        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody
            {
                ContentType = BodyType.Html,
                Content = htmlBody
            },
            ToRecipients = new List<Recipient>
            {
                new Recipient
                {
                    EmailAddress = new EmailAddress { Address = toAddress }
                }
            }
        };

        await _graph.Users["noreply@alpinebits.ch"]
            .SendMail
            .PostAsync(new SendMailPostRequestBody
            {
                Message = message,
                SaveToSentItems = true
            });
    }
}
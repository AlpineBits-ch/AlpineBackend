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
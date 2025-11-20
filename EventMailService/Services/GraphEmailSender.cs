using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
namespace EventMailService.Services;
public sealed class GraphEmailSender : IEmailSender
{
    private readonly GraphServiceClient _graph;
    private readonly string _sender;
    public GraphEmailSender(IConfiguration cfg)
    {
        var g = cfg.GetSection("Email:Graph");
        var tenantId = g["TenantId"]!;
        var clientId = g["ClientId"]!;
        var secret = GetSecret(g["ClientSecret"]);
        _sender = g["SenderAddress"]!;
        _graph = new GraphServiceClient(new ClientSecretCredential(tenantId, clientId, secret));
    }
    public async Task SendAsync(string toCsvOrAlias, string subject, string htmlBody)
    {
        var recipients = Split(toCsvOrAlias).Select(a => new Recipient
        {
            EmailAddress = new EmailAddress { Address = a }
        }).ToList();
        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody { ContentType = BodyType.Html, Content = htmlBody },
            ToRecipients = recipients
        };
        await _graph.Users[_sender].SendMail.PostAsync(new Microsoft.Graph.Users.Item.SendMail.SendMailPostRequestBody { Message = message, SaveToSentItems = true });
    }
    private static string[] Split(string csv) => csv.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string GetSecret(string? val) => (val?.StartsWith("env:", StringComparison.OrdinalIgnoreCase) == true) ? Environment.GetEnvironmentVariable(val[4..]) ?? "" : val ?? "";
}
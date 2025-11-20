namespace EventMailService.Services;
public interface IEmailSender
{
    Task SendAsync(string toCsvOrAlias, string subject, string htmlBody);
}

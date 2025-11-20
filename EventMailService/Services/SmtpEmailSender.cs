using System.Net;
using System.Net.Mail;
namespace EventMailService.Services;
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly string _host;
    private readonly int _port;
    private readonly bool _useTls;
    private readonly string _username;
    private readonly string _password;
    public SmtpEmailSender(IConfiguration cfg)
    {
        var s = cfg.GetSection("Email:Smtp");
        _host = s["Host"] ?? "smtp.office365.com";
        _port = int.TryParse(s["Port"], out var p) ? p : 587;
        _useTls = bool.TryParse(s["UseTls"], out var tls) && tls;
        _username = s["Username"] ?? throw new InvalidOperationException("SMTP Username missing");
        _password = ResolveSecret(s["Password"]);
    }
    public async Task SendAsync(string toCsvOrAlias, string subject, string htmlBody)
    {
        using var client = new SmtpClient(_host, _port)
        {
            EnableSsl = _useTls,
            Credentials = new NetworkCredential(_username, _password)
        };
        using var msg = new MailMessage
        {
            From = new MailAddress(_username),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        foreach (var addr in SplitRecipients(toCsvOrAlias))
            msg.To.Add(addr);
        await client.SendMailAsync(msg);
    }
    private static string[] SplitRecipients(string csv) => csv.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string ResolveSecret(string? val) => (val?.StartsWith("env:", StringComparison.OrdinalIgnoreCase) == true) ? Environment.GetEnvironmentVariable(val[4..]) ?? "" : val ?? "";
}
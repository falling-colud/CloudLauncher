using System.Net;
using System.Net.Mail;
using CloudLauncher.Server.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudLauncher.Server.Auth;

public sealed class AccountEmailSender(EmailOptions opts, IHostEnvironment env, ILogger<AccountEmailSender> log) : IAccountEmailSender
{
    public async Task SendEmailConfirmationAsync(AppUser user, string confirmationLink, CancellationToken ct = default)
    {
        var to = user.Email ?? throw new InvalidOperationException("User has no email");
        var subject = "Confirm your CloudLauncher account";
        var body = $"""
            <p>Hi {WebUtility.HtmlEncode(user.UserName)},</p>
            <p>Please confirm your email address by clicking the link below:</p>
            <p><a href="{WebUtility.HtmlEncode(confirmationLink)}">Confirm email</a></p>
            <p>If you did not create this account, you can ignore this message.</p>
            """;

        if (!opts.IsConfigured)
        {
            // The link contains a one-time token, so it is only logged in Development (for local testing).
            // Elsewhere the failure is logged without it.
            if (env.IsDevelopment())
                log.LogWarning(
                    "SMTP is not configured, so a confirmation email was not sent. Link: {Link}",
                    confirmationLink);
            else
                log.LogError("SMTP is not configured, so a confirmation email could not be sent.");
            return;
        }

        using var client = new SmtpClient(opts.SmtpHost, opts.SmtpPort)
        {
            EnableSsl = opts.UseSsl,
            Credentials = string.IsNullOrEmpty(opts.SmtpUser)
                ? null
                : new NetworkCredential(opts.SmtpUser, opts.SmtpPassword)
        };

        using var message = new MailMessage
        {
            From = new MailAddress(opts.FromAddress, opts.FromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };
        message.To.Add(to);

        try
        {
            await client.SendMailAsync(message, ct);
        }
        catch (SmtpException ex)
        {
            // Logged without the exception: the relay's reply often quotes the recipient's address.
            log.LogError("Sending a confirmation email failed (SMTP status {Status}).", ex.StatusCode);
        }
    }
}

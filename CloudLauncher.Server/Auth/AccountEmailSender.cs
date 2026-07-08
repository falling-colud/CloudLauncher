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
            // The confirmation link embeds a valid one-time token — treat it as a secret.
            // Only echo it to the log in Development (convenience for local testing); in any
            // other environment, log the failure without leaking the token.
            if (env.IsDevelopment())
                log.LogWarning(
                    "SMTP is not configured — email confirmation for {Email} was not sent. Link: {Link}",
                    to, confirmationLink);
            else
                log.LogError(
                    "SMTP is not configured — email confirmation for {Email} could not be sent.", to);
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

        await client.SendMailAsync(message, ct);
    }
}

using CloudLauncher.Server.Data;

namespace CloudLauncher.Server.Auth;

public interface IAccountEmailSender
{
    Task SendEmailConfirmationAsync(AppUser user, string confirmationLink, CancellationToken ct = default);
}

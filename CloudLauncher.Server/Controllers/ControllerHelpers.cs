using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace CloudLauncher.Server.Controllers;

internal static class ControllerHelpers
{
    public static Guid UserId(this ControllerBase c) =>
        Guid.Parse(c.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)!);
}

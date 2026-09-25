using CloudLauncher.Server.Data;
using Microsoft.AspNetCore.Identity;

namespace CloudLauncher.Server.Auth;

/// <summary>Refuses new passwords that are on the common-password list or that repeat the
/// account's own username or email address.</summary>
/// <remarks>Runs alongside Identity's length rule, only when a password is set or changed.
/// Stored passwords are never re-checked, so existing accounts keep signing in.</remarks>
public sealed class CommonPasswordValidator : IPasswordValidator<AppUser>
{
    public Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user, string? password)
    {
        // An empty password is the length rule's to report.
        if (string.IsNullOrEmpty(password))
            return Task.FromResult(IdentityResult.Success);

        if (SameText(password, user.UserName) || SameText(password, user.Email))
            return Fail("PasswordMatchesAccount", "Your password can't be your username or email address.");

        if (CommonPasswords.Contains(password))
            return Fail("PasswordTooCommon", "That password is too common. Pick one that is harder to guess.");

        return Task.FromResult(IdentityResult.Success);
    }

    private static bool SameText(string password, string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(password.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase);

    private static Task<IdentityResult> Fail(string code, string description) =>
        Task.FromResult(IdentityResult.Failed(new IdentityError { Code = code, Description = description }));
}

/// <summary>The most commonly used passwords, compared without regard to case.</summary>
public static class CommonPasswords
{
    public static bool Contains(string password) => List.Contains(password.Trim());

    private static readonly HashSet<string> List = new(StringComparer.OrdinalIgnoreCase)
    {
        "123456", "123456789", "12345678", "1234567890", "12345", "1234567", "1234", "123123",
        "123321", "654321", "666666", "111111", "000000", "121212", "555555", "7777777",
        "888888", "987654321", "159753", "112233", "11111111", "1111111111", "0000000000",
        "0123456789", "0987654321", "9876543210", "12345678910", "1234512345", "123123123",
        "123456123456", "1234554321", "12344321", "11223344", "1122334455", "147258369",
        "741852963", "963852741", "159357", "123654", "12341234", "5201314",
        "password", "password1", "password12", "password123", "password1234", "password12345",
        "passw0rd", "passw0rd123", "p@ssw0rd", "p@ssword", "p@ssw0rd123", "p@ssword123",
        "password!", "password1!", "passwordpassword", "mypassword", "newpassword", "secret",
        "secret123", "qwerty", "qwerty1", "qwerty12", "qwerty123", "qwerty1234", "qwerty12345",
        "qwerty123456", "qwertyuiop", "qwertyuiop1", "qwertyuiop123", "qwe123", "qweasd",
        "qweasdzxc", "123qwe", "123qweasd", "123qweasdzxc", "1q2w3e", "1q2w3e4r", "1q2w3e4r5t",
        "1q2w3e4r5t6y", "q1w2e3r4", "q1w2e3r4t5", "q1w2e3r4t5y6", "1qaz2wsx", "1qaz2wsx3edc",
        "1qazxsw2", "zaq12wsx", "zaq1zaq1", "12qwaszx", "qazwsx", "qazwsxedc", "qazwsxedcrfv",
        "asdfgh", "asdfghjkl", "asdfghjkl1", "asdfasdf", "asdfasdfasdf", "asd123", "zxcvbnm",
        "zxcvbnm1", "zxcvbnm123", "zxc123", "1234qwer", "abc123", "abc12345", "abcd1234",
        "abcdefg", "abcdefgh", "abcdefghij", "abcdefghijk", "abcdef123", "a123456", "a12345",
        "aa123456", "123456a", "123456789a", "1234567890a", "a123456789", "aaaaaa", "aaaaaaaaaa",
        "iloveyou", "iloveyou1", "iloveyou12", "iloveyou123", "iloveyou2", "loveyou", "lovelove",
        "loveme", "lovely", "welcome", "welcome1", "welcome123", "welcome1234", "letmein",
        "letmein1", "letmein123", "letmeinnow", "trustno1", "changeme", "changeme123", "whatever",
        "whatever1", "hello", "hello123", "helloworld", "freedom", "access", "login", "master",
        "master123", "admin", "admin1", "admin123", "admin1234", "admin12345", "admin123456",
        "adminadmin", "administrator", "root", "root123", "rootroot", "toor", "test", "test123",
        "test1234", "testtest", "guest", "default", "dragon", "dragon123", "monkey", "monkey123",
        "shadow", "shadow123", "sunshine", "sunshine123", "princess", "princess123", "football",
        "football123", "baseball", "baseball123", "soccer", "hockey", "superman", "superman123",
        "batman", "batman123", "spiderman", "starwars", "starwars123", "pokemon", "pokemon123",
        "minecraft", "minecraft1", "minecraft12", "minecraft123", "fortnite", "fortnite123",
        "roblox", "roblox123", "google", "google123", "samsung", "samsung123", "computer",
        "computer123", "internet", "internet123", "michael", "michael123", "charlie",
        "charlie123", "jennifer", "jessica", "ashley", "daniel", "joshua", "thomas", "jordan",
        "jordan23", "hunter", "hunter2", "killer", "ranger", "buster", "tigger", "pepper",
        "ginger", "cheese", "summer", "orange", "maggie", "bailey", "matrix", "mustang", "harley",
        "ninja", "flower", "cookie", "purple", "chocolate", "butterfly", "liverpool", "arsenal",
        "chelsea", "barcelona", "blink182", "qwertyqwerty", "zxcvbnmasdfghjkl",
    };
}

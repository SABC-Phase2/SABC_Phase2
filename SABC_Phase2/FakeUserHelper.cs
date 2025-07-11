using System.Security.Claims;

public static class FakeUserHelper
{
    public static ClaimsPrincipal GetFakeUser()
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, "F151DE65-3679-4421-B63B-E00CBA65588A"),
            new Claim(ClaimTypes.Name, "Dev User"),
            // Add more claims as needed (roles, email, etc)
        };

        var identity = new ClaimsIdentity(claims, "FakeDev");
        return new ClaimsPrincipal(identity);
    }
}
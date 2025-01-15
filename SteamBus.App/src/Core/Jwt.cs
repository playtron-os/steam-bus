using System.IdentityModel.Tokens.Jwt;

static class Jwt
{
    public static string? GetSub(string token)
    {
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        return jwt.Claims.FirstOrDefault((c) => c.Type.ToLower() == "sub")?.Value;
    }
}
using System.IdentityModel.Tokens.Jwt;

static class Jwt
{
    public static string? GetSub(string token)
    {
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        return jwt.Claims.FirstOrDefault((c) => c.Type.ToLower() == "sub")?.Value;
    }

    public static DateTime? GetExpiration(string token)
    {
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);
        var expStr = jwt.Claims.FirstOrDefault((c) => c.Type.ToLower() == "exp")?.Value;
        if (expStr == null) return null;

        var exp = uint.Parse(expStr);
        DateTime expirationTime = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;

        return expirationTime;
    }

    public static bool IsExpired(string token)
    {
        var expirationTime = GetExpiration(token);
        if (expirationTime == null) return true;

        return expirationTime < DateTime.UtcNow;
    }
}
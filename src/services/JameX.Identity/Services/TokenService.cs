using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using JameX.ServiceDefaults.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JameX.Identity.Services;

/// <summary>
/// Issues the one credential the rest of the system trusts. This is the
/// "authenticate once" half of the design this project has named from phase
/// 2 onward — see <c>ICurrentUser</c>'s own remarks — with the Gateway
/// holding up the other half by validating what this issues and refusing to
/// forward anything it didn't.
/// </summary>
public interface ITokenService
{
    /// <summary>The subject claim is the user id — everything downstream that reads a caller's identity already expects a bare <see cref="Guid"/>, via <c>X-JameX-User</c>.</summary>
    string IssueToken(Guid userId, string email);
}

internal sealed class TokenService(IOptions<JwtOptions> jwtOptions) : ITokenService
{
    public string IssueToken(Guid userId, string email)
    {
        var options = jwtOptions.Value;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email),
            // A unique id per token, not because anything checks it today, but
            // because two tokens issued in the same second for the same user
            // would otherwise be byte-for-byte identical — an odd property for
            // a credential to have.
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(options.ExpiryMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

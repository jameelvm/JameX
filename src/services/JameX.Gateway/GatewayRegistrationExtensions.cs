using System.Text;
using JameX.Gateway.Clients;
using JameX.Gateway.Configuration;
using JameX.Gateway.Services;
using JameX.ServiceDefaults.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JameX.Gateway;

public static class GatewayRegistrationExtensions
{
    public static IServiceCollection AddGatewayServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BffClientOptions>(configuration.GetSection(BffClientOptions.SectionName));

        services.AddScoped<IWatchAggregationService, WatchAggregationService>();

        services.AddJameXJwtBearer(configuration);

        services.AddHttpClient<ICatalogReadClient, CatalogReadClient>((provider, http) =>
            http.BaseAddress = new Uri(Options(provider).CatalogBaseUrl));

        services.AddHttpClient<IEngagementReadClient, EngagementReadClient>((provider, http) =>
            http.BaseAddress = new Uri(Options(provider).EngagementBaseUrl));

        services.AddHttpClient<IIdentityReadClient, IdentityReadClient>((provider, http) =>
            http.BaseAddress = new Uri(Options(provider).IdentityBaseUrl));

        return services;
    }

    private static BffClientOptions Options(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<BffClientOptions>>().Value;

    /// <summary>
    /// The other half of "authenticate once, forward a trusted identity" —
    /// see <c>ICurrentUser</c>'s remarks and this project's Program.cs
    /// comment. Everything this validates was signed by Identity's own
    /// <c>TokenService</c> with the identical key, read from the same
    /// <see cref="JwtOptions"/> section — see that class's remarks on why a
    /// symmetric key is enough here.
    /// <para>
    /// Deliberately not wired to reject unauthenticated requests by default:
    /// no route in this Gateway carries <c>[Authorize]</c>. Browsing stays
    /// open to anyone; <see cref="Program"/>'s header-rewrite middleware is
    /// what turns a valid token into a trusted identity for the handful of
    /// downstream endpoints that require one, via the exact same
    /// <c>RequireUserId()</c> check they already had.
    /// </para>
    /// </summary>
    private static IServiceCollection AddJameXJwtBearer(
        this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Off, so claim types on the resulting ClaimsPrincipal stay
                // exactly what Identity issued ("sub", "email") rather than
                // being silently rewritten to the legacy XML-namespace URIs
                // (ClaimTypes.NameIdentifier and friends) .NET's default inbound
                // map uses — a well-known source of "the claim I know I set
                // isn't there" confusion.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    // The default is 5 minutes of slack on expiry — this stack
                    // has no clustered clock-skew problem to guard against
                    // (Identity and the Gateway share a single system clock in
                    // every environment this actually runs in), and zero is a
                    // more honest reflection of a token's real expiry moment.
                    ClockSkew = TimeSpan.Zero,
                };
            });

        services.AddAuthorization();

        return services;
    }
}

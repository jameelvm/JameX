using JameX.Identity.Domain;
using JameX.Identity.Repositories;
using JameX.Identity.Services;
using Microsoft.AspNetCore.Identity;

namespace JameX.Identity;

/// <summary>
/// One place naming every layer this service is built from, so Program.cs stays
/// a description of the host rather than a list of registrations.
/// </summary>
public static class IdentityRegistration
{
    public static IServiceCollection AddIdentityServices(this IServiceCollection services)
    {
        // Scoped, matching the DbContext lifetime: one context, one unit of
        // work, one request. A singleton repository would capture a disposed
        // context and fail on the second request.
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IChannelRepository, ChannelRepository>();

        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IChannelService, ChannelService>();

        // Stateless, so a singleton — PasswordHasher<T> carries no per-request
        // state, and TokenService only reads immutable IOptions config.
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddSingleton<ITokenService, TokenService>();

        return services;
    }
}

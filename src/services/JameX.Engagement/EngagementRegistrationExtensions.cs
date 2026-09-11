using JameX.Engagement.Configuration;
using JameX.Engagement.Data;
using JameX.Engagement.EventHandlers;
using JameX.Engagement.Repositories;
using JameX.ServiceDefaults.Data;
using JameX.ServiceDefaults.Hosting;

namespace JameX.Engagement;

public static class EngagementRegistrationExtensions
{
    public static IServiceCollection AddEngagementServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EngagementOptions>(configuration.GetSection(EngagementOptions.SectionName));

        services.AddScoped<IVideoCounterRepository, VideoCounterRepository>();
        services.AddScoped<IUserReactionRepository, UserReactionRepository>();

        // Bound to EngagementDbContext, so the inbox claim commits in the same
        // transaction as anything else staged on that context — comments,
        // once Module 5 adds them. It buys nothing for the counter writes
        // below, which live in DynamoDB and cannot join this transaction; see
        // the handlers themselves for how they stay safe under redelivery
        // anyway.
        services.AddScoped<IInboxUnitOfWork, InboxUnitOfWork<EngagementDbContext>>();

        // One handler per event type this service subscribes to — must match
        // the jamex-engagement-events filter policy in infra/localstack/init.
        services.AddEventHandler<VideoEncodedHandler>();
        services.AddEventHandler<VideoDeletedHandler>();

        return services;
    }
}

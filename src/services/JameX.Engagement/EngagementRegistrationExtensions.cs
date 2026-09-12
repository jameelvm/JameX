using JameX.Engagement.Configuration;
using JameX.Engagement.Data;
using JameX.Engagement.EventHandlers;
using JameX.Engagement.Repositories;
using JameX.Engagement.Services;
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
        services.AddScoped<ICommentRepository, CommentRepository>();

        services.AddScoped<IReactionService, ReactionService>();
        services.AddScoped<IViewService, ViewService>();
        services.AddScoped<IEngagementQueryService, EngagementQueryService>();
        services.AddScoped<ICommentService, CommentService>();

        // Bound to EngagementDbContext, so the inbox claim commits in the same
        // transaction as anything else staged on that context — comments
        // included. It buys nothing for the counter writes below, which live
        // in DynamoDB and cannot join this transaction; see the handlers
        // themselves for how they stay safe under redelivery anyway.
        services.AddScoped<IInboxUnitOfWork, InboxUnitOfWork<EngagementDbContext>>();

        // Plain commits for comment CRUD, which has no inbox/outbox
        // involvement of its own.
        services.AddScoped<IUnitOfWork, UnitOfWork<EngagementDbContext>>();

        // One handler per event type this service subscribes to — must match
        // the jamex-engagement-events filter policy in infra/localstack/init.
        services.AddEventHandler<VideoEncodedHandler>();
        services.AddEventHandler<VideoDeletedHandler>();

        return services;
    }
}

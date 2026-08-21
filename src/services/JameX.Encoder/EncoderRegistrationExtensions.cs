using JameX.Encoder.Configuration;
using JameX.Encoder.Encoding;
using JameX.Encoder.EventHandlers;
using JameX.Encoder.Storage;
using JameX.ServiceDefaults.Hosting;

namespace JameX.Encoder;

public static class EncoderRegistrationExtensions
{
    public static IServiceCollection AddEncoderServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EncodingOptions>(configuration.GetSection(EncodingOptions.SectionName));

        // Singleton: the runner holds no per-request state, and every job is a
        // separate process anyway.
        //
        // Swapping to MediaConvert is this one line — nothing that consumes
        // IEncodingJobRunner needs to know.
        services.AddSingleton<IEncodingJobRunner, FfmpegEncodingJobRunner>();

        services.AddScoped<IMediaStore, MediaStore>();

        // The only event Encoder subscribes to — its queue's filter policy
        // allows VideoUploaded and nothing else.
        services.AddEventHandler<VideoUploadedHandler>();

        return services;
    }
}

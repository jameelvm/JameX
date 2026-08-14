using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using JameX.Contracts.Events;
using JameX.ServiceDefaults.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JameX.ServiceDefaults.Messaging;

public interface IEventPublisher
{
    /// <summary>
    /// Publishes a video lifecycle event to the shared SNS topic.
    /// </summary>
    /// <param name="eventType">One of <see cref="EventTypes"/>. Sent as the
    /// <c>eventType</c> message attribute so subscription filter policies can
    /// route it to only the queues that care.</param>
    Task PublishAsync<T>(string eventType, T data, CancellationToken ct = default);
}

public sealed class SnsEventPublisher : IEventPublisher
{
    private readonly IAmazonSimpleNotificationService _sns;
    private readonly MessagingOptions _options;
    private readonly ILogger<SnsEventPublisher> _logger;
    private readonly string _source;

    private string? _topicArn;
    private readonly SemaphoreSlim _arnLock = new(1, 1);

    public SnsEventPublisher(
        IAmazonSimpleNotificationService sns,
        IOptions<MessagingOptions> options,
        ILogger<SnsEventPublisher> logger,
        IServiceIdentity identity)
    {
        _sns = sns;
        _options = options.Value;
        _logger = logger;
        _source = identity.ServiceName;
    }

    public async Task PublishAsync<T>(string eventType, T data, CancellationToken ct = default)
    {
        var envelope = new EventEnvelope<T>(
            EventId: Guid.NewGuid(),
            EventType: eventType,
            OccurredAt: DateTimeOffset.UtcNow,
            Source: _source,
            Data: data);

        var topicArn = await ResolveTopicArnAsync(ct);

        var request = new PublishRequest
        {
            TopicArn = topicArn,
            Message = JameXJson.Serialize(envelope),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                // Filter policies on each subscription match on this attribute.
                // Without it every consumer receives every event and discards
                // what it does not handle — wasted receives and wasted cost.
                ["eventType"] = new() { DataType = "String", StringValue = eventType },
                ["source"] = new() { DataType = "String", StringValue = _source }
            }
        };

        await _sns.PublishAsync(request, ct);

        _logger.LogInformation(
            "Published {EventType} {EventId} from {Source}",
            eventType, envelope.EventId, _source);
    }

    /// <summary>
    /// Resolves the topic name to an ARN once and caches it. CreateTopic is
    /// idempotent in SNS — calling it for an existing topic returns the
    /// existing ARN — so this doubles as create-if-absent.
    /// </summary>
    private async Task<string> ResolveTopicArnAsync(CancellationToken ct)
    {
        if (_topicArn is not null) return _topicArn;

        await _arnLock.WaitAsync(ct);
        try
        {
            if (_topicArn is not null) return _topicArn;

            var response = await _sns.CreateTopicAsync(_options.TopicName, ct);
            _topicArn = response.TopicArn;
            _logger.LogDebug("Resolved topic {TopicName} to {TopicArn}", _options.TopicName, _topicArn);
            return _topicArn;
        }
        finally
        {
            _arnLock.Release();
        }
    }
}

/// <summary>
/// Lets shared plumbing label logs and events with the owning service's name
/// without every service having to pass it around.
/// </summary>
public interface IServiceIdentity
{
    string ServiceName { get; }
}

public sealed record ServiceIdentity(string ServiceName) : IServiceIdentity;

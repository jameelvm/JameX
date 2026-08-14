using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using JameX.ServiceDefaults.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JameX.ServiceDefaults.Messaging;

/// <summary>
/// The consumer loop every event-driven service runs: long-poll its own SQS
/// queue, dispatch each message to the handler registered for its event type,
/// and delete only on success.
/// <para>
/// Deleting only on success is what makes the DLQ work. A handler that throws
/// leaves the message undeleted; it becomes visible again after the visibility
/// timeout, is retried, and after the queue's <c>maxReceiveCount</c> is
/// redriven to the dead-letter queue instead of blocking the queue forever.
/// </para>
/// </summary>
public sealed class SqsEventConsumerService : BackgroundService
{
    private readonly IAmazonSQS _sqs;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MessagingOptions _options;
    private readonly ILogger<SqsEventConsumerService> _logger;
    private readonly string _serviceName;

    private string? _queueUrl;

    public SqsEventConsumerService(
        IAmazonSQS sqs,
        IServiceScopeFactory scopeFactory,
        IOptions<MessagingOptions> options,
        ILogger<SqsEventConsumerService> logger,
        IServiceIdentity identity)
    {
        _sqs = sqs;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _serviceName = identity.ServiceName;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.QueueName))
        {
            _logger.LogInformation(
                "{Service} has no queue configured; running as a publisher only", _serviceName);
            return;
        }

        _queueUrl = await ResolveQueueUrlAsync(_options.QueueName, stoppingToken);
        if (_queueUrl is null) return;

        _logger.LogInformation(
            "{Service} consuming {QueueName} (long poll {Wait}s, batch {Batch})",
            _serviceName, _options.QueueName, _options.WaitTimeSeconds, _options.MaxMessagesPerReceive);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = _queueUrl,
                    MaxNumberOfMessages = _options.MaxMessagesPerReceive,
                    // Long polling: hold the connection open rather than
                    // returning empty immediately and billing for the round trip.
                    WaitTimeSeconds = _options.WaitTimeSeconds,
                    VisibilityTimeout = _options.VisibilityTimeoutSeconds,
                    MessageAttributeNames = ["All"],
                    MessageSystemAttributeNames = ["ApproximateReceiveCount"]
                }, stoppingToken);

                if (response.Messages is not { Count: > 0 }) continue;

                // Events in a batch are independent, so process them together.
                await Task.WhenAll(response.Messages.Select(m => ProcessAsync(m, stoppingToken)));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the loop die: a transient SQS or network fault must
                // not take the consumer offline until the pod is restarted.
                _logger.LogError(ex, "{Service} receive loop error; backing off", _serviceName);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(Message message, CancellationToken ct)
    {
        var receiveCount = ReadReceiveCount(message);

        try
        {
            var (eventType, body) = Unwrap(message);

            if (eventType is null)
            {
                // Nothing can handle this. Deleting rather than retrying avoids
                // burning three receives on a message that will never succeed.
                _logger.LogWarning(
                    "{Service} received a message with no eventType attribute; discarding {MessageId}",
                    _serviceName, message.MessageId);
                await DeleteAsync(message, ct);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var handler = scope.ServiceProvider
                .GetServices<IEventHandler>()
                .FirstOrDefault(h => h.EventType == eventType);

            if (handler is null)
            {
                // The subscription filter policy let something through that
                // this service does not handle. Worth a warning — it means the
                // filter policy and the code have drifted apart.
                _logger.LogWarning(
                    "{Service} has no handler for {EventType}; discarding. Check the subscription filter policy.",
                    _serviceName, eventType);
                await DeleteAsync(message, ct);
                return;
            }

            // Keep the message invisible while a slow handler works, rather
            // than setting one enormous visibility timeout up front — a large
            // timeout also delays recovery when a consumer dies mid-message.
            using var heartbeat = new VisibilityHeartbeat(
                _sqs, _queueUrl!, message.ReceiptHandle, _options.VisibilityTimeoutSeconds, _logger);

            await handler.HandleAsync(body, ct);
            await DeleteAsync(message, ct);
        }
        catch (Exception ex)
        {
            // Do not delete. The message reappears after the visibility timeout
            // and, once maxReceiveCount is exceeded, moves to the DLQ.
            _logger.LogError(ex,
                "{Service} failed to handle {MessageId} (receive #{ReceiveCount}); leaving it for retry",
                _serviceName, message.MessageId, receiveCount);
        }
    }

    /// <summary>
    /// Extracts the event type and the envelope JSON.
    /// <para>
    /// Subscriptions use raw message delivery, so the SQS body is the SNS
    /// message itself and the attributes pass straight through. The fallback
    /// path handles the non-raw form, where SNS wraps the payload in its own
    /// notification envelope — a common and confusing difference to hit.
    /// </para>
    /// </summary>
    private static (string? EventType, string Body) Unwrap(Message message)
    {
        if (message.MessageAttributes is not null &&
            message.MessageAttributes.TryGetValue("eventType", out var attribute) &&
            !string.IsNullOrWhiteSpace(attribute.StringValue))
        {
            return (attribute.StringValue, message.Body);
        }

        try
        {
            using var document = JsonDocument.Parse(message.Body);
            var root = document.RootElement;

            if (root.TryGetProperty("Type", out var type) &&
                type.GetString() == "Notification" &&
                root.TryGetProperty("Message", out var inner))
            {
                var body = inner.GetString() ?? "";
                string? eventType = null;

                if (root.TryGetProperty("MessageAttributes", out var attributes) &&
                    attributes.TryGetProperty("eventType", out var wrapped) &&
                    wrapped.TryGetProperty("Value", out var value))
                {
                    eventType = value.GetString();
                }

                // Last resort: the envelope carries its own type in the body.
                if (eventType is null && !string.IsNullOrEmpty(body))
                {
                    using var innerDocument = JsonDocument.Parse(body);
                    if (innerDocument.RootElement.TryGetProperty("eventType", out var fromBody))
                        eventType = fromBody.GetString();
                }

                return (eventType, body);
            }

            if (root.TryGetProperty("eventType", out var direct))
                return (direct.GetString(), message.Body);
        }
        catch (JsonException)
        {
            // Not JSON at all — falls through to the discard path.
        }

        return (null, message.Body);
    }

    private Task DeleteAsync(Message message, CancellationToken ct) =>
        _sqs.DeleteMessageAsync(_queueUrl, message.ReceiptHandle, ct);

    private static int ReadReceiveCount(Message message) =>
        message.Attributes is not null &&
        message.Attributes.TryGetValue("ApproximateReceiveCount", out var raw) &&
        int.TryParse(raw, out var count)
            ? count
            : 0;

    /// <summary>
    /// Waits for the queue to exist. On a cold compose start the service can
    /// come up before the LocalStack bootstrap has finished provisioning.
    /// </summary>
    private async Task<string?> ResolveQueueUrlAsync(string queueName, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 30 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                var response = await _sqs.GetQueueUrlAsync(queueName, ct);
                return response.QueueUrl;
            }
            catch (Exception ex) when (ex is QueueDoesNotExistException or AmazonSQSException)
            {
                _logger.LogDebug(
                    "Queue {QueueName} not available yet (attempt {Attempt}/30)", queueName, attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }

        _logger.LogError(
            "{Service} gave up waiting for queue {QueueName}", _serviceName, queueName);
        return null;
    }
}

/// <summary>
/// Extends a message's visibility on a timer while its handler runs, so a long
/// job is not redelivered to a second consumer mid-flight.
/// </summary>
internal sealed class VisibilityHeartbeat : IDisposable
{
    private readonly Timer _timer;
    private readonly CancellationTokenSource _cts = new();

    public VisibilityHeartbeat(
        IAmazonSQS sqs, string queueUrl, string receiptHandle, int visibilitySeconds, ILogger logger)
    {
        // Refresh at a third of the window so a slow call still lands in time.
        var interval = TimeSpan.FromSeconds(Math.Max(10, visibilitySeconds / 3d));

        _timer = new Timer(async void (_) =>
        {
            try
            {
                await sqs.ChangeMessageVisibilityAsync(
                    queueUrl, receiptHandle, visibilitySeconds, _cts.Token);
                logger.LogDebug("Extended visibility by {Seconds}s", visibilitySeconds);
            }
            catch (OperationCanceledException)
            {
                // Handler finished; the heartbeat is being torn down.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to extend message visibility");
            }
        }, null, interval, interval);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();
    }
}

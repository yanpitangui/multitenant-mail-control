using System.Text;
using System.Text.Json;
using Akka;
using Akka.Streams;
using Akka.Streams.Amqp.RabbitMq.Dsl;
using Akka.Util;

namespace MultiTenantMailControl.App;

public class QueueReaderActor : ReceiveActor
{
    private readonly IActorRef _shardingTenant;
    private readonly Source<CommittableIncomingMessage, NotUsed> _source;
    private readonly Dictionary<Guid, CommittableIncomingMessage> _messages;

    public QueueReaderActor(Source<CommittableIncomingMessage, NotUsed> source, IActorRef shardingTenant)
    {
        _shardingTenant = shardingTenant;
        _source = source;
        _messages = new Dictionary<Guid, CommittableIncomingMessage>();
        
        ReceiveAsync<Events.Ack>(e =>
        {
            if (_messages.Remove(e.MessageId, out var message))
            {
                return message.Ack();
            }
            return Task.CompletedTask;
        });
        
        ReceiveAsync<Events.Nack>(e =>
        {
            if (_messages.Remove(e.MessageId, out var message))
            {
                return message.Nack();
            }
            return Task.CompletedTask;
        });
    }
    
    protected override void PreStart()
    {
        var logger = Context.GetLogger();

        logger.Info("Starting reading from stream");
        _source
            .SelectAsync(10,async m =>
            {
                try
                {
                    var message = JsonSerializer.Deserialize<TenantCommands.SendEmail>(m.Message.Bytes.ToString(Encoding.UTF8));

                    if (message is not null)
                    {
                        _messages.TryAdd(message.MessageId, m);
                        return message;
                    }
                    return Option<TenantCommands.SendEmail>.None;
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "Failed parsing message, discarding");
                    await m.Ack();
                    return Option<TenantCommands.SendEmail>.None;
                }

            })
            .Where(x => x.HasValue)
            .Select(x => x.Value)
            .RunForeach(_shardingTenant.Tell, Context.System.Materializer())
            .ContinueWith(x =>
            {
                if (x.IsCompletedSuccessfully)
                {
                    logger.Info("Stream completed successfully");
                }
                else
                {
                    logger.Error(x.Exception, "Stream completed with failure");
                }
            });

    }
    
}

public static class Events
{
    public record Ack(Guid MessageId);
    public record Nack(Guid MessageId, string Reason);
}
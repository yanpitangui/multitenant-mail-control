using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Akka;
using Akka.Streams;
using Akka.Streams.Amqp.RabbitMq.Dsl;
using Akka.Util;

namespace MultiTenantMailControl.App;

public class QueueReaderActor<T> : ReceiveActor where T: IWithTenantId, IWithMessageId
{
    private readonly ILoggingAdapter _logger = Context.GetLogger();
    private readonly IActorRef _shardingTenant;
    private readonly Source<CommittableIncomingMessage, NotUsed> _source;
    private readonly Dictionary<Guid, CommittableIncomingMessage> _messages;

    public QueueReaderActor(Source<CommittableIncomingMessage, NotUsed> source, IActorRef shardingTenant)
    {
        _shardingTenant = shardingTenant;
        _source = source;
        _messages = new Dictionary<Guid, CommittableIncomingMessage>();
        
        Receive<AddToCtrl>(e =>
        {
            _messages.Add(e.MessageId, e.Message);
        });
        
        ReceiveAsync<Events.Ack>(async e =>
        {
            if (_messages.Remove(e.MessageId, out var message))
            { 
                await message.Ack();
            }
        });
        
        ReceiveAsync<Events.Nack>(async e =>
        {
            if (_messages.Remove(e.MessageId, out var message))
            {
                await message.Nack();
            }
        });
        
    }
    
    protected override void PreStart()
    {

        var self = Context.Self;
        _logger.Info("Starting reading from stream");
        _source
            .SelectAsync(10, async m =>
            {
                try
                {
                    var message = JsonSerializer.Deserialize<T>(m.Message.Bytes.ToString(Encoding.UTF8));

                    if (message is not null)
                    {
                        self.Tell(new AddToCtrl(message.MessageId, m));
                        return message;
                    }
                    return Option<T>.None;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed parsing message, discarding");
                    await m.Ack();
                    return Option<T>.None;
                }

            })
            .Where(x => x.HasValue)
            .Select(x => x.Value)
            .RunForeach(m => _shardingTenant.Tell(m, self), Context.System.Materializer())
            .ContinueWith(x =>
            {
                if (x.IsCompletedSuccessfully)
                {
                    _logger.Info("Stream completed successfully");
                }
                else
                {
                    _logger.Error(x.Exception, "Stream completed with failure");
                }
            });

    }
    
    private record AddToCtrl(Guid MessageId, CommittableIncomingMessage Message);
}

public static class Events
{
    public record Ack(Guid MessageId);
    public record Nack(Guid MessageId, string Reason);
}
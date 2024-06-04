using Akka.Persistence;

namespace MultiTenantMailControl.App;


/// <summary>
/// Should use sharding delivery to guarantee at-least-once delivery
/// https://getakka.net/articles/clustering/cluster-sharding-delivery.html
/// </summary>
public class TenantActor : ReceivePersistentActor
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    public TenantActor(string tenantId)
    {
        PersistenceId = $"tenant-{tenantId}";
        Command<TenantCommands.SendEmail>(message =>
        {
            var sender = Sender;
            PersistAsync(message, m =>
            {
                sender.Tell(new Events.Ack(m.MessageId));
                _log.Info("Persisted message {0}", m.MessageId);
            });
        });
    }
    
    public override string PersistenceId { get; }
    
    
    
    public static Props Props(string tenantId) => Akka.Actor.Props.Create<TenantActor>(tenantId);
}

public static class TenantCommands
{
    public record SendEmail : IWithTenantId, IWithMessageId
    {
        public SendEmail(string TenantId, Guid MessageId, string EmailContent, string EmailSubject)
        {
            this.TenantId = TenantId;
            this.MessageId = MessageId;
            this.EmailContent = EmailContent;
            this.EmailSubject = EmailSubject;
        }

        public SendEmail()
        {
            
        }
        public required string TenantId { get; init; }
        public Guid MessageId { get; init; }
        public required string EmailContent { get; init; }
        public required string EmailSubject { get; init; }


    }
}
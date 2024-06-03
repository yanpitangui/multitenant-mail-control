using Akka.Persistence;

namespace MultiTenantMailControl.App;


/// <summary>
/// Should use sharding delivery to guarantee at-least-once delivery
/// https://getakka.net/articles/clustering/cluster-sharding-delivery.html
/// </summary>
public class TenantActor : ReceivePersistentActor
{
    public TenantActor(string tenantId)
    {
        PersistenceId = $"tenant-{tenantId}";
        Command<TenantCommands.SendEmail>(message =>
        {
            var sender = Sender;
            PersistAsync(message, m =>
            {
                sender.Tell(new Events.Ack(m.MessageId));
            });
        });
    }
    
    public override string PersistenceId { get; }
    
    
    
    public static Props Props(string tenantId) => Akka.Actor.Props.Create<TenantActor>(tenantId);
}

public static class TenantCommands
{
    public record SendEmail(string TenantId, Guid MessageId, string EmailContent, string EmailSubject) : IWithTenantId, IWithMessageId;
}
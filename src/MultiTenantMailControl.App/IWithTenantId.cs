using Akka.Cluster.Sharding;

namespace MultiTenantMailControl.App;

public interface IWithTenantId
{
    public string TenantId { get; }
}

public interface IWithMessageId
{
    public Guid MessageId { get; }
}

public class TenantIdExtractor : HashCodeMessageExtractor
{
    public TenantIdExtractor(int maxNumberOfShards) : base(maxNumberOfShards)
    {
    }

    public override string? EntityId(object message)
    {
        if (message is IWithTenantId withTenantId)
        {
            return withTenantId.TenantId;
        }

        return null;
    }
}
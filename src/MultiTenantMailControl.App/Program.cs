using Akka.Cluster.Hosting;
using Akka.Cluster.Sharding;
using Akka.Hosting;
using Akka.Streams;
using Akka.Streams.Amqp.RabbitMq;
using Akka.Streams.Amqp.RabbitMq.Dsl;
using MultiTenantMailControl.App;
using Microsoft.Extensions.Hosting;

var hostBuilder = new HostBuilder();

hostBuilder.ConfigureServices((context, services) =>
{
    const string actorSystemName = "MultiTenantControl";
    services.AddAkka(actorSystemName, (builder, sp) =>
    {
        var defaultShardOptions = new ShardOptions()
        {
            Role = actorSystemName,
            PassivateIdleEntityAfter = TimeSpan.FromMinutes(1),
            ShouldPassivateIdleEntities = false,
            RememberEntities = true,
            RememberEntitiesStore = RememberEntitiesStore.DData
        };
        
        var extractor = new TenantIdExtractor(50);
        builder
            .WithClustering()
            .WithShardRegion<TenantActor>(nameof(TenantActor), TenantActor.Props, extractor, defaultShardOptions)
            .WithActors((system, registry, resolver) =>
            {
                const string queueName = "send-mail";
                var connectionSettings = AmqpConnectionDetails
                    .Create("localhost", 5672)
                    .WithAutomaticRecoveryEnabled(true)
                    .WithNetworkRecoveryInterval(TimeSpan.FromSeconds(1));
        
        
                //queue declaration
                var queueDeclaration = QueueDeclaration
                    .Create(queueName)
                    .WithDurable(true)
                    .WithAutoDelete(false);
        
                //create source
                var amqpSource = AmqpSource.CommittableSource( 
                    NamedQueueSourceSettings
                        .Create(connectionSettings, queueName)
                        .WithDeclarations(queueDeclaration),bufferSize: 10);

                
                var resilientSource = RestartSource.WithBackoff(() => amqpSource, RestartSettings.Create(minBackoff: TimeSpan.FromSeconds(3), maxBackoff: TimeSpan.FromSeconds(30),
                        randomFactor: 0.2)
                    .WithMaxRestarts(20, TimeSpan.FromMinutes(5)));
                var shard = registry.Get<TenantActor>();
                var queueReader = system.ActorOf(Props.Create(() => new QueueReaderActor(resilientSource, shard)), "reader");
                registry.Register<QueueReaderActor>(queueReader);
            });
    });
});

var host = hostBuilder.Build();

var completionTask = host.RunAsync();

await completionTask; // wait for the host to shut down
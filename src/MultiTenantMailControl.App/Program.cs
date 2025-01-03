﻿using Akka.Cluster.Hosting;
using Akka.Cluster.Sharding;
using Akka.Hosting;
using Akka.Persistence.Hosting;
using Akka.Remote.Hosting;
using Akka.Streams;
using Akka.Streams.Amqp.RabbitMq;
using Akka.Streams.Amqp.RabbitMq.Dsl;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MultiTenantMailControl.App;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

var hostBuilder = Host.CreateDefaultBuilder(args);

hostBuilder
    .ConfigureServices((context, services) =>
{
    const string actorSystemName = "MultiTenantControl";
    services.AddSingleton<IEmailSender, StubEmailSender>();
    services.AddSerilog(o =>
    {
        o.ReadFrom.Configuration(context.Configuration);
    }).AddAkka(actorSystemName, (builder, sp) =>
    {
        var defaultShardOptions = new ShardOptions()
        {
            Role = actorSystemName,
            PassivateIdleEntityAfter = TimeSpan.FromMinutes(1),
            ShouldPassivateIdleEntities = false,
            RememberEntities = true,
            RememberEntitiesStore = RememberEntitiesStore.DData
        };
        
        var clusterOptions = new ClusterOptions
        {
            MinimumNumberOfMembers = 1,
            SeedNodes = new[] { $"akka.tcp://{actorSystemName}@0.0.0.0:5213" },
            Roles = new[] { actorSystemName }
        };
        
        var remoteOptions = new RemoteOptions
        {
            HostName = "0.0.0.0",
            Port = 5213, 
        };
        var factory = sp.GetRequiredService<ILoggerFactory>();
        
        var extractor = new TenantIdExtractor(50);
        builder
            .ConfigureLoggers(l =>
            {
                l.ClearLoggers();
                l.AddLoggerFactory(factory);
            })
            .WithRemoting(remoteOptions)
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithClustering(clusterOptions)
            .WithShardRegion<TenantActor>(nameof(TenantActor), (system, registry, resolver) => 
                (tenantId) => resolver.Props<TenantActor>(tenantId), extractor, defaultShardOptions)
            .WithActors((system, registry, resolver) =>
            {
                const string queueName = "send-mail";
        
                //queue declaration
                var queueDeclaration = QueueDeclaration
                    .Create(queueName)
                    .WithDurable(true)
                    .WithAutoDelete(false);
        
                //create source
                var amqpSource = AmqpSource.CommittableSource( 
                    NamedQueueSourceSettings
                        .Create(AmqpConnectionUri.Create(context.Configuration.GetConnectionString("RabbitMq")), queueName)
                        .WithDeclarations(queueDeclaration),bufferSize: 10);

                
                var resilientSource = RestartSource.WithBackoff(() => amqpSource, RestartSettings.Create(minBackoff: TimeSpan.FromSeconds(3), maxBackoff: TimeSpan.FromSeconds(30),
                        randomFactor: 0.2)
                    .WithMaxRestarts(20, TimeSpan.FromMinutes(5)));
                var shard = registry.Get<TenantActor>();
                var queueReader = system.ActorOf(Props.Create(() => new QueueReaderActor<TenantCommands.SendEmail>(resilientSource, shard)), "reader");
                registry.Register<QueueReaderActor<TenantCommands.SendEmail>>(queueReader);
            });
    });
});

var host = hostBuilder.Build();

var completionTask = host.RunAsync();

await completionTask; // wait for the host to shut down
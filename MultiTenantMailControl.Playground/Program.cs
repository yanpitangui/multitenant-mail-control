// See https://aka.ms/new-console-template for more information

using System.Text.Json;
using Akka.Actor;
using Akka.IO;
using Akka.Streams;
using Akka.Streams.Amqp.RabbitMq;
using Akka.Streams.Amqp.RabbitMq.Dsl;
using Akka.Streams.Dsl;
using Bogus;
using MultiTenantMailControl.App;

Console.WriteLine("Hello, World!");
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

//create sink
var amqpSink = AmqpSink.CreateSimple( 
    AmqpSinkSettings
        .Create(connectionSettings)
        .WithRoutingKey(queueName)
        .WithDeclarations(queueDeclaration));

using var system = ActorSystem.Create("system");
using var mat = system.Materializer();
var (queue, src) = Source.ActorRef<TenantCommands.SendEmail>(100, OverflowStrategy.Fail)
    .PreMaterialize(system);

_ = src
    .Select(x => JsonSerializer.Serialize(x))
    .Select(ByteString.FromString)
    .RunWith(amqpSink, mat);

var tenants = Enumerable.Range(1, 100)
    .Select(x => $"tenant-{x}")
    .ToArray();
var generator = new Faker<TenantCommands.SendEmail>();
generator.RuleFor(x => x.MessageId, f => Guid.NewGuid());
generator.RuleFor(x => x.EmailSubject, f => f.Lorem.Sentence());
generator.RuleFor(x => x.EmailContent, f => f.Lorem.Paragraph());
generator.RuleFor(x => x.TenantId, f => f.PickRandom(tenants));
while (true)
{
    foreach (var m in generator.Generate(10))
    {
        queue.Tell(m);
    }
    await Task.Delay(500);
}
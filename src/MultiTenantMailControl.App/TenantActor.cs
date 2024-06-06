using System.Threading.RateLimiting;
using Akka.Persistence;
using Polly;
using Polly.Retry;

namespace MultiTenantMailControl.App;

public class TenantActor : ReceivePersistentActor, IWithTimers
{
    private readonly ILoggingAdapter _log = Context.GetLogger();
    private TenantState _state = new TenantState();
    private TenantRateLimitConfig _config = null!;
    private FixedWindowRateLimiter _rateLimiter = null!;
    
    public TenantActor(string tenantId, IEmailSender emailSender)
    {
        var resiliency = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Linear,
                Delay = TimeSpan.FromSeconds(5),
                UseJitter = true,
                OnRetry = (arg) =>
                {
                    _log.Warning("Retrying email {0} for tenant {1}. Retry count {2}", arg.Context.OperationKey, tenantId, arg.AttemptNumber);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
        
        PersistenceId = $"tenant-{tenantId}";
        Command<TenantCommands.SendEmail>(message =>
        {
            var sender = Sender;
            PersistAsync(new InternalCommands.AddToQueue(message), m =>
            {
                sender.Tell(new Events.Ack(m.Message.MessageId));
                _log.Info("Persisted message {0}", m.Message.MessageId);
                Context.Self.Tell(m);
            });
        });
        
        Recover<SnapshotOffer>(o =>
        {
            if (o.Snapshot is TenantState state)
            {
                _state = state;
            }
        });

        Recover<InternalCommands.AddToQueue>(m =>
        {
            _state.Queue.Enqueue(m.Message);
        });
        
        Recover<InternalCommands.RemoveFromQueue>(r => { DequeueAndIncrementDailyTokens(r.IncrementTokens); });

        Command<InternalCommands.AddToQueue>(q =>
        {
            _state.Queue.Enqueue(q.Message);
            if(!Timers.IsTimerActive(nameof(InternalCommands.ProcessQueue)))
            {
                RestartProcessQueueTimer();
            }
        });

        // Restarts daily tokens to the maximum, and sets the last refresh date
        Command<InternalCommands.RefreshDailyTokens>(r =>
        {
            if(_state.LastRefresh is null || _state.LastRefresh <= DateOnly.FromDateTime(Context.System.Scheduler.Now.DateTime))
            {
                RefreshDailyTokens();
                PersistAsync(r, _ =>
                {
                    SaveSnapshot(_state);
                    _log.Info("Refreshed daily tokens for tenant {0}", tenantId);
                    if(_state.Queue.Count >= 0)
                    {
                        RestartProcessQueueTimer();
                    }
                });
            }
        });
        
        
        // Is called on a timer to process the internal queue
        CommandAsync<InternalCommands.ProcessQueue>(async _ =>
        {
            _log.Info("Processing queue for tenant {0}", tenantId);

            while (true)
            {
                var lease = _rateLimiter.AttemptAcquire();
                if (lease.IsAcquired)
                {
                    var hasMessages = _state.Queue.TryPeek(out var message);
                    if (!hasMessages)
                    {
                        _log.Info("No more messages in queue for tenant {0}", tenantId);
                        Timers.Cancel(nameof(InternalCommands.ProcessQueue));
                        break;
                    }
                    
                    if(_config.DailyTokens - _state.UsedDailyTokens <= 0)
                    {
                        Timers.Cancel(nameof(InternalCommands.ProcessQueue));
                        _log.Warning("Daily tokens exhausted for tenant {0}, waiting for next cycle", tenantId);
                        break;
                    }
                    _log.Info("Sending email {0} to {1}", message!.MessageId, message.TenantId);
                    ResilienceContext context = ResilienceContextPool.Shared.Get(message.MessageId.ToString());
                    try
                    {
                        await resiliency.ExecuteAsync(async (ctx, m) => await emailSender.SendEmail(m, ctx.CancellationToken), context,
                            message);
                        DequeueAndIncrementDailyTokens();
                        PersistAsync(new InternalCommands.RemoveFromQueue(), _ => { });
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Failed to send email {0} to {1}, sending it back to end of queue",
                            message.MessageId, message.TenantId);
                        _state.Queue.TryDequeue(out var _);
                        _state.Queue.Enqueue(message);
                        PersistAllAsync<InternalCommands.IInternalCommand>(
                            [new InternalCommands.RemoveFromQueue(false), new InternalCommands.AddToQueue(message)],
                            _ => { });
                        // Breaks to avoid infinite loop
                        if (_state.Queue.Count == 1)
                        {
                            break;
                        }
                    }
                    finally
                    {
                        ResilienceContextPool.Shared.Return(context);
                    }
                         
                }
                else
                {
                    _log.Warning("Rate limit exceeded for tenant {0}, waiting for next cycle", tenantId);
                    RestartProcessQueueTimer();
                    break;
                }
            }
        });
    }

    private void RefreshDailyTokens()
    {
        _state.UsedDailyTokens = 0;
        _state.LastRefresh = DateOnly.FromDateTime(Context.System.Scheduler.Now.DateTime);
    }

    private void DequeueAndIncrementDailyTokens(bool incrementTokens = true)
    {
        _state.Queue.TryDequeue(out _);
        if (incrementTokens)
        {
            _state.UsedDailyTokens++;
        }
    }

    protected override void PreStart()
    {
        _config = new TenantRateLimitConfig
        {
            DailyTokens = 500,
            MaxPerMinute = 5
        };
        _rateLimiter = new(new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
            PermitLimit = _config.MaxPerMinute
        });
        if (_state.LastRefresh is null || _state.LastRefresh <= DateOnly.FromDateTime(Context.System.Scheduler.Now.DateTime))
        {
            // Force refresh of daily tokens
        }
        RestartProcessQueueTimer();
        SetRefreshDailyTokensTimer();
    }
    
    private void SetRefreshDailyTokensTimer()
    {
        var now = Context.System.Scheduler.Now;
        var midnight = now.Date.AddDays(1);
        _log.Info("Setting refresh daily tokens timer for tenant {0}: {1} ", PersistenceId, midnight);
        Timers.StartPeriodicTimer(nameof(RefreshDailyTokens), InternalCommands.RefreshDailyTokens.Instance, midnight - now, TimeSpan.FromDays(1));
    }

    private void RestartProcessQueueTimer()
    {
        Timers.StartPeriodicTimer(nameof(InternalCommands.ProcessQueue), InternalCommands.ProcessQueue.Instance, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public override string PersistenceId { get; }
    
    public ITimerScheduler Timers { get; set; } = null!;

    private record TenantRateLimitConfig
    {
        public int DailyTokens { get; init; }
        public int MaxPerMinute { get; init; }
    }
    private record TenantState
    {
        public Queue<TenantCommands.SendEmail> Queue { get; set; } = new();
        
        public Dictionary<Guid, int> RetryCount { get; set; } = new();
        
        public int UsedDailyTokens { get; set; }
        
        public DateOnly? LastRefresh { get; set; }
    }

    private static class InternalCommands
    {
        public record RefreshDailyTokens
        {
            private RefreshDailyTokens()
            {
            
            }
            public static RefreshDailyTokens Instance { get; } = new();
        }

        public interface IInternalCommand;
    
        public record AddToQueue(TenantCommands.SendEmail Message) : IInternalCommand;

        public record RemoveFromQueue(bool IncrementTokens = true) : IInternalCommand;

        public record ProcessQueue
        {
            private ProcessQueue()
            {
            }

            public static ProcessQueue Instance { get; } = new();
        }
    }
 
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
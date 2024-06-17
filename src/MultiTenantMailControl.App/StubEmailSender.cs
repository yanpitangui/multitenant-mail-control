namespace MultiTenantMailControl.App;

public class StubEmailSender : IEmailSender
{
    private static int Counter = 0; 
    public async Task SendEmail(TenantCommands.SendEmail email, CancellationToken ct)
    {

        Interlocked.Increment(ref Counter);
        Console.WriteLine(Counter);

    }
}
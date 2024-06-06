namespace MultiTenantMailControl.App;

public class StubEmailSender : IEmailSender
{
    private static int Counter = 0; 
    public async Task SendEmail(TenantCommands.SendEmail email, CancellationToken ct)
    {
        await Task.Delay(2000, ct);
        var failure = Random.Shared.Next(0, 10);
        if (failure <= 3)
        {
            throw new Exception("Failed to send email");
        }

        Interlocked.Increment(ref Counter);
        Console.WriteLine(Counter);

    }
}
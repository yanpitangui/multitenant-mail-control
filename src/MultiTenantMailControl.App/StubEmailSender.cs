namespace MultiTenantMailControl.App;

public class StubEmailSender : IEmailSender
{
    public async Task SendEmail(TenantCommands.SendEmail email, CancellationToken ct)
    {
        await Task.Delay(2000, ct);
        var failure = Random.Shared.Next(0, 10);
        if (failure <= 3)
        {
            throw new Exception("Failed to send email");
        }
        
    }
}
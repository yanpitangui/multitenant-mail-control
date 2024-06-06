namespace MultiTenantMailControl.App;

public interface IEmailSender
{
    public Task SendEmail(TenantCommands.SendEmail email, CancellationToken ct);
}
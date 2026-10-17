using Microsoft.Extensions.Logging;
using Samian.Application.Contracts;

namespace Samian.Infrastructure.Notifications;

// IEmailSender, written to disk instead of SMTP. That is not laziness, it is the boundary being honest:
// a mail transport needs credentials, a retry policy and a queue, none of which belong in a sample —
// and the application code cannot tell, because it only knows IEmailSender.
//
// Where this goes in production: an SMTP/Mailgun/SendGrid adapter, and (because a failing mail provider
// must not fail a placed order) it is called from a domain event handler, which is exactly how it is
// wired here. The outbox folder also makes the side effect observable in a code review: run the API,
// place an order, and read the file to prove the handler ran.
public sealed class FileEmailSender : IEmailSender
{
    private readonly ILogger<FileEmailSender> _logger;

    public FileEmailSender(ILogger<FileEmailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(to);

        // One file per message, timestamped, in the temp directory so nothing writes into the source
        // tree. The name carries the recipient so a reviewer can find the message for a given order.
        var directory = Path.Combine(Path.GetTempPath(), "samian-outbox");
        Directory.CreateDirectory(directory);

        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{to.Replace('@', '_').Replace('<', '-').Replace('>', '-')}.eml";

        var message =
            $"To: {to}{Environment.NewLine}" +
            $"Subject: {subject}{Environment.NewLine}" +
            $"Date: {DateTimeOffset.UtcNow:R}{Environment.NewLine}" +
            $"{Environment.NewLine}" +
            $"{body}{Environment.NewLine}";

        await File.WriteAllTextAsync(Path.Combine(directory, fileName), message, cancellationToken);

        _logger.LogInformation("E-mail {Subject} written to the outbox folder for {To}.", subject, to);
    }
}

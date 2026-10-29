using CoachBot.Core.Abstractions;
using CoachBot.Core.Services;
using CoachBot.Infrastructure.Localization;
using CoachBot.Infrastructure.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoachBot.Infrastructure.Tests;

public class BackgroundWorkTests
{
    private sealed class CapturingSender : IChatSender
    {
        public List<(long ChatId, OutboundMessage Message)> Sent { get; } = [];

        public Task SendAsync(long chatId, OutboundMessage message, CancellationToken ct)
        {
            lock (Sent)
            {
                Sent.Add((chatId, message));
            }

            return Task.CompletedTask;
        }
    }

    private static readonly MessageFormatter Fmt = new(new JsonTextCatalog(strict: true));

    private static (BackgroundWork Work, CapturingSender Sender, ServiceProvider Services) Create()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var sender = new CapturingSender();
        return (new BackgroundWork(services.GetRequiredService<IServiceScopeFactory>(), sender, Fmt, NullLogger<BackgroundWork>.Instance), sender, services);
    }

    [Fact]
    public async Task Runs_one_job_per_chat_and_key_and_sends_the_notice_before_the_result()
    {
        var (work, sender, services) = Create();
        await using var _ = services;
        var release = new TaskCompletionSource();

        Assert.True(work.TryStart(1, "plan", "en", new OutboundMessage("started"), async (_, _) =>
        {
            await release.Task;
            return new OutboundMessage("plan ready");
        }));
        Assert.False(work.TryStart(1, "plan", "en", null, (_, _) => Task.FromResult<OutboundMessage?>(null)));
        Assert.True(work.TryStart(1, "report", "en", null, (_, _) => Task.FromResult<OutboundMessage?>(new OutboundMessage("report"))));
        Assert.True(work.TryStart(2, "plan", "en", null, (_, _) => Task.FromResult<OutboundMessage?>(null)));

        release.SetResult();
        await work.WhenDone(1, "plan");
        await work.WhenDone(1, "report");
        await work.WhenDone(2, "plan");

        var chat1 = sender.Sent.Where(s => s.ChatId == 1).Select(s => s.Message.Text).ToList();
        Assert.True(chat1.IndexOf("started") < chat1.IndexOf("plan ready"));
        Assert.Contains("report", chat1);
        Assert.DoesNotContain(sender.Sent, s => s.ChatId == 2);
        Assert.True(work.TryStart(1, "plan", "en", null, (_, _) => Task.FromResult<OutboundMessage?>(null)), "the key is free again");
    }

    [Fact]
    public async Task Failures_become_a_localized_message()
    {
        var (work, sender, services) = Create();
        await using var _ = services;

        work.TryStart(1, "plan", "en", null, (_, _) => throw new LlmException("plan_design", LlmFailure.Timeout, "t"));
        await work.WhenDone(1, "plan");
        work.TryStart(1, "report", "vi", null, (_, _) => throw new InvalidOperationException("bug"));
        await work.WhenDone(1, "report");

        Assert.Equal(Fmt.T("en", "llm.failed.plan", Fmt.T("en", "llm.reason.timeout")), sender.Sent[0].Message.Text);
        Assert.Equal(Fmt.T("vi", "error.background", Fmt.T("vi", "work.report")), sender.Sent[1].Message.Text);
    }
}

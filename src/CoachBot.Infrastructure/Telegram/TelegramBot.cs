using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace CoachBot.Infrastructure.Telegram;

public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    public string? BotToken { get; set; }

    /// <summary>Only this chat is served. Empty: the bot only answers /start with the chat id to configure.</summary>
    public long? AllowedChatId { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken);
}

/// <summary>Lazily created bot client shared by the polling service and the notifier.</summary>
public sealed class TelegramClientHolder(IOptions<TelegramOptions> options, IHttpClientFactory httpFactory)
{
    private readonly Lazy<ITelegramBotClient?> _client = new(() => options.Value.IsConfigured
        ? new TelegramBotClient(options.Value.BotToken!, httpFactory.CreateClient("telegram"))
        : null);

    public ITelegramBotClient? Client => _client.Value;
}

/// <summary>Delivers messages to the athlete's linked Telegram chat. Without a bot token, messages are only logged.</summary>
public sealed class TelegramNotifier(TelegramClientHolder holder, ICoachDb db, ILogger<TelegramNotifier> logger) : INotifier
{
    private const int MaxLength = 4000;

    public async Task<long?> SendAsync(Guid athleteId, OutboundMessage message, CancellationToken ct)
    {
        var chatId = await db.Athletes.Where(a => a.Id == athleteId).Select(a => a.TelegramChatId).FirstOrDefaultAsync(ct);
        if (holder.Client is null || chatId is null)
        {
            logger.LogInformation("Notification not delivered (bot not configured or chat not linked): {Preview}",
                message.Text.Length > 120 ? message.Text[..120] + "…" : message.Text);
            return null;
        }

        return await SendToChatAsync(holder.Client, chatId.Value, message, ct);
    }

    public static async Task<long?> SendToChatAsync(ITelegramBotClient bot, long chatId, OutboundMessage message, CancellationToken ct)
    {
        var chunks = Split(message.Text);
        Message? last = null;
        for (var i = 0; i < chunks.Count; i++)
        {
            var markup = i == chunks.Count - 1 && message.Buttons is { Count: > 0 } ? Keyboard(message.Buttons) : null;
            last = await bot.SendMessage(chatId, chunks[i], ParseMode.None, replyMarkup: markup, cancellationToken: ct);
        }

        return last?.MessageId;
    }

    public static InlineKeyboardMarkup Keyboard(IReadOnlyList<IReadOnlyList<MessageButton>> rows) =>
        new(rows.Select(r => r.Select(b => InlineKeyboardButton.WithCallbackData(b.Label, b.CallbackData))));

    private static List<string> Split(string text)
    {
        var chunks = new List<string>();
        while (text.Length > MaxLength)
        {
            var cut = text.LastIndexOf('\n', MaxLength);
            cut = cut <= 0 ? MaxLength : cut;
            chunks.Add(text[..cut]);
            text = text[cut..].TrimStart('\n');
        }

        chunks.Add(text);
        return chunks;
    }
}

/// <summary>Long-polling receiver (no public URL needed). Each update is handled in its own DI scope.</summary>
public sealed class TelegramBotService(
    TelegramClientHolder holder,
    IServiceScopeFactory scopes,
    MessageFormatter fmt,
    ILogger<TelegramBotService> logger) : BackgroundService, IUpdateHandler
{
    private static readonly BotCommand[] Commands =
    [
        new() { Command = "today", Description = "Today's workout and readiness" },
        new() { Command = "week", Description = "This week's plan" },
        new() { Command = "daily", Description = "Morning check now (readiness + today)" },
        new() { Command = "plan", Description = "Generate a training plan" },
        new() { Command = "goal", Description = "Set race: /goal 2027-03-14 3:59 Name" },
        new() { Command = "profile", Description = "Show/update profile" },
        new() { Command = "sync", Description = "Sync COROS now" },
        new() { Command = "feedback", Description = "Tell the coach how you feel" },
        new() { Command = "symptom", Description = "Report a symptom (pauses plan if serious)" },
        new() { Command = "status", Description = "Goal, plan and COROS status" },
        new() { Command = "report", Description = "Weekly report now" },
        new() { Command = "connect", Description = "Connect COROS" },
        new() { Command = "pause", Description = "Pause the plan" },
        new() { Command = "resume", Description = "Resume the plan" },
        new() { Command = "help", Description = "All commands" },
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bot = holder.Client;
        if (bot is null)
        {
            logger.LogWarning("Telegram__BotToken not set: bot disabled; notifications will only be logged");
            return;
        }

        try
        {
            var me = await bot.GetMe(stoppingToken);
            await bot.DeleteWebhook(cancellationToken: stoppingToken);
            await bot.SetMyCommands(Commands, cancellationToken: stoppingToken);
            logger.LogInformation("Telegram bot @{Username} polling for updates", me.Username);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Telegram bot could not start (check Telegram__BotToken)");
            return;
        }

        await bot.ReceiveAsync(this, new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery] }, stoppingToken);
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        var bot = botClient;
        var ct = cancellationToken;
        await using var scope = scopes.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<TelegramUpdateHandler>();
        try
        {
            await handler.HandleAsync(bot, update, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A timeout is an OperationCanceledException too; only a shutdown should go unanswered.
            logger.LogError(ex, "Failed to handle Telegram update {UpdateId}", update.Id);
            long? chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id;
            if (chatId is long id && handler.IsAllowed(id))
            {
                await bot.SendMessage(id, fmt.T(handler.Language, "error.generic"), cancellationToken: ct);
            }
        }
    }

    public Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
    {
        logger.LogWarning(exception, "Telegram polling error ({Source})", source);
        return Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }
}

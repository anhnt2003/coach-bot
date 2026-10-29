using System.Collections.Concurrent;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoachBot.Infrastructure.Telegram;

/// <summary>Delivers a message to a chat id. Without a bot token the message is only logged.</summary>
public interface IChatSender
{
    Task SendAsync(long chatId, OutboundMessage message, CancellationToken ct);
}

public sealed class TelegramChatSender(TelegramClientHolder holder, ILogger<TelegramChatSender> logger) : IChatSender
{
    public Task SendAsync(long chatId, OutboundMessage message, CancellationToken ct)
    {
        if (holder.Client is null)
        {
            logger.LogInformation("Message to chat {ChatId} not delivered (bot not configured): {Preview}", chatId,
                message.Text.Length > 120 ? message.Text[..120] + "…" : message.Text);
            return Task.CompletedTask;
        }

        return TelegramNotifier.SendToChatAsync(holder.Client, chatId, message, ct);
    }
}

/// <summary>Work queued by a command. Resolves its services from the scope it is given; the result is sent to the chat.</summary>
public delegate Task<OutboundMessage?> BackgroundJob(IServiceProvider services, CancellationToken ct);

/// <summary>
/// Runs commands that wait for the LLM outside the Telegram update loop, which handles one update at a time: a plan can
/// take ten minutes, and the bot must keep answering meanwhile. One job per chat and key; each job gets its own DI scope,
/// is cancelled on shutdown, and its result or a localized error is sent to the chat.
/// </summary>
public sealed class BackgroundWork(
    IServiceScopeFactory scopes,
    IChatSender sender,
    MessageFormatter fmt,
    ILogger<BackgroundWork> logger,
    IHostApplicationLifetime? lifetime = null) : IHostedService
{
    private readonly ConcurrentDictionary<(long ChatId, string Key), Task> _running = new();
    private readonly CancellationToken _stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;

    /// <summary>
    /// Starts <paramref name="work"/> unless the same key already runs for this chat. <paramref name="started"/> is sent
    /// first, so it always arrives before the result.
    /// </summary>
    /// <param name="lang">Language for the error message when the job fails.</param>
    public bool TryStart(long chatId, string key, string lang, OutboundMessage? started, BackgroundJob work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_running.TryAdd((chatId, key), done.Task))
        {
            return false;
        }

        _ = Task.Run(() => RunAsync(chatId, key, lang, started, work, done), CancellationToken.None);
        return true;
    }

    /// <summary>
    /// What a job does, for its messages: the key up to the first ':'. A key can carry an id ("note:…") so that each
    /// note gets its own job instead of waiting behind the previous one.
    /// </summary>
    public static string Kind(string key) => key.Split(':')[0];

    /// <summary>Completes when the job for this chat and key has finished; immediately when none runs.</summary>
    public Task WhenDone(long chatId, string key) => _running.TryGetValue((chatId, key), out var task) ? task : Task.CompletedTask;

    private async Task RunAsync(long chatId, string key, string lang, OutboundMessage? started, BackgroundJob work, TaskCompletionSource done)
    {
        try
        {
            if (started is not null)
            {
                await sender.SendAsync(chatId, started, _stopping);
            }

            OutboundMessage? reply;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                reply = await work(scope.ServiceProvider, _stopping);
            }
            catch (LlmException ex)
            {
                logger.LogWarning("Background {Key} for chat {ChatId} failed: LLM {Purpose} {Reason}", key, chatId, ex.Purpose, ex.Reason);
                reply = new OutboundMessage(fmt.T(lang, $"llm.failed.{Kind(key)}", fmt.T(lang, $"llm.reason.{ex.Reason.ToString().ToLowerInvariant()}")));
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                logger.LogInformation("Background {Key} for chat {ChatId} cancelled by shutdown", key, chatId);
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background {Key} for chat {ChatId} failed", key, chatId);
                reply = new OutboundMessage(fmt.T(lang, "error.background", fmt.T(lang, $"work.{Kind(key)}")));
            }

            if (reply is not null)
            {
                await sender.SendAsync(chatId, reply, _stopping);
            }
        }
        catch (Exception ex) when (!_stopping.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not deliver the result of {Key} to chat {ChatId}", key, chatId);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            _running.TryRemove((chatId, key), out _);
            done.TrySetResult();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>ApplicationStopping has already cancelled the jobs; wait for them to unwind before services are disposed.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(_running.Values).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Background work still running at shutdown");
        }
    }
}

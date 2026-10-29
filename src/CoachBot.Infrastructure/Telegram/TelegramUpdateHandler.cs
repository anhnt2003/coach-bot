using System.Globalization;
using System.Text.RegularExpressions;
using CoachBot.Core.Abstractions;
using CoachBot.Core.Domain;
using CoachBot.Core.Services;
using CoachBot.Infrastructure.Coros;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace CoachBot.Infrastructure.Telegram;

/// <summary>Routes Telegram commands, free text and button callbacks to the application services. Scoped per update.</summary>
public sealed partial class TelegramUpdateHandler(
    IOptions<TelegramOptions> telegram,
    IOptions<CoachOptions> coach,
    AthleteService athletes,
    PlanService plans,
    ReportService reports,
    FeedbackService feedback,
    RecommendationService recommendations,
    CorosPushService push,
    CorosConnectService connect,
    ICorosGateway gateway,
    BackgroundWork background,
    MessageFormatter fmt,
    TimeProvider time,
    ILogger<TelegramUpdateHandler> logger)
{
    private ITelegramBotClient _bot = null!;
    private long _chatId;

    /// <summary>Work that runs after the update has been answered, in its own scope with a freshly loaded athlete.</summary>
    private delegate Task<OutboundMessage?> AthleteJob(IServiceProvider services, Athlete athlete, CancellationToken ct);

    /// <summary>The athlete's language once known; used for the error message when handling fails.</summary>
    public string Language { get; private set; } = coach.Value.Language;

    public bool IsAllowed(long chatId) => telegram.Value.AllowedChatId == chatId;

    public async Task HandleAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        _bot = bot;
        if (update.Message is { Text: { } text } message)
        {
            _chatId = message.Chat.Id;
            await OnMessageAsync(message.From?.FirstName, text.Trim(), ct);
        }
        else if (update.CallbackQuery is { Data: { } data, Message: { } source } callback)
        {
            _chatId = source.Chat.Id;
            try
            {
                await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
            }
            catch (ApiRequestException ex)
            {
                // Only stops the button's spinner. A tap made while the bot was offline is too old to answer,
                // but the action itself must still run.
                logger.LogDebug("Could not answer callback query: {Message}", ex.Message);
            }

            if (IsAllowed(_chatId))
            {
                await OnCallbackAsync(data, source.MessageId, ct);
            }
        }
    }

    private Task ReplyAsync(OutboundMessage message, CancellationToken ct) =>
        TelegramNotifier.SendToChatAsync(_bot, _chatId, message, ct);

    private Task ReplyAsync(string text, CancellationToken ct) => ReplyAsync(new OutboundMessage(text), ct);

    private async Task OnMessageAsync(string? firstName, string text, CancellationToken ct)
    {
        var parts = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts.Length > 0 && parts[0].StartsWith('/') ? parts[0].Split('@')[0].ToLowerInvariant() : null;
        var rest = parts.Length > 1 ? parts[1].Trim() : "";

        if (telegram.Value.AllowedChatId is null)
        {
            // Not configured yet: only reveal the chat id needed for Telegram__AllowedChatId.
            if (command == "/start")
            {
                await ReplyAsync(fmt.T(coach.Value.Language, "start.configure_chat_id", _chatId), ct);
            }

            return;
        }

        if (!IsAllowed(_chatId))
        {
            logger.LogWarning("Ignoring message from unauthorized chat {ChatId}", _chatId);
            return;
        }

        var athlete = command == "/start"
            ? await athletes.LinkTelegramAsync(_chatId, firstName, ct)
            : await athletes.GetOrCreatePrimaryAsync(ct);
        if (athlete.TelegramChatId != _chatId)
        {
            athlete = await athletes.LinkTelegramAsync(_chatId, firstName, ct);
        }

        var lang = Language = athlete.Language;
        // The coach works from COROS data only: until COROS is connected, every other command asks for /connect.
        var connected = await connect.IsConnectedAsync(athlete.Id, ct);
        var isRedirect = command is null && text.Contains("/oauth/coros/callback?", StringComparison.Ordinal);
        if (!connected && !isRedirect && command is not ("/start" or "/help" or "/connect" or "/language"))
        {
            await ReplyAsync(fmt.T(lang, "connect.required"), ct);
            return;
        }

        switch (command)
        {
            case "/start":
            case "/help":
                await ReplyAsync(fmt.T(lang, "help") + "\n\n" + fmt.T(lang, "disclaimer") + (connected ? "" : "\n\n" + fmt.T(lang, "connect.required")), ct);
                break;
            case "/goal":
                await GoalAsync(athlete, rest, ct);
                break;
            case "/profile":
                await ProfileAsync(athlete, rest, ct);
                break;
            case "/language":
                if (rest.ToLowerInvariant() is not ("en" or "vi"))
                {
                    await ReplyAsync(fmt.T(lang, "profile.language_usage"), ct);
                    break;
                }

                await athletes.UpdateProfileAsync(athlete, null, null, null, null, rest.ToLowerInvariant(), ct);
                await ReplyAsync(fmt.T(athlete.Language, "profile.language_set"), ct);
                break;
            case "/plan":
                await StartAsync(athlete, "plan", new OutboundMessage(fmt.T(lang, "queued.plan")), PlanJobAsync, ct);
                break;
            case "/today":
                await ReplyAsync(await reports.TodayAsync(athlete, ct), ct);
                break;
            case "/week":
                await ReplyAsync(await reports.WeekAsync(athlete, ct), ct);
                break;
            case "/daily":
                // Same message the 06:30 job sends; handy for testing.
                await StartAsync(athlete, "daily", new OutboundMessage(fmt.T(lang, "queued.daily")), static async (sp, a, token) =>
                    await sp.GetRequiredService<ReportService>().DailyCheckAsync(a, token)
                    ?? new OutboundMessage(sp.GetRequiredService<MessageFormatter>().T(a.Language, "report.nothing")), ct);
                break;
            case "/status":
                await ReplyAsync(await reports.StatusAsync(athlete, ct), ct);
                break;
            case "/report":
                await StartAsync(athlete, "report", new OutboundMessage(fmt.T(lang, "queued.report")), static async (sp, a, token) =>
                    await sp.GetRequiredService<ReportService>().WeeklyReportAsync(a, token)
                    ?? new OutboundMessage(sp.GetRequiredService<MessageFormatter>().T(a.Language, "report.nothing")), ct);
                break;
            case "/sync":
                await StartSyncAsync(athlete, rest == "full", ct);
                break;
            case "/push":
                await PushAsync(athlete, ct);
                break;
            case "/connect":
                await ConnectAsync(athlete, ct);
                break;
            case "/disconnect":
                await connect.DisconnectAsync(athlete.Id, ct);
                await ReplyAsync(fmt.T(lang, "connect.disconnected"), ct);
                break;
            case "/feedback" when rest.Length == 0:
                await ReplyAsync(fmt.T(lang, "feedback.usage"), ct);
                break;
            case "/feedback":
                await StartNoteAsync(athlete, rest, ct);
                break;
            case "/soreness" when int.TryParse(rest, out var soreness):
                await ReplyAsync(await feedback.RecordSorenessAsync(athlete, soreness, ct), ct);
                break;
            case "/symptom":
                await SymptomAsync(athlete, rest, ct);
                break;
            case "/pause":
                await ReplyAsync(fmt.T(lang, await feedback.PauseAsync(athlete, "athlete", ct) ? "pause.done" : "pause.no_plan"), ct);
                break;
            case "/resume":
                await ReplyAsync(await feedback.RequestResumeAsync(athlete, ct), ct);
                break;
            case null when isRedirect:
                await CompleteConnectAsync(athlete, text, ct);
                break;
            case null:
                // Any free text is feedback, and is always screened for red-flag symptoms.
                await StartNoteAsync(athlete, text, ct);
                break;
            default:
                await ReplyAsync(fmt.T(lang, "help"), ct);
                break;
        }
    }

    private async Task OnCallbackAsync(string data, int messageId, CancellationToken ct)
    {
        var athlete = await athletes.GetOrCreatePrimaryAsync(ct);
        var lang = Language = athlete.Language;
        if (!await connect.IsConnectedAsync(athlete.Id, ct))
        {
            // Buttons stay, so they can be tapped again once COROS is connected.
            await ReplyAsync(fmt.T(lang, "connect.required"), ct);
            return;
        }

        var p = data.Split(':');

        async Task ClearButtonsAsync() => await _bot.EditMessageReplyMarkup(_chatId, messageId, null, cancellationToken: ct);

        switch (p)
        {
            case ["plan", "approve", var id] when Guid.TryParse(id, out var planId):
                await ClearButtonsAsync();
                await ApprovePlanAsync(athlete, planId, ct);
                break;
            case ["plan", "reject", var id] when Guid.TryParse(id, out var planId):
                await ClearButtonsAsync();
                await ReplyAsync(fmt.T(lang, await plans.RejectAsync(athlete.Id, planId, ct) ? "plan.rejected" : "plan.not_pending"), ct);
                break;
            case ["rec", "approve", var id] when Guid.TryParse(id, out var recId):
                await ClearButtonsAsync();
                var (outcome, _) = await recommendations.ApproveAsync(athlete, recId, ct);
                await ReplyAsync(fmt.T(lang, OutcomeKey("approved", outcome)), ct);
                break;
            case ["rec", "reject", var id] when Guid.TryParse(id, out var recId):
                await ClearButtonsAsync();
                await ReplyAsync(fmt.T(lang, OutcomeKey("rejected", await recommendations.RejectAsync(athlete, recId, ct))), ct);
                break;
            case ["rec", "undo", var id] when Guid.TryParse(id, out var recId):
                await ClearButtonsAsync();
                await ReplyAsync(fmt.T(lang, OutcomeKey("undone", await recommendations.UndoAsync(athlete, recId, ct))), ct);
                break;
            case ["rpe", var id, var value] when Guid.TryParse(id, out var activityId) && int.TryParse(value, out var rpe):
                await ClearButtonsAsync();
                await ReplyAsync(await feedback.RecordRpeAsync(athlete, activityId, rpe, ct), ct);
                break;
            case ["sym", var category] when Enum.TryParse<SymptomCategory>(category, true, out var symptom):
                await ClearButtonsAsync();
                await ReplyAsync(await feedback.ReportCategoryAsync(athlete, symptom, ct), ct);
                break;
            case ["resume", "confirm"]:
                await ClearButtonsAsync();
                await ReplyAsync(await feedback.ConfirmResumeAsync(athlete, ct), ct);
                break;
            case ["resume", "cancel"]:
                await ClearButtonsAsync();
                break;
            default:
                logger.LogWarning("Unknown callback {Data}", data);
                break;
        }
    }

    private static string OutcomeKey(string doneKey, DecisionOutcome outcome) => outcome switch
    {
        DecisionOutcome.Done => $"rec.{doneKey}",
        DecisionOutcome.Expired => "rec.expired",
        DecisionOutcome.NoLongerValid => "rec.no_longer_valid",
        DecisionOutcome.AlreadyDecided => "rec.already_decided",
        _ => "rec.not_found",
    };

    private async Task GoalAsync(Athlete athlete, string args, CancellationToken ct)
    {
        var lang = athlete.Language;
        if (!GoalParser.TryParse(args, out var input, out var error))
        {
            await ReplyAsync(fmt.T(lang, error!), ct);
            return;
        }

        var today = AthleteClock.Today(time, athlete.TimeZone);
        if (input!.Date <= today || input.Date > today.AddYears(2))
        {
            await ReplyAsync(fmt.T(lang, "goal.invalid_date"), ct);
            return;
        }

        var goal = await athletes.SetGoalAsync(athlete.Id, input.Date, input.GoalSeconds, input.Name, input.DistanceKm, ct);
        await ReplyAsync(fmt.T(lang, "goal.saved", goal.Name, fmt.RaceDistance(lang, goal.DistanceKm), fmt.Day(lang, goal.RaceDate),
            (goal.RaceDate.DayNumber - today.DayNumber) / 7,
            goal.GoalTimeSeconds is int g ? MessageFormatter.Time(g) : fmt.T(lang, "goal.no_time")), ct);
    }

    private async Task ProfileAsync(Athlete athlete, string args, CancellationToken ct)
    {
        int? days = null, maxHr = null, born = null;
        DayOfWeek? longDay = null;
        string? lang = null;
        foreach (var pair in args.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(a => a.Split('=', 2)).Where(a => a.Length == 2))
        {
            switch (pair[0].ToLowerInvariant())
            {
                case "days" when int.TryParse(pair[1], out var d): days = d; break;
                case "maxhr" when int.TryParse(pair[1], out var h) && h is > 120 and < 230: maxHr = h; break;
                case "born" when int.TryParse(pair[1], out var y) && y is > 1930 and < 2015: born = y; break;
                case "long": longDay = ParseDay(pair[1]); break;
                case "lang": lang = pair[1].ToLowerInvariant(); break;
            }
        }

        if (days is not null || maxHr is not null || born is not null || longDay is not null || lang is not null)
        {
            await athletes.UpdateProfileAsync(athlete, days, longDay, maxHr, born, lang, ct);
        }

        var l = athlete.Language;
        await ReplyAsync(fmt.T(l, "profile.show", athlete.RunDaysPerWeek,
            MessageFormatter.Culture(l).DateTimeFormat.GetDayName(athlete.LongRunDay),
            athlete.MaxHr?.ToString(CultureInfo.InvariantCulture) ?? "–", athlete.BirthYear?.ToString(CultureInfo.InvariantCulture) ?? "–", athlete.Language), ct);
    }

    private static DayOfWeek? ParseDay(string value) => value.ToLowerInvariant() switch
    {
        "mon" or "t2" => DayOfWeek.Monday,
        "tue" or "t3" => DayOfWeek.Tuesday,
        "wed" or "t4" => DayOfWeek.Wednesday,
        "thu" or "t5" => DayOfWeek.Thursday,
        "fri" or "t6" => DayOfWeek.Friday,
        "sat" or "t7" => DayOfWeek.Saturday,
        "sun" or "cn" => DayOfWeek.Sunday,
        _ => null,
    };

    /// <summary>
    /// Queues work that waits for the LLM, so the update loop stays free. The job never touches this handler: it runs in
    /// its own scope with the athlete reloaded there.
    /// </summary>
    private async Task StartAsync(Athlete athlete, string key, OutboundMessage? started, AthleteJob job, CancellationToken ct)
    {
        var athleteId = athlete.Id;
        var accepted = background.TryStart(_chatId, key, athlete.Language, started, async (sp, token) =>
        {
            var reloaded = await sp.GetRequiredService<ICoachDb>().Athletes.SingleAsync(a => a.Id == athleteId, token);
            return await job(sp, reloaded, token);
        });
        if (!accepted)
        {
            await ReplyAsync(fmt.T(athlete.Language, "busy.running", fmt.T(athlete.Language, $"work.{key}")), ct);
        }
    }

    private static async Task<OutboundMessage?> PlanJobAsync(IServiceProvider sp, Athlete athlete, CancellationToken ct)
    {
        var plans = sp.GetRequiredService<PlanService>();
        var fmt = sp.GetRequiredService<MessageFormatter>();
        var lang = athlete.Language;
        var result = await plans.GenerateAsync(athlete, ct);
        if (result.Plan is null)
        {
            return new OutboundMessage(fmt.T(lang, result.FailureKey ?? "plan.failed"));
        }

        var goal = (await sp.GetRequiredService<AthleteService>().GetActiveGoalAsync(athlete.Id, ct))!;
        return plans.RenderProposal(athlete, result.Plan, goal);
    }

    private Task StartNoteAsync(Athlete athlete, string text, CancellationToken ct) =>
        StartAsync(athlete, "note", null, (sp, a, token) => RecordNoteAsync(sp, a, text, false, token), ct);

    private static async Task<OutboundMessage?> RecordNoteAsync(IServiceProvider sp, Athlete athlete, string text, bool explicitSymptomReport, CancellationToken ct) =>
        await sp.GetRequiredService<FeedbackService>().RecordNoteAsync(athlete, text, explicitSymptomReport, ct);

    private async Task ApprovePlanAsync(Athlete athlete, Guid planId, CancellationToken ct)
    {
        var lang = athlete.Language;
        var (plan, orphaned) = await plans.ApproveAsync(athlete.Id, planId, ct);
        if (plan is null)
        {
            await ReplyAsync(fmt.T(lang, "plan.not_pending"), ct);
            return;
        }

        await ReplyAsync(fmt.T(lang, "plan.approved"), ct);
        if (orphaned.Count > 0)
        {
            await ReplyAsync(fmt.T(lang, "plan.orphaned_on_coros") + "\n" + string.Join("\n", orphaned.Select(w => "• " + fmt.Line(lang, w))), ct);
        }

        await PushAsync(athlete, ct);
    }

    /// <summary>Pulls COROS data, then analyzes recent runs; each analysis sends its own message.</summary>
    private Task StartSyncAsync(Athlete athlete, bool full, CancellationToken ct) =>
        StartAsync(athlete, "sync", null, async (sp, a, token) =>
        {
            var sync = sp.GetRequiredService<SyncService>();
            var f = sp.GetRequiredService<MessageFormatter>();
            var result = full
                ? await sync.SyncAsync(a, SyncService.FullActivityDays, SyncService.FullMetricDays, token)
                : await sync.SyncAsync(a, SyncService.ActivityDays, SyncService.MetricDays, token);
            if (!result.Success)
            {
                return new OutboundMessage(f.T(a.Language, "sync.failed", result.Error));
            }

            await sp.GetRequiredService<INotifier>().SendAsync(a.Id, new OutboundMessage(f.T(a.Language, "sync.done", result.NewActivities, result.MetricDays)), token);
            await sp.GetRequiredService<AnalysisService>().AnalyzePendingAsync(a, token);
            return null;
        }, ct);

    private async Task PushAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        var summary = await push.PushUpcomingAsync(athlete, ct);
        await ReplyAsync(summary.SkippedReason is null
            ? fmt.T(lang, "push.summary", summary.Pushed, summary.Updated, summary.Failed)
            : fmt.T(lang, $"push.skipped.{summary.SkippedReason}"), ct);
    }

    private async Task ConnectAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        // Verified live: COROS only grants authorization_code (+PKCE) to dynamically registered clients, no device flow.
        var url = await connect.StartBrowserLoginAsync(athlete.Id, ct);
        await ReplyAsync(fmt.T(lang, "connect.browser", url), ct);
    }

    private async Task CompleteConnectAsync(Athlete athlete, string redirectUrl, CancellationToken ct)
    {
        var lang = athlete.Language;
        if (!await connect.CompleteFromRedirectUrlAsync(redirectUrl, ct))
        {
            await ReplyAsync(fmt.T(lang, "connect.failed"), ct);
            return;
        }

        await AfterConnectAsync(athlete, ct);
    }

    /// <summary>Verifies the tools, then backfills history in the background so a plan can be generated from real data.</summary>
    private async Task AfterConnectAsync(Athlete athlete, CancellationToken ct)
    {
        var lang = athlete.Language;
        var caps = await gateway.GetCapabilitiesAsync(athlete.Id, ct);
        await ReplyAsync(fmt.T(lang, "connect.done", caps.Tools.Count, fmt.T(lang, caps.CanPushWorkouts ? "common.yes" : "common.no")), ct);
        await StartSyncAsync(athlete, full: true, ct);
    }

    private async Task SymptomAsync(Athlete athlete, string text, CancellationToken ct)
    {
        var lang = athlete.Language;
        if (text.Length > 0)
        {
            await StartAsync(athlete, "symptom", new OutboundMessage(fmt.T(lang, "queued.symptom")),
                (sp, a, token) => RecordNoteAsync(sp, a, text, true, token), ct);
            return;
        }

        await ReplyAsync(new OutboundMessage(fmt.T(lang, "symptom.ask"),
        [
            [new MessageButton(fmt.T(lang, "symptom.button.cardiac"), "sym:cardiac")],
            [new MessageButton(fmt.T(lang, "symptom.button.injury"), "sym:injury")],
            [new MessageButton(fmt.T(lang, "symptom.button.illness"), "sym:illness")],
            [new MessageButton(fmt.T(lang, "symptom.button.none"), "sym:none")],
        ]), ct);
    }
}

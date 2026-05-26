using Hangfire;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using YtAudio.Api.Data;
using YtAudio.Api.Jobs;
using YtAudio.Api.Models;

namespace YtAudio.Api.Services;

public class TelegramBotService(
    IConfiguration config,
    IServiceScopeFactory scopeFactory,
    YouTubeSearchService youTubeSearch,
    IBackgroundJobClient jobs,
    ILogger<TelegramBotService> logger) : BackgroundService
{
    private TelegramBotClient? _bot;

    private readonly HashSet<long> _allowedUserIds = config
        .GetSection("Telegram:AllowedUserIds")
        .Get<string[]>()?
        .Select(long.Parse)
        .ToHashSet() ?? [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var token = config["Telegram:BotToken"]
            ?? throw new InvalidOperationException("Telegram:BotToken is missing.");

        _bot = new TelegramBotClient(token);

        var me = await _bot.GetMe(ct);
        logger.LogInformation("Telegram bot started: @{Username}", me.Username);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.InlineQuery, UpdateType.ChosenInlineResult, UpdateType.Message]
        };

        await _bot.ReceiveAsync(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: ct);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            switch (update.Type)
            {
                case UpdateType.InlineQuery when update.InlineQuery is not null:
                    await HandleInlineQueryAsync(bot, update.InlineQuery, ct);
                    break;

                case UpdateType.ChosenInlineResult when update.ChosenInlineResult is not null:
                    await HandleChosenResultAsync(bot, update.ChosenInlineResult, ct);
                    break;

                case UpdateType.Message when update.Message is not null:
                    await HandleMessageAsync(bot, update.Message, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling update {UpdateId}", update.Id);
        }
    }

    private async Task HandleInlineQueryAsync(ITelegramBotClient bot, InlineQuery query, CancellationToken ct)
    {
        if (!IsAllowed(query.From.Id))
        {
            await bot.AnswerInlineQuery(query.Id, [], cancellationToken: ct);
            return;
        }

        var searchQuery = query.Query.Trim();
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            await bot.AnswerInlineQuery(query.Id, [], cancellationToken: ct);
            return;
        }

        logger.LogInformation("Inline query from {User}: {Query}", query.From.Username, searchQuery);

        var results = await youTubeSearch.SearchAsync(searchQuery, maxResults: 8, ct);

        var inlineResults = results.Select(r => new InlineQueryResultArticle(
            id: r.VideoId,
            title: r.Title,
            inputMessageContent: new InputTextMessageContent($"🎵 {r.Title}\n📺 {r.ChannelName}")
        )
        {
            Description = r.ChannelName,
            ThumbnailUrl = r.ThumbnailUrl,
        }).ToList<InlineQueryResult>();

        await bot.AnswerInlineQuery(
            query.Id,
            inlineResults,
            cacheTime: 60, 
            cancellationToken: ct);
    }

    private async Task HandleChosenResultAsync(ITelegramBotClient bot, ChosenInlineResult chosen, CancellationToken ct)
    {
        if (!IsAllowed(chosen.From.Id)) return;

        var videoId = chosen.ResultId;
        var youtubeUrl = $"https://www.youtube.com/watch?v={videoId}";
        var chatId = chosen.From.Id;

        logger.LogInformation("User {User} chose video {VideoId}", chosen.From.Username, videoId);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existingTrack = await db.Tracks.FirstOrDefaultAsync(t => t.YoutubeId == videoId, ct);

        if (existingTrack is not null)
        {
            logger.LogInformation("Track {Id} already exists, sending from storage.", videoId);
            await SendAudioFromStorageAsync(bot, chatId, existingTrack, ct);
        }
        else
        {
            await bot.SendMessage(chatId,
                $"⏳ Скачиваю аудио...\nЭто займёт ~10-30 секунд.",
                cancellationToken: ct);

            var task = new DownloadTask
            {
                Id = Guid.NewGuid(),
                YoutubeUrl = youtubeUrl,
                CreatedAt = DateTime.UtcNow
            };
            db.DownloadTasks.Add(task);
            await db.SaveChangesAsync(ct);

            jobs.Enqueue<TelegramDownloadAndSendJob>(j =>
                j.ExecuteAsync(task.Id, chatId, CancellationToken.None));
        }
    }

    private async Task HandleMessageAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
    {
        if (message.From is null) return;
        if (!IsAllowed(message.From.Id))
        {
            await bot.SendMessage(message.Chat.Id, "⛔ Доступ запрещён.", cancellationToken: ct);
            return;
        }

        if (message.Text == "/start")
        {
            await bot.SendMessage(message.Chat.Id,
                "🎵 *YtAudio Bot*\n\n" +
                "Используй меня inline в любом чате:\n" +
                "`@mybot название трека`\n\n" +
                "Выбери видео из списка — я пришлю аудио.",
                parseMode: ParseMode.Markdown,
                cancellationToken: ct);
        }
    }

    public static async Task SendAudioFromStorageAsync(
        ITelegramBotClient bot, long chatId, Models.Track track, CancellationToken ct)
    {
        await using var stream = File.OpenRead(track.FilePath);
        await bot.SendAudio(
            chatId,
            InputFile.FromStream(stream, $"{track.Title}.{track.FileExtension}"),
            title: track.Title,
            performer: track.Artist,
            cancellationToken: ct);
    }

    private bool IsAllowed(long userId) => _allowedUserIds.Contains(userId);

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        logger.LogError(ex, "Telegram polling error");
        return Task.CompletedTask;
    }
}
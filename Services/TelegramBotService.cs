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

        const string Trigger = "??";
        var raw = query.Query.Trim();

        if (!raw.EndsWith(Trigger, StringComparison.OrdinalIgnoreCase))
        {
            await bot.AnswerInlineQuery(query.Id, [], cancellationToken: ct);
            return;
        }

        var searchQuery = raw[..^Trigger.Length].Trim();
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            await bot.AnswerInlineQuery(query.Id, [], cacheTime: 0, cancellationToken: ct);
            return;
        }

        logger.LogInformation("Inline query from {User}: {Query}", query.From.Username, searchQuery);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ytResults = await youTubeSearch.SearchAsync(searchQuery, maxResults: 8, ct);
        var videoIds = ytResults.Select(r => r.VideoId).ToList();

        var libraryTracks = await db.Tracks
            .Where(t => videoIds.Contains(t.YoutubeId))
            .ToDictionaryAsync(t => t.YoutubeId, ct);

        var inlineResults = new List<InlineQueryResult>();

        foreach (var r in ytResults)
        {
            if (libraryTracks.TryGetValue(r.VideoId, out var track))
            {
                if (track.TelegramFileId is not null)
                {
                    inlineResults.Add(new InlineQueryResultCachedAudio(
                        id: r.VideoId,
                        audioFileId: track.TelegramFileId
                    ));
                }
                else
                {
                    inlineResults.Add(new InlineQueryResultArticle(
                        id: r.VideoId,
                        title: track.Title,
                        inputMessageContent: new InputTextMessageContent(
                            $"⏳ Отправляю из библиотеки: *{track.Title}*")
                        { ParseMode = ParseMode.Markdown }
                    )
                    {
                        Description = $"📚 В библиотеке · {track.Artist ?? r.ChannelName} · Нажми чтобы получить",
                        ThumbnailUrl = track.ThumbnailUrl ?? r.ThumbnailUrl,
                    });
                }
            }
            else
            {
                inlineResults.Add(new InlineQueryResultArticle(
                    id: r.VideoId,
                    title: r.Title,
                    inputMessageContent: new InputTextMessageContent(
                        $"⏳ Скачиваю: *{r.Title}*\nАудио придёт в личных сообщениях бота.")
                    { ParseMode = ParseMode.Markdown }
                )
                {
                    Description = $"🎵 {r.ChannelName} · Нажми чтобы скачать",
                    ThumbnailUrl = r.ThumbnailUrl,
                });
            }
        }

        await bot.AnswerInlineQuery(query.Id, inlineResults, cacheTime: 30, cancellationToken: ct);
    }

    private async Task HandleChosenResultAsync(ITelegramBotClient bot, ChosenInlineResult chosen, CancellationToken ct)
    {
        if (!IsAllowed(chosen.From.Id)) return;

        var videoId = chosen.ResultId;
        var chatId = chosen.From.Id;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.Tracks.FirstOrDefaultAsync(t => t.YoutubeId == videoId, ct);

        if (existing is not null)
        {
            if (existing.TelegramFileId is not null)
            {
                logger.LogInformation("Track {Id} delivered via Telegram inline cache.", videoId);
                return;
            }

            logger.LogInformation("Track {Id} found in library (no TelegramFileId), sending to chat {ChatId}", videoId, chatId);
            try
            {
                var fileId = await SendAudioFromStorageAsync(bot, chatId, existing, ct);
                if (!string.IsNullOrEmpty(fileId))
                {
                    existing.TelegramFileId = fileId;
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("TelegramFileId saved for track {Id}", videoId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send track {Id} from storage to chat {ChatId}", videoId, chatId);
                await bot.SendMessage(chatId,
                    $"❌ Не удалось отправить трек из библиотеки.\n`{ex.Message}`",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: ct);
            }
            return;
        }

        var youtubeUrl = $"https://www.youtube.com/watch?v={videoId}";
        logger.LogInformation("Queuing download for {VideoId} → chat {ChatId}", videoId, chatId);

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

    private async Task HandleMessageAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
    {
        if (message.From is null) return;
        if (!IsAllowed(message.From.Id))
        {
            await bot.SendMessage(message.Chat.Id, "⛔ Доступ запрещён.", cancellationToken: ct);
            return;
        }

        var text = message.Text?.Trim() ?? string.Empty;

        switch (text)
        {
            case "/start":
                await bot.SendMessage(message.Chat.Id,
                    "🎵 *YtAudio Bot*\n\n" +
                    "*Inline-поиск:*\n" +
                    "В любом чате: `@botname название ??`\n\n" +
                    "*Скачать по ссылке:*\n" +
                    "Просто отправь YouTube-ссылку сюда\n\n" +
                    "/status — статистика библиотеки",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: ct);
                return;

            case "/status":
                using (var scope = scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var total = await db.Tracks.CountAsync(ct);
                    var cached = await db.Tracks.CountAsync(t => t.TelegramFileId != null, ct);
                    await bot.SendMessage(message.Chat.Id,
                        $"📚 *Треков в библиотеке:* {total}\n" +
                        $"⚡ *С Telegram-кэшем:* {cached}",
                        parseMode: ParseMode.Markdown,
                        cancellationToken: ct);
                }
                return;
        }

        if (IsYouTubeUrl(text))
        {
            await HandleYouTubeLinkAsync(bot, message.Chat.Id, text, ct);
            return;
        }

        await bot.SendMessage(message.Chat.Id,
            "❓ Не понимаю. Отправь YouTube-ссылку или используй /start.",
            cancellationToken: ct);
    }

    private async Task HandleYouTubeLinkAsync(ITelegramBotClient bot, long chatId, string url, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var videoId = ExtractVideoId(url);
        if (videoId is not null)
        {
            var existing = await db.Tracks.FirstOrDefaultAsync(t => t.YoutubeId == videoId, ct);
            if (existing is not null)
            {
                await bot.SendMessage(chatId,
                    $"📚 *{existing.Title}* уже в библиотеке — отправляю...",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: ct);
                try
                {
                    var fileId = await SendAudioFromStorageAsync(bot, chatId, existing, ct);
                    if (!string.IsNullOrEmpty(fileId) && existing.TelegramFileId is null)
                    {
                        existing.TelegramFileId = fileId;
                        await db.SaveChangesAsync(ct);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send existing track {VideoId}", videoId);
                    await bot.SendMessage(chatId, $"❌ Ошибка при отправке: `{ex.Message}`",
                        parseMode: ParseMode.Markdown, cancellationToken: ct);
                }
                return;
            }
        }

        var task = new DownloadTask
        {
            Id = Guid.NewGuid(),
            YoutubeUrl = url,
            CreatedAt = DateTime.UtcNow
        };
        db.DownloadTasks.Add(task);
        await db.SaveChangesAsync(ct);

        jobs.Enqueue<TelegramDownloadAndSendJob>(j =>
            j.ExecuteAsync(task.Id, chatId, CancellationToken.None));

        await bot.SendMessage(chatId,
            "⏳ Поставил на скачивание. Аудио придёт сюда как только будет готово.",
            cancellationToken: ct);
    }

    public static async Task<string> SendAudioFromStorageAsync(
        ITelegramBotClient bot, long chatId, Models.Track track, CancellationToken ct)
    {
        await using var stream = File.OpenRead(track.FilePath);

        Message msg;

        if (!string.IsNullOrEmpty(track.ThumbnailUrl))
        {
            try
            {
                msg = await bot.SendAudio(
                    chatId,
                    InputFile.FromStream(stream, $"{track.Title}.{track.FileExtension}"),
                    title: track.Title,
                    performer: track.Artist,
                    thumbnail: InputFile.FromUri(new Uri(track.ThumbnailUrl)),
                    cancellationToken: ct);
                return msg.Audio?.FileId ?? string.Empty;
            }
            catch
            {
                stream.Seek(0, SeekOrigin.Begin);
            }
        }

        msg = await bot.SendAudio(
            chatId,
            InputFile.FromStream(stream, $"{track.Title}.{track.FileExtension}"),
            title: track.Title,
            performer: track.Artist,
            cancellationToken: ct);

        return msg.Audio?.FileId ?? string.Empty;
    }

    private static bool IsYouTubeUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
        (uri.Host.Contains("youtube.com") || uri.Host is "youtu.be" or "www.youtu.be");

    private static string? ExtractVideoId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        if (uri.Host is "youtu.be" or "www.youtu.be")
            return uri.AbsolutePath.TrimStart('/').Split('/')[0];

        if (uri.AbsolutePath.StartsWith("/shorts/"))
            return uri.AbsolutePath.Split('/').LastOrDefault(s => s.Length > 0);

        foreach (var param in uri.Query.TrimStart('?').Split('&'))
        {
            var kv = param.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "v")
                return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    private bool IsAllowed(long userId) => _allowedUserIds.Contains(userId);

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        logger.LogError(ex, "Telegram polling error");
        return Task.CompletedTask;
    }
}
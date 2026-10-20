using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudLauncher.Server.Auth;
using CloudLauncher.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Discord;

/// <summary>
/// Keeps every linked Discord channel showing its pack's planning boards: one message per board,
/// edited in place when that board changes, deleted when the board goes.
/// </summary>
/// <remarks>
/// <para>Polls rather than listening to <c>PlanChangeSignals</c>: the stored revision is the truth, so
/// a restart, a missed signal or a Discord outage all end in the same catch-up, and a board is posted
/// only once it has been left alone for <see cref="DiscordOptions.QuietSeconds"/> (a drag is one edit,
/// not twenty).</para>
/// <para>A board whose content hash hasn't moved keeps its message untouched, so editing one board
/// doesn't re-upload the others' pictures. The state saved after every message means a crash
/// part-way never posts a board twice.</para>
/// </remarks>
public sealed class DiscordPlanPublisher(
    IServiceScopeFactory scopes,
    DiscordRest discord,
    PlanAssets assets,
    DiscordOptions options,
    AppOptions app,
    ILogger<DiscordPlanPublisher> log) : BackgroundService
{
    /// <summary>Bumped when the picture or message changes shape, so every board is redrawn once.</summary>
    private const string RenderVersion = "2";

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _backoff = new();

    /// <summary>Look again now (a link was made or a refresh asked for), not at the next tick.</summary>
    public void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* already woken */ }
    }

    public static string ViewUrl(AppOptions app, PackDiscordLink link) =>
        $"{app.PublicBaseUrl.TrimEnd('/')}/plans/view/{ViewToken(link)}";

    public static string ViewToken(PackDiscordLink link) => $"{link.Id:N}.{link.ViewKey}";

    public static string NewViewKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.PublisherEnabled)
        {
            log.LogInformation("Discord board publisher is off (Discord:PublisherEnabled=false)");
            return;
        }
        try { await Task.Delay(TimeSpan.FromSeconds(10), stop); }
        catch (OperationCanceledException) { return; }

        while (!stop.IsCancellationRequested)
        {
            try
            {
                await PassAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Discord board pass failed");
            }
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(4), stop); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        var links = await (
            from l in db.PackDiscordLinks
            where !l.Broken && (l.WebhookId != null || l.ChannelId != null)
            join p in db.PackPlans on l.PackId equals p.PackId into plans
            from p in plans.DefaultIfEmpty()
            select new { Link = l, Revision = p == null ? 0 : p.Revision, UpdatedAt = p == null ? (DateTimeOffset?)null : p.UpdatedAt }
        ).ToListAsync(ct);

        foreach (var x in links)
        {
            var link = x.Link;
            if (_backoff.TryGetValue(link.Id, out var until) && until > now && !link.RefreshRequested) continue;
            if (x.Revision == link.PublishedRevision && !link.RefreshRequested) continue;
            if (!link.RefreshRequested)
            {
                if (x.UpdatedAt is { } changed && now - changed < TimeSpan.FromSeconds(options.QuietSeconds)) continue;
                if (link.PublishedAt is { } last && now - last < TimeSpan.FromSeconds(options.MinIntervalSeconds)) continue;
            }

            try
            {
                await PublishAsync(db, link, ct);
                _backoff.TryRemove(link.Id, out _);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Unlinked while its boards were being posted: whatever went up is the unlink's to take
                // down, and this pass has nothing more to do for it.
                log.LogInformation("Discord link {Link} was removed during an update", link.Id);
                db.ChangeTracker.Clear();
            }
            catch (DiscordApiException e) when (e.TargetGone)
            {
                link.Broken = true;
                link.RefreshRequested = false;
                Fail(link, link.Mode == "webhook"
                    ? "The webhook was deleted, or no longer works. Link the channel again with a new one."
                    : "CloudLauncher can no longer post in that channel (it was deleted, or the app lost access).");
                log.LogInformation("Discord link {Link} broken: {Message}", link.Id, e.Message);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception e) when (e is DiscordApiException or HttpRequestException or TaskCanceledException
                                          && !ct.IsCancellationRequested)
            {
                var wait = e is DiscordApiException { RetryAfter: { } r } ? r : TimeSpan.FromMinutes(1);
                _backoff[link.Id] = DateTimeOffset.UtcNow + wait;
                link.RefreshRequested = false;
                Fail(link, "Discord didn't take the update; trying again shortly. " + Short(e.Message));
                log.LogInformation("Discord link {Link} update failed, retrying in {Wait}: {Message}", link.Id, wait, e.Message);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
    }

    private async Task PublishAsync(AppDbContext db, PackDiscordLink link, CancellationToken ct)
    {
        var plan = await db.PackPlans.AsNoTracking().FirstOrDefaultAsync(p => p.PackId == link.PackId, ct);
        var packName = await db.Packs.AsNoTracking().Where(p => p.Id == link.PackId).Select(p => p.Name)
            .FirstOrDefaultAsync(ct) ?? "Modpack";
        var doc = plan is null ? null : PlanDocData.TryParse(plan.Json);
        var boards = (doc?.Boards ?? new List<PlanBoardData>())
            .Where(b => link.BoardId is null || b.Id == link.BoardId)
            .Where(b => !b.IsEmpty)
            .ToList();

        var state = PostedBoard.Read(link.MessagesJson);
        var viewUrl = ViewUrl(app, link);
        var activity = link.Mode == "bot" && options.ActivityEnabled && options.BotConfigured;
        var labels = await assets.ModLabelsAsync(boards, ct);
        var salt = $"{RenderVersion}|{packName}|{viewUrl}|{activity}";
        var changedAt = plan?.UpdatedAt ?? DateTimeOffset.UtcNow;

        foreach (var board in boards)
        {
            var hash = board.ContentHash(salt);
            var entry = state.FirstOrDefault(e => e.B == board.Id);
            if (entry is not null && entry.H == hash && !link.RefreshRequested) continue;

            // A redraw of an unchanged board keeps the time it really changed.
            var when = entry is not null && entry.H == hash ? entry.T : changedAt;
            var (message, files) = await BuildAsync(board, packName, when, hash, viewUrl, activity, link, labels, ct);

            if (entry is null)
            {
                var id = await PostAsync(link, message, files, ct);
                state.Add(new PostedBoard { B = board.Id, M = id, H = hash, T = when });
                log.LogInformation("Discord link {Link}: posted board {Board}", link.Id, board.Name);
            }
            else
            {
                try
                {
                    await EditAsync(link, entry.M, PlanMessageBuilder.ForEdit(message.DeepClone().AsObject()), files, ct);
                    log.LogInformation("Discord link {Link}: updated board {Board}", link.Id, board.Name);
                }
                catch (DiscordApiException e) when (e.MessageGone)
                {
                    entry.M = await PostAsync(link, message, files, ct);
                    log.LogInformation("Discord link {Link}: posted board {Board} again (its message was deleted)", link.Id, board.Name);
                }
                entry.H = hash;
                entry.T = when;
            }
            link.MessagesJson = PostedBoard.Write(state);
            await db.SaveChangesAsync(ct);
        }

        foreach (var gone in state.Where(e => boards.All(b => b.Id != e.B)).ToList())
        {
            try { await DeleteAsync(link, gone.M, ct); }
            catch (DiscordApiException e) when (e.Status == 404) { /* already gone */ }
            state.Remove(gone);
            log.LogInformation("Discord link {Link}: took down the message of a board that is gone or empty", link.Id);
        }

        link.MessagesJson = PostedBoard.Write(state);
        link.PublishedRevision = plan?.Revision ?? 0;
        link.PublishedAt = DateTimeOffset.UtcNow;
        link.RefreshRequested = false;
        link.LastError = boards.Count == 0
            ? (plan is null
                ? "This pack has no planning boards on the server yet. Open its planning page in CloudLauncher 0.9.8 or newer."
                : "Every board this channel shows is empty, so there is nothing to post yet.")
            : null;
        link.LastErrorAt = link.LastError is null ? null : DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task<(JsonObject Message, DiscordFile[] Files)> BuildAsync(PlanBoardData board, string packName,
        DateTimeOffset changedAt, string hash, string viewUrl, bool activity, PackDiscordLink link,
        IReadOnlyDictionary<string, ModLabel> labels, CancellationToken ct)
    {
        var drawing = PlanBoardDrawing.Layout(board, labels);
        var icons = await assets.IconsAsync(drawing.IconUrls, ct);
        var cards = board.Nodes.Count(n => n.Kind != PlanNodeData.KindSection);
        var subtitle = $"{packName}  ·  {cards} card{(cards == 1 ? "" : "s")}  ·  updated "
                       + changedAt.UtcDateTime.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture) + " UTC";
        var png = drawing.Png(board.Name, subtitle, icons);
        var imageName = PlanMessageBuilder.ImageName(board, hash);
        var message = PlanMessageBuilder.Board(board, packName, changedAt, imageName, viewUrl, activity,
            asWebhook: link.Mode == "webhook");
        return (message, [new DiscordFile(imageName, png)]);
    }

    private Task<string> PostAsync(PackDiscordLink link, JsonObject message, DiscordFile[] files, CancellationToken ct) =>
        link.Mode == "webhook"
            ? discord.ExecuteWebhookAsync(link.WebhookId!, link.WebhookToken!, message, files, ct)
            : discord.CreateMessageAsync(link.ChannelId!, message, files, ct);

    private Task EditAsync(PackDiscordLink link, string messageId, JsonObject message, DiscordFile[] files, CancellationToken ct) =>
        link.Mode == "webhook"
            ? discord.EditWebhookMessageAsync(link.WebhookId!, link.WebhookToken!, messageId, message, files, ct)
            : discord.EditMessageAsync(link.ChannelId!, messageId, message, files, ct);

    private Task DeleteAsync(PackDiscordLink link, string messageId, CancellationToken ct) =>
        link.Mode == "webhook"
            ? discord.DeleteWebhookMessageAsync(link.WebhookId!, link.WebhookToken!, messageId, ct)
            : discord.DeleteMessageAsync(link.ChannelId!, messageId, ct);

    /// <summary>Takes a link's messages down (it is being unlinked). Best effort: a message someone
    /// already deleted, or a channel that is gone, is simply skipped.</summary>
    public async Task<int> RemoveMessagesAsync(PackDiscordLink link, CancellationToken ct)
    {
        var removed = 0;
        foreach (var entry in PostedBoard.Read(link.MessagesJson))
        {
            try
            {
                await DeleteAsync(link, entry.M, ct);
                removed++;
            }
            catch (Exception e) when (e is DiscordApiException or HttpRequestException or TaskCanceledException)
            {
                log.LogInformation("Message {Message} of Discord link {Link} not deleted: {Error}", entry.M, link.Id, e.Message);
            }
        }
        return removed;
    }

    private static void Fail(PackDiscordLink link, string message)
    {
        link.LastError = message.Length > 500 ? message[..500] : message;
        link.LastErrorAt = DateTimeOffset.UtcNow;
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] + "..." : s;
}

/// <summary>One board's message in a linked channel: board id, message id, the hash of what it
/// shows and when that last changed. Stored as JSON on the link.</summary>
internal sealed class PostedBoard
{
    public string B { get; set; } = "";
    public string M { get; set; } = "";
    public string H { get; set; } = "";
    public DateTimeOffset T { get; set; }

    public static List<PostedBoard> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<PostedBoard>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static string Write(List<PostedBoard> boards) => JsonSerializer.Serialize(boards);
}

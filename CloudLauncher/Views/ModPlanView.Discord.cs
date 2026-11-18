using System.Windows;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

// The board's Discord tick. While one of the pack's Discord channels shows the open board, a "Discord"
// box sits beside its name at the top: untick it and the server keeps that board out of every channel,
// and out of the live view and the Activity their messages open (PlanDocData.ShownIn on the server).
// The tick is the board's ShowInDiscord, so it saves and syncs with the boards the way their names do,
// and like a name it isn't a step Ctrl+Z goes back through: undoing a card move must never put a board
// back in Discord. Which channels there are comes from the server, the same list Tools > Show in Discord
// manages, looked up when the page opens and again after that card closes.
public partial class ModPlanView
{
    /// <summary>The channels each pack's boards were last seen in, so reopening the page puts the tick up
    /// at once while it looks again.</summary>
    private static readonly Dictionary<Guid, (IReadOnlyList<PackDiscordLinkDto> Links, DateTimeOffset At)> SeenDiscordLinks = new();

    /// <summary>How long one look at a pack's channels holds before opening the page looks again.</summary>
    private static readonly TimeSpan DiscordLinksFresh = TimeSpan.FromSeconds(30);

    /// <summary>The open pack's Discord channels; null while unknown (the pack isn't on the server, or
    /// the server hasn't said yet).</summary>
    private IReadOnlyList<PackDiscordLinkDto>? _discordLinks;

    /// <summary>The pack <see cref="_discordLinks"/> belongs to.</summary>
    private Guid _discordLinksPack;

    private bool _discordLinksLoading, _discordLinksAgain;

    /// <summary>Whether one of the pack's channels shows this board while it's ticked: a channel showing
    /// every board, or one showing just this one.</summary>
    private bool InDiscordChannel(PlanBoard board) =>
        _discordLinksPack == _packId && _discordLinks is { Count: > 0 } links
        && links.Any(l => l.BoardId is null || l.BoardId == board.Id);

    /// <summary>Asks the server which Discord channels show the pack's boards, and puts the tick up or
    /// away to match. Never throws: without an answer the page simply carries on without the tick.</summary>
    /// <param name="force">Ask even when the last answer is recent (after the Show in Discord card, which
    /// may have linked or removed a channel).</param>
    private async Task LoadDiscordLinksAsync(bool force = false)
    {
        var packId = _packId;
        if (packId == Guid.Empty) return;
        var api = App.State.Api;
        if (!api.IsSignedIn || api.IsLocalPack(packId))
        {
            ShowDiscordLinks(packId, null);
            return;
        }
        if (SeenDiscordLinks.TryGetValue(packId, out var seen))
        {
            ShowDiscordLinks(packId, seen.Links);
            if (!force && DateTimeOffset.UtcNow - seen.At < DiscordLinksFresh) return;
        }
        if (_discordLinksLoading)
        {
            _discordLinksAgain |= force;
            return;
        }

        _discordLinksLoading = true;
        try
        {
            // Null from a server older than the Discord links: there are none to show the boards in.
            var info = await api.GetPackDiscordAsync(packId);
            IReadOnlyList<PackDiscordLinkDto> links = info?.Links ?? [];
            SeenDiscordLinks[packId] = (links, DateTimeOffset.UtcNow);
            ShowDiscordLinks(packId, links);
        }
        catch (Exception ex)
        {
            AppLog.Log("plans", $"Discord channels of {packId:N} not looked up: {ex.Message}");
        }
        finally
        {
            _discordLinksLoading = false;
        }
        if (_discordLinksAgain)
        {
            _discordLinksAgain = false;
            await LoadDiscordLinksAsync(force: true);
        }
    }

    private void ShowDiscordLinks(Guid packId, IReadOnlyList<PackDiscordLinkDto>? links)
    {
        if (packId != _packId) return; // answered for a pack that is no longer open
        var before = _discordLinksPack == packId ? Coverage(_discordLinks) : "";
        _discordLinks = links;
        _discordLinksPack = packId;
        // The rail marks the boards kept out of Discord, so it is redrawn when the boards a channel shows
        // change; the redraw puts the tick up too.
        if (Coverage(links) != before) RefreshBoardRail();
        else UpdateDiscordToggle();
    }

    /// <summary>Which boards the channels show, as a key: "*" for a channel showing all of them.</summary>
    private static string Coverage(IReadOnlyList<PackDiscordLinkDto>? links) =>
        links is null ? "" : string.Join(",", links.Select(l => l.BoardId ?? "*").Order(StringComparer.Ordinal));

    /// <summary>Only someone whose changes reach the server can change what Discord shows: a view-only
    /// account's tick would stay on its own PC.</summary>
    private bool CanChangeDiscordTick =>
        App.State.PlanSync.StateOf(_packId) != PlanSyncState.ViewOnly
        && (App.State.Api.PeekPackDetail(_packId)?.EffectivePermissions.HasFlag(PackPermissions.UploadShared) ?? true);

    /// <summary>Puts the tick up for the open board while a channel would show it, and away otherwise.</summary>
    /// <remarks>Guarded, like the rail: it is decoration, and must never take the board down with it.</remarks>
    private void UpdateDiscordToggle()
    {
        try
        {
            var board = _board;
            if (board is null || !InDiscordChannel(board))
            {
                DiscordToggle.Visibility = Visibility.Collapsed;
                return;
            }
            DiscordToggle.Visibility = Visibility.Visible;
            DiscordToggle.IsChecked = board.ShowInDiscord;
            var canChange = CanChangeDiscordTick;
            DiscordToggle.IsEnabled = canChange;
            var where = ChannelNames(board);
            DiscordToggle.ToolTip = Tip(!canChange
                ? $"{(board.ShowInDiscord ? "Shown in" : "Kept out of")} {where}. Only the pack's editors can change that."
                : board.ShowInDiscord
                    ? $"Shown in {where}. Untick to keep this board out of Discord: it comes down from there, "
                      + "and the live view the messages link to stops showing it."
                    : $"Kept out of {where}. Tick to show this board there again.");
        }
        catch (Exception ex) { AppLog.LogError(nameof(ModPlanView), ex); }
    }

    /// <summary>The channels that would show this board, for the tick's tooltip: "#planning",
    /// "#planning and a Discord channel", "3 Discord channels". A webhook link is called by the
    /// webhook's name, which says nothing about the channel, so only "#name" labels are used.</summary>
    private string ChannelNames(PlanBoard board)
    {
        var names = (_discordLinks ?? [])
            .Where(l => l.BoardId is null || l.BoardId == board.Id)
            .Select(l => l.Label is { Length: > 1 } label && label[0] == '#' ? label : "a Discord channel")
            .ToList();
        return names.Count switch
        {
            0 => "Discord",
            1 => names[0],
            2 when names[0] != names[1] => $"{names[0]} and {names[1]}",
            _ => $"{names.Count} Discord channels"
        };
    }

    private void OnDiscordToggled(object sender, RoutedEventArgs e)
    {
        if (_board is null) return;
        var show = DiscordToggle.IsChecked == true;
        if (_board.ShowInDiscord == show) return;
        _board.ShowInDiscord = show;
        Save(); // saved and synced, but no undo step (see the top of this file)
        RefreshBoardRail();

        // Short, for a narrow toolbar; the tooltip has the rest.
        var offline = App.State.PlanSync.StateOf(_packId) == PlanSyncState.Offline;
        PlanStatus.Text = show ? "Shown in Discord again." : "Kept out of Discord.";
        PlanStatus.ToolTip = Tip((show
            ? "The board goes back up in the pack's Discord channels"
            : "The board comes down from the pack's Discord channels, and the live view their messages link to stops showing it")
            + (offline ? " once the server can be reached." : " within half a minute."));
    }
}

using System.IO;
using CloudLauncher.Services;

namespace CloudLauncher.Views;

/// <summary>What a shared item's two buttons do, for every family: open its page, and manage who
/// can reach it. The Sharing page's Overview rows and its Share menu both go through here.</summary>
/// <remarks>Every family opens in the side panel like any other item page.</remarks>
public static class SharingActions
{
    /// <summary>Opens the thing's own page.</summary>
    public static void Open(MainWindow shell, SharingRow row) => Open(shell, row.Family, row.Id, row.Name);

    /// <inheritdoc cref="Open(MainWindow, SharingRow)"/>
    public static void Open(MainWindow shell, SharedFamily family, Guid id, string name)
    {
        switch (family)
        {
            case SharedFamily.Mod:
                shell.OpenModDetail(id, name);
                break;
            case SharedFamily.ResourcePack:
                shell.OpenResourcePackDetail(id, name);
                break;
            case SharedFamily.Bundle:
                shell.OpenBundleDetail(id, name);
                break;
            case SharedFamily.World:
                OpenWorld(shell, id, name);
                break;
            default:
                shell.OpenPackDetail(id, name);
                break;
        }
    }

    /// <summary>A hosted world opens as the save it came from when this PC has it (that page has
    /// its versions, backups and people), otherwise on its entry in Download a world.</summary>
    private static void OpenWorld(MainWindow shell, Guid id, string name)
    {
        var linked = App.State.Settings.Worlds
            .Where(kv => kv.Value.SharedWorldId == id && SaveExists(kv.Key))
            .Select(kv => kv.Key)
            .FirstOrDefault();
        if (linked is not null) shell.OpenWorldDetail(linked, name);
        else shell.OpenHostedWorld(id, name);
    }

    /// <summary>True when a world key ("{instance}:{folder}") still names a save on disk. Settings
    /// can outlive the saves they describe.</summary>
    private static bool SaveExists(string key)
    {
        try
        {
            var parts = key.Split(':', 2);
            if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var packId)) return false;
            return WorldService.IsWorldFolder(Path.Combine(App.State.Worlds.SavesDir(packId), parts[1]));
        }
        catch { return false; }
    }

    /// <summary>Opens access management: an instance's Share &amp; sync tab, the people dialog for
    /// a hosted mod, world or resource pack, or a bundle's own page (its people, invitations and
    /// share link are all there).</summary>
    /// <returns>True when a dialog closed and the list may be stale; false when this navigated away
    /// and the page will refresh when shown again.</returns>
    public static async Task<bool> ManageAccessAsync(MainWindow shell, SharingRow row, SharingSnapshot? snapshot)
    {
        switch (row.Family)
        {
            case SharedFamily.Mod:
            {
                var detail = await App.State.Api.GetModAsync(row.Id);
                new PermissionsDialog(detail) { Owner = shell }.ShowDialog();
                return true;
            }
            case SharedFamily.World:
            {
                var detail = await App.State.Api.GetSharedWorldAsync(row.Id);
                new PermissionsDialog(detail) { Owner = shell }.ShowDialog();
                return true;
            }
            case SharedFamily.ResourcePack:
            {
                var detail = await App.State.Api.GetResourcePackAsync(row.Id);
                new PermissionsDialog(detail) { Owner = shell }.ShowDialog();
                return true;
            }
            case SharedFamily.Bundle:
                shell.OpenBundleDetail(row.Id, row.Name);
                return false;
            default:
                // The Share & sync tab needs the instance detail, which the snapshot has for every
                // instance this account can manage. Otherwise open the instance's own page, which
                // can fetch it.
                if (snapshot?.Details.TryGetValue(row.Id, out var pack) == true)
                    FileManagementView.OpenOn(shell, pack, FileManagementTab.Share);
                else
                    shell.OpenPackDetail(row.Id, row.Name);
                return false;
        }
    }
}

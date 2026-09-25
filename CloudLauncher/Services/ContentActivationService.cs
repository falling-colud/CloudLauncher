using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>What one instance's activation pass actually switched on and off.</summary>
/// <param name="Enabled">Resource pack file names added to <c>options.txt</c>.</param>
/// <param name="Disabled">Resource pack file names taken back out (only ones this engine
/// added).</param>
/// <param name="ShaderActivated">The shader pack the instance was pointed at, or null.</param>
/// <param name="ShaderCleared">True when a shader this engine had set was switched back off.</param>
/// <param name="Notes">Sentences for the user: a refusal to displace their shader, a missing loader,
/// a write that failed.</param>
/// <param name="Refused">True when nothing was attempted because the game is running in this
/// instance.</param>
public sealed record ContentActivationResult(
    IReadOnlyList<string> Enabled,
    IReadOnlyList<string> Disabled,
    string? ShaderActivated,
    bool ShaderCleared,
    IReadOnlyList<string> Notes,
    bool Refused = false)
{
    public static readonly ContentActivationResult Nothing = new([], [], null, false, []);

    public static ContentActivationResult RefusedBecause(string why) =>
        new([], [], null, false, [why], Refused: true);

    public bool DidAnything => Enabled.Count > 0 || Disabled.Count > 0 || ShaderActivated is not null || ShaderCleared;

    /// <summary>One status-bar clause, in the same shape as <c>LibraryApplyResult.Summary</c>.</summary>
    public string Summary()
    {
        if (Refused) return Notes.Count > 0 ? Notes[0] : "Nothing was switched on - the game is running.";
        var parts = new List<string>();
        if (Enabled.Count > 0) parts.Add($"{Enabled.Count} pack(s) switched on");
        if (Disabled.Count > 0) parts.Add($"{Disabled.Count} switched off");
        if (ShaderActivated is { } s) parts.Add($"shader set to {ShaderPackService.PrettyName(s)}");
        if (ShaderCleared) parts.Add("shader switched off");
        if (Notes.Count > 0) parts.Add(Notes[0]);
        return parts.Count == 0 ? "Nothing needed switching on." : string.Join("  ·  ", parts);
    }
}

/// <summary>Turns default content on: the ordered <c>resourcePacks</c> line in <c>options.txt</c>,
/// and the shader loader's <c>shaderPack=</c>.</summary>
/// <remarks>
/// <para><see cref="ContentLibraryService"/> only places files and never touches load order
/// (re-applying must not reshuffle stacks), so every <c>options.txt</c> and <c>iris.properties</c>
/// write for the defaults engine is here.</para>
/// <para>Defaults are inserted at the top of the stack, as Minecraft does for a pack you enable.
/// Existing entries keep their order, a default already in the line is never moved, and only names
/// this engine inserted are ever removed.</para>
/// <para>The Minecraft version is passed down because <see cref="ResourcePackService.SetActive"/>
/// spells entries <c>file/Name.zip</c> on 1.13+ and <c>Name.zip</c> before that, and an empty stack
/// has nothing to copy the spelling from. The wrong spelling turns every pack off.</para>
/// <para>Only one shader can be active. <see cref="ContentDefaultsService.Plan"/> picks one winner
/// and reports the rest as <see cref="ContentStepKind.BlockedShaderConflict"/>, and a shader the
/// user chose is only replaced by an item that is <see cref="ContentDefaultChoice.Forced"/> in that
/// instance.</para>
/// <para>Never writes under a running game: Minecraft rewrites <c>options.txt</c> from memory on
/// exit, so the change would be lost.</para>
/// </remarks>
public sealed class ContentActivationService(
    ResourcePackService resourcePacks,
    ShaderPackService shaders,
    MinecraftInstanceService instances)
{
    /// <summary>Runs the <see cref="ContentStepKind.Activate"/> and
    /// <see cref="ContentStepKind.Deactivate"/> steps of a plan against one instance.</summary>
    /// <param name="overrides">Updated in place with what was switched on and off. The caller saves it,
    /// once, after the world and bundle records are in too.</param>
    /// <param name="duringLaunch">True on the launch path, where the instance is "Launching" but the
    /// game hasn't read anything yet. See <see cref="ContentWriteGate.Allows"/>.</param>
    public ContentActivationResult Apply(
        PackSummary instance, IReadOnlyList<ContentDefaultStep> steps,
        InstanceContentOverrides overrides, IProgress<string>? log = null, bool duringLaunch = false)
    {
        if (!ContentWriteGate.Allows(instances, instance.Id, duringLaunch, out var blocked))
            return ContentActivationResult.RefusedBecause(blocked);

        var notes = new List<string>();
        var enabled = new List<string>();
        var disabled = new List<string>();
        string? shaderSet = null;
        var shaderCleared = false;

        void Report(string m) { log?.Report(m); AppLog.Log("content-defaults", $"{instance.Name}: {m}"); }

        // ── resource packs: one read, one write ──
        var wantOn = steps
            .Where(s => s.Kind == ContentStepKind.Activate && s.Item.Kind == LibraryKind.ResourcePack)
            .OrderByDescending(s => s.Item.Defaults?.Order ?? 0)
            .ThenBy(s => s.Item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var wantOff = steps
            .Where(s => s.Kind == ContentStepKind.Deactivate && s.Item.Kind == LibraryKind.ResourcePack)
            .ToList();

        if (wantOn.Count > 0 || wantOff.Count > 0)
        {
            try
            {
                var stack = resourcePacks.ActiveFor(instance.Id, instance.Name);   // highest priority first
                var changed = false;

                // Insert as a block at the top, highest Order first. A running index (not
                // repeated inserts at 0) keeps the block in order and leaves the user's own
                // entries in their relative order.
                var at = 0;
                foreach (var step in wantOn)
                {
                    var name = step.Item.FileName;
                    if (stack.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) continue;
                    stack.Insert(Math.Min(at++, stack.Count), name);
                    enabled.Add(name);
                    changed = true;
                }

                foreach (var step in wantOff)
                {
                    var name = step.Item.FileName;
                    // The plan only deactivates names we recorded, but check anyway: a pack the user
                    // enabled must survive a default falling out of compatibility.
                    if (!overrides.DidActivate(name)) continue;
                    if (stack.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) == 0) continue;
                    disabled.Add(name);
                    changed = true;
                }

                if (changed)
                {
                    resourcePacks.SetActive(instance.Id, instance.Name, stack, instance.MinecraftVersion);
                    Report($"resource pack stack: +{enabled.Count} / -{disabled.Count}");
                }
            }
            catch (Exception ex)
            {
                AppLog.LogError("content-defaults", ex);
                notes.Add($"{instance.Name}: the resource pack list could not be written - {ex.Message}");
                enabled.Clear();
                disabled.Clear();
            }
        }

        foreach (var name in enabled)
            if (!overrides.DidActivate(name)) overrides.ActivatedByDefaults.Add(name);
        foreach (var name in disabled)
            overrides.ActivatedByDefaults.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

        // ── the one shader ──
        var shaderOn = steps.FirstOrDefault(s => s.Kind == ContentStepKind.Activate && s.Item.Kind == LibraryKind.ShaderPack);
        if (shaderOn is not null)
        {
            if (TrySetShader(instance, shaderOn.Item.FileName, overrides.IsForced(shaderOn.Item.Key), overrides, out var why))
            {
                shaderSet = shaderOn.Item.FileName;
                Report($"shader set to {shaderOn.Item.FileName}");
            }
            else if (why.Length > 0) notes.Add(why);
        }
        else if (steps.Any(s => s.Kind == ContentStepKind.Deactivate && s.Item.Kind == LibraryKind.ShaderPack))
        {
            shaderCleared = ClearShader(instance, overrides);
            if (shaderCleared) Report("shader switched off");
        }

        return new ContentActivationResult(enabled, disabled, shaderSet, shaderCleared, notes);
    }

    /// <inheritdoc cref="Apply"/>
    public Task<ContentActivationResult> ApplyAsync(
        PackSummary instance, IReadOnlyList<ContentDefaultStep> steps, InstanceContentOverrides overrides,
        IProgress<string>? log = null, bool duringLaunch = false, CancellationToken ct = default) =>
        Task.Run(() => Apply(instance, steps, overrides, log, duringLaunch), ct);

    // ── the single-item operations, for a page's own buttons ──────────────────

    /// <summary>Adds one pack to an instance's stack at the position <paramref name="order"/> implies,
    /// and records that this engine did it. Returns false when it was already on.</summary>
    /// <remarks>The caller saves <paramref name="overrides"/>.</remarks>
    public bool ActivateResourcePack(PackSummary instance, string fileName, int order, InstanceContentOverrides overrides)
    {
        var stack = resourcePacks.ActiveFor(instance.Id, instance.Name);
        if (stack.Any(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase))) return false;

        // A negative order means the bottom of the stack: present, but everything else wins.
        if (order < 0) stack.Add(fileName);
        else stack.Insert(0, fileName);
        resourcePacks.SetActive(instance.Id, instance.Name, stack, instance.MinecraftVersion);
        if (!overrides.DidActivate(fileName)) overrides.ActivatedByDefaults.Add(fileName);
        return true;
    }

    /// <summary>Takes one pack back out of an instance's stack, only if this engine put it there.
    /// Returns false when it wasn't ours or wasn't on.</summary>
    /// <remarks>A pack the user enabled themselves must survive a default falling out of
    /// compatibility. Same rule as <see cref="LowModeService"/>, which only rolls back keys still
    /// at the value it wrote.</remarks>
    public bool DeactivateResourcePack(PackSummary instance, string fileName, InstanceContentOverrides overrides)
    {
        if (!overrides.DidActivate(fileName)) return false;

        var stack = resourcePacks.ActiveFor(instance.Id, instance.Name);
        if (stack.RemoveAll(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            // Gone from the line already (the user removed it): stop claiming authorship so we never
            // re-add it on a later pass.
            overrides.ActivatedByDefaults.RemoveAll(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase));
            return false;
        }
        resourcePacks.SetActive(instance.Id, instance.Name, stack, instance.MinecraftVersion);
        overrides.ActivatedByDefaults.RemoveAll(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>Points the instance's shader loader at <paramref name="fileName"/>.</summary>
    /// <param name="force">Allowed to displace a shader the user chose. Only true for an item the
    /// instance marked <see cref="ContentDefaultChoice.Forced"/>.</param>
    /// <param name="why">Empty on success and on "already active"; otherwise the sentence to show.</param>
    /// <remarks>Refuses when the instance has no shader loader, since the config would be written but
    /// never read (see <see cref="ShaderPackService.SetActiveShader"/>).</remarks>
    public bool TrySetShader(PackSummary instance, string fileName, bool force, InstanceContentOverrides overrides, out string why)
    {
        why = "";
        try
        {
            if (shaders.DetectLoader(instance.Id, instance.Name) == ShaderLoader.None)
            {
                why = $"{instance.Name} has no Iris, Oculus or OptiFine, so a shader pack cannot be switched on there.";
                return false;
            }

            var current = shaders.ActiveShader(instance.Id, instance.Name);
            if (string.Equals(current, fileName, StringComparison.OrdinalIgnoreCase)) return false;

            // A shader that is on and is not one we set is the user's choice. Only an item they
            // explicitly forced into this instance may take its place.
            var oursAlready = current is not null && overrides.ActivatedShader is { } ours
                              && string.Equals(current, ours, StringComparison.OrdinalIgnoreCase);
            if (current is { Length: > 0 } && !oursAlready && !force)
            {
                why = $"{instance.Name} is already using {ShaderPackService.PrettyName(current)}, which you chose - "
                    + "this default was left off. Force it for this instance to override that.";
                return false;
            }

            shaders.SetActiveShader(instance.Id, fileName);
            overrides.ActivatedShader = fileName;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.LogError("content-defaults", ex);
            why = $"{instance.Name}: the shader could not be switched on - {ex.Message}";
            return false;
        }
    }

    /// <summary>Switches shaders off, but only when the active one is the one this engine set.</summary>
    public bool ClearShader(PackSummary instance, InstanceContentOverrides overrides)
    {
        if (overrides.ActivatedShader is not { Length: > 0 } ours) return false;
        try
        {
            var current = shaders.ActiveShader(instance.Id, instance.Name);
            overrides.ActivatedShader = null;
            if (!string.Equals(current, ours, StringComparison.OrdinalIgnoreCase)) return false;
            shaders.SetActiveShader(instance.Id, null);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.LogError("content-defaults", ex);
            return false;
        }
    }
}

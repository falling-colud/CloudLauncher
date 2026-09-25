using System.IO;
using System.IO.Compression;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>What one instance's reconcile actually did.</summary>
/// <param name="Placed">The result of the one <see cref="ContentLibraryService.ApplyAsync"/> call, or
/// null when there was nothing to place.</param>
/// <param name="Removed">The result of the one <see cref="ContentLibraryService.UnapplyAsync"/> call,
/// or null when nothing fell out of compatibility.</param>
/// <param name="Blocked">Sentences for the things that were stopped, ready to show as-is.</param>
/// <param name="Refused">True when nothing was attempted because the game is running there.</param>
public sealed record ContentReconcileResult(
    Guid PackId,
    string PackName,
    LibraryApplyResult? Placed,
    LibraryApplyResult? Removed,
    int WorldsCopied,
    int BundlesUnpacked,
    int BundleFilesWritten,
    int BundlesRemoved,
    ContentActivationResult Activation,
    IReadOnlyList<string> Blocked,
    IReadOnlyList<string> Failures,
    bool Refused = false)
{
    public static ContentReconcileResult Nothing(PackSummary instance) =>
        new(instance.Id, instance.Name, null, null, 0, 0, 0, 0, ContentActivationResult.Nothing, [], []);

    public static ContentReconcileResult RefusedBecause(PackSummary instance, string why) =>
        new(instance.Id, instance.Name, null, null, 0, 0, 0, 0, ContentActivationResult.Nothing,
            [why], [], Refused: true);

    public bool DidAnything =>
        (Placed?.Targets.Count ?? 0) > 0 || (Removed?.Removed ?? 0) > 0
        || WorldsCopied > 0 || BundlesUnpacked > 0 || BundlesRemoved > 0 || Activation.DidAnything;

    /// <summary>One status-bar line: counts first, then the one problem worth naming, in the same
    /// shape as <c>LibraryApplyResult.Summary</c> and the config hub's copy card.</summary>
    public string Summary()
    {
        if (Refused) return Blocked.Count > 0 ? Blocked[0] : "Nothing was changed - the game is running.";

        var parts = new List<string>();
        var linked = (Placed?.Linked ?? 0) + (Placed?.Copied ?? 0);
        if (linked > 0) parts.Add($"{linked} added");
        if (WorldsCopied > 0) parts.Add($"{WorldsCopied} world(s) copied in");
        if (BundlesUnpacked > 0) parts.Add($"{BundlesUnpacked} bundle(s) unpacked ({BundleFilesWritten} file(s))");
        if ((Removed?.Removed ?? 0) > 0) parts.Add($"{Removed!.Removed} removed");
        if (BundlesRemoved > 0) parts.Add($"{BundlesRemoved} bundle(s) removed");
        if (Activation.DidAnything) parts.Add(Activation.Summary());
        if (Blocked.Count > 0) parts.Add($"{Blocked.Count} blocked - {Blocked[0]}");
        if (Failures.Count > 0) parts.Add($"{Failures.Count} failed - {Failures[0]}");
        return parts.Count == 0 ? "Already up to date." : string.Join("  ·  ", parts);
    }
}

/// <summary>The result of "apply my defaults everywhere now".</summary>
public sealed record ContentReconcileAllResult(IReadOnlyList<ContentReconcileResult> Instances)
{
    public int Changed => Instances.Count(r => r.DidAnything);
    public int RefusedCount => Instances.Count(r => r.Refused);

    public string Summary()
    {
        if (Instances.Count == 0) return "No instances to update.";
        var parts = new List<string> { $"{Changed} of {Instances.Count} instance(s) updated" };
        if (RefusedCount > 0) parts.Add($"{RefusedCount} skipped - the game is running there");
        var failed = Instances.Sum(r => r.Failures.Count);
        if (failed > 0) parts.Add($"{failed} failure(s)");
        return string.Join("  ·  ", parts);
    }
}

/// <summary>One item to take out of an instance and into the library.</summary>
/// <param name="PackFormat">A resource pack's <c>pack_format</c> when the caller already read it
/// (<see cref="ResourcePackService.ReadMeta"/>), used only to fall back on a version rule when the
/// source instance has no recorded Minecraft version.</param>
public sealed record ContentAddRequest(
    string SourcePath,
    LibraryKind Kind,
    string? DisplayName = null,
    ModSource? Source = null,
    string? ProjectId = null,
    string? VersionId = null,
    string? VersionNumber = null,
    int? PackFormat = null);

/// <summary>What happened to one item of an "add from pack".</summary>
/// <param name="AlreadyInLibrary">True when the library already held this exact file, so nothing was
/// copied. The one outcome that must not be reported as a failure.</param>
/// <param name="Relinked">True when the source instance now shares the library's bytes rather than
/// keeping its own copy.</param>
public sealed record ContentAddOutcome(
    string SourcePath,
    LibraryItem? Item,
    bool AlreadyInLibrary,
    bool Relinked,
    string Detail);

/// <param name="Refused">Set when nothing was attempted at all because the game is running in the
/// source instance, with the sentence saying so. Per-item failures are never reported this way.</param>
public sealed record ContentAddResult(IReadOnlyList<ContentAddOutcome> Items, string? Refused = null)
{
    public int Added => Items.Count(i => i.Item is not null && !i.AlreadyInLibrary);
    public int AlreadyThere => Items.Count(i => i.AlreadyInLibrary);
    public int Failed => Items.Count(i => i.Item is null && !i.AlreadyInLibrary);

    public string Summary()
    {
        if (Refused is { Length: > 0 } why) return why;
        var parts = new List<string>();
        var relinked = Items.Count(i => i.Relinked);
        if (Added > 0) parts.Add($"{Added} added");
        if (relinked > 0) parts.Add($"{relinked} now shared with the instance");
        if (AlreadyThere > 0) parts.Add($"{AlreadyThere} already there");
        if (Failed > 0)
        {
            var first = Items.First(i => i.Item is null && !i.AlreadyInLibrary);
            parts.Add($"{Failed} failed - {Path.GetFileName(first.SourcePath)}: {first.Detail}");
        }
        return parts.Count == 0 ? "Nothing to add." : string.Join("  ·  ", parts);
    }
}

/// <summary>An old per-item "compatible with" list that could become a shared default.</summary>
/// <param name="SettingsKey">The <see cref="AppSettings"/> key it came from, so a page can clear it
/// once the user has answered.</param>
public sealed record ContentMigrationCandidate(
    LibraryKind Kind,
    string Name,
    string SettingsKey,
    Guid SourcePackId,
    bool CompatibleWithAll,
    IReadOnlyList<Guid> CompatiblePackIds);

/// <param name="PoliciesWritten">Library items that gained a switched-off policy carrying the old
/// list.</param>
/// <param name="Candidates">Items whose content is not in the library yet, so there was nothing to
/// attach a policy to. A page can offer to add them.</param>
public sealed record ContentMigrationReport(
    int PoliciesWritten,
    IReadOnlyList<ContentMigrationCandidate> Candidates,
    bool AlreadyDone)
{
    public string Summary() =>
        AlreadyDone ? "Already migrated."
        : PoliciesWritten == 0 && Candidates.Count == 0 ? "Nothing to migrate."
        : $"{PoliciesWritten} rule(s) carried over, {Candidates.Count} item(s) could become defaults.";
}

/// <summary>Puts the user's default worlds, shader packs, resource packs, configs and scripts into
/// every compatible instance.</summary>
/// <remarks>
/// <para>Owns no storage: it decides which instances get what and drives the library,
/// <see cref="ResourcePackService"/>, <see cref="ShaderPackService"/> and <see cref="WorldService"/>.
/// <see cref="Plan"/> writes nothing and explains each step; <see cref="ReconcileAsync"/> then applies
/// the plan with one library call each way.</para>
/// <para>A world is copied once and never reconsidered, so a template never lands on a real save.
/// Bundles unpack into <c>local/</c> and only recorded paths are removed. Runs at launch, on instance
/// creation and on request, never on a timer: Minecraft rewrites options.txt on exit.</para>
/// </remarks>
public sealed class ContentDefaultsService(
    AppSettings settings,
    PackFolderService packs,
    ContentLibraryService library,
    WorldService worlds,
    ResourcePackService resourcePacks,
    ShaderPackService shaders,
    ContentActivationService activation,
    MinecraftInstanceService instances)
{
    /// <summary>Bumped when the one-off migration in
    /// <see cref="MigrateManualCompatibility"/> changes shape.</summary>
    private const int MigrationVersion = 1;

    // ── planning ─────────────────────────────────────────────────────────────

    /// <summary>
    /// What the user's defaults would do to one instance. Writes nothing.
    /// </summary>
    /// <remarks>Reads the disk (the library's view of the instance, and options.txt for what is switched
    /// on), so call it off the UI thread for more than a couple of instances, or use
    /// <see cref="PlanAllAsync"/>, which scans the library once for all of them.</remarks>
    public ContentDefaultPlan Plan(PackSummary instance) =>
        BuildPlan(instance, library.Scan(), InstanceContentOverrides.Load(packs, instance.Id), duringLaunch: false);

    /// <inheritdoc cref="Plan"/>
    public Task<ContentDefaultPlan> PlanAsync(PackSummary instance, CancellationToken ct = default) =>
        Task.Run(() => Plan(instance), ct);

    /// <summary>One plan per instance, off one library scan.</summary>
    public Task<List<ContentDefaultPlan>> PlanAllAsync(
        IReadOnlyList<PackSummary> targets, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var items = library.Scan();
            var plans = new List<ContentDefaultPlan>();
            foreach (var instance in targets)
            {
                ct.ThrowIfCancellationRequested();
                plans.Add(BuildPlan(instance, items, InstanceContentOverrides.Load(packs, instance.Id), false));
            }
            return plans;
        }, ct);

    private ContentDefaultPlan BuildPlan(
        PackSummary instance, IReadOnlyList<LibraryItem> items,
        InstanceContentOverrides overrides, bool duringLaunch)
    {
        var canWrite = ContentWriteGate.Allows(instances, instance.Id, duringLaunch, out var busyWhy);
        var shaderLoader = library.LoaderFor(instance);

        // Read lazily, once per instance rather than once per item.
        List<string>? stack = null;
        List<string> Stack() => stack ??= SafeStack(instance);
        var shaderRead = false;
        string? activeShader = null;
        string? ActiveShader()
        {
            if (shaderRead) return activeShader;
            shaderRead = true;
            try { activeShader = shaders.ActiveShader(instance.Id, instance.Name); } catch { activeShader = null; }
            return activeShader;
        }

        var steps = new List<ContentDefaultStep>();
        var shaderCandidates = new List<(LibraryItem Item, bool Forced)>();
        var wantedOn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (item.Defaults is not { Enabled: true } policy) continue;   // an ordinary library item

            var choice = overrides.ChoiceFor(item.Key);
            var forced = choice == ContentDefaultChoice.Forced;

            // A world already copied in belongs to the instance and is never reconsidered, whatever the
            // exclusions, rules or library say.
            if (item.Kind == LibraryKind.World && overrides.WorldFolderFor(item.Key) is { } already)
            {
                steps.Add(new ContentDefaultStep(item, ContentStepKind.NothingToDo,
                    $"this save was copied in as saves/{already} and belongs to this instance now - your progress there is never touched again."));
                continue;
            }

            if (choice == ContentDefaultChoice.Excluded)
            {
                steps.Add(IsPlacedIn(item, instance, overrides)
                    ? new ContentDefaultStep(item, ContentStepKind.RemoveApply,
                        "you turned this off for this instance, so it will be taken back out.")
                    : new ContentDefaultStep(item, ContentStepKind.SkippedExcluded,
                        "you turned this off for this instance."));
                continue;
            }

            if (overrides.OptOutOfAllDefaults && !forced)
            {
                steps.Add(IsPlacedIn(item, instance, overrides)
                    ? new ContentDefaultStep(item, ContentStepKind.RemoveApply,
                        "this instance is opted out of your shared defaults, so it will be taken back out.")
                    : new ContentDefaultStep(item, ContentStepKind.SkippedExcluded,
                        "this instance is opted out of your shared defaults."));
                continue;
            }

            // The folders this item is filed under narrow it further. Read per item (it's cheap) so the
            // planner and the pages always agree on where content lands.
            var folderRule = ContentFolderRules.EffectiveFor(item, settings);
            var matches = ContentCompatibility.Matches(policy, instance, item, shaderLoader, out var why, folderRule);
            if (forced && !matches)
            {
                matches = true;
                why = "you forced this one on for this instance, so its rules were not consulted.";
            }

            if (!matches)
            {
                steps.Add(IsPlacedIn(item, instance, overrides)
                    ? new ContentDefaultStep(item, ContentStepKind.RemoveApply, why + " It will be taken back out.")
                    : new ContentDefaultStep(item, ContentStepKind.SkippedIncompatible, why));
                continue;
            }

            // A shader in an instance with no shader loader does nothing in game; say so here.
            if (item.Kind == LibraryKind.ShaderPack && shaderLoader == ShaderLoader.None)
            {
                steps.Add(new ContentDefaultStep(item, ContentStepKind.BlockedNoLoader,
                    $"{instance.Name} has no Iris, Oculus or OptiFine, so a shader pack there would do nothing."));
                continue;
            }

            steps.Add(PlacementStep(item, instance, overrides, canWrite, busyWhy, why));

            if (!policy.Activate) continue;
            if (item.Kind == LibraryKind.ResourcePack)
            {
                wantedOn.Add(item.FileName);
                if (!Stack().Any(n => string.Equals(n, item.FileName, StringComparison.OrdinalIgnoreCase)))
                    steps.Add(canWrite
                        ? new ContentDefaultStep(item, ContentStepKind.Activate,
                            "it will be switched on in this instance, above the packs already in the list.")
                        : new ContentDefaultStep(item, ContentStepKind.BlockedRunning, busyWhy));
            }
            else if (item.Kind == LibraryKind.ShaderPack)
            {
                shaderCandidates.Add((item, forced));
            }
        }

        // ── the one shader ──
        // One winner is chosen and every other candidate is reported as a conflict the page can show,
        // since a shader default that lost can't be diagnosed from inside the game.
        if (shaderCandidates.Count > 0)
        {
            var ordered = shaderCandidates
                .OrderByDescending(c => c.Forced)
                .ThenByDescending(c => c.Item.Defaults?.Order ?? 0)
                .ThenBy(c => c.Item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var winner = ordered[0].Item;
            var current = ActiveShader();
            var currentIsOurs = current is { Length: > 0 }
                && string.Equals(current, overrides.ActivatedShader, StringComparison.OrdinalIgnoreCase);

            if (string.Equals(current, winner.FileName, StringComparison.OrdinalIgnoreCase))
            {
                // Already active. Reported anyway, so the page can confirm the default is on
                // without launching the game.
                steps.Add(new ContentDefaultStep(winner, ContentStepKind.NothingToDo,
                    "already this instance's shader."));
            }
            else if (!canWrite)
            {
                steps.Add(new ContentDefaultStep(winner, ContentStepKind.BlockedRunning, busyWhy));
            }
            else if (current is { Length: > 0 } && !currentIsOurs && !ordered[0].Forced)
            {
                // The active shader is one the user picked, and a default never displaces it. The
                // plan reports that, so the activation pass isn't asked to do something it would refuse.
                steps.Add(new ContentDefaultStep(winner, ContentStepKind.BlockedShaderConflict,
                    $"{instance.Name} is already using {ShaderPackService.PrettyName(current)}, which you chose. "
                    + "Force this default for that instance if you want it to win."));
            }
            else
            {
                steps.Add(new ContentDefaultStep(winner, ContentStepKind.Activate,
                    "it will be set as this instance's shader."));
            }

            foreach (var loser in ordered.Skip(1))
                steps.Add(new ContentDefaultStep(loser.Item, ContentStepKind.BlockedShaderConflict,
                    $"only one shader can be active per instance, and {winner.DisplayName} is the one set for {instance.Name}."));
        }

        // ── switching off what we switched on, and nothing else ──
        foreach (var name in overrides.ActivatedByDefaults)
        {
            if (wantedOn.Contains(name)) continue;
            if (!Stack().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) continue;
            var item = items.FirstOrDefault(i =>
                           i.Kind == LibraryKind.ResourcePack
                           && string.Equals(i.FileName, name, StringComparison.OrdinalIgnoreCase))
                       ?? LibraryItem.Missing(LibraryKind.ResourcePack, name);
            steps.Add(canWrite
                ? new ContentDefaultStep(item, ContentStepKind.Deactivate,
                    "this instance no longer takes this default, so it will be switched off again - only because the launcher is the one that switched it on.")
                : new ContentDefaultStep(item, ContentStepKind.BlockedRunning, busyWhy));
        }

        if (overrides.ActivatedShader is { Length: > 0 } ourShader
            && !shaderCandidates.Any(c => string.Equals(c.Item.FileName, ourShader, StringComparison.OrdinalIgnoreCase))
            && string.Equals(ActiveShader(), ourShader, StringComparison.OrdinalIgnoreCase))
        {
            var item = items.FirstOrDefault(i =>
                           i.Kind == LibraryKind.ShaderPack
                           && string.Equals(i.FileName, ourShader, StringComparison.OrdinalIgnoreCase))
                       ?? LibraryItem.Missing(LibraryKind.ShaderPack, ourShader);
            steps.Add(canWrite
                ? new ContentDefaultStep(item, ContentStepKind.Deactivate,
                    "this instance no longer takes this shader default, so shaders will be switched off again.")
                : new ContentDefaultStep(item, ContentStepKind.BlockedRunning, busyWhy));
        }

        return new ContentDefaultPlan(instance.Id, instance.Name, steps);
    }

    /// <summary>The placement half of one item's plan: apply, copy a world in, or say what is
    /// blocking it.</summary>
    private ContentDefaultStep PlacementStep(
        LibraryItem item, PackSummary instance, InstanceContentOverrides overrides,
        bool canWrite, string busyWhy, string why)
    {
        if (item.Kind == LibraryKind.World)
        {
            // Disabled for now: the Worlds page has no control for world defaults, so an enabled
            // one stored by an older build would copy saves on every launch with no way to see or
            // turn it off. The stored flag is kept for when the Worlds page gets a per-row toggle
            // again.
            return new ContentDefaultStep(item, ContentStepKind.NothingToDo,
                "worlds are not placed automatically -- copy this one in from its own page.");

#pragma warning disable CS0162 // unreachable while the above early-return stands
            // A save with that name exists and we didn't put it there, so the user copied it in. Leave it
            // alone and report it rather than copying a second one beside it.
            if (ExistingSaveFolder(instance, item) is { } existing)
                return new ContentDefaultStep(item, ContentStepKind.NothingToDo,
                    $"{instance.Name} already has a save called {existing}, so it was left alone.");
            return canWrite
                ? new ContentDefaultStep(item, ContentStepKind.CopyWorld,
                    "a copy of this save will be made in this instance - the original is kept as the template, and the copy is yours to play.")
                : new ContentDefaultStep(item, ContentStepKind.BlockedRunning, busyWhy);
        }

        if (IsBundle(item.Kind))
        {
            if (overrides.BundleFilesFor(item.Key).Count > 0)
                return new ContentDefaultStep(item, ContentStepKind.NothingToDo,
                    "already unpacked into this instance.");
            return canWrite
                ? new ContentDefaultStep(item, ContentStepKind.Apply,
                    "it will be unpacked into this instance, without replacing any file the instance already has.")
                : new ContentDefaultStep(item, ContentStepKind.BlockedRunning, busyWhy);
        }

        var state = library.Inspect(item, instance);
        if (state.Diverged)
            return new ContentDefaultStep(item, ContentStepKind.BlockedShadowed,
                "this instance has its own edited copy of this file, so it is no longer using the shared one - it was left alone.");
        if (state.HasOwnCopy)
            return new ContentDefaultStep(item, ContentStepKind.BlockedShadowed,
                $"{instance.Name} has its own file of that name, which wins at launch - remove it there if you want the shared copy instead.");
        if (!state.Applied)
            return canWrite
                ? new ContentDefaultStep(item, ContentStepKind.Apply, why)
                : new ContentDefaultStep(item, ContentStepKind.BlockedRunning, busyWhy);
        return new ContentDefaultStep(item, ContentStepKind.NothingToDo, "already using the shared copy here.");
    }

    // ── reconciling ──────────────────────────────────────────────────────────

    /// <summary>Brings one instance in line with the user's defaults.</summary>
    /// <param name="activate">Also write options.txt and the shader config. False on the launch path's
    /// first pass, where the launch overlay has not run yet.</param>
    /// <remarks>Refuses while the game is running in that instance, and says so in the result rather
    /// than throwing.</remarks>
    public Task<ContentReconcileResult> ReconcileAsync(
        PackSummary instance, bool activate = true, IProgress<string>? log = null, CancellationToken ct = default) =>
        ReconcileCoreAsync(instance, activate, duringLaunch: false, log, ct);

    /// <summary>"Apply my defaults everywhere now." One library scan, then one instance at a
    /// time.</summary>
    /// <remarks>Sequential, since instances reconciled in parallel would hard-link out of the same
    /// library folder and rewrite index.json at the same time.</remarks>
    public async Task<ContentReconcileAllResult> ReconcileAllAsync(
        IReadOnlyList<PackSummary> targets, bool activate = true,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var results = new List<ContentReconcileResult>();
        foreach (var instance in targets)
        {
            ct.ThrowIfCancellationRequested();
            log?.Report($"{instance.Name}...");
            results.Add(await ReconcileCoreAsync(instance, activate, false, log, ct));
        }
        return new ContentReconcileAllResult(results);
    }

    private async Task<ContentReconcileResult> ReconcileCoreAsync(
        PackSummary instance, bool activate, bool duringLaunch, IProgress<string>? log, CancellationToken ct)
    {
        if (!ContentWriteGate.Allows(instances, instance.Id, duringLaunch, out var blocked))
        {
            log?.Report($"{instance.Name}: {blocked}");
            return ContentReconcileResult.RefusedBecause(instance, blocked);
        }

        var overrides = InstanceContentOverrides.Load(packs, instance.Id);
        var plan = await Task.Run(() => BuildPlan(instance, library.Scan(), overrides, duringLaunch), ct);
        if (plan.Steps.Count == 0) return ContentReconcileResult.Nothing(instance);

        var failures = new List<string>();
        var blockedLines = plan.Blocked.Select(s => $"{s.Item.DisplayName}: {s.Why}").ToList();

        // One call each into the library, not one per item: both loop over items and targets and save
        // settings once at the end.
        var toPlace = plan.Of(ContentStepKind.Apply).Select(s => s.Item)
            .Where(i => !IsBundle(i.Kind)).ToList();
        var toRemove = plan.Of(ContentStepKind.RemoveApply).Select(s => s.Item)
            .Where(i => !IsBundle(i.Kind) && i.Kind != LibraryKind.World).ToList();

        LibraryApplyResult? placed = null;
        LibraryApplyResult? removed = null;
        if (toPlace.Count > 0)
            placed = await library.ApplyAsync(toPlace, [instance], replaceOwnCopy: false, log, ct);
        if (toRemove.Count > 0)
            removed = await library.UnapplyAsync(toRemove, [instance], log, ct);

        if (placed is not null)
            failures.AddRange(placed.Targets
                .Where(t => t.Outcome == LibraryApplyOutcome.Failed)
                .Select(t => $"{t.PackName}: {t.Detail}"));

        // ── worlds: copied, never linked ──
        var worldsCopied = 0;
        foreach (var step in plan.Of(ContentStepKind.CopyWorld))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var folder = await CopyWorldTemplateAsync(instance, step.Item, ct);
                overrides.InstantiatedWorlds[step.Item.Key] = folder;
                worldsCopied++;
                log?.Report($"{instance.Name}: copied the world template {step.Item.DisplayName} in as saves/{folder}");
                AppLog.Log("content-defaults", $"{instance.Name}: world template {step.Item.Key} > saves/{folder}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.LogError("content-defaults", ex);
                failures.Add($"{step.Item.DisplayName}: the save could not be copied - {ex.Message}");
            }
        }

        // ── config / KubeJS bundles: unpacked into local/, with the paths written down ──
        var bundlesUnpacked = 0;
        var bundleFiles = 0;
        foreach (var step in plan.Of(ContentStepKind.Apply).Where(s => IsBundle(s.Item.Kind)))
        {
            ct.ThrowIfCancellationRequested();
            var (written, failure) = UnpackBundle(instance, step.Item, overrides, log, ct);
            if (failure is not null) { failures.Add($"{step.Item.DisplayName}: {failure}"); continue; }
            bundlesUnpacked++;
            bundleFiles += written;
        }

        var bundlesRemoved = 0;
        foreach (var step in plan.Of(ContentStepKind.RemoveApply).Where(s => IsBundle(s.Item.Kind)))
        {
            ct.ThrowIfCancellationRequested();
            if (RemoveBundle(instance, step.Item, overrides, log)) bundlesRemoved++;
        }

        // ── switching on, last, because it is the part that writes options.txt ──
        var activationResult = activate
            ? activation.Apply(instance, plan.Steps, overrides, log, duringLaunch)
            : ContentActivationResult.Nothing;
        // Notes are refusals and advisories ("the shader you chose stays"), not failures, so they are
        // reported as reasons rather than counted as failed.
        blockedLines.AddRange(activationResult.Notes);

        // Always saved: the pass may have dropped a stale claim (a pack the user switched off by hand, a
        // bundle whose files are gone), and the next pass must not act on it.
        overrides.Save(packs, instance.Id);

        var result = new ContentReconcileResult(
            instance.Id, instance.Name, placed, removed,
            worldsCopied, bundlesUnpacked, bundleFiles, bundlesRemoved,
            activationResult, blockedLines, failures);

        // Only when something changed. This runs on every launch, and clearing the scan caches for
        // nothing would make every page redo its folder walk after each Play.
        if (result.DidAnything)
        {
            ScanCaches.InvalidatePack(instance.Id,
                ScanScope.ResourcePacks | ScanScope.Shaders | ScanScope.Worlds | ScanScope.Config);
            AppLog.Log("content-defaults", $"{instance.Name}: {result.Summary()}");
        }
        return result;
    }

    // ── the launch path ──────────────────────────────────────────────────────

    /// <summary>The <see cref="PackSummary"/> view of a <see cref="PackDetail"/>, which is what the
    /// launch path has and every rule here is written against.</summary>
    public static PackSummary AsSummary(PackDetail pack) => new(
        pack.Id, pack.Name, pack.Description, pack.OwnerId, pack.OwnerUsername, pack.Visibility,
        pack.IsShared, pack.IsEmpty, pack.MinecraftVersion, pack.Loader, pack.LoaderVersion,
        pack.CreatedAt, pack.UpdatedAt, pack.EffectivePermissions, pack.Summary);

    /// <summary>
    /// Step one of a launch: place the files into <c>local/</c>, before the launch overlay runs.
    /// </summary>
    /// <remarks>Placement only. The overlay puts these files into <c>game/</c>, so anything that writes
    /// into <c>game/</c> (options.txt, the shader config) waits for
    /// <see cref="ActivateForLaunchAsync"/>.</remarks>
    public Task<ContentReconcileResult> ReconcileForLaunchAsync(
        PackDetail pack, IProgress<string>? log = null, CancellationToken ct = default) =>
        ReconcileCoreAsync(AsSummary(pack), activate: false, duringLaunch: true, log, ct);

    /// <summary>Step three of a launch: switch the defaults on, after the overlay and before low
    /// mode.</summary>
    /// <remarks>Re-plans, since the overlay has moved files into <c>game/</c> and step one's plan would
    /// re-add a name already in the stack. Must run before <c>LowModeService.Apply</c>, which also writes
    /// options.txt and needs the last word.</remarks>
    public async Task<ContentActivationResult> ActivateForLaunchAsync(
        PackDetail pack, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var instance = AsSummary(pack);
        if (!ContentWriteGate.Allows(instances, instance.Id, duringLaunch: true, out var blocked))
            return ContentActivationResult.RefusedBecause(blocked);

        var overrides = InstanceContentOverrides.Load(packs, instance.Id);
        var plan = await Task.Run(() => BuildPlan(instance, library.Scan(), overrides, duringLaunch: true), ct);
        var result = activation.Apply(instance, plan.Steps, overrides, log, duringLaunch: true);
        if (result.DidAnything)
        {
            overrides.Save(packs, instance.Id);
            ScanCaches.InvalidatePack(instance.Id, ScanScope.ResourcePacks | ScanScope.Shaders);
        }
        return result;
    }

    // ── per-instance choices ─────────────────────────────────────────────────

    /// <summary>One instance's answers to the defaults. A page that changes one calls
    /// <see cref="SetChoice"/> or <see cref="SetOptOut"/> rather than saving this itself.</summary>
    public InstanceContentOverrides OverridesFor(Guid packId) => InstanceContentOverrides.Load(packs, packId);

    /// <summary>Records "never here" / "always here" / "follow the rules" for one item in one
    /// instance, and saves it.</summary>
    public void SetChoice(Guid packId, string libraryKey, ContentDefaultChoice choice)
    {
        var overrides = InstanceContentOverrides.Load(packs, packId);
        overrides.SetChoice(libraryKey, choice);
        overrides.Save(packs, packId);
    }

    /// <summary>"Leave this instance out of my defaults entirely", and saves it.</summary>
    public void SetOptOut(Guid packId, bool optOut)
    {
        var overrides = InstanceContentOverrides.Load(packs, packId);
        overrides.OptOutOfAllDefaults = optOut;
        overrides.Save(packs, packId);
    }

    // ── add from pack / add all from pack ────────────────────────────────────

    /// <summary>Takes content already inside one instance into the library, and leaves the instance
    /// sharing the library's bytes instead of its own copy.</summary>
    /// <param name="makeDefault">Also give each item a switched-on policy seeded from this instance's
    /// Minecraft version and loader.</param>
    /// <param name="policyTemplate">A policy to copy instead of the seeded one (to set
    /// <see cref="ContentDefaultPolicy.Activate"/> or an order too). Empty version and loader csvs are
    /// still seeded from the instance.</param>
    /// <remarks>Adds each item, then relinks the instance's copy to the library's while the two are
    /// byte-identical. Worlds are only copied. Items already in the library (same name, same file) are
    /// skipped, since Add would otherwise save a second copy as "Pack-2.zip".</remarks>
    public async Task<ContentAddResult> AddFromInstanceAsync(
        PackSummary instance, IReadOnlyList<ContentAddRequest> items, bool makeDefault,
        ContentDefaultPolicy? policyTemplate = null,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        // Refused while the game is running there: relinking replaces the instance's own file, and a
        // running game keeps writing its save, so copying one out isn't safe either.
        if (!ContentWriteGate.Allows(instances, instance.Id, duringLaunch: false, out var blocked))
            return new ContentAddResult([], blocked);

        var existing = library.Scan();           // one scan for the whole batch
        var outcomes = new List<ContentAddOutcome>();

        foreach (var request in items)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(request.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            try
            {
                var duplicate = existing.FirstOrDefault(i =>
                    i.Kind == request.Kind
                    && string.Equals(i.FileName, name, StringComparison.OrdinalIgnoreCase)
                    && PackFolderService.EntriesReferToSameContent(i.Path, request.SourcePath));
                if (duplicate is not null)
                {
                    outcomes.Add(new ContentAddOutcome(request.SourcePath, duplicate, true, false,
                        "this instance is already using the shared copy of it."));
                    continue;
                }

                log?.Report($"Adding {name}...");
                var item = await library.AddAsync(request.SourcePath, request.Kind, request.DisplayName,
                    request.Source, request.ProjectId, request.VersionId, request.VersionNumber, ct);
                existing.Add(item);

                if (makeDefault)
                {
                    var policy = policyTemplate?.Clone() ?? new ContentDefaultPolicy();
                    policy.Enabled = true;
                    policy.McVersionsCsv ??= ContentDefaults.VersionCsvFor(instance)
                                             ?? ResourcePackFormats.SuggestVersionCsv(request.PackFormat);
                    policy.LoadersCsv ??= ContentDefaults.LoaderCsvFor(instance);
                    library.SetDefaults(item.Key, policy);
                    if (policy.KeepLocal) library.SetKeepLocal(item.Key, true);
                }

                // Saves stay with the instance (templates). Everything else is relinked without an Apply
                // first: the file came from this instance's game/, so Apply would only add an inert copy in
                // local/ (Shadowed) and Relink would then report AlreadyThere without sharing anything.
                var relinked = false;
                var unpacked = false;
                if (request.Kind != LibraryKind.World && !IsBundle(request.Kind))
                {
                    var relink = await library.RelinkAsync(item, [instance], ct);
                    relinked = relink.Linked > 0 || relink.Copied > 0;
                    // Relink only handles zips, since an unpacked folder can't be compared as one
                    // file. The instance keeps its own copy and the message below says so.
                    unpacked = !relinked && item.IsFolder;
                }

                outcomes.Add(new ContentAddOutcome(request.SourcePath, item, false, relinked,
                    request.Kind == LibraryKind.World
                        ? "kept as a template - this instance keeps playing its own save."
                        : relinked
                            ? "added, and this instance now uses the shared copy."
                            : unpacked
                                ? "added. It is an unpacked folder rather than a zip, so "
                                  + "this instance keeps its own copy of the files."
                                : "added."));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.LogError("content-defaults", ex);
                outcomes.Add(new ContentAddOutcome(request.SourcePath, null, false, false, ex.Message));
            }
        }

        return new ContentAddResult(outcomes);
    }

    // ── migrating the old manual lists ───────────────────────────────────────

    /// <summary>Turns the old per-item "compatible with" lists into switched-off policies.</summary>
    /// <remarks>Off, because those lists meant "may be used here" and a default means "will be put
    /// here"; pages can offer to switch them on. Runs once (recorded in the library index). Items not in
    /// the library come back as <see cref="ContentMigrationCandidate"/>s. <see cref="AppSettings"/> is
    /// only read, so the old lists keep working where they are still used.</remarks>
    public ContentMigrationReport MigrateManualCompatibility(bool force = false)
    {
        if (!force && library.DefaultsMigrationVersion >= MigrationVersion)
            return new ContentMigrationReport(0, [], AlreadyDone: true);

        var items = library.Scan();
        var candidates = new List<ContentMigrationCandidate>();
        var written = 0;

        void Consider(LibraryKind kind, string settingsKey, string fileName, Guid sourcePackId,
                      bool all, List<Guid> allowed)
        {
            if (!all && allowed.Count == 0) return;

            var match = items.FirstOrDefault(i =>
                i.Kind == kind && string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                // Nothing in the library to attach a rule to. Copying the content in is the user's
                // decision, not the migration's.
                candidates.Add(new ContentMigrationCandidate(kind, fileName, settingsKey, sourcePackId, all, allowed));
                return;
            }
            if (match.Defaults is not null) return;   // already has a rule; never overwrite one

            var policy = new ContentDefaultPolicy { Enabled = false };
            if (!all)
            {
                // The old list named exact instances, and the source instance was always implicitly
                // on it (as in ResourcePackService.IsCompatible).
                policy.IncludePackIds.Add(sourcePackId);
                foreach (var id in allowed)
                    if (!policy.IncludePackIds.Contains(id)) policy.IncludePackIds.Add(id);
            }
            library.SetDefaults(match.Key, policy);
            written++;
        }

        foreach (var (key, entry) in settings.ResourcePacks.ToList())
        {
            var (packId, fileName) = SplitSettingsKey(key);
            if (packId == Guid.Empty || fileName.Length == 0) continue;
            Consider(LibraryKind.ResourcePack, key, fileName, packId, entry.CompatibleWithAll, entry.CompatiblePackIds);
        }
        foreach (var (key, entry) in settings.Worlds.ToList())
        {
            var (packId, folderName) = SplitSettingsKey(key);
            if (packId == Guid.Empty || folderName.Length == 0) continue;
            Consider(LibraryKind.World, key, folderName, packId, entry.CompatibleWithAll, entry.CompatiblePackIds);
        }

        library.SetDefaultsMigrationVersion(MigrationVersion);
        var report = new ContentMigrationReport(written, candidates, AlreadyDone: false);
        AppLog.Log("content-defaults", $"Migration of the old compatibility lists: {report.Summary()}");
        return report;
    }

    /// <summary>Splits a <c>{packId:N}:{name}</c> settings key, dropping the <c>local/</c> qualifier
    /// that <see cref="ResourcePackService.Key(Guid,string,ResourcePackOrigin)"/> adds.</summary>
    private static (Guid PackId, string Name) SplitSettingsKey(string key)
    {
        var colon = key.IndexOf(':');
        if (colon <= 0 || !Guid.TryParse(key[..colon], out var packId)) return (Guid.Empty, "");
        var rest = key[(colon + 1)..];
        if (rest.StartsWith("local/", StringComparison.OrdinalIgnoreCase)) rest = rest["local/".Length..];
        return (packId, rest);
    }

    // ── the bits that touch disk ─────────────────────────────────────────────

    private static bool IsBundle(LibraryKind kind) =>
        kind is LibraryKind.ConfigBundle or LibraryKind.KubeJsBundle;

    /// <summary>True when this item is currently in this instance because of the library.</summary>
    /// <remarks>Not just "a file of that name is there": the instance's own same-named pack is not ours
    /// and must not be removed. Linked items are checked by identity
    /// (<see cref="LibraryTargetState.LinkedToLibrary"/>), worlds and bundles by our own record.</remarks>
    private bool IsPlacedIn(LibraryItem item, PackSummary instance, InstanceContentOverrides overrides)
    {
        if (item.Kind == LibraryKind.World) return overrides.WorldFolderFor(item.Key) is not null;
        if (IsBundle(item.Kind)) return overrides.BundleFilesFor(item.Key).Count > 0;
        var state = library.Inspect(item, instance);
        return state.Applied && state.LinkedToLibrary;
    }

    private List<string> SafeStack(PackSummary instance)
    {
        try { return resourcePacks.ActiveFor(instance.Id, instance.Name); }
        catch { return []; }   // an instance with no game folder yet has nothing switched on
    }

    /// <summary>The save folder a world template would land in, when one of that name is already
    /// there. Null when the instance has no such save.</summary>
    private string? ExistingSaveFolder(PackSummary instance, LibraryItem item)
    {
        try
        {
            var candidate = item.IsFolder
                ? item.FileName
                : WorldService.SafeFolderName(Path.GetFileNameWithoutExtension(item.FileName));
            var savesDir = worlds.SavesDir(instance.Id, instance.Name);
            return Directory.Exists(Path.Combine(savesDir, candidate)) ? candidate : null;
        }
        catch { return null; }
    }

    /// <summary>Copies a world template into an instance and returns the save folder it became.</summary>
    /// <remarks>Copied, never linked (see the class remarks). A zipped template goes through
    /// <see cref="WorldService.ImportZipAsync"/>, which unwraps a single top-level folder (common in
    /// world zips) and refuses an archive with no level.dat.</remarks>
    private async Task<string> CopyWorldTemplateAsync(PackSummary instance, LibraryItem item, CancellationToken ct)
    {
        if (!item.IsFolder)
            return await worlds.ImportZipAsync(item.Path, instance.Id, instance.Name,
                Path.GetFileNameWithoutExtension(item.FileName), null, ct);

        var savesDir = worlds.SavesDir(instance.Id, instance.Name);
        Directory.CreateDirectory(savesDir);
        var folderName = WorldService.UniqueFolderName(savesDir, item.FileName);
        await WorldService.CopyWorldAsync(item.Path, Path.Combine(savesDir, folderName), null, ct);
        return folderName;
    }

    /// <summary>Unpacks a config or KubeJS bundle into the instance's <c>local/</c> side, recording
    /// every path it wrote.</summary>
    /// <remarks>Never over an existing file, so the instance's own config wins. Validation
    /// (<c>BundleSafePath</c>) is all-or-nothing before anything is written. Entries not rooted at
    /// <c>config/</c> or <c>kubejs/</c> are taken as relative to the kind's folder, the layout
    /// <c>ContentBundleService.Compose</c> writes.</remarks>
    private (int Written, string? Failure) UnpackBundle(
        PackSummary instance, LibraryItem item, InstanceContentOverrides overrides,
        IProgress<string>? log, CancellationToken ct)
    {
        var root = item.Kind == LibraryKind.KubeJsBundle ? BundleTargets.KubeJs : BundleTargets.Config;
        try
        {
            using var archive = ZipFile.OpenRead(item.Path);
            var entries = archive.Entries
                .Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\') && e.Name.Length > 0)
                .ToList();
            if (entries.Count == 0) return (0, "the archive is empty.");
            if (entries.Count > BundleSafePath.MaxEntries)
                return (0, $"the archive holds {entries.Count:N0} files, beyond the {BundleSafePath.MaxEntries:N0} limit.");
            if (SafeZip.CheckLimits(entries) is { } overLimit)
                return (0, $"refused: {overLimit}. Nothing was written.");

            var alreadyRooted = entries.All(e =>
                e.FullName.Replace('\\', '/').StartsWith(root + "/", StringComparison.OrdinalIgnoreCase));

            var localDir = packs.LocalDir(instance.Id);
            var localFull = Path.GetFullPath(localDir);

            var planned = new List<(ZipArchiveEntry Entry, string Relative, string Dest)>();
            long total = 0;
            foreach (var entry in entries)
            {
                var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (!alreadyRooted) relative = $"{root}/{relative}";
                if (BundleSafePath.ValidateEntry(relative) is { } why)
                    return (0, $"refused - \"{entry.FullName}\" {why}. Nothing was written.");
                if (entry.Length > BundleSafePath.MaxEntryBytes)
                    return (0, $"refused - \"{entry.FullName}\" is beyond the per-file limit. Nothing was written.");
                if (SafeZip.CheckEntry(entry) is { } implausible)
                    return (0, $"refused: \"{entry.FullName}\", {implausible}. Nothing was written.");
                // Resolved here rather than while writing, so an entry that does not land plainly inside
                // local/ refuses the whole bundle before its first file is written.
                var dest = PathSafety.ResolveInside(localDir, relative);
                if (dest is null || !BundleSafePath.StaysInside(localFull, dest))
                    return (0, $"refused: \"{entry.FullName}\" does not resolve to a plain path inside the instance folder. Nothing was written.");
                total += entry.Length;
                planned.Add((entry, relative, dest));
            }
            if (total > BundleSafePath.MaxTotalBytes)
                return (0, "refused - the archive unpacks to more than the size limit. Nothing was written.");

            Directory.CreateDirectory(localDir);

            var written = new List<UnpackedFile>();
            foreach (var (entry, relative, dest) in planned)
            {
                ct.ThrowIfCancellationRequested();

                // Never over an existing file: it is either the instance's own or an earlier unpack
                // the user has since edited.
                if (File.Exists(dest)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                try
                {
                    SafeZip.ExtractToFile(entry, dest, overwrite: false);
                }
                catch (InvalidDataException ex)
                {
                    // Only detected while inflating: the entry is not what the archive claims.
                    // Nothing was left at its path, so it isn't recorded as written.
                    AppLog.Log("content-defaults", $"{instance.Name}: skipped {entry.FullName} in {item.Key}: {ex.Message}");
                    continue;
                }

                // Stamped from the written file, not the zip entry: ExtractToFile sets the
                // last-write time from the archive, and un-apply compares against what is on disk.
                var info = new FileInfo(dest);
                written.Add(new UnpackedFile
                {
                    Path = relative,
                    Size = info.Length,
                    MTicks = info.LastWriteTimeUtc.Ticks,
                });
            }

            overrides.UnpackedBundleFiles[item.Key] = written;
            log?.Report($"{instance.Name}: unpacked {item.DisplayName} - {written.Count} file(s) into local/{root}/");
            AppLog.Log("content-defaults",
                $"{instance.Name}: unpacked {item.Key} into local/ ({written.Count} file(s), {planned.Count - written.Count} left alone)");
            return (written.Count, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppLog.LogError("content-defaults", ex);
            return (0, $"the bundle could not be unpacked - {ex.Message}");
        }
    }

    /// <summary>Deletes only the files one bundle wrote.</summary>
    /// <remarks>Mistakes here destroy the user's config edits, so only paths recorded at unpack time are
    /// considered, the <c>game/</c> twin is deleted only when it is the same file (identity, not name),
    /// and files the user has since changed are kept.</remarks>
    private bool RemoveBundle(
        PackSummary instance, LibraryItem item, InstanceContentOverrides overrides, IProgress<string>? log)
    {
        var recorded = overrides.BundleFilesFor(item.Key);
        if (recorded.Count == 0)
        {
            overrides.UnpackedBundleFiles.Remove(item.Key);
            return false;
        }

        var localDir = packs.LocalDir(instance.Id);
        var gameDir = packs.GameDir(instance.Id);
        var deleted = 0;
        var kept = 0;
        foreach (var file in recorded)
        {
            try
            {
                var relative = file.Path;
                var localPath = PathSafety.ResolveInside(localDir, relative);
                var gamePath = PathSafety.ResolveInside(gameDir, relative);

                // The recorded paths are archive entry names. One that is not a plain path inside both
                // folders, or that runs through a junction or symbolic link, is never deleted.
                if (localPath is null || gamePath is null
                    || PathSafety.CrossesLink(localDir, localPath) || PathSafety.CrossesLink(gameDir, gamePath))
                {
                    AppLog.Log("content-defaults",
                        $"{instance.Name}: did not remove {relative} for {item.Key}; it is not a plain path inside the instance folder or it runs through a linked folder.");
                    continue;
                }

                // Files the user has edited since unpacking are kept: turning a default off is not
                // permission to throw away their work. Checked on both sides, since after a launch
                // the overlay has linked local/ into game/ and an in-place editor writes through
                // the link.
                if (!file.StillUntouched(localPath) || !file.StillUntouched(gamePath))
                {
                    kept++;
                    continue;
                }

                // The twin first, and only while both exist: the same-file check needs both paths.
                if (PackFolderService.PathsReferToSameFile(localPath, gamePath) && File.Exists(gamePath))
                    File.Delete(gamePath);
                if (File.Exists(localPath)) { File.Delete(localPath); deleted++; }
            }
            catch (Exception ex)
            {
                AppLog.LogError("content-defaults", ex);
            }
        }

        // Kept files stay on the record, so a later reconcile still knows the bundle put them there.
        if (kept == 0) overrides.UnpackedBundleFiles.Remove(item.Key);
        else overrides.UnpackedBundleFiles[item.Key] =
            recorded.Where(f => PathSafety.ResolveInside(localDir, f.Path) is { } onDisk
                                && !f.StillUntouched(onDisk)).ToList();

        var keptNote = kept == 0 ? "" : $", and kept {kept} you had edited";
        log?.Report($"{instance.Name}: removed {deleted} file(s) that {item.DisplayName} had added{keptNote}");
        AppLog.Log("content-defaults",
            $"{instance.Name}: removed bundle {item.Key} ({deleted} of {recorded.Count} file(s), {kept} edited and left alone)");
        return deleted > 0;
    }
}

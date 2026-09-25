using System.IO;
using System.Text;
using System.Text.Json;
using CloudLauncher.Shared;

namespace Showcase;

/// <summary>Writes the throwaway profile and the fictional instances folder. Rebuilt from nothing on
/// every run, so a run never depends on what the last one left behind.</summary>
public static class Seed
{
    public static void Profile(string profileDir, string packsRoot, int port)
    {
        if (Directory.Exists(profileDir)) Directory.Delete(profileDir, recursive: true);
        Directory.CreateDirectory(profileDir);
        if (Directory.Exists(packsRoot)) Directory.Delete(packsRoot, recursive: true);
        Directory.CreateDirectory(packsRoot);

        var settings = new Dictionary<string, object?>
        {
            ["ServerUrl"] = $"http://127.0.0.1:{port}",
            ["AccessToken"] = "showcase-access",
            ["RefreshToken"] = "showcase-refresh",
            ["AccessTokenExpiresAt"] = "2099-01-01T00:00:00+00:00",
            ["Username"] = "mara",
            ["UserId"] = FakeWorld.Mara,
            ["IsLoggedIn"] = true,
            ["PacksRoot"] = packsRoot,
            ["UseSidePanel"] = true,
            ["DefaultPackSeeded"] = true,
            ["CustomGameWindowDefaultApplied"] = true,
            ["LookAccentDefaultApplied"] = true,
            ["UseCustomGameWindow"] = false,
            ["Look"] = new Dictionary<string, object>
            {
                ["Style"] = "Slate", ["Skin"] = "Dark", ["Accent"] = "#601B00", ["Motion"] = 1.0, ["Radius"] = 3,
                ["PixelHeadings"] = true, ["PixelText"] = false, ["PixelIcons"] = true, ["Shadows"] = true,
                ["Transitions"] = true, ["UiSounds"] = false   // the harness must never make a sound
            }
        };
        File.WriteAllText(Path.Combine(profileDir, "settings.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

        foreach (var inst in FakeWorld.Instances.Where(i => i.InMyLibrary))
        {
            var root = Path.Combine(packsRoot, FakeWorld.Slug(inst.Name));
            var game = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(game, "mods"));
            File.WriteAllText(Path.Combine(root, ".packid"), inst.Id.ToString());
            if (inst.Id == FakeWorld.Hearthstone) Logs(game);
        }
    }

    /// <summary>A believable NeoForge session log and one crash report, for the Logs tab.</summary>
    private static void Logs(string game)
    {
        var logs = Path.Combine(game, "logs");
        Directory.CreateDirectory(logs);
        var sb = new StringBuilder();
        var t = new DateTime(2026, 9, 24, 21, 4, 12);
        string[] info =
        [
            "[main/INFO] [net.neoforged.fml.loading.FMLLoader/]: Loading 212 mods",
            "[main/INFO] [mixin/]: SpongePowered MIXIN Subsystem Version=0.8.7 Source=union:/mixin-0.8.7.jar",
            "[Render thread/INFO] [net.minecraft.client.Minecraft/]: Setting user: mara",
            "[Render thread/INFO] [com.mojang.blaze3d.systems.RenderSystem/]: Backend library: LWJGL version 3.3.3",
            "[Worker-Main-4/INFO] [net.minecraft.server.packs.resources.ReloadableResourceManager/]: Reloading ResourceManager: vanilla, mod_resources, Soft Ores",
            "[Render thread/INFO] [net.minecraft.client.sounds.SoundEngine/]: Sound engine started",
            "[Server thread/INFO] [net.minecraft.server.MinecraftServer/]: Preparing level \"Valley Survival\"",
            "[Server thread/INFO] [net.minecraft.server.level.progress.LoggerChunkProgressListener/]: Preparing spawn area: 84%",
            "[Server thread/INFO] [net.minecraft.server.MinecraftServer/]: mara joined the game",
            "[Render thread/INFO] [net.minecraft.client.gui.components.ChatComponent/]: [CHAT] The lanterns are lit.",
        ];
        string[] warn =
        [
            "[Render thread/WARN] [net.minecraft.client.renderer.texture.SpriteLoader/]: Texture lanterntweaks:block/hanging_lantern with size 18x16 limits mip level from 4 to 1",
            "[Worker-Main-2/WARN] [net.minecraft.world.item.crafting.RecipeManager/]: Parsing error loading recipe crewscripts:mill/flour: Unknown item 'farmersdelight:wheat_dough'",
            "[Server thread/WARN] [net.minecraft.server.MinecraftServer/]: Can't keep up! Is the server overloaded? Running 2041ms or 40 ticks behind",
        ];
        string[] error =
        [
            "[Render thread/ERROR] [net.minecraft.client.renderer.ShaderManager/]: Failed to load shader: valley_shaders:program/dusk_sky",
        ];
        var rng = new Random(7);
        for (var i = 0; i < 420; i++)
        {
            t = t.AddMilliseconds(rng.Next(40, 2400));
            var line = i % 97 == 45 ? error[0] : i % 23 == 11 ? warn[rng.Next(warn.Length)] : info[rng.Next(info.Length)];
            sb.Append('[').Append(t.ToString("dd MMM yyyy HH:mm:ss.fff")).Append("] ").Append(line).Append('\n');
        }
        File.WriteAllText(Path.Combine(logs, "latest.log"), sb.ToString());
        File.WriteAllText(Path.Combine(logs, "debug.log"), sb.ToString() + sb);

        var crashes = Path.Combine(game, "crash-reports");
        Directory.CreateDirectory(crashes);
        File.WriteAllText(Path.Combine(crashes, "crash-2026-09-23_22.41.07-client.txt"),
            "---- Minecraft Crash Report ----\n// Uh... Did I do that?\n\nTime: 2026-09-23 22:41:07\nDescription: Rendering overlay\n\n" +
            "java.lang.NullPointerException: Cannot invoke \"net.minecraft.world.level.Level.getBlockState\" because \"level\" is null\n" +
            "\tat lanterntweaks.client.LanternGlow.render(LanternGlow.java:88)\n");
    }
}

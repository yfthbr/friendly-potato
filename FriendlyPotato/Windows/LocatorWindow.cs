using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace FriendlyPotato.Windows;

public sealed class LocatorWindow : Window, IDisposable
{
    private const double HealthTrackInterval = 1;
    private const float BlockHeight = 80f;
    private readonly Configuration configuration;

    private readonly Vector4 green = new(0f, 0.7f, 0f, 1f);
    private readonly Dictionary<uint, HealthTrack> healths = new();
    private readonly Vector4 red = new(0.7f, 0f, 0f, 1f);
    private readonly Vector4 yellow = new(0.7f, 0.7f, 0f, 1f);
    private readonly Dictionary<ObjectLocation.Variant, ISharedImmediateTexture> arrowTextures = new();
    private readonly ISharedImmediateTexture defaultArrowTexture = FriendlyPotato.TextureProvider.GetFromFile(FriendlyPotato.AssetPath("_bluearrow.png"));

    // We give this window a constant ID using ###
    // This allows for labels being dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public LocatorWindow(FriendlyPotato plugin) : base(
        "Friendly Potato Hunt Locator###FriendlyPotatoHuntLocator")
    {
        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground;
        SizeCondition = ImGuiCond.Always;
        PositionCondition = ImGuiCond.Always;
        configuration = plugin.Configuration;

        arrowTextures[ObjectLocation.Variant.SRank] = FriendlyPotato.TextureProvider.GetFromFile(FriendlyPotato.AssetPath("_arrow.png"));
        arrowTextures[ObjectLocation.Variant.ARank] = FriendlyPotato.TextureProvider.GetFromFile(FriendlyPotato.AssetPath("_purplearrow.png"));
        arrowTextures[ObjectLocation.Variant.Fate] = FriendlyPotato.TextureProvider.GetFromFile(FriendlyPotato.AssetPath("_greenarrow.png"));
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        base.PreDraw();

        var fateCount = configuration.FateLocatorEnabled ? FriendlyPotato.VisibleFates.Count : 0;
        var arrowCount = FriendlyPotato.VisibleHunts.Count + fateCount;
        var screenPos = ImGuiHelpers.MainViewport.Pos;
        var screenSize = ImGuiHelpers.MainViewport.Size;
        PositionCondition = ImGuiCond.Always;
        var windowHeight = arrowCount * BlockHeight;
        var yOffset = configuration.ExpandLocatorUp ? windowHeight : 0;
        Position = screenPos
                   // Center, accounting for list alignment offset
                   + new Vector2(screenSize.X / 2, (screenSize.Y / 2) - yOffset)
                   // User-configured position
                   + new Vector2(
                       screenSize.X / 100 * configuration.LocatorOffsetX / 2,
                       screenSize.Y / 100 * configuration.LocatorOffsetY / 2);

        // Minimum width to fit: Sanu Vali of Dancing Wings + some more room for initial target
        Size = new Vector2(266f, windowHeight);
        if (configuration.HuntLocatorBackgroundEnabled && arrowCount > 0)
            Flags &= ~ImGuiWindowFlags.NoBackground;
        else
            Flags |= ImGuiWindowFlags.NoBackground;

        // Workaround some naughty plugins impacting the window size
        if (arrowCount > 0)
            Flags &= ~ImGuiWindowFlags.NoInputs;
        else
            Flags |= ImGuiWindowFlags.NoInputs;
    }

    public override void Draw()
    {
        var visible = new List<uint>(FriendlyPotato.VisibleHunts);
        if (configuration.FateLocatorEnabled)
            visible.AddRange(FriendlyPotato.VisibleFates.Select(fId => fId + FriendlyPotato.FateOffset));
        if (configuration.TreasureLocatorEnabled)
            visible.AddRange(FriendlyPotato.VisibleTreasure.Select(id => id + FriendlyPotato.TreasureOffset));

        if (visible.Count == 0)
        {
            healths.Clear();
            return;
        }

        var playerHeight = FriendlyPotato.LastPlayerPosition.Y;

        foreach (var id in visible)
        {
            if (!FriendlyPotato.ObjectLocations.TryGetValue(id, out var obj)) continue;

            if (obj.Distance < 0f) return;

            if (!arrowTextures.TryGetValue(obj.Type, out var sharedTexture))
            {
                sharedTexture = defaultArrowTexture;
            }

            var texture = sharedTexture.GetWrapOrDefault();
            if (texture == null)
            {
                FriendlyPotato.PluginLog.Warning("Could not find texture for type `{0}`", obj.Type);
                return;
            }

            DrawImageRotated(texture, obj.Angle);
            ImGui.SameLine();

            var killTimeEstimate = EstimateKillTime(id, obj.Health);
            var estimatedTime = killTimeEstimate > 0 ? $"(est. {EstimateString(killTimeEstimate)})" : "";
            var hp = obj.Health < 100f ? $"{obj.Health:F1}%" : $"{obj.Health:F0}%";
            var flag = FriendlyPotato.PositionToFlag(obj.Position);
            var text = $"\n{DurationString(obj.Duration)}(x {flag.X} , y {flag.Y}) {obj.Distance:F1}y {obj.Height - playerHeight:+0.0;-0.0;0.0}m";
            if (obj.Health >= 0)
            {
                using (ImRaii.PushColor(ImGuiCol.Text, killTimeEstimate switch
                {
                    0 => green,
                    < 10 => red,
                    < 30 => yellow,
                    _ => green
                }))
                {
                    text += $"\nHP {hp} {estimatedTime}";
                }
            }

            text += $"\n{obj.Target ?? " "}";

            if (SmallButton(obj.Name, Math.Abs(obj.Height - playerHeight) >= 100f))
            {
                FriendlyPotato.PluginLog.Debug("Clicked on " + obj.Name);
                FriendlyPotato.SetFocusTarget(obj);
                FriendlyPotato.SetMapFlag(obj.Position);
            }

            ImGui.SameLine(60, 0);

            ImGui.Text(text);

            ImGui.Spacing();
        }
    }

    private static bool SmallButton(string label, bool faded)
    {
        if (faded)
        {
            using var style = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.5f);
            return ImGui.SmallButton(label);
        }
        else
        {
            return ImGui.SmallButton(label);
        }
    }

    private static string DurationString(long duration)
    {
        if (duration < 0) return "";
        var minutes = Math.Floor(duration / 60f);
        var seconds = Math.Floor(duration % 60f);
        return $"{minutes:00}:{seconds:00} | ";
    }

    private static string EstimateString(double duration)
    {
        if (duration < 0) return "";
        var minutes = Math.Floor(duration / 60f);
        var seconds = Math.Floor(duration % 60f);
        return minutes >= 1 ? $"{minutes:F0}m{seconds:F0}s" : $"{seconds:F0}s";
    }

    private double EstimateKillTime(uint huntId, float currentHealth)
    {
        if (currentHealth < 0) return 0;

        if (healths.TryGetValue(huntId, out var health))
        {
            if (DateTime.Now - health.When > TimeSpan.FromSeconds(HealthTrackInterval))
                health.AddHealthDiff(currentHealth);
        }
        else
        {
            health = new HealthTrack(currentHealth, DateTime.Now, huntId switch
            {
                >= FriendlyPotato.FateOffset => 185,
                _ => 35
            });
            healths.Add(huntId, health);
        }

        return health.EstimatedKillTime();
    }

    private static void DrawImageRotated(IDalamudTextureWrap texture, float angle)
    {
        var drawList = ImGui.GetWindowDrawList();

        var rotation = angle * MathF.PI / 180f; // Replace with your desired rotation angle

        // Get the image size and center position
        var imageSize = new Vector2(texture.Width, texture.Height);
        var center = ImGui.GetCursorScreenPos() + (imageSize / 2);

        // Calculate cosine and sine of the rotation angle
        var cos = MathF.Cos(rotation);
        var sin = MathF.Sin(rotation);

        // Define half-size for convenience
        var halfSize = imageSize / 2;

        // Calculate rotated vertex positions
        var vertices = new Vector2[4];
        vertices[0] = center + new Vector2((-halfSize.X * cos) - (-halfSize.Y * sin),
                                           (-halfSize.X * sin) + (-halfSize.Y * cos));
        vertices[1] = center + new Vector2((halfSize.X * cos) - (-halfSize.Y * sin),
                                           (halfSize.X * sin) + (-halfSize.Y * cos));
        vertices[2] = center + new Vector2((halfSize.X * cos) - (halfSize.Y * sin),
                                           (halfSize.X * sin) + (halfSize.Y * cos));
        vertices[3] = center + new Vector2((-halfSize.X * cos) - (halfSize.Y * sin),
                                           (-halfSize.X * sin) + (halfSize.Y * cos));

        drawList.AddImageQuad(texture.Handle, vertices[0], vertices[1], vertices[2], vertices[3]);

        ImGui.Dummy(new Vector2(texture.Width, texture.Height));
    }

    private class HealthTrack
    {
        private readonly float[] healthDifferences;
        private readonly TimeSpan[] healthTrackDurations;
        private readonly int trackedWindows;
        private int filled;
        public float Health;
        public DateTime When;

        public HealthTrack(float initialHealth, DateTime when, int trackedWindows = 35)
        {
            this.trackedWindows = trackedWindows;
            healthDifferences = new float[trackedWindows];
            healthTrackDurations = new TimeSpan[trackedWindows];
            When = when;
            Health = initialHealth;
        }

        public void AddHealthDiff(float newHealth)
        {
            if (newHealth > Health || newHealth > 99.99f)
            {
                Health = newHealth;
                When = DateTime.Now;
                filled = 0;
                return;
            }

            for (var i = filled - 1; i > 0; i--)
            {
                healthDifferences[i] = healthDifferences[i - 1];
                healthTrackDurations[i] = healthTrackDurations[i - 1];
            }

            var diff = Health - newHealth;
            var now = DateTime.Now;

            healthDifferences[0] = diff;
            healthTrackDurations[0] = now - When;
            When = now;
            Health = newHealth;
            if (filled < trackedWindows) filled++;
        }

        public double EstimatedKillTime()
        {
            var healthDifference = 0f;
            var time = TimeSpan.Zero;
            for (var i = 0; i < filled; i++)
            {
                var multiplier = time.TotalMilliseconds switch
                {
                    < 8000 => 4,
                    < 15000 => 3,
                    _ => 1
                };

                healthDifference += multiplier * healthDifferences[i];
                time += multiplier * healthTrackDurations[i];
            }

            if (healthDifference == 0f) return 0;

            var timeToKill = Health / healthDifference * time.TotalMilliseconds / 1000f;
            var timeSinceLastInterval = (DateTime.Now - When).TotalMilliseconds / 1000f;
            return timeToKill - timeSinceLastInterval;
        }
    }
}

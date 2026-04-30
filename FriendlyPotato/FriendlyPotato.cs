using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FriendlyPotato.Windows;
using Lumina.Excel.Sheets;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace FriendlyPotato;

// ReSharper disable once ClassNeverInstantiated.Global - instantiated by Dalamud
public sealed partial class FriendlyPotato : IDalamudPlugin
{
    private const string CommandName = "/friendlypotato";
    private const string DebugCommandName = "/fpotdbg";
    private const string VisiblePlayers = "Visible Players: ";
    private const string Friends = "Friends: ";
    private const string Dead = "Dead: ";
    private const string OffWorlders = "Off-Worlders: ";
    private const string Wees = "Wees: ";
    private const string Doomed = "Doomed: ";
    private const string DrawDistance = "Draw Distance: ";
    private const ushort SeColorWineRed = 14;
    private const ushort SeColorWhite = 64;
    public const uint FateOffset = 0x10000000;
    public const uint TreasureOffset = 0x20000000;
    private const uint RenderFlagHidden = 2048;
    private readonly uint[] aRanks;
    private readonly uint[] bRanks;

    private readonly Payload deadIcon = new IconPayload(BitmapFontIcon.Disconnecting);
    private readonly Payload doomedIcon = new IconPayload(BitmapFontIcon.OrangeDiamond);
    private readonly Payload drawDistanceIcon = new IconPayload(BitmapFontIcon.CameraMode);
    private readonly Payload dtrSeparator = new TextPayload("  ");
    private readonly Payload friendIcon = new IconPayload(BitmapFontIcon.Returner);

    private readonly ByteColor highlightColor = new() { R = 240, G = 0, B = 0, A = 155 };

    private readonly uint[] interestingFates =
    [
        // Each unique position has its own fate id
        1862, // Drink
        1871, // Snek
        1922, // Mica
        831,  // Cerf's up
        877,  // Prey online
        878,  // Prey online
        879,  // Prey online
        1431, // Archie 1/2
        1432, // Archie 2/2
        196,  // Odin central
        197,  // Odin central
        198,  // Odin east
        199,  // Odin east
        200,  // Odin east
        201,  // Odin south
        202,  // Odin south
        203,  // Odin south
        204,  // Odin south
        205,  // Odin north
        206,  // Odin north
        207,  // Odin north
        1106, // Foxy Lady
        1107, // Foxy Lady
        1108, // Foxy Lady
        1855, // Chi
        505,  // Behemoth 1/2
        506,  // Behemoth 2/2
        1103, // Ixion
        1104, // Ixion
        1105, // Ixion
        1464, // Formi
        1763, // Dave
        902,  // Coeurlregina 1/3
        903,  // Coeurlregina 2/3
        904,  // Coeurlregina 2/3
        905,  // Coeurlregina 3/3
        906,  // Coeurlregina 3/3
        907,  // Coeurlregina 3/3
        1259  // Tribute
    ];

    private readonly Payload offWorldIcon = new IconPayload(BitmapFontIcon.CrossWorld);
    private readonly Payload playerIcon = new IconPayload(BitmapFontIcon.AnyClass);

    private readonly PlayerInformation playerInformation = new();
    private readonly uint[] sRanks;
    private readonly ByteColor unknownHighlightColor = new() { R = 240, G = 240, B = 50, A = 155 };

    public readonly Version Version = Assembly.GetExecutingAssembly().GetName().Version!;
    private readonly Payload weesIcon = new IconPayload(BitmapFontIcon.Meteor);

    public readonly WindowSystem WindowSystem = new("FriendlyPotato");
    private readonly CommandPanel commandPanel;

    public FriendlyPotato()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ConfigWindow = new ConfigWindow(this);
        PlayerListWindow = new PlayerListWindow(this, playerInformation);
        LocatorWindow = new LocatorWindow(this);
        commandPanel = new(Configuration);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(PlayerListWindow);
        WindowSystem.AddWindow(LocatorWindow);
        WindowSystem.AddWindow(commandPanel);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open settings for FriendlyPotato"
        });

        CommandManager.AddHandler(DebugCommandName, new CommandInfo(OnDebugCommand)
        {
            HelpMessage = "Print some info in debug log"
        });

        RuntimeData = new RuntimeDataManager(PluginInterface, PluginLog);

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += () => PlayerListWindow.Toggle();

        ClientState.Logout += Logout;

        NearbyDtrBarEntry = DtrBar.Get("FriendlyPotatoNearby");
        NearbyDtrBarEntry.OnClick += _ =>
        {
            if (KeyState[VirtualKey.CONTROL])
            {
                ToggleConfigUi();
                return;
            }

            PlayerListWindow.Toggle();
        };

        Framework.Update += FrameworkOnUpdateEvent;

        var hunts = NotoriousMonsters();
        sRanks = hunts.SRanks;
        aRanks = hunts.ARanks;
        bRanks = hunts.BRanks;

        // ZoneInit occurs also on instance hops, TerritoryChanged only occurs when changing areas altogether
        ClientState.ZoneInit += ZoneInit;

        AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "CrossWorldLinkshell", OnCWLSEvent);
        AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "CrossWorldLinkshell", HighlightCrossworldLinkshellUsers);
        AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "LinkShell", OnLSEvent);
        AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "LinkShell", HighlightLinkshellUsers);
    }

    public void Dispose()
    {
        Framework.Update -= FrameworkOnUpdateEvent;
        ClientState.Logout -= Logout;

        WindowSystem.RemoveAllWindows();

        NearbyDtrBarEntry.Remove();
        ConfigWindow.Dispose();
        PlayerListWindow.Dispose();
        LocatorWindow.Dispose();
        commandPanel.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(DebugCommandName);

        ClientState.ZoneInit -= ZoneInit;

        AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "CrossWorldLinkshell", OnCWLSEvent);
        AddonLifecycle.UnregisterListener(AddonEvent.PostDraw, "CrossWorldLinkshell", HighlightCrossworldLinkshellUsers);
        AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, "LinkShell", OnLSEvent);
        AddonLifecycle.UnregisterListener(AddonEvent.PostDraw, "LinkShell", HighlightLinkshellUsers);
    }

    public static RuntimeDataManager RuntimeData { get; private set; } = null!;

    public static ConcurrentDictionary<uint, ObjectLocation> ObjectLocations { get; private set; } = [];
    public static ImmutableList<uint> VisibleHunts { get; private set; } = ImmutableList<uint>.Empty;
    public static ImmutableList<uint> VisibleFates { get; private set; } = ImmutableList<uint>.Empty;
    public static ImmutableList<uint> VisibleTreasure { get; private set; } = ImmutableList<uint>.Empty;

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    [PluginService]
    internal static IDtrBar DtrBar { get; private set; } = null!;

    [PluginService]
    internal static IObjectTable ObjectTable { get; private set; } = null!;

    [PluginService]
    internal static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    internal static ITargetManager TargetManager { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog PluginLog { get; private set; } = null!;

    [PluginService]
    internal static IKeyState KeyState { get; private set; } = null!;

    [PluginService]
    internal static ITextureProvider TextureProvider { get; private set; } = null!;

    [PluginService]
    internal static IChatGui ChatGui { get; private set; } = null!;

    [PluginService]
    internal static IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    internal static IGameGui GameGui { get; private set; } = null!;

    [PluginService]
    internal static IFateTable FateTable { get; private set; } = null!;

    [PluginService]
    internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    [PluginService]
#pragma warning disable Dalamud001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    internal static IReliableFileStorage ReliableFileStorage { get; private set; } = null!;
#pragma warning restore Dalamud001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

    public Configuration Configuration { get; init; }
    private ConfigWindow ConfigWindow { get; init; }
    private PlayerListWindow PlayerListWindow { get; init; }
    private LocatorWindow LocatorWindow { get; init; }
    private IDtrBarEntry NearbyDtrBarEntry { get; set; }
    public static Vector3 LastPlayerPosition { get; set; } = Vector3.Zero;

    private static void OnCWLSEvent(AddonEvent _, AddonArgs __)
    {
        ProcessCrossworldLinkshellUsers();
    }

    private static void OnLSEvent(AddonEvent _, AddonArgs __)
    {
        ProcessLinkshellUsers();
    }

    private void ZoneInit(ZoneInitEventArgs args)
    {
        ObjectLocations.Clear();
    }

    public static string AssetPath(string assetName)
    {
        return Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, assetName);
    }

    private void Logout(int _, int __)
    {
        if (PlayerListWindow.IsOpen) PlayerListWindow.Toggle();
        Framework.RunOnFrameworkThread(() =>
        {
            VisibleHunts = ImmutableList<uint>.Empty;
            ObjectLocations = [];
        });
    }

    private void FrameworkOnUpdateEvent(IFramework framework)
    {
        EnsureIsOnFramework();
        if (!ClientState.IsLoggedIn || ObjectTable.LocalPlayer == null) return;
        LastPlayerPosition = ObjectTable.LocalPlayer.Position;
        UpdatePlayerList();
        UpdateDtrBar();

        if (LocatorWindow.IsOpen != Configuration.ShowHuntLocator) LocatorWindow.Toggle();

        UpdateVisibleHunts();
        UpdateVisibleFates();
        UpdateVisibleTreasure();
    }

    private static bool IsTreasureHuntArea()
    {
        return ClientState.MapId == 967; // Occult Crescent
    }

    private static unsafe bool IsVisible(IGameObject gameObject)
    {
        var csObject = (GameObject*)gameObject.Address;
        return (csObject->RenderFlags & VisibilityFlags.Model) == 0;
    }

    private static bool IsTreasureCoffer(IGameObject o)
    {
        switch (o.ObjectKind)
        {
            case ObjectKind.Treasure:
            case ObjectKind.EventObj
                when o.BaseId is 2010139 /* Name "Destination" carrot */ or 2014695 /* Survey Point */
                         or 2014743 /* Pot coffer */:
                return IsVisible(o);
            default:
                return false;
        }
    }

    private static string TreasureName(IGameObject o)
    {
        return o.BaseId switch
        {
            2010139 => "Carrot",
            2014695 => "Survey Point",
            _ => o.Name.TextValue
        };
    }

    private void UpdateVisibleTreasure()
    {
        EnsureIsOnFramework();

        List<uint> visible = [];
        if (IsTreasureHuntArea())
        {
            foreach (var obj in ObjectTable.Where(IsTreasureCoffer))
            {
                if (!VisibleTreasure.Contains(obj.BaseId) && Configuration.TreasureSoundEnabled)
                    UIGlobals.PlayChatSoundEffect(6);

                var objLoc = new ObjectLocation
                {
                    Angle = (float)CameraAngles.AngleToTarget(obj.Position, CameraAngles.OwnCamAngle()),
                    Distance = DistanceToTarget(obj.Position),
                    Position = new Vector2(obj.Position.X, obj.Position.Z),
                    Height = obj.Position.Y,
                    Name = TreasureName(obj),
                    Type = ObjectLocation.Variant.Treasure
                };
                ObjectLocations[obj.BaseId + TreasureOffset] = objLoc;
                visible.Add(obj.BaseId);
            }
        }

        VisibleTreasure = visible.ToImmutableList();
    }

    private void UpdateVisibleFates()
    {
        EnsureIsOnFramework();

        List<uint> visible = [];
        // Process interesting fates that have a position set
        foreach (var fate in FateTable.Where(f => !f.Position.Equals(Vector3.Zero) &&
                                                  interestingFates.Contains(f.FateId)))
        {
            var pos = new Vector2(fate.Position.X, fate.Position.Z);

            if (!VisibleFates.Contains(fate.FateId))
            {
                if (Configuration.FateSoundEnabled) UIGlobals.PlayChatSoundEffect(3);

                if (Configuration.FateChatEnabled)
                    SendChatFlag(pos, GetInstance(), $"A FATE catches your eye... {fate.Name.TextValue}", SeColorWhite);
            }

            var objLoc = new ObjectLocation
            {
                Angle = (float)CameraAngles.AngleToTarget(fate.Position, CameraAngles.OwnCamAngle()),
                Distance = DistanceToTarget(fate.Position),
                Position = pos,
                Height = fate.Position.Y,
                Name = fate.Name.TextValue,
                Type = ObjectLocation.Variant.Fate,
                Health = 100f - fate.Progress,
                Duration = fate.TimeRemaining
            };
            ObjectLocations[fate.FateId + FateOffset] = objLoc;
            visible.Add(fate.FateId);
        }

        VisibleFates = visible.ToImmutableList();
    }

    private void UpdateVisibleHunts()
    {
        EnsureIsOnFramework();

        List<uint> visible = [];
        foreach (var mob in ObjectTable.Skip(1).OfType<IBattleNpc>())
        {
            var pos = new Vector2(mob.Position.X, mob.Position.Z);
            var previouslyVisible = VisibleHunts.Contains(mob.BaseId);

            var instance = GetInstance();

            ObjectLocation.Variant variant;
            if (sRanks.Contains(mob.BaseId))
            {
                variant = ObjectLocation.Variant.SRank;

                if (!previouslyVisible)
                {
                    if (Configuration.SChatLocatorEnabled)
                    {
                        SendChatFlag(pos, instance, $"You sense the presence of a powerful mark... {mob.Name}",
                                     SeColorWineRed);
                    }

                    if (Configuration.SRankSoundEnabled)
                        UIGlobals.PlayChatSoundEffect(2);
                }
            }
            else if (aRanks.Contains(mob.BaseId))
            {
                variant = ObjectLocation.Variant.ARank;

                if (!previouslyVisible)
                {
                    if (Configuration.ChatLocatorARanksEnabled)
                        SendChatFlag(pos, instance, $"A-rank detected... {mob.Name}", 1);

                    if (Configuration.ARankSoundEnabled)
                        UIGlobals.PlayChatSoundEffect(2);
                }
            }
            else if (bRanks.Contains(mob.BaseId) && Configuration.ShowBRanks)
                variant = ObjectLocation.Variant.BRank;
            else
            {
                // Not interested
                continue;
            }

            string? targetPlayer = null;
            if (ObjectLocations.TryGetValue(mob.BaseId, out var previousLocation))
            {
                if (mob.StatusFlags.HasFlag(StatusFlags.InCombat))
                {
                    // Preserving previous target, if in combat
                    if (previousLocation.Target is not null)
                        targetPlayer = previousLocation.Target;

                    // Ping when entering combat
                    if (!previousLocation.Status.HasFlag(StatusFlags.InCombat) && Configuration.PingOnPull)
                        UIGlobals.PlayChatSoundEffect(10);
                }
            }
            // Found in combat ping
            else if (mob.StatusFlags.HasFlag(StatusFlags.InCombat))
            {
                if (Configuration.PingOnPull)
                    UIGlobals.PlayChatSoundEffect(10);
            }

            if (targetPlayer is null && mob.TargetObject is IPlayerCharacter target)
                targetPlayer = $"{target.Name.TextValue}@{HomeWorldName(target.HomeWorld.RowId)}";

            var objLoc = new ObjectLocation
            {
                Angle = (float)CameraAngles.AngleToTarget(mob.Position, CameraAngles.OwnCamAngle()),
                Distance = DistanceToTarget(mob.Position),
                Position = pos,
                Height = mob.Position.Y,
                Name = mob.Name.TextValue,
                Type = variant,
                Health = 100f * mob.CurrentHp / mob.MaxHp,
                Target = targetPlayer,
                Status = mob.StatusFlags
            };
            ObjectLocations[mob.BaseId] = objLoc;
            visible.Add(mob.BaseId);
        }

#if DEBUG
        if (Configuration.DebugList)
        {
            var target = ObjectTable.LocalPlayer!.TargetObject;
            if (target is IBattleNpc mob)
            {
                var pos = new Vector2(mob.Position.X, mob.Position.Z);

                var objLoc = new ObjectLocation
                {
                    Angle = (float)CameraAngles.AngleToTarget(mob.Position, CameraAngles.OwnCamAngle()),
                    Distance = DistanceToTarget(mob.Position),
                    Position = pos,
                    Name = mob.Name.TextValue,
                    Type = ObjectLocation.Variant.SRank
                };
                visible.Add(mob.BaseId);
                ObjectLocations[mob.BaseId] = objLoc;
            }
        }
#endif

        VisibleHunts = visible.ToImmutableList();
    }

    private void UpdatePlayerList()
    {
        EnsureIsOnFramework();
        playerInformation.Players = ObjectTable
                                    .Skip(1).OfType<IPlayerCharacter>()
                                    .Where(p => !string.IsNullOrEmpty(p.Name.TextValue))
                                    .Select(p => new PlayerCharacterDetails
                                    {
                                        Character = p
                                    }).ToImmutableList();
        foreach (var player in playerInformation.Players)
        {
            var nameKey =
                $"{player.Character.Name.TextValue}@{player.Character.HomeWorld.Value.Name.ToDalamudString().TextValue}";
            RuntimeData.MarkSeen(nameKey, false);
            playerInformation.SeenHistory[nameKey] = DateTime.Now;
        }

        if (Configuration.DtrDrawDistanceEnabled)
        {
            playerInformation.FurthestEntity = ObjectTable.Aggregate((double)0, (acc, x) =>
            {
                var dist = DistanceToTarget(x.Position);
                return dist > acc ? dist : acc;
            });
        }

        playerInformation.ClearOld();

        UpdatePlayerTypes();
    }

    private void UpdatePlayerTypes()
    {
        EnsureIsOnFramework();
        const uint weeEaId = 423;

        var friends = 0;
        var dead = 0;
        var offWorlders = 0;
        // Local player is not included in Players list, so include own wee here
        var wees = ObjectTable.LocalPlayer!.CurrentMinion?.ValueNullable?.RowId == weeEaId ? 1 : 0;
        var doomed = 0;
        var raised = 0;

        foreach (var player in playerInformation.Players)
        {
            var minion = player.Character.CurrentMinion;
            if (minion?.RowId == weeEaId) wees++;

            if (player.Character.IsDead)
            {
                player.AddKind(PlayerCharacterKind.Dead);
                dead++;
            }

            if (player.Character.HomeWorld.RowId != ObjectTable.LocalPlayer.CurrentWorld.RowId)
            {
                player.AddKind(PlayerCharacterKind.OffWorlder);
                offWorlders++;
            }

            if (player.Character.StatusFlags.HasFlag(StatusFlags.Friend))
            {
                player.AddKind(PlayerCharacterKind.Friend);
                friends++;
            }

            int processed = 0;
            try
            {
                foreach (var status in player.Character.StatusList)
                {
                    processed++;
                    if (Configuration.DebugStatuses && status.RemainingTime > 0)
                    {
                        PluginLog.Verbose(
                            $"Player {player.Character.Name} @ {player.Character.HomeWorld} has status {status.StatusId} - remaining: {status.RemainingTime}");
                    }

                    switch (status.StatusId)
                    {
                        case 148 or 1140:
                            player.Raised = true;
                            raised++;
                            break;
                        case 1970:
                            player.Doomed = true;
                            player.AddKind(PlayerCharacterKind.Doomed);
                            doomed++;
                            break;
                    }
                }
            }
            catch (IndexOutOfRangeException ex)
            {
                PluginLog.Error($"Error processing statuses for player {player.Character.Name}: index was out of range. {player.Character.StatusList.Length} statuses reported. {processed} statuses processed before error. Exception: {ex}");
            }
        }

        playerInformation.Friends = friends;
        playerInformation.Dead = dead;
        playerInformation.OffWorlders = offWorlders;
        playerInformation.Wees = wees;
        playerInformation.Doomed = doomed;
        playerInformation.Raised = raised;
    }

    private void UpdateDtrBar()
    {
        // const string dtrSeparator = "  ";
        const char tooltipSeparator = '\n';

        if (!Configuration.DtrEnabled)
        {
            NearbyDtrBarEntry.Shown = false;
            return;
        }

        NearbyDtrBarEntry.Shown = true;
        EnsureIsOnFramework();

        var tooltip = new StringBuilder("");
        List<Payload> payloads = [];
        if (Configuration.DtrTotalEnabled)
        {
            tooltip.Append(tooltipSeparator).Append(VisiblePlayers).Append(playerInformation.Total);
            payloads.Add(dtrSeparator);
            payloads.Add(playerIcon);
            payloads.Add(new TextPayload(playerInformation.Total.ToString()));
        }

        if (Configuration.DtrFriendsEnabled)
        {
            tooltip.Append(tooltipSeparator).Append(Friends).Append(playerInformation.Friends);
            payloads.Add(dtrSeparator);
            payloads.Add(friendIcon);
            payloads.Add(new TextPayload(playerInformation.Friends.ToString()));
        }

        if (Configuration.DtrOffWorldEnabled)
        {
            tooltip.Append(tooltipSeparator).Append(OffWorlders).Append(playerInformation.OffWorlders);
            payloads.Add(dtrSeparator);
            payloads.Add(offWorldIcon);
            payloads.Add(new TextPayload(playerInformation.OffWorlders.ToString()));
        }

        if (Configuration.DtrDeadEnabled)
        {
            tooltip.Append(tooltipSeparator).Append(Dead).Append(playerInformation.Dead);
            payloads.Add(dtrSeparator);
            payloads.Add(deadIcon);
            payloads.Add(new TextPayload(playerInformation.Dead.ToString()));
        }

        if (Configuration.DtrDoomEnabled)
        {
            tooltip.Append(tooltipSeparator).Append(Doomed).Append(playerInformation.Doomed);
            payloads.Add(dtrSeparator);
            payloads.Add(doomedIcon);
            payloads.Add(new TextPayload(playerInformation.Doomed.ToString()));
        }

        if (Configuration.DtrShowWees)
        {
            tooltip.Append(tooltipSeparator).Append(Wees).Append(playerInformation.Wees);
            payloads.Add(dtrSeparator);
            payloads.Add(weesIcon);
            payloads.Add(new TextPayload(playerInformation.Wees.ToString()));
        }

        if (Configuration.DtrDrawDistanceEnabled)
        {
            tooltip.Append(tooltipSeparator).Append(DrawDistance)
                   .Append(playerInformation.FurthestEntityString);
            payloads.Add(dtrSeparator);
            payloads.Add(drawDistanceIcon);
            payloads.Add(
                new TextPayload(playerInformation.FurthestEntityString));
        }

        NearbyDtrBarEntry.Text = new SeString(payloads.Skip(1).ToList());
        NearbyDtrBarEntry.Tooltip = new SeString(new TextPayload(tooltip.ToString().Trim()));
    }

    public static double DistanceToTarget(Vector3 pos)
    {
        return Vector2.Distance(
            new Vector2(LastPlayerPosition.X,
                        LastPlayerPosition.Z),
            new Vector2(pos.X, pos.Z));
    }

    public static Vector2 PositionToFlag(Vector2 position)
    {
        var scale = 100f;
        var x = position.X;
        var y = position.Y;
        // Handle special scaling for certain maps like HW where flags go to 44x44 instead of the standard 42x42
        if (DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>()?.TryGetRow(ClientState.MapId, out var mapRow) == true)
        {
            scale = mapRow.SizeFactor;
            x += mapRow.OffsetX;
            y += mapRow.OffsetY;
        }

        return new Vector2(ScaleCoord(x), ScaleCoord(y));

        float ScaleCoord(float coord)
        {
            return (float)Math.Floor(((2048f / scale) + (coord / 50f) + 1f) * 10) / 10f;
        }
    }

    private static void SendChatFlag(Vector2 position, int? instance, string text, ushort colorKey = 0)
    {
        var flagCoords = PositionToFlag(position);
        var mapLink = SeString.CreateMapLinkWithInstance(ClientState.TerritoryType, ClientState.MapId, instance,
                                                         flagCoords.X, flagCoords.Y);
        var message = new SeStringBuilder();
        message.AddUiForeground(colorKey);
        message.AddText(text);
        message.AddUiForegroundOff();
        message.AddText(" - ");
        message.AddUiForeground(SeColorWhite);
        message.Append(mapLink);
        message.AddUiForegroundOff();
        ChatGui.Print(message.BuiltString);
    }

    internal static unsafe void SetMapFlag(Vector2 position)
    {
        // var flagCoords = PositionToFlag(position);
        // var mapLink = new MapLinkPayload(ClientState.TerritoryType, ClientState.MapId, flagCoords.X, flagCoords.Y);
        // if (!GameGui.OpenMapWithMapLink(mapLink))
        // {
        //     PluginLog.Error("Failed to open map with map link payload.");
        // }
        var map = AgentMap.Instance();
        if (map == null)
        {
            PluginLog.Error("Failed to open map: AgentMap instance is null.");
            return;
        }
        map->FlagMarkerCount = 0;
        map->SetFlagMapMarker(ClientState.TerritoryType, ClientState.MapId, position.X, position.Y);
        map->OpenMap(ClientState.MapId, ClientState.TerritoryType);
    }

    public static void EnsureIsOnFramework()
    {
        if (!Framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("This method must be called from the framework update thread.");
    }

    private void OnCommand(string command, string args)
    {
        PlayerListWindow.Toggle();
    }

    private void OnDebugCommand(string command, string args)
    {
        unsafe
        {
            // var player = (GameObject*)ObjectTable.LocalPlayer!.Address;
            // PluginLog.Debug($"Player object address: {(nint)player:X16}, vtable address {(nint)player->VirtualTable:X16}");
            // for (var i = 1; i <= 77; i++)
            // {
            //     var offset = i * sizeof(nint);
            //     var funcPtr = Marshal.ReadIntPtr((nint)player->VirtualTable + offset);
            //     PluginLog.Debug($"VTable[{i}] at offset {offset:X} : {(nint)funcPtr:X16}");
            // }

            var statuses = (StatusManager*)ObjectTable.LocalPlayer!.StatusList.Address;
            PluginLog.Debug($"Statuses count: {statuses->NumValidStatuses}");
        }

        // SendChatFlag(new Vector2(LastPlayerPosition.X, LastPlayerPosition.Z), GetInstance(), "Debug flag from /fpotdbg", SeColorWhite);

        // var env = System.Environment.GetEnvironmentVariables();
        // foreach (var key in env.Keys)
        // {
        //     PluginLog.Debug($"Env {key}: {env[key]}");
        // }

        // Framework.RunOnTick(() =>
        // {
        //     unsafe
        //     {
        //         // var control = Control.Instance();
        //         // PluginLog.Debug($"Control address: {(nint)control:X16} ; walking address: {(nint)control + 29976:X16}");
        //         // PluginLog.Debug($"control: {control->IsWalking} {Marshal.ReadByte((nint)control + 29976)} {Marshal.ReadByte((nint)control + 30260)} {Marshal.ReadByte((nint)control + 0x76a0)}");
        //         //control->IsWalking = true; // doesn't work during auto-run
        //         //Marshal.WriteByte((nint)control + 29976, 0x1); // works for both auto-run and manual movement
        //         var camera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance()->CurrentCamera;
        //         PluginLog.Debug($"Camera address: {(nint)camera:X16}");
        //         var bytes = new List<byte>();
        //         for (var i = 0; i < 256 /* size of Camera struct */; i++)
        //         {
        //             bytes.Add(Marshal.ReadByte((nint)camera + i));
        //         }
        //         PluginLog.Debug($"Camera bytes: {BitConverter.ToString(bytes.ToArray())}");
        //     }
        // }, TimeSpan.FromSeconds(1));
        // PluginLog.Debug($"Player's job abbreviation is {ObjectTable.LocalPlayer?.ClassJob.ValueNullable?.Abbreviation.ToString()}");
        // var pos = ObjectTable.LocalPlayer!.Position;
        // var posFlat = new Vector2(pos.X, pos.Z);
        // SendChatFlag(posFlat, GetInstance(), "Debug flag from /fpotdbg", SeColorWhite);
        // unsafe
        // {
        //     var addonPtr = GameGui.GetAddonByName("QuickPanel");
        //     if (addonPtr == nint.Zero) return;

        //     var addon = (AtkUnitBase*)addonPtr.Address;
        //     for (uint i = 0; i < addon->RootNode->ChildCount; i++)
        //     {
        //         var node = addon->GetComponentNodeById(i);
        //         PluginLog.Debug($"{i}: {(nint)node:X16}");
        //     }
        // }
        // PluginLog.Debug(
        //     $"Current Position: {posFlat} - {PositionToFlag(posFlat)} - {ClientState.TerritoryType} - {ClientState.MapId}");
        // var target = ObjectTable.LocalPlayer.TargetObject;
        // if (target is IBattleNpc npc)
        // {
        //     PluginLog.Debug(
        //         $"Name: {npc.Name} - DataId: {npc.BaseId} - is A rank? {aRanks.Contains(npc.BaseId)} - is S rank? {sRanks.Contains(npc.BaseId)}");
        // }

        // if (target is IBattleChara targetCharacter)
        // {
        //     PluginLog.Debug($"Statuses {targetCharacter.StatusList.Length}");
        //     PluginLog.Debug(
        //         $"{string.Join(", ", targetCharacter.StatusList.Select(x => $"{x.StatusId} remaining {x.RemainingTime}"))}");
        // }


        // if (target != null)
        // {
        //     PluginLog.Debug(
        //         $"Name: {target.Name} - DataId: {target.BaseId} - {target.EntityId} - Kind: {target.ObjectKind}, {target.SubKind}");
        // }

        // PluginLog.Debug($"Visible Hunts: {string.Join(", ", VisibleHunts)}");
        // PluginLog.Debug($"Visible Fates: {string.Join(", ", VisibleFates)}");

        // PluginLog.Debug($"Config dir: {PluginInterface.GetPluginConfigDirectory()}");


        // // Time remaining = seconds left
        // foreach (var fate in FateTable)
        //     PluginLog.Debug(
        //         $"{fate.FateId} - {fate.Name.TextValue} - {fate.TimeRemaining} - {fate.Progress} - {fate.HandInCount}");

        // Framework.RunOnFrameworkThread(() =>
        // {
        //     IGameObject? furthest = null;
        //     double howFar = 0;
        //     foreach (var o in ObjectTable)
        //     {
        //         var dist = DistanceToTarget(o.Position);
        //         if (dist < 10)
        //         {
        //             PluginLog.Debug(
        //                 $"{o.Name} - {o.Position} - {o.EntityId} - {o.BaseId} - {o.ObjectKind} - {o.SubKind} - {DistanceToTarget(o.Position)}y");
        //             unsafe
        //             {
        //                 var csObj = (GameObject*)o.Address;
        //                 PluginLog.Debug($"Render flags: {csObj->RenderFlags}");
        //             }

        //             PluginLog.Debug($"OwnerId: {o.OwnerId}");
        //         }

        //         if (dist > howFar)
        //         {
        //             furthest = o;
        //             howFar = dist;
        //         }
        //     }

        //     if (furthest != null)
        //     {
        //         PluginLog.Info(
        //             $"Furthest object {furthest.Name} {howFar}y away. {ObjectTable.Count(o => o.Name != SeString.Empty)} non-zero objects visible");
        //     }
        // });

        // unsafe
        // {
        //     PluginLog.Debug($"Currently in instance: {UIState.Instance()->PublicInstance.InstanceId}");
        // }
    }

    private static (uint[] SRanks, uint[] ARanks, uint[] BRanks) NotoriousMonsters()
    {
        const byte sRank = 3;
        const byte aRank = 2;
        const byte bRank = 1;
        return (Ranks(sRank), Ranks(aRank), Ranks(bRank));

        uint[] Ranks(byte typeOfRank)
        {
            return DataManager.GetExcelSheet<NotoriousMonster>().Where(m => m.Rank == typeOfRank)
                              .Select(m => m.BNpcBase.ValueNullable?.RowId).Where(m => m.HasValue).Select(m => m!.Value)
                              .ToArray();
        }
    }

    private void DrawUi()
    {
        WindowSystem.Draw();
    }

    public void ToggleConfigUi()
    {
        ConfigWindow.Toggle();
    }

    private static unsafe void ProcessCrossworldLinkshellUsers()
    {
        if (InfoProxyCrossWorldLinkshell.Instance() == null ||
            AgentCrossWorldLinkshell.Instance() == null ||
            InfoProxyCrossWorldLinkshellMember.Instance() == null)
            return;

        foreach (var c in InfoProxyCrossWorldLinkshellMember.Instance()->CharDataSpan)
        {
            var charName = CharacterFullName(c);
            PluginLog.Debug($"Found cw linkshell user: {charName}");
            Framework.Run(() =>
            {
                if (c.State != InfoProxyCommonList.CharacterData.OnlineStatus.Offline) RuntimeData.MarkSeen(charName);
            });
        }
    }

    private static unsafe string? FindCurrentCrossworldLinkshellUser(string who)
    {
        if (InfoProxyCrossWorldLinkshellMember.Instance() == null)
        {
            PluginLog.Debug("Called to log cwls users but an instance was null");
            return null;
        }

        foreach (var c in InfoProxyCrossWorldLinkshellMember.Instance()->CharDataSpan)
            if (c.NameString == who)
                return CharacterFullName(c);

        return null;
    }

    private static unsafe void ProcessLinkshellUsers()
    {
        if (InfoProxyLinkshell.Instance() == null || InfoProxyLinkshellMember.Instance() == null ||
            AgentLinkshell.Instance() == null)
            return;

        foreach (var c in InfoProxyLinkshellMember.Instance()->CharDataSpan)
        {
            var charName = CharacterFullName(c);
            PluginLog.Debug($"Found linkshell user: {charName}");
            Framework.Run(() =>
            {
                if (c.State != InfoProxyCommonList.CharacterData.OnlineStatus.Offline) RuntimeData.MarkSeen(charName);
            });
        }
    }

    private unsafe void HighlightCrossworldLinkshellUsers(AddonEvent type, AddonArgs args)
    {
        if (!Configuration.HighlightInactive) return;

        var lsAddonPtr = GameGui.GetAddonByName("CrossWorldLinkshell");
        if (lsAddonPtr == nint.Zero) return;

        // TODO: use more managed/safe properties
        var lsAddon = (AtkUnitBase*)lsAddonPtr.Address;
        var componentList = lsAddon->GetComponentListById(36);
        if (componentList == null) return;

        foreach (nint i in Enumerable.Range(0, componentList->ListLength))
        {
            var renderer = componentList->ItemRendererList[i].AtkComponentListItemRenderer;
            if (renderer == null) continue;

            var text = renderer->GetTextNodeById(5);
            if (text == null) continue;

            var name = SeString.Parse(text->GetText().Value).TextValue;
            if (name.StartsWith('(')) continue;
            var runtimeDataName = FindCurrentCrossworldLinkshellUser(NameIconStripper().Replace(name, ""));
            if (runtimeDataName == null) continue;

            var lastSeen =
                RuntimeData.LastSeen(runtimeDataName);
            double days = -1;
            if (lastSeen != null) days = (DateTime.Now - lastSeen.Value).TotalDays;

            if (days < 0)
            {
                text->TextColor = unknownHighlightColor;
                text->SetText($"{name}");
            }
            else if (days >= Configuration.InactivityThreshold)
            {
                text->TextColor = highlightColor;
                text->SetText($"({days:F0}d) {name}");
            }
        }
    }

    private unsafe void HighlightLinkshellUsers(AddonEvent type, AddonArgs args)
    {
        if (!Configuration.HighlightInactive) return;

        var lsAddonPtr = GameGui.GetAddonByName("LinkShell");
        if (lsAddonPtr == nint.Zero) return;

        var lsAddon = (AtkUnitBase*)lsAddonPtr.Address;
        var componentList = lsAddon->GetComponentListById(22);
        if (componentList == null) return;

        foreach (nint i in Enumerable.Range(0, componentList->ListLength))
        {
            var renderer = componentList->ItemRendererList[i].AtkComponentListItemRenderer;
            if (renderer == null) continue;

            var text = renderer->GetTextNodeById(5);
            if (text == null) continue;

            var name = SeString.Parse(text->GetText().Value).TextValue;
            if (name.StartsWith('(')) continue;
            var compareName = NameIconStripper().Replace(name, "");

            var lastSeen =
                RuntimeData.LastSeen(
                    $"{compareName}@{ObjectTable.LocalPlayer!.HomeWorld.Value.Name.ToDalamudString().TextValue}");
            double days = -1;
            if (lastSeen != null) days = (DateTime.Now - lastSeen.Value).TotalDays;

            if (days < 0)
            {
                text->TextColor = unknownHighlightColor;
                text->SetText($"{name}");
            }
            else if (days >= Configuration.InactivityThreshold)
            {
                text->TextColor = highlightColor;
                text->SetText($"({days:F0}d) {name}");
            }
        }
    }

    private static unsafe int? GetInstance()
    {
        int? instance = null;
        try
        {
            instance = Convert.ToInt32(UIState.Instance()->PublicInstance.InstanceId);
            if (instance.Value == 0) instance = null;
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex.ToString());
        }

        return instance;
    }

    private static string CharacterFullName(InfoProxyCommonList.CharacterData c)
    {
        return $"{c.NameString}@{HomeWorldName(c.HomeWorld)}";
    }

    private static string HomeWorldName(uint id)
    {
        return DataManager.GetExcelSheet<World>().First(w => w.RowId == id).Name.ToDalamudString().TextValue;
    }

    [GeneratedRegex(@"^[^A-Z']")]
    private static partial Regex NameIconStripper();

    public static void SetFocusTarget(ObjectLocation objLoc)
    {
        Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                var obj = ObjectTable.Single(o => objLoc.Name == TreasureName(o) &&
                                                  Vector2.Distance(objLoc.Position,
                                                                   new Vector2(o.Position.X, o.Position.Z)) < 15);
                TargetManager.FocusTarget = obj;
            }
            catch (InvalidOperationException)
            {
                PluginLog.Debug($"Could not find object to focus target on for {objLoc.Name} at {PositionToFlag(objLoc.Position)}");
            }
        });
    }
}

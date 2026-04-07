using System;
using System.Collections.Generic;
using System.Linq;
using RagdollSystem.Animation;
using RagdollSystem.Core;
using RagdollSystem.Game;
using RagdollSystem.Gui;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace RagdollSystem;

public sealed class RagdollSystemPlugin : IDalamudPlugin
{
    private const string CommandName = "/ragdoll";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private readonly Configuration config;
    private readonly BoneTransformService boneTransformService;
    private readonly DeathDetector deathDetector;
    private readonly MainWindow mainWindow;

    // Player ragdoll controller (single instance)
    private RagdollController? playerRagdoll;

    // NPC ragdoll controllers (multiple concurrent)
    private readonly Dictionary<nint, RagdollController> npcRagdolls = new();
    // Track activation time for auto-cleanup
    private readonly Dictionary<nint, float> npcRagdollTimers = new();

    public RagdollSystemPlugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        IGameInteropProvider gameInterop,
        IGameGui gameGui,
        IChatGui chatGui,
        ICondition condition,
        ISigScanner sigScanner,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.clientState = clientState;
        this.framework = framework;
        this.chatGui = chatGui;
        this.log = log;

        Services.Init(pluginInterface, clientState, objectTable, framework,
            gameInterop, gameGui, chatGui, condition, log);

        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pluginInterface);

        boneTransformService = new BoneTransformService(gameInterop, sigScanner, log);
        deathDetector = new DeathDetector(clientState, objectTable, log);

        mainWindow = new MainWindow(config, this, clientState, log);

        // Wire death events
        deathDetector.OnPlayerDeath += OnPlayerDeath;
        deathDetector.OnPlayerRevive += OnPlayerRevive;
        deathDetector.OnNpcDeath += OnNpcDeath;

        // Register command
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Ragdoll System settings.",
        });

        pluginInterface.UiBuilder.Draw += OnDraw;
        pluginInterface.UiBuilder.OpenMainUi += OnOpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfig;
        framework.Update += OnFrameworkUpdate;
        clientState.TerritoryChanged += OnTerritoryChanged;

        log.Info("Ragdoll System loaded.");
    }

    public BoneTransformService BoneTransformService => boneTransformService;
    public RagdollController? PlayerRagdoll => playerRagdoll;

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        pluginInterface.UiBuilder.Draw -= OnDraw;
        pluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfig;
        commandManager.RemoveHandler(CommandName);

        deathDetector.OnPlayerDeath -= OnPlayerDeath;
        deathDetector.OnPlayerRevive -= OnPlayerRevive;
        deathDetector.OnNpcDeath -= OnNpcDeath;

        DeactivateAll();

        deathDetector.Dispose();
        mainWindow.Dispose();
        boneTransformService.Dispose();

        log.Info("Ragdoll System unloaded.");
    }

    private void OnCommand(string command, string args)
    {
        config.ShowMainWindow = !config.ShowMainWindow;
        config.Save();
    }

    private void OnDraw()
    {
        if (config.ShowMainWindow)
            mainWindow.Draw();
    }

    private void OnOpenMainUi()
    {
        config.ShowMainWindow = true;
        config.Save();
    }

    private void OnOpenConfig()
    {
        config.ShowMainWindow = true;
        config.Save();
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        try
        {
            deathDetector.Tick(config.EnableNpcDeathRagdoll);

            // Auto-cleanup NPC ragdolls after duration
            if (npcRagdolls.Count > 0)
            {
                var dt = 1.0f / 60.0f;
                var toRemove = new List<nint>();
                foreach (var kvp in npcRagdollTimers)
                {
                    npcRagdollTimers[kvp.Key] = kvp.Value + dt;
                    if (kvp.Value + dt >= config.RagdollDuration)
                        toRemove.Add(kvp.Key);
                }
                foreach (var addr in toRemove)
                    RemoveNpcRagdoll(addr);
            }

            // Auto-cleanup player ragdoll after duration
            if (playerRagdoll != null && playerRagdoll.IsActive)
            {
                // Use elapsed tracked internally; we track separately for player too
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Error in framework update");
        }
    }

    private void OnPlayerDeath(nint address)
    {
        if (!config.EnableRagdoll) return;

        log.Info($"Ragdoll: Player death detected, activating ragdoll");

        playerRagdoll?.Dispose();
        playerRagdoll = new RagdollController(boneTransformService, config, log);
        playerRagdoll.Activate(address);
    }

    private void OnPlayerRevive(nint address)
    {
        if (playerRagdoll == null) return;

        log.Info($"Ragdoll: Player revived, deactivating ragdoll");
        playerRagdoll.Dispose();
        playerRagdoll = null;
    }

    private void OnNpcDeath(nint address, uint gameObjectId)
    {
        if (!config.EnableRagdoll || !config.EnableNpcDeathRagdoll) return;

        // Cap concurrent NPC ragdolls
        if (npcRagdolls.Count >= config.MaxNpcRagdolls)
        {
            // Remove oldest
            var oldest = npcRagdollTimers.OrderByDescending(kvp => kvp.Value).First().Key;
            RemoveNpcRagdoll(oldest);
        }

        if (npcRagdolls.ContainsKey(address)) return;

        log.Info($"Ragdoll: NPC death detected at 0x{address:X}, activating ragdoll");

        var controller = new RagdollController(boneTransformService, config, log);
        controller.Activate(address, config.NpcRagdollActivationDelay);
        npcRagdolls[address] = controller;
        npcRagdollTimers[address] = 0f;
    }

    private void RemoveNpcRagdoll(nint address)
    {
        if (npcRagdolls.TryGetValue(address, out var controller))
        {
            controller.Dispose();
            npcRagdolls.Remove(address);
            npcRagdollTimers.Remove(address);
        }
    }

    private void DeactivateAll()
    {
        playerRagdoll?.Dispose();
        playerRagdoll = null;

        foreach (var controller in npcRagdolls.Values)
            controller.Dispose();
        npcRagdolls.Clear();
        npcRagdollTimers.Clear();
    }

    private void OnTerritoryChanged(ushort territoryId)
    {
        log.Info($"Territory changed to {territoryId} — deactivating all ragdolls.");
        DeactivateAll();
        deathDetector.Reset();
    }

    /// <summary>Manually trigger ragdoll on the player (for testing).</summary>
    public void ManualActivatePlayer()
    {
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) return;

        playerRagdoll?.Dispose();
        playerRagdoll = new RagdollController(boneTransformService, config, log);
        playerRagdoll.Activate(player.Address);
    }

    /// <summary>Manually deactivate player ragdoll.</summary>
    public void ManualDeactivatePlayer()
    {
        playerRagdoll?.Dispose();
        playerRagdoll = null;
    }
}

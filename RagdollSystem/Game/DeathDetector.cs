using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace RagdollSystem.Game;

/// <summary>
/// Monitors game state each framework tick to detect player and NPC deaths
/// by tracking HP transitions from >0 to 0.
/// </summary>
public class DeathDetector : IDisposable
{
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    // Track previous HP per entity (keyed by GameObjectId)
    private uint previousPlayerHp;
    private bool playerTracked;
    private readonly Dictionary<uint, uint> previousNpcHp = new();

    // Addresses of entities we've already fired death for (prevent re-triggering)
    private readonly HashSet<uint> firedDeathIds = new();

    /// <summary>Fired when the local player's HP transitions from >0 to 0.</summary>
    public event Action<nint>? OnPlayerDeath;

    /// <summary>Fired when the local player's HP transitions from 0 to >0 (revive).</summary>
    public event Action<nint>? OnPlayerRevive;

    /// <summary>Fired when a BattleNpc's HP transitions from >0 to 0. Args: (address, gameObjectId)</summary>
    public event Action<nint, uint>? OnNpcDeath;

    public DeathDetector(IClientState clientState, IObjectTable objectTable, IPluginLog log)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.log = log;
    }

    /// <summary>
    /// Call once per framework tick to check for death transitions.
    /// </summary>
    public void Tick(bool npcDetectionEnabled)
    {
        CheckPlayer();

        if (npcDetectionEnabled)
            CheckNpcs();
    }

    private unsafe void CheckPlayer()
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            playerTracked = false;
            return;
        }

        var currentHp = player.CurrentHp;
        var address = player.Address;

        if (playerTracked)
        {
            // Detect death: HP was >0, now 0
            if (previousPlayerHp > 0 && currentHp == 0)
            {
                log.Info($"DeathDetector: Player died (HP {previousPlayerHp} → 0)");
                OnPlayerDeath?.Invoke(address);
            }
            // Detect revive: HP was 0, now >0
            else if (previousPlayerHp == 0 && currentHp > 0)
            {
                log.Info($"DeathDetector: Player revived (HP 0 → {currentHp})");
                OnPlayerRevive?.Invoke(address);
            }
        }

        previousPlayerHp = currentHp;
        playerTracked = true;
    }

    private unsafe void CheckNpcs()
    {
        // Track which IDs we see this frame (for cleanup)
        var seenIds = new HashSet<uint>();

        foreach (var obj in objectTable)
        {
            if ((byte)obj.ObjectKind != (byte)ObjectKind.BattleNpc)
                continue;

            var battleNpc = obj as IBattleNpc;
            if (battleNpc == null) continue;

            var id = obj.GameObjectId;
            seenIds.Add((uint)id);

            var currentHp = battleNpc.CurrentHp;

            if (previousNpcHp.TryGetValue((uint)id, out var prevHp))
            {
                if (prevHp > 0 && currentHp == 0 && !firedDeathIds.Contains((uint)id))
                {
                    log.Info($"DeathDetector: NPC '{obj.Name}' died (HP {prevHp} → 0) at 0x{obj.Address:X}");
                    firedDeathIds.Add((uint)id);
                    OnNpcDeath?.Invoke(obj.Address, (uint)id);
                }
            }

            previousNpcHp[(uint)id] = currentHp;
        }

        // Clean up stale entries for objects that left the table
        var staleIds = new List<uint>();
        foreach (var id in previousNpcHp.Keys)
        {
            if (!seenIds.Contains(id))
                staleIds.Add(id);
        }
        foreach (var id in staleIds)
        {
            previousNpcHp.Remove(id);
            firedDeathIds.Remove(id);
        }
    }

    /// <summary>Reset all tracking state (e.g., on zone change).</summary>
    public void Reset()
    {
        playerTracked = false;
        previousPlayerHp = 0;
        previousNpcHp.Clear();
        firedDeathIds.Clear();
    }

    public void Dispose()
    {
        OnPlayerDeath = null;
        OnPlayerRevive = null;
        OnNpcDeath = null;
    }
}

using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using RagdollSystem.Core;

namespace RagdollSystem.Safety;

public unsafe class MovementBlockHook : IDisposable
{
    private readonly HashSet<nint> approachBlockedAddresses = new();
    private Hook<SetPositionDelegate>? setPositionHook;
    private Hook<SetRotationDelegate>? setRotationHook;
    private bool allowApproachUpdate;

    private delegate void SetPositionDelegate(GameObject* thisPtr, float x, float y, float z);
    private delegate void SetRotationDelegate(GameObject* thisPtr, float value);

    public bool IsBlocking { get; set; }

    public MovementBlockHook(IGameInteropProvider gameInterop, IPluginLog log)
    {
        try
        {
            setPositionHook = gameInterop.HookFromAddress<SetPositionDelegate>(
                (nint)GameObject.MemberFunctionPointers.SetPosition,
                SetPositionDetour);

            setRotationHook = gameInterop.HookFromAddress<SetRotationDelegate>(
                (nint)GameObject.MemberFunctionPointers.SetRotation,
                SetRotationDetour);

            setPositionHook.Enable();
            setRotationHook.Enable();
            log.Info("MovementBlockHook: Position/rotation hooks enabled.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "MovementBlockHook: Failed to create hooks.");
        }
    }

    public void AddApproachNpc(nint address) => approachBlockedAddresses.Add(address);
    public void RemoveApproachNpc(nint address) => approachBlockedAddresses.Remove(address);
    public void ClearApproachNpcs() => approachBlockedAddresses.Clear();

    public void SetApproachPosition(GameObject* obj, float x, float y, float z)
    {
        if (setPositionHook == null) return;
        allowApproachUpdate = true;
        setPositionHook.Original(obj, x, y, z);
        allowApproachUpdate = false;
    }

    public void SetApproachRotation(GameObject* obj, float value)
    {
        if (setRotationHook == null) return;
        allowApproachUpdate = true;
        setRotationHook.Original(obj, value);
        allowApproachUpdate = false;
    }

    private void SetPositionDetour(GameObject* thisPtr, float x, float y, float z)
    {
        if (IsBlocking && IsLocalPlayer(thisPtr)) return;
        if (!allowApproachUpdate && approachBlockedAddresses.Contains((nint)thisPtr)) return;
        setPositionHook!.Original(thisPtr, x, y, z);
    }

    private void SetRotationDetour(GameObject* thisPtr, float value)
    {
        if (IsBlocking && IsLocalPlayer(thisPtr)) return;
        if (!allowApproachUpdate && approachBlockedAddresses.Contains((nint)thisPtr)) return;
        setRotationHook!.Original(thisPtr, value);
    }

    private static bool IsLocalPlayer(GameObject* obj)
    {
        var player = Services.ObjectTable.LocalPlayer;
        return player != null && (nint)obj == player.Address;
    }

    public void Dispose()
    {
        Reset();
        setPositionHook?.Dispose();
        setRotationHook?.Dispose();
    }

    public void Reset()
    {
        IsBlocking = false;
        allowApproachUpdate = false;
        approachBlockedAddresses.Clear();
    }
}

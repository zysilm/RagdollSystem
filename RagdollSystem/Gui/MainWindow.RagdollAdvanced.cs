using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using RagdollSystem.Animation;

namespace RagdollSystem.Gui;

public unsafe partial class MainWindow
{
    // Skeleton bone cache for the advanced per-bone UI
    private string[] skeletonBoneNames = Array.Empty<string>();
    private Dictionary<string, string?> skeletonBoneParents = new();
    private bool skeletonBonesLoaded;

    // Bone profile UI state
    private string newBoneProfileName = "";
    private int selectedBoneProfileIndex = -1;
    private bool boneProfileOverwritePopupOpen = false;
    private string boneProfileOverwriteTarget = "";

    private void DrawRagdollAdvancedSection()
    {
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Per-Bone Physics Parameters");
        ImGui.TextWrapped("Mesh-aware geometry is authoritative by default. Disable it to author explicit per-bone volumes and optional bones.");
        ImGui.Spacing();

        var surfaceProfiles = config.RagdollCharacterSurfaceProfiles;
        if (ImGui.Checkbox("Mesh-Derived Body Thickness (Experimental)##ragdollSurfaceProfiles", ref surfaceProfiles))
        {
            config.RagdollCharacterSurfaceProfiles = surfaceProfiles;
            config.Save();
            ReactivatePlayerRagdoll();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Fits collision center, axis, length and thickness from the weighted body mesh.");
        ImGui.Spacing();

        var debugOverlay = config.RagdollDebugOverlay;
        if (ImGui.Checkbox("Show Debug Overlay##ragdollAdv", ref debugOverlay))
        {
            config.RagdollDebugOverlay = debugOverlay;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("Renders capsules and joints in 3D.");
        ImGui.Spacing();

        var groundLift = config.RagdollGroundPenetrationLift;
        if (ImGui.Checkbox("Ground-Penetration Lift##ragdollAdv", ref groundLift))
        {
            config.RagdollGroundPenetrationLift = groundLift;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("On activation, uniformly lifts the whole rig if any bone would start underground (usually a bent knee). Off by default — conflicts with Ragdoll Follow.");
        ImGui.Spacing();

        if (surfaceProfiles)
        {
            ImGui.TextDisabled("Per-bone profiles are not read while mesh-derived geometry is enabled. Anatomical joint topology is invariant in both modes.");
            return;
        }

        DrawRagdollBoneProfilesSection();
        ImGui.Spacing();

        // Quick toggle for weapon holster/sheathe bones
        {
            var bukiBones = new[] { "j_buki_kosi_l", "j_buki_kosi_r", "j_buki2_kosi_l", "j_buki2_kosi_r", "j_buki_sebo_l", "j_buki_sebo_r" };
            bool anyOn = false;
            foreach (var b in config.RagdollBoneConfigs)
                if (Array.IndexOf(bukiBones, b.Name) >= 0 && b.Enabled) { anyOn = true; break; }

            var bukiEnabled = anyOn;
            if (ImGui.Checkbox("Sheathed Weapon Physics##ragdollAdv", ref bukiEnabled))
            {
                foreach (var b in config.RagdollBoneConfigs)
                    if (Array.IndexOf(bukiBones, b.Name) >= 0)
                        b.Enabled = bukiEnabled;
                config.Save();
                ReactivatePlayerRagdoll();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("Toggle all j_buki holster/scabbard bones.");
        }

        if (plugin.PlayerRagdoll != null && plugin.PlayerRagdoll.IsActive)
        {
            if (ImGui.Button("Apply Changes (Reactivate Ragdoll)"))
                ReactivatePlayerRagdoll();
            ImGui.SameLine();
            ImGui.TextDisabled("Press to apply.");
            ImGui.Spacing();
        }

        if (!skeletonBonesLoaded)
            RefreshSkeletonBones();

        if (ImGui.Button("Refresh Bones"))
            RefreshSkeletonBones();
        ImGui.SameLine();
        ImGui.TextDisabled($"{skeletonBoneNames.Length} bones in skeleton");
        ImGui.Spacing();

        if (config.RagdollBoneConfigs.Count == 0)
            PopulateBoneConfigsFromDefaults();

        SyncConfigWithSkeleton();

        if (ImGui.Button("Reset All to Defaults##boneconfigs"))
        {
            config.RagdollBoneConfigs.Clear();
            config.Save();
            PopulateBoneConfigsFromDefaults();
            ReactivatePlayerRagdoll();
        }

        var enabledCount = 0;
        foreach (var b in config.RagdollBoneConfigs)
            if (b.Enabled) enabledCount++;
        ImGui.SameLine();
        ImGui.TextDisabled($"{enabledCount}/{config.RagdollBoneConfigs.Count} bones active");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var jointTypes = new[] { "Ball", "Hinge" };
        var anatomicalRoles = Enum.GetNames<RagdollController.AnatomicalRole>();
        var colliderShapes = Enum.GetNames<RagdollController.RagdollColliderShape>();
        var changed = false;

        for (int i = 0; i < config.RagdollBoneConfigs.Count; i++)
        {
            var bone = config.RagdollBoneConfigs[i];
            var id = $"##{bone.Name}";

            var enabled = bone.Enabled;
            if (ImGui.Checkbox($"##en{bone.Name}", ref enabled))
            {
                bone.Enabled = enabled;
                changed = true;
                EditingBoneName = bone.Name;
                if (plugin.PlayerRagdoll != null && plugin.PlayerRagdoll.IsActive)
                {
                    config.Save();
                    ReactivatePlayerRagdoll();
                }
            }
            ImGui.SameLine();

            var headerColor = bone.Enabled
                ? new Vector4(0.9f, 0.95f, 1f, 1f)
                : new Vector4(0.5f, 0.5f, 0.5f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Text, headerColor);

            var headerLabel = bone.Enabled
                ? $"{bone.Name} ({(bone.JointType == 0 ? "Ball" : "Hinge")}){id}"
                : $"{bone.Name} (off){id}";

            var isOpen = ImGui.CollapsingHeader(headerLabel);
            ImGui.PopStyleColor();

            if (isOpen)
            {
                ImGui.Indent(10);

                if (bone.SkeletonParent != null)
                    ImGui.TextDisabled($"Skeleton parent: {bone.SkeletonParent}");

                if (bone.Enabled)
                {
                    var jt = bone.JointType;
                    if (ImGui.Combo($"Joint Type{id}", ref jt, jointTypes, jointTypes.Length))
                    { bone.JointType = jt; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                    var role = bone.AnatomicalRole;
                    if (ImGui.Combo($"Anatomical Role{id}", ref role, anatomicalRoles, anatomicalRoles.Length))
                    { bone.AnatomicalRole = role; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                    var shape = bone.ColliderShape;
                    if (ImGui.Combo($"Collider Shape{id}", ref shape, colliderShapes, colliderShapes.Length))
                    { bone.ColliderShape = shape; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                    var radius = bone.CapsuleRadius;
                    if (ImGui.SliderFloat($"Capsule Radius{id}", ref radius, 0.01f, 0.3f, "%.3f"))
                    { bone.CapsuleRadius = radius; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                    var halfLen = bone.CapsuleHalfLength;
                    if (ImGui.SliderFloat($"Capsule Half-Length{id}", ref halfLen, 0.0f, 0.3f, "%.3f"))
                    { bone.CapsuleHalfLength = halfLen; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                    if ((RagdollController.RagdollColliderShape)bone.ColliderShape == RagdollController.RagdollColliderShape.Box)
                    {
                        var boxX = bone.BoxHalfExtentX;
                        if (ImGui.SliderFloat($"Box Half X{id}", ref boxX, 0.005f, 0.3f, "%.3f"))
                        { bone.BoxHalfExtentX = boxX; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                        var boxY = bone.BoxHalfExtentY;
                        if (ImGui.SliderFloat($"Box Half Y{id}", ref boxY, 0.005f, 0.3f, "%.3f"))
                        { bone.BoxHalfExtentY = boxY; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                        var boxZ = bone.BoxHalfExtentZ;
                        if (ImGui.SliderFloat($"Box Half Z{id}", ref boxZ, 0.005f, 0.3f, "%.3f"))
                        { bone.BoxHalfExtentZ = boxZ; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }
                    }

                    var mass = bone.Mass;
                    if (ImGui.SliderFloat($"Mass{id}", ref mass, 0.1f, 15.0f, "%.1f"))
                    { bone.Mass = mass; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                    var swing = bone.SwingLimit;
                    if (ImGui.SliderFloat($"Swing Limit (rad){id}", ref swing, 0.0f, MathF.PI, "%.2f"))
                    { bone.SwingLimit = swing; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.Swing; }

                    if ((RagdollController.JointType)bone.JointType == RagdollController.JointType.Hinge)
                    {
                        var swingMin = bone.SwingMinLimit ?? 0f;
                        if (ImGui.SliderFloat($"Swing Min Limit (rad){id}", ref swingMin, 0.0f, MathF.PI, "%.2f"))
                        { bone.SwingMinLimit = swingMin; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.Swing; }

                        var restAngle = bone.HingeRestAngle ?? 0f;
                        if (ImGui.SliderFloat($"Hinge Rest Angle (rad){id}", ref restAngle, 0.0f, MathF.PI, "%.2f"))
                        { bone.HingeRestAngle = restAngle; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.Swing; }

                        var restFreq = bone.HingeRestSpringFreq ?? 0f;
                        if (ImGui.SliderFloat($"Hinge Rest Freq (Hz){id}", ref restFreq, 0.0f, 30.0f, "%.1f"))
                        { bone.HingeRestSpringFreq = restFreq; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                        var restForce = bone.HingeRestMaxForce ?? 0f;
                        if (ImGui.SliderFloat($"Hinge Rest Max Force{id}", ref restForce, 0.0f, 500.0f, "%.0f"))
                        { bone.HingeRestMaxForce = restForce; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }
                    }

                    var twistMin = bone.TwistMinAngle;
                    if (ImGui.SliderFloat($"Twist Min (rad){id}", ref twistMin, -MathF.PI, 0f, "%.2f"))
                    { bone.TwistMinAngle = twistMin; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.TwistMin; }

                    var twistMax = bone.TwistMaxAngle;
                    if (ImGui.SliderFloat($"Twist Max (rad){id}", ref twistMax, 0f, MathF.PI, "%.2f"))
                    { bone.TwistMaxAngle = twistMax; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.TwistMax; }

                    var softBody = bone.SoftBody;
                    if (ImGui.Checkbox($"Soft Body##soft{bone.Name}", ref softBody))
                    { bone.SoftBody = softBody; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }
                    ImGui.SameLine();
                    ImGui.TextDisabled("Bouncy spring physics (breast/jiggle)");

                    if (bone.SoftBody)
                    {
                        var ssFreq = bone.SoftSpringFreq;
                        if (ImGui.SliderFloat($"Spring Freq (Hz){id}", ref ssFreq, 1f, 30f, "%.1f"))
                        { bone.SoftSpringFreq = ssFreq; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                        var ssDamp = bone.SoftSpringDamp;
                        if (ImGui.SliderFloat($"Spring Damping{id}", ref ssDamp, 0.05f, 1.0f, "%.2f"))
                        { bone.SoftSpringDamp = ssDamp; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                        var svFreq = bone.SoftServoFreq;
                        if (ImGui.SliderFloat($"Servo Freq (Hz){id}", ref svFreq, 1f, 20f, "%.1f"))
                        { bone.SoftServoFreq = svFreq; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }

                        var svDamp = bone.SoftServoDamp;
                        if (ImGui.SliderFloat($"Servo Damping{id}", ref svDamp, 0.05f, 1.0f, "%.2f"))
                        { bone.SoftServoDamp = svDamp; changed = true; EditingBoneName = bone.Name; EditingParameter = EditParam.None; }
                    }

                    var defaultBone = Array.Find(
                        RagdollController.AllBoneDefaults, candidate => candidate.Name == bone.Name);
                    if (defaultBone != null)
                    {
                        if (ImGui.SmallButton($"Reset{id}"))
                        {
                            var def = CloneBoneConfig(defaultBone);
                            bone.CapsuleRadius = def.CapsuleRadius;
                            bone.CapsuleHalfLength = def.CapsuleHalfLength;
                            bone.Mass = def.Mass;
                            bone.SwingLimit = def.SwingLimit;
                            bone.SwingMinLimit = def.SwingMinLimit;
                            bone.HingeRestAngle = def.HingeRestAngle;
                            bone.HingeRestSpringFreq = def.HingeRestSpringFreq;
                            bone.HingeRestMaxForce = def.HingeRestMaxForce;
                            bone.JointType = def.JointType;
                            bone.TwistMinAngle = def.TwistMinAngle;
                            bone.TwistMaxAngle = def.TwistMaxAngle;
                            bone.AnatomicalRole = def.AnatomicalRole;
                            bone.ColliderShape = def.ColliderShape;
                            bone.BoxHalfExtentX = def.BoxHalfExtentX;
                            bone.BoxHalfExtentY = def.BoxHalfExtentY;
                            bone.BoxHalfExtentZ = def.BoxHalfExtentZ;
                            bone.Enabled = def.Enabled;
                            bone.SoftBody = def.SoftBody;
                            bone.SoftSpringFreq = def.SoftSpringFreq;
                            bone.SoftSpringDamp = def.SoftSpringDamp;
                            bone.SoftServoFreq = def.SoftServoFreq;
                            bone.SoftServoDamp = def.SoftServoDamp;
                            changed = true;
                            EditingBoneName = bone.Name;
                        }
                    }
                }
                else
                {
                    ImGui.TextDisabled("Enable this bone to edit parameters.");
                }

                ImGui.Unindent(10);
                ImGui.Spacing();
            }
        }

        if (changed)
            config.Save();
    }

    private void RefreshSkeletonBones()
    {
        skeletonBonesLoaded = true;
        var player = Core.Services.ObjectTable.LocalPlayer;
        if (player == null) return;

        var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
        if (gameObj->DrawObject == null) return;
        var charBase = (CharacterBase*)gameObj->DrawObject;
        var skeleton = charBase->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount < 1) return;
        var partial = &skeleton->PartialSkeletons[0];
        var pose = partial->GetHavokPose(0);
        if (pose == null || pose->Skeleton == null) return;

        var havokBones = pose->Skeleton->Bones;
        var parentIndices = pose->Skeleton->ParentIndices;
        var names = new List<string>();
        var parents = new Dictionary<string, string?>();

        for (int i = 0; i < havokBones.Length; i++)
        {
            var name = havokBones[i].Name.String;
            if (string.IsNullOrWhiteSpace(name)) continue;
            names.Add(name);

            string? parentName = null;
            if (i < parentIndices.Length)
            {
                var pi = parentIndices[i];
                if (pi >= 0 && pi < havokBones.Length)
                    parentName = havokBones[pi].Name.String;
            }
            parents[name] = parentName;
        }

        skeletonBoneNames = names.ToArray();
        skeletonBoneParents = parents;
    }

    /// <summary>Populate config from C# AllBoneDefaults (source of truth).</summary>
    private void PopulateBoneConfigsFromDefaults()
    {
        config.RagdollBoneConfigs.Clear();
        foreach (var def in RagdollController.AllBoneDefaults)
            config.RagdollBoneConfigs.Add(CloneBoneConfig(def));
        config.Save();
    }

    /// <summary>Add skeleton bones not yet in config. Never modifies existing entries.</summary>
    private void SyncConfigWithSkeleton()
    {
        if (skeletonBoneNames.Length == 0) return;

        var existing = new HashSet<string>();
        foreach (var c in config.RagdollBoneConfigs)
            existing.Add(c.Name);

        bool added = false;
        foreach (var boneName in skeletonBoneNames)
        {
            if (existing.Contains(boneName)) continue;

            skeletonBoneParents.TryGetValue(boneName, out var skelParent);

            RagdollBoneConfig? known = null;
            foreach (var def in RagdollController.AllBoneDefaults)
                if (def.Name == boneName) { known = CloneBoneConfig(def); break; }

            if (known != null)
            {
                known.SkeletonParent = skelParent;
                config.RagdollBoneConfigs.Add(known);
            }
            else
            {
                var unknown = new RagdollBoneConfig
                {
                    Name = boneName,
                    SkeletonParent = skelParent,
                    Enabled = false,
                    CapsuleRadius = 0.03f,
                    CapsuleHalfLength = 0.03f,
                    Mass = 1.0f,
                    SwingLimit = 0.3f,
                    JointType = 0,
                    TwistMinAngle = -0.2f,
                    TwistMaxAngle = 0.2f,
                };
                RagdollController.FillProfileDefaults(unknown);
                config.RagdollBoneConfigs.Add(unknown);
            }
            added = true;
        }

        if (added) config.Save();
    }

    private static RagdollBoneConfig CloneBoneConfig(RagdollBoneConfig src)
    {
        var clone = new RagdollBoneConfig
        {
            Name = src.Name,
            SkeletonParent = src.SkeletonParent,
            Enabled = src.Enabled,
            CapsuleRadius = src.CapsuleRadius,
            CapsuleHalfLength = src.CapsuleHalfLength,
            Mass = src.Mass,
            SwingLimit = src.SwingLimit,
            SwingMinLimit = src.SwingMinLimit,
            HingeRestAngle = src.HingeRestAngle,
            HingeRestSpringFreq = src.HingeRestSpringFreq,
            HingeRestMaxForce = src.HingeRestMaxForce,
            JointType = src.JointType,
            TwistMinAngle = src.TwistMinAngle,
            TwistMaxAngle = src.TwistMaxAngle,
            AnatomicalRole = src.AnatomicalRole,
            ColliderShape = src.ColliderShape,
            BoxHalfExtentX = src.BoxHalfExtentX,
            BoxHalfExtentY = src.BoxHalfExtentY,
            BoxHalfExtentZ = src.BoxHalfExtentZ,
            Description = src.Description,
            SoftBody = src.SoftBody,
            SoftSpringFreq = src.SoftSpringFreq,
            SoftSpringDamp = src.SoftSpringDamp,
            SoftServoFreq = src.SoftServoFreq,
            SoftServoDamp = src.SoftServoDamp,
        };
        RagdollController.FillProfileDefaults(clone);
        return clone;
    }

    private void DrawRagdollBoneProfilesSection()
    {
        if (!ImGui.CollapsingHeader("Bone Profiles", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var profiles = config.RagdollBoneProfiles;
        var profileNames = new string[profiles.Count];
        for (int i = 0; i < profiles.Count; i++)
            profileNames[i] = profiles[i].Name;

        var hasSelection = selectedBoneProfileIndex >= 0 && selectedBoneProfileIndex < profiles.Count;

        if (ImGui.BeginListBox("##BoneProfileSelect",
                new Vector2(250, ImGui.GetTextLineHeightWithSpacing() * 6 + ImGui.GetStyle().FramePadding.Y * 2)))
        {
            for (int i = 0; i < profileNames.Length; i++)
            {
                bool isSelected = selectedBoneProfileIndex == i;
                if (ImGui.Selectable(profileNames[i], isSelected))
                    selectedBoneProfileIndex = i;
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndListBox();
        }

        ImGui.SameLine();
        if (ImGui.Button("Load##boneprofile") && hasSelection)
            LoadBoneProfile(profiles[selectedBoneProfileIndex]);

        ImGui.SameLine();
        if (ImGui.Button("Overwrite##boneprofile") && hasSelection)
        {
            boneProfileOverwriteTarget = profiles[selectedBoneProfileIndex].Name;
            boneProfileOverwritePopupOpen = true;
            ImGui.OpenPopup("Confirm Overwrite##BoneProfileOverwrite");
        }

        ImGui.SameLine();
        var io = ImGui.GetIO();
        bool ctrlShiftHeld = io.KeyCtrl && io.KeyShift;
        if (!ctrlShiftHeld)
        {
            ImGui.BeginDisabled();
            ImGui.Button("Delete##boneprofile");
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl+Shift to enable delete.");
        }
        else if (ImGui.Button("Delete##boneprofile") && hasSelection)
        {
            profiles.RemoveAt(selectedBoneProfileIndex);
            selectedBoneProfileIndex = Math.Min(selectedBoneProfileIndex, profiles.Count - 1);
            config.Save();
        }

        ImGui.SetNextItemWidth(250);
        ImGui.InputText("##BoneProfileName", ref newBoneProfileName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Save Profile##boneprofile") && newBoneProfileName.Length > 0)
        {
            var existingIdx = profiles.FindIndex(p =>
                p.Name.Equals(newBoneProfileName, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0)
            {
                boneProfileOverwriteTarget = newBoneProfileName;
                boneProfileOverwritePopupOpen = true;
                ImGui.OpenPopup("Confirm Overwrite##BoneProfileOverwrite");
            }
            else
            {
                SaveBoneProfile(newBoneProfileName);
                newBoneProfileName = "";
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset to Defaults##boneprofile"))
            LoadBoneDefaults();
        HelpMarker("Replace the live per-bone config list with built-in defaults from RagdollController.AllBoneDefaults. Does not modify saved profiles.");

        if (ImGui.BeginPopupModal("Confirm Overwrite##BoneProfileOverwrite", ref boneProfileOverwritePopupOpen,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
        {
            ImGui.Text($"Overwrite profile \"{boneProfileOverwriteTarget}\"?");
            ImGui.Spacing();

            if (ImGui.Button("Yes", new Vector2(80, 0)))
            {
                SaveBoneProfile(boneProfileOverwriteTarget);
                newBoneProfileName = "";
                boneProfileOverwritePopupOpen = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("No", new Vector2(80, 0)))
            {
                boneProfileOverwritePopupOpen = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private void SaveBoneProfile(string name)
    {
        var snapshot = new RagdollBoneProfile { Name = name };
        foreach (var b in config.RagdollBoneConfigs)
            snapshot.Bones.Add(CloneBoneConfig(b));

        var existing = config.RagdollBoneProfiles.FindIndex(p => p.Name == name);
        if (existing >= 0)
            config.RagdollBoneProfiles[existing] = snapshot;
        else
            config.RagdollBoneProfiles.Add(snapshot);

        config.Save();
        log.Info($"Bone profile '{name}' saved ({snapshot.Bones.Count} bones).");
    }

    private void LoadBoneProfile(RagdollBoneProfile p)
    {
        config.RagdollBoneConfigs.Clear();
        foreach (var b in p.Bones)
            config.RagdollBoneConfigs.Add(CloneBoneConfig(b));
        config.Save();
        ReactivatePlayerRagdoll();
        log.Info($"Bone profile '{p.Name}' loaded ({p.Bones.Count} bones).");
    }

    private void LoadBoneDefaults()
    {
        config.RagdollBoneConfigs.Clear();
        foreach (var def in RagdollController.AllBoneDefaults)
            config.RagdollBoneConfigs.Add(CloneBoneConfig(def));
        config.Save();
        ReactivatePlayerRagdoll();
    }

    private void ReactivatePlayerRagdoll()
    {
        if (plugin.PlayerRagdoll == null || !plugin.PlayerRagdoll.IsActive) return;
        plugin.ManualDeactivatePlayer();
        plugin.ManualActivatePlayer();
    }
}

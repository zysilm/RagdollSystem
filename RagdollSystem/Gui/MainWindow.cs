using System;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Dalamud.Bindings.ImGui;
using RagdollSystem.Animation;

namespace RagdollSystem.Gui;

public unsafe class MainWindow : IDisposable
{
    private readonly Configuration config;
    private readonly RagdollSystemPlugin plugin;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    // Skeleton bone cache for ragdoll advanced UI
    private string[] skeletonBoneNames = Array.Empty<string>();
    private bool skeletonBonesLoaded;

    // Currently editing bone name (for debug overlay highlight)
    public string? EditingBoneName;

    public MainWindow(Configuration config, RagdollSystemPlugin plugin, IClientState clientState, IPluginLog log)
    {
        this.config = config;
        this.plugin = plugin;
        this.clientState = clientState;
        this.log = log;
    }

    public void Draw()
    {
        var open = config.ShowMainWindow;
        ImGui.SetNextWindowSize(new Vector2(500, 600), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Ragdoll System", ref open))
        {
            ImGui.End();
            if (!open) { config.ShowMainWindow = false; config.Save(); }
            return;
        }
        if (!open) { config.ShowMainWindow = false; config.Save(); }

        if (ImGui.BeginTabBar("RagdollTabs"))
        {
            if (ImGui.BeginTabItem("General"))
            {
                DrawGeneralTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("NPC"))
            {
                DrawNpcTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Ragdoll (Adv)"))
            {
                DrawRagdollAdvancedSection();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private void DrawGeneralTab()
    {
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Ragdoll Physics");
        ImGui.Spacing();

        var enableRagdoll = config.EnableRagdoll;
        if (ImGui.Checkbox("Enable Ragdoll on Death", ref enableRagdoll))
        { config.EnableRagdoll = enableRagdoll; config.Save(); }

        ImGui.Spacing();

        var delay = config.RagdollActivationDelay;
        if (ImGui.SliderFloat("Activation Delay (s)", ref delay, 0f, 5f, "%.1f"))
        { config.RagdollActivationDelay = delay; config.Save(); }
        ImGui.SameLine(); ImGui.TextDisabled("Delay before ragdoll starts after death.");

        var duration = config.RagdollDuration;
        if (ImGui.SliderFloat("Duration (s)", ref duration, 5f, 120f, "%.0f"))
        { config.RagdollDuration = duration; config.Save(); }
        ImGui.SameLine(); ImGui.TextDisabled("Auto-cleanup after this time.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Physics Parameters");
        ImGui.Spacing();

        var gravity = config.RagdollGravity;
        if (ImGui.SliderFloat("Gravity", ref gravity, 1f, 30f, "%.1f"))
        { config.RagdollGravity = gravity; config.Save(); }

        var damping = config.RagdollDamping;
        if (ImGui.SliderFloat("Linear Damping", ref damping, 0.8f, 1.0f, "%.3f"))
        { config.RagdollDamping = damping; config.Save(); }

        var solverIterations = config.RagdollSolverIterations;
        if (ImGui.SliderInt("Solver Iterations", ref solverIterations, 1, 16))
        { config.RagdollSolverIterations = solverIterations; config.Save(); }

        var selfCollision = config.RagdollSelfCollision;
        if (ImGui.Checkbox("Self-Collision", ref selfCollision))
        { config.RagdollSelfCollision = selfCollision; config.Save(); }
        ImGui.SameLine(); ImGui.TextDisabled("Body parts collide with each other.");

        var friction = config.RagdollFriction;
        if (ImGui.SliderFloat("Surface Friction", ref friction, 0f, 2f, "%.2f"))
        { config.RagdollFriction = friction; config.Save(); }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Hair Physics");
        ImGui.Spacing();

        var hairPhysics = config.RagdollHairPhysics;
        if (ImGui.Checkbox("Enable Hair Physics", ref hairPhysics))
        { config.RagdollHairPhysics = hairPhysics; config.Save(); }

        if (config.RagdollHairPhysics)
        {
            var hairGravity = config.RagdollHairGravityStrength;
            if (ImGui.SliderFloat("Hair Gravity", ref hairGravity, 0f, 1f, "%.2f"))
            { config.RagdollHairGravityStrength = hairGravity; config.Save(); }

            var hairDamping = config.RagdollHairDamping;
            if (ImGui.SliderFloat("Hair Damping", ref hairDamping, 0.8f, 1f, "%.3f"))
            { config.RagdollHairDamping = hairDamping; config.Save(); }

            var hairStiffness = config.RagdollHairStiffness;
            if (ImGui.SliderFloat("Hair Stiffness", ref hairStiffness, 0f, 0.5f, "%.3f"))
            { config.RagdollHairStiffness = hairStiffness; config.Save(); }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Debug / Testing");
        ImGui.Spacing();

        var verboseLog = config.RagdollVerboseLog;
        if (ImGui.Checkbox("Verbose Logging", ref verboseLog))
        { config.RagdollVerboseLog = verboseLog; config.Save(); }

        ImGui.Spacing();
        if (ImGui.Button("Test: Activate Ragdoll on Player"))
        {
            plugin.ManualActivatePlayer();
        }
        ImGui.SameLine();
        if (ImGui.Button("Test: Deactivate"))
        {
            plugin.ManualDeactivatePlayer();
        }
    }

    private void DrawNpcTab()
    {
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "NPC Death Ragdoll");
        ImGui.Spacing();

        var enableNpc = config.EnableNpcDeathRagdoll;
        if (ImGui.Checkbox("Enable NPC Death Ragdoll", ref enableNpc))
        { config.EnableNpcDeathRagdoll = enableNpc; config.Save(); }
        ImGui.TextWrapped("When enabled, nearby battle NPCs that die will also trigger ragdoll physics. " +
                          "Non-humanoid NPCs (monsters, objects) may have different bone sets — " +
                          "the system will gracefully use whatever matching bones exist.");

        ImGui.Spacing();

        var npcDelay = config.NpcRagdollActivationDelay;
        if (ImGui.SliderFloat("NPC Activation Delay (s)", ref npcDelay, 0f, 3f, "%.1f"))
        { config.NpcRagdollActivationDelay = npcDelay; config.Save(); }

        var maxNpc = config.MaxNpcRagdolls;
        if (ImGui.SliderInt("Max Concurrent NPC Ragdolls", ref maxNpc, 1, 10))
        { config.MaxNpcRagdolls = maxNpc; config.Save(); }
        ImGui.TextDisabled("Limits performance impact. Oldest ragdolls are removed first.");
    }

    private void DrawRagdollAdvancedSection()
    {
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "Per-Bone Physics Parameters");
        ImGui.TextWrapped("Toggle bones on/off for physics. Adjust rotation limits, capsule volume, and mass.");
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
        ImGui.Spacing();

        if (plugin.PlayerRagdoll != null && plugin.PlayerRagdoll.IsActive)
        {
            if (ImGui.Button("Apply Changes (Reactivate Ragdoll)"))
            {
                ReactivatePlayerRagdoll();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("Press to apply.");
            ImGui.Spacing();
        }

        // Read skeleton bones from player character (once, or on refresh)
        if (!skeletonBonesLoaded)
            RefreshSkeletonBones();

        if (ImGui.Button("Refresh Bones"))
            RefreshSkeletonBones();
        ImGui.SameLine();
        ImGui.TextDisabled($"{skeletonBoneNames.Length} bones in skeleton");
        ImGui.Spacing();

        // Populate config from defaults if empty
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
                    { bone.JointType = jt; changed = true; EditingBoneName = bone.Name; }

                    var radius = bone.CapsuleRadius;
                    if (ImGui.SliderFloat($"Capsule Radius{id}", ref radius, 0.01f, 0.3f, "%.3f"))
                    { bone.CapsuleRadius = radius; changed = true; EditingBoneName = bone.Name; }

                    var halfLen = bone.CapsuleHalfLength;
                    if (ImGui.SliderFloat($"Capsule Half-Length{id}", ref halfLen, 0.0f, 0.3f, "%.3f"))
                    { bone.CapsuleHalfLength = halfLen; changed = true; EditingBoneName = bone.Name; }

                    var mass = bone.Mass;
                    if (ImGui.SliderFloat($"Mass{id}", ref mass, 0.1f, 15.0f, "%.1f"))
                    { bone.Mass = mass; changed = true; EditingBoneName = bone.Name; }

                    var swing = bone.SwingLimit;
                    if (ImGui.SliderFloat($"Swing Limit (rad){id}", ref swing, 0.0f, MathF.PI, "%.2f"))
                    { bone.SwingLimit = swing; changed = true; EditingBoneName = bone.Name; }

                    var twistMin = bone.TwistMinAngle;
                    if (ImGui.SliderFloat($"Twist Min (rad){id}", ref twistMin, -MathF.PI, 0f, "%.2f"))
                    { bone.TwistMinAngle = twistMin; changed = true; EditingBoneName = bone.Name; }

                    var twistMax = bone.TwistMaxAngle;
                    if (ImGui.SliderFloat($"Twist Max (rad){id}", ref twistMax, 0f, MathF.PI, "%.2f"))
                    { bone.TwistMaxAngle = twistMax; changed = true; EditingBoneName = bone.Name; }

                    // Soft body settings
                    var softBody = bone.SoftBody;
                    if (ImGui.Checkbox($"Soft Body##soft{bone.Name}", ref softBody))
                    { bone.SoftBody = softBody; changed = true; EditingBoneName = bone.Name; }
                    ImGui.SameLine();
                    ImGui.TextDisabled("Bouncy spring physics (breast/jiggle)");

                    if (bone.SoftBody)
                    {
                        var ssFreq = bone.SoftSpringFreq;
                        if (ImGui.SliderFloat($"Spring Freq (Hz){id}", ref ssFreq, 1f, 30f, "%.1f"))
                        { bone.SoftSpringFreq = ssFreq; changed = true; EditingBoneName = bone.Name; }

                        var ssDamp = bone.SoftSpringDamp;
                        if (ImGui.SliderFloat($"Spring Damping{id}", ref ssDamp, 0.05f, 1.0f, "%.2f"))
                        { bone.SoftSpringDamp = ssDamp; changed = true; EditingBoneName = bone.Name; }

                        var svFreq = bone.SoftServoFreq;
                        if (ImGui.SliderFloat($"Servo Freq (Hz){id}", ref svFreq, 1f, 20f, "%.1f"))
                        { bone.SoftServoFreq = svFreq; changed = true; EditingBoneName = bone.Name; }

                        var svDamp = bone.SoftServoDamp;
                        if (ImGui.SliderFloat($"Servo Damping{id}", ref svDamp, 0.05f, 1.0f, "%.2f"))
                        { bone.SoftServoDamp = svDamp; changed = true; EditingBoneName = bone.Name; }
                    }

                    // Reset this bone to its default
                    if (i < RagdollController.AllBoneDefaults.Length)
                    {
                        if (ImGui.SmallButton($"Reset{id}"))
                        {
                            var def = RagdollController.AllBoneDefaults[i];
                            bone.CapsuleRadius = def.CapsuleRadius;
                            bone.CapsuleHalfLength = def.CapsuleHalfLength;
                            bone.Mass = def.Mass;
                            bone.SwingLimit = def.SwingLimit;
                            bone.JointType = def.JointType;
                            bone.TwistMinAngle = def.TwistMinAngle;
                            bone.TwistMaxAngle = def.TwistMaxAngle;
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
        var player = RagdollSystem.Core.Services.ObjectTable.LocalPlayer;
        if (player == null) return;

        var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
        if (gameObj->DrawObject == null) return;
        var charBase = (CharacterBase*)gameObj->DrawObject;
        var skeleton = charBase->Skeleton;
        if (skeleton == null || skeleton->PartialSkeletonCount < 1) return;

        var partial = &skeleton->PartialSkeletons[0];
        var pose = partial->GetHavokPose(0);
        if (pose == null || pose->Skeleton == null) return;

        var bones = pose->Skeleton->Bones;
        var names = new string[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            names[i] = bones[i].Name.String ?? $"bone_{i}";
        skeletonBoneNames = names;
    }

    private void PopulateBoneConfigsFromDefaults()
    {
        config.RagdollBoneConfigs.Clear();
        foreach (var def in RagdollController.AllBoneDefaults)
        {
            config.RagdollBoneConfigs.Add(new RagdollBoneConfig
            {
                Name = def.Name,
                SkeletonParent = def.SkeletonParent,
                Enabled = def.Enabled,
                CapsuleRadius = def.CapsuleRadius,
                CapsuleHalfLength = def.CapsuleHalfLength,
                Mass = def.Mass,
                SwingLimit = def.SwingLimit,
                JointType = def.JointType,
                TwistMinAngle = def.TwistMinAngle,
                TwistMaxAngle = def.TwistMaxAngle,
                Description = def.Description,
                SoftBody = def.SoftBody,
                SoftSpringFreq = def.SoftSpringFreq,
                SoftSpringDamp = def.SoftSpringDamp,
                SoftServoFreq = def.SoftServoFreq,
                SoftServoDamp = def.SoftServoDamp,
            });
        }
        config.Save();
    }

    private void SyncConfigWithSkeleton()
    {
        if (skeletonBoneNames.Length == 0) return;

        var configNames = new System.Collections.Generic.HashSet<string>();
        foreach (var b in config.RagdollBoneConfigs)
            configNames.Add(b.Name);

        var added = false;
        foreach (var boneName in skeletonBoneNames)
        {
            if (configNames.Contains(boneName)) continue;

            // Check if it has a default
            RagdollBoneConfig? defConfig = null;
            foreach (var def in RagdollController.AllBoneDefaults)
            {
                if (def.Name == boneName)
                {
                    defConfig = def;
                    break;
                }
            }

            if (defConfig != null)
            {
                config.RagdollBoneConfigs.Add(new RagdollBoneConfig
                {
                    Name = defConfig.Name,
                    SkeletonParent = defConfig.SkeletonParent,
                    Enabled = defConfig.Enabled,
                    CapsuleRadius = defConfig.CapsuleRadius,
                    CapsuleHalfLength = defConfig.CapsuleHalfLength,
                    Mass = defConfig.Mass,
                    SwingLimit = defConfig.SwingLimit,
                    JointType = defConfig.JointType,
                    TwistMinAngle = defConfig.TwistMinAngle,
                    TwistMaxAngle = defConfig.TwistMaxAngle,
                    Description = defConfig.Description,
                    SoftBody = defConfig.SoftBody,
                    SoftSpringFreq = defConfig.SoftSpringFreq,
                    SoftSpringDamp = defConfig.SoftSpringDamp,
                    SoftServoFreq = defConfig.SoftServoFreq,
                    SoftServoDamp = defConfig.SoftServoDamp,
                });
                added = true;
            }
        }

        if (added)
            config.Save();
    }

    private void ReactivatePlayerRagdoll()
    {
        if (plugin.PlayerRagdoll == null || !plugin.PlayerRagdoll.IsActive) return;
        var addr = plugin.PlayerRagdoll.TargetCharacterAddress;
        plugin.ManualDeactivatePlayer();
        if (addr != nint.Zero)
        {
            var controller = new RagdollController(plugin.BoneTransformService, config, log);
            controller.Activate(addr);
            // We need to set this through the plugin — use manual activate instead
        }
        // Actually just use the manual methods which handle cleanup
        plugin.ManualActivatePlayer();
    }

    public void Dispose()
    {
    }
}

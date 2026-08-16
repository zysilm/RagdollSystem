using System;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using RagdollSystem.Animation;
using RagdollSystem.Dev;
using RagdollSystem.Integration;

namespace RagdollSystem.Gui;

public partial class MainWindow : IDisposable
{
    private readonly Configuration config;
    private readonly RagdollSystemPlugin plugin;
    private readonly IClientState clientState;
    private readonly KoStripController koStripController;
    private readonly IPluginLog log;

    // Currently editing bone name (for debug overlay highlight)
    public string? EditingBoneName;
    public EditParam EditingParameter { get; private set; }

    public enum EditParam
    {
        None,
        Swing,
        TwistMin,
        TwistMax,
    }

    private static readonly string[] TabNames = { "Ragdoll", "Ragdoll (Adv)", "Armor Detachment" };
    private int selectedTab = 0;

    public MainWindow(Configuration config, RagdollSystemPlugin plugin, IClientState clientState,
        KoStripController koStripController, IPluginLog log)
    {
        this.config = config;
        this.plugin = plugin;
        this.clientState = clientState;
        this.koStripController = koStripController;
        this.log = log;
    }

    private static void HelpMarker(string desc)
    {
        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 20.0f);
            ImGui.TextUnformatted(desc);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    public void Draw()
    {
        var showWindow = config.ShowMainWindow;
        ImGui.SetNextWindowSize(new Vector2(560, 600), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Ragdoll System", ref showWindow))
        {
            config.ShowMainWindow = showWindow;
            ImGui.End();
            return;
        }
        config.ShowMainWindow = showWindow;

        var contentHeight = ImGui.GetContentRegionAvail().Y;
        var totalWidth = ImGui.GetContentRegionAvail().X;
        var sidebarWidth = Math.Clamp(config.SidebarWidth, 80f, totalWidth - 150f);

        ImGui.BeginChild("##sidebar", new Vector2(sidebarWidth, contentHeight), true);
        for (int i = 0; i < TabNames.Length; i++)
        {
            if (ImGui.Selectable(TabNames[i], selectedTab == i))
                selectedTab = i;
        }
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.Button("##splitter", new Vector2(4, contentHeight));
        if (ImGui.IsItemActive())
        {
            config.SidebarWidth = sidebarWidth + ImGui.GetIO().MouseDelta.X;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor((ImGuiMouseCursor)5); // ResizeEW

        ImGui.SameLine();

        ImGui.BeginChild("##content", new Vector2(0, contentHeight), true);
        switch (selectedTab)
        {
            case 0:
                DrawRagdollFollowEntrySection();
                DrawRagdollSection();
                DrawGuidedCollapseSection();
                DrawNpcCollisionSection();
                break;
            case 1:
                DrawRagdollAdvancedSection();
                break;
            case 2:
                DrawArmorDetachmentSection(koStripController);
                break;
        }
        ImGui.EndChild();

        ImGui.End();
    }

    private void DrawRagdollFollowEntrySection()
    {
        if (!ImGui.CollapsingHeader("Ragdoll Follow"))
            return;

        var follow = config.RagdollFollowPosition;
        if (ImGui.Checkbox("Follow flung corpses##ragdollfollow", ref follow))
        {
            config.RagdollFollowPosition = follow;
            config.Save();
        }
        HelpMarker("Keeps a corpse's actual game-object position moving with its ragdoll body while it falls or slides, " +
                   "so a corpse thrown far away doesn't get culled or unloaded. On by default.");
    }

    private void DrawRagdollSection()
    {
        if (ImGui.CollapsingHeader("Ragdoll Trigger", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var enabled = config.EnableRagdoll;
            if (ImGui.Checkbox("Enable Ragdoll##ragdoll", ref enabled))
            {
                config.EnableRagdoll = enabled;
                config.Save();
            }
            HelpMarker("Replace death animation with ragdoll physics after a configurable delay.");

            if (config.EnableRagdoll)
            {
                ImGui.Indent();

                var ragdollActive = plugin.PlayerRagdoll?.IsActive ?? false;
                if (ImGui.Checkbox("Ragdoll Now", ref ragdollActive))
                {
                    if (ragdollActive) plugin.ManualActivatePlayer();
                    else plugin.ManualDeactivatePlayer();
                }
                HelpMarker("Instantly toggle ragdoll on the player character.");

                ImGui.Separator();

                var delay = config.RagdollActivationDelay;
                if (ImGui.SliderFloat("Activation Delay (s)##ragdoll", ref delay, 0.0f, 20.0f, "%.1f"))
                {
                    config.RagdollActivationDelay = delay;
                    config.Save();
                }
                HelpMarker("Seconds after death before ragdoll physics take over.");

                var duration = config.RagdollDuration;
                if (ImGui.SliderFloat("Duration (s)##ragdoll", ref duration, 5f, 120f, "%.0f"))
                {
                    config.RagdollDuration = duration;
                    config.Save();
                }
                HelpMarker("Auto-cleanup: the ragdoll is disposed and normal animation restored after this many seconds.");

                ImGui.Unindent();
            }
        }

        // The rest of the ragdoll settings live in sibling headers — one giant "Ragdoll"
        // header had accreted five unrelated groups and become unscannable.
        if (config.EnableRagdoll && ImGui.CollapsingHeader("Ground Detection"))
        {
            ImGui.Indent();

            var extendTerrain = config.ExtendTerrainDetection;
            if (ImGui.Checkbox("Extend Terrain Detection##ragdoll", ref extendTerrain))
            {
                config.ExtendTerrainDetection = extendTerrain;
                config.Save();
            }
            HelpMarker("Also build ground collision under nearby enemies, not just the death spot. " +
                       "Costs extra raycasts at activation — may cause a brief hitch. Default off.");

            if (config.ExtendTerrainDetection)
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.2f, 1f), "Warning: may cause severe stuttering.");

            ImGui.Unindent();
        }

        if (config.EnableRagdoll && ImGui.CollapsingHeader("Basic Parameters"))
        {
            ImGui.Indent();

            var gravity = config.RagdollGravity;
            if (ImGui.SliderFloat("Gravity##ragdoll", ref gravity, 0.1f, 30.0f, "%.1f"))
            { config.RagdollGravity = gravity; config.Save(); }

            var damping = config.RagdollDamping;
            if (ImGui.SliderFloat("Damping##ragdoll", ref damping, 0.8f, 1.0f, "%.3f"))
            { config.RagdollDamping = damping; config.Save(); }
            HelpMarker("Velocity damping per frame. Lower = more energy loss.");

            var solverIter = config.RagdollSolverIterations;
            if (ImGui.SliderInt("Solver Iterations##ragdoll", ref solverIter, 1, 64))
            { config.RagdollSolverIterations = solverIter; config.Save(); }
            HelpMarker("Constraint solver velocity iterations per SUBSTEP (not per frame) — total solver work " +
                       "is Iterations x Substeps. Takes effect on next ragdoll activation.");

            var solverSubsteps = config.RagdollSolverSubsteps;
            if (ImGui.SliderInt("Solver Substeps##ragdoll", ref solverSubsteps, 1, 64))
            { config.RagdollSolverSubsteps = solverSubsteps; config.Save(); }
            HelpMarker("Velocity-solve substeps per fixed timestep. 1 = legacy. Raising this is what a stiff joint " +
                       "limit wall needs to be represented well; costs ~linearly and multiplies with Iterations. " +
                       "Takes effect on next ragdoll activation.");

            var limitFreq = config.RagdollLimitSpringFrequency;
            if (ImGui.SliderFloat("Limit Wall Stiffness (Hz)##ragdoll", ref limitFreq, 30f, 180f, "%.0f"))
            { config.RagdollLimitSpringFrequency = limitFreq; config.Save(); }
            HelpMarker("Spring frequency of the joint LIMIT walls (swing cones + twist ranges). Higher = firmer " +
                       "wall so joints don't over-rotate past their range. Takes effect on next ragdoll activation.");

            var softLimits = config.RagdollSoftLimits;
            if (ImGui.Checkbox("Soft Swing Limits (balls)##ragdoll", ref softLimits))
            { config.RagdollSoftLimits = softLimits; config.Save(); }
            HelpMarker("Ball joints get a low-frequency, overdamped spring on their swing limits instead of the " +
                       "hard wall above, so a limb sliding toward its range edge decelerates and settles rather " +
                       "than pinning at the extreme angle. Knees/elbows keep the hard wall.");

            if (config.RagdollSoftLimits)
            {
                var softFreq = config.RagdollSoftLimitFrequency;
                if (ImGui.SliderFloat("Soft Limit Frequency (Hz)##ragdoll", ref softFreq, 2f, 40f, "%.0f"))
                { config.RagdollSoftLimitFrequency = softFreq; config.Save(); }

                var softDamp = config.RagdollSoftLimitDamping;
                if (ImGui.SliderFloat("Soft Limit Damping##ragdoll", ref softDamp, 0.5f, 10f, "%.1f"))
                { config.RagdollSoftLimitDamping = softDamp; config.Save(); }
                HelpMarker("Damping ratio at the soft edge. 1 = critical (springy), 4 = overdamped default (sinks and stays).");
            }

            var jointFreq = MathF.Max(45f, config.RagdollJointSpringFrequency);
            if (ImGui.SliderFloat("Joint Stiffness (Hz)##ragdoll", ref jointFreq, 45f, 120f, "%.0f"))
            { config.RagdollJointSpringFrequency = jointFreq; config.Save(); }
            HelpMarker("Spring frequency of the POSITIONAL joints that hold bones together, not the limit walls. " +
                       "Higher reduces visible anchor stretch but needs enough solver substeps.");

            var footJointFreq = config.RagdollFootJointSpringFrequency;
            if (ImGui.SliderFloat("Foot Joint Stiffness (Hz)##ragdoll", ref footJointFreq, 0f, 150f, "%.0f"))
            { config.RagdollFootJointSpringFrequency = footJointFreq; config.Save(); }
            HelpMarker("Positional stiffness of the calf->foot joint specifically. 0 = use the body Joint Stiffness above.");

            ImGui.Unindent();
        }

        if (config.EnableRagdoll && ImGui.CollapsingHeader("Advanced Filters"))
        {
            ImGui.Indent();

            var impactWeight = config.RagdollImpactWeight;
            if (ImGui.Checkbox("Impact weight##ragdoll", ref impactWeight))
            { config.RagdollImpactWeight = impactWeight; config.Save(); }
            HelpMarker("Stage a hard landing rather than only simulating one: a brief freeze, one heavy heave " +
                       "with a whip through the limbs, and a shake of the camera.");

            var heavyBody = config.RagdollHeavyBody;
            if (ImGui.Checkbox("Heavy body##ragdoll", ref heavyBody))
            { config.RagdollHeavyBody = heavyBody; config.Save(); }
            HelpMarker("Make the body itself read heavy: falls harder than true gravity, and the limbs get more " +
                       "rotational inertia than a thin capsule implies. Unlike Impact weight, this changes " +
                       "trajectories. Takes effect on the next ragdoll.");

            var carryVel = config.RagdollCarryAnimationVelocity;
            if (ImGui.Checkbox("Carry animation velocity##ragdoll", ref carryVel))
            { config.RagdollCarryAnimationVelocity = carryVel; config.Save(); }
            HelpMarker("At the animation->physics handoff, seed each body with the velocity the death animation " +
                       "was carrying instead of starting at rest. Removes the 'freeze' hitch.");

            using (ImRaii.Disabled(!config.RagdollCarryAnimationVelocity))
            {
                var velScale = config.RagdollHandoffVelocityScale;
                if (ImGui.SliderFloat("Handoff velocity scale##ragdoll", ref velScale, 0f, 5f, "%.2f"))
                { config.RagdollHandoffVelocityScale = velScale; config.Save(); }
            }

            var anthropometricMass = config.RagdollAnthropometricMass;
            if (ImGui.Checkbox("Anthropometric Mass##ragdoll", ref anthropometricMass))
            { config.RagdollAnthropometricMass = anthropometricMass; config.Save(); }
            HelpMarker("Resolve per-bone mass from body-segment mass fractions x total body mass instead of " +
                       "hand-picked values. Takes effect on next ragdoll activation.");

            if (config.RagdollAnthropometricMass)
            {
                var bodyMass = config.RagdollBodyMass;
                if (ImGui.SliderFloat("Body Mass (kg)##ragdoll", ref bodyMass, 30f, 150f, "%.0f"))
                { config.RagdollBodyMass = bodyMass; config.Save(); }
            }

            var anatomicalHingeRestBias = config.RagdollAnatomicalHingeRestBias;
            if (ImGui.Checkbox("Anatomical Hinge Rest Bias##ragdoll", ref anatomicalHingeRestBias))
            { config.RagdollAnatomicalHingeRestBias = anatomicalHingeRestBias; config.Save(); }
            HelpMarker("Passive spring on the knee/elbow hinge pulling it toward straight, so a limb resting on " +
                       "the ground returns toward straight instead of staying visibly bent.");

            var anatomicalRom = config.RagdollAnatomicalRom;
            if (ImGui.Checkbox("Anatomical ROM (asymmetric limits)##ragdoll", ref anatomicalRom))
            { config.RagdollAnatomicalRom = anatomicalRom; config.Save(); }
            HelpMarker("Draw axial twist range and knee/elbow flexion/hyperextension bounds from a clinical " +
                       "anatomical ROM table instead of the hand-set per-bone values. Off by default.");

            var selfCollision = config.RagdollSelfCollision;
            if (ImGui.Checkbox("Self Collision##ragdoll", ref selfCollision))
            { config.RagdollSelfCollision = selfCollision; config.Save(); }
            HelpMarker("Enables contact between every non-adjacent ragdoll body (arms vs torso, legs vs legs).");

            var friction = config.RagdollFriction;
            if (ImGui.SliderFloat("Friction##ragdoll", ref friction, 0.0f, 2.0f, "%.2f"))
            { config.RagdollFriction = friction; config.Save(); }
            HelpMarker("Surface friction for all ragdoll contacts. 0 = ice, 1 = grippy (default).");

            ImGui.Unindent();
        }

        if (config.EnableRagdoll && ImGui.CollapsingHeader("Hair Physics"))
        {
            ImGui.Indent();

            var hairPhysics = config.RagdollHairPhysics;
            if (ImGui.Checkbox("Enable Hair Physics##ragdoll", ref hairPhysics))
            { config.RagdollHairPhysics = hairPhysics; config.Save(); }
            HelpMarker("Simulate hair as real jointed rigid-body strands, with head-driven inertia/whip and " +
                       "ground contact. Works for any hairstyle. Takes effect on next ragdoll activation.");

            if (config.RagdollHairPhysics)
            {
                ImGui.Indent();

                var hairCollision = config.RagdollHairCollision;
                if (ImGui.Checkbox("Hair collision (experimental)##hairrig", ref hairCollision))
                { config.RagdollHairCollision = hairCollision; config.Save(); }
                HelpMarker("Strands also collide with the corpse and NPC volumes instead of only the ground. " +
                           "Experimental — hair roots spawn overlapping the head.");

                var swing = config.RagdollHairRigSwingLimit;
                if (ImGui.SliderFloat("Strand swing ROM (rad)##hairrig", ref swing, 0.1f, 1.5f, "%.2f"))
                { config.RagdollHairRigSwingLimit = swing; config.Save(); }
                HelpMarker("How far each strand joint can bend. Higher = floppier hair.");

                var mass = config.RagdollHairRigSegmentMass;
                if (ImGui.SliderFloat("Strand mass##hairrig", ref mass, 0.005f, 0.1f, "%.3f"))
                { config.RagdollHairRigSegmentMass = mass; config.Save(); }

                var thickness = config.RagdollHairRigThickness;
                if (ImGui.SliderFloat("Strand thickness (m)##hairrig", ref thickness, 0.003f, 0.02f, "%.3f"))
                { config.RagdollHairRigThickness = thickness; config.Save(); }
                HelpMarker("Collision thickness of a strand against the body/ground.");

                var poseForce = config.RagdollHairRigPoseGuideForce;
                if (ImGui.SliderFloat("Style-hold force##hairrig", ref poseForce, 0.0f, 20.0f, "%.1f"))
                { config.RagdollHairRigPoseGuideForce = poseForce; config.Save(); }
                HelpMarker("Servo force that holds the hairstyle at the death instant, then fades over the settle window.");

                var settle = config.RagdollHairRigSettleSeconds;
                if (ImGui.SliderFloat("Settle time (s)##hairrig", ref settle, 0.2f, 3.0f, "%.1f"))
                { config.RagdollHairRigSettleSeconds = settle; config.Save(); }
                HelpMarker("Time to relax strand ROM to full and fade the style-hold servo to zero.");

                if (ImGui.Button("Reset hair rig params##hairrig"))
                {
                    config.RagdollHairRigSegmentMass = 0.02f;
                    config.RagdollHairRigThickness = 0.008f;
                    config.RagdollHairRigSwingLimit = 0.6f;
                    config.RagdollHairRigInitialSwingFactor = 0.28f;
                    config.RagdollHairRigPoseGuideForce = 4f;
                    config.RagdollHairRigSettleSeconds = 1.0f;
                    config.Save();
                }

                ImGui.Unindent();
            }

            ImGui.Unindent();
        }

        if (config.EnableRagdoll && ImGui.CollapsingHeader("Soft Tissue"))
        {
            ImGui.Indent();

            var modSoftBones = config.RagdollSoftTissueModBones;
            if (ImGui.Checkbox("Mod skeleton jiggle bones##softtissue", ref modSoftBones))
            { config.RagdollSoftTissueModBones = modSoftBones; config.Save(); }
            HelpMarker("Auto-detect extra soft-tissue bones from body-mod skeletons by name prefix and simulate " +
                       "them as jiggle bodies during ragdoll. Does nothing on vanilla skeletons.");

            if (config.RagdollSoftTissueModBones)
            {
                ImGui.Indent();
                var scope = config.RagdollSoftTissueScope;
                ImGui.SetNextItemWidth(220);
                if (ImGui.Combo("Bone coverage##softtissue", ref scope, SoftTissueScopeNames, SoftTissueScopeNames.Length))
                {
                    config.RagdollSoftTissueScope = Math.Clamp(scope, 0, SoftTissueScopeNames.Length - 1);
                    config.Save();
                }
                HelpMarker("Standard: extra bones from body-mod skeletons, excluding fingers and toes. " +
                           "All bones: every skeleton bone not already a ragdoll body. All bones except digits: " +
                           "the same, minus fingers and toes.");
                ImGui.Unindent();
            }

            var softCollision = config.RagdollSoftTissueCollision;
            if (ImGui.Checkbox("Soft tissue collision (experimental)##softtissue", ref softCollision))
            { config.RagdollSoftTissueCollision = softCollision; config.Save(); }
            HelpMarker("Soft bones also collide with the ground, so flesh pressed against the floor reacts " +
                       "instead of sinking in.");

            var squash = config.RagdollSquashStretch;
            if (ImGui.Checkbox("Squash & stretch (experimental)##softtissue", ref squash))
            { config.RagdollSquashStretch = squash; config.Save(); }
            HelpMarker("Soft bones visibly compress on hard impacts and flatten against the ground once the " +
                       "corpse settles.");

            if (config.RagdollSquashStretch)
            {
                var squashIntensity = config.RagdollSquashIntensity;
                if (ImGui.SliderFloat("Squash intensity##softtissue", ref squashIntensity, 0.0f, 1.0f, "%.2f"))
                { config.RagdollSquashIntensity = squashIntensity; config.Save(); }
            }

            ImGui.Unindent();
        }

        if (config.EnableRagdoll && ImGui.CollapsingHeader("NPC Ragdoll"))
        {
            ImGui.Indent();

            var npcRagdoll = config.EnableNpcDeathRagdoll;
            if (ImGui.Checkbox("Ragdoll enemies on death##npcragdoll", ref npcRagdoll))
            {
                config.EnableNpcDeathRagdoll = npcRagdoll;
                config.Save();
            }
            HelpMarker("Apply ragdoll physics to nearby battle NPCs when they die.");

            if (config.EnableNpcDeathRagdoll)
            {
                var npcDelay = config.NpcRagdollActivationDelay;
                if (ImGui.SliderFloat("Enemy activation delay (s)##npcragdoll", ref npcDelay, 0.0f, 5.0f, "%.1f"))
                { config.NpcRagdollActivationDelay = npcDelay; config.Save(); }
                HelpMarker("Seconds after enemy death before ragdoll physics take over.");

                var maxNpc = config.MaxNpcRagdolls;
                if (ImGui.SliderInt("Max Concurrent NPC Ragdolls##npcragdoll", ref maxNpc, 1, 10))
                { config.MaxNpcRagdolls = maxNpc; config.Save(); }
                HelpMarker("Limits performance impact. Oldest ragdolls are removed first.");
            }

            ImGui.Separator();
            ImGui.TextDisabled("Solver budget (NPC corpses)");

            var npcIter = config.NpcRagdollSolverIterations;
            if (ImGui.SliderInt("NPC Solver Iterations##npcragdoll", ref npcIter, 1, 32))
            { config.NpcRagdollSolverIterations = npcIter; config.Save(); }
            HelpMarker("Velocity iterations per substep for enemy corpses, kept separate from the player's own. " +
                       "A ragdoll is only expensive for the second or two before it settles, so this is the figure " +
                       "that gets multiplied by a wave of simultaneous deaths.");

            var npcSubsteps = config.NpcRagdollSolverSubsteps;
            if (ImGui.SliderInt("NPC Solver Substeps##npcragdoll", ref npcSubsteps, 1, 64))
            { config.NpcRagdollSolverSubsteps = npcSubsteps; config.Save(); }
            HelpMarker("Substeps for enemy corpses. Raise it if enemy corpses visibly punch through their joint " +
                       "limits or refuse to settle.");

            ImGui.Unindent();
        }
    }

    private void DrawNpcCollisionSection()
    {
        if (ImGui.CollapsingHeader("NPC Collision"))
        {
            var npcCollision = config.RagdollNpcCollision;
            if (ImGui.Checkbox("Enable NPC Collision##npccol", ref npcCollision))
            {
                config.RagdollNpcCollision = npcCollision;
                config.Save();
            }
            HelpMarker("Nearby battle NPCs get per-bone collision volumes so ragdolls can collide with them.");

            if (config.RagdollNpcCollision)
            {
                ImGui.Indent();

                var corpseTraversal = config.RagdollNpcCorpseTraversal;
                if (ImGui.Checkbox("Allow NPCs to Step on Corpses##npccorpsetraversal", ref corpseTraversal))
                { config.RagdollNpcCorpseTraversal = corpseTraversal; config.Save(); }
                HelpMarker("Nearby NPCs can climb onto low corpse surfaces instead of pushing straight through " +
                           "them. On by default.");

                var collisionMode = (int)config.RagdollNpcCollisionMode;
                if (collisionMode < 0 || collisionMode >= NpcCollisionModeLabels.Length)
                    collisionMode = (int)RagdollNpcCollisionMode.BoneCapsule;
                if (ImGui.Combo("Collision shape##npccolmode", ref collisionMode, NpcCollisionModeLabels, NpcCollisionModeLabels.Length))
                {
                    config.RagdollNpcCollisionMode = (RagdollNpcCollisionMode)collisionMode;
                    config.RagdollNpcCollisionConvexHull = config.RagdollNpcCollisionMode == RagdollNpcCollisionMode.ConvexHull;
                    config.Save();
                }
                HelpMarker("Bone capsule uses the existing per-bone capsule proxies. Convex hull builds a single " +
                           "activation-pose hull. Mesh snapshots the rendered model mesh. Takes effect on next " +
                           "ragdoll activation.");

                ImGui.Unindent();
            }
        }
    }

    private static readonly string[] NpcCollisionModeLabels =
    {
        "Bone capsule",
        "Convex hull",
        "Mesh (skinned)",
        "Animated mesh (experimental)",
    };

    private static readonly string[] SoftTissueScopeNames =
        { "Standard (mod bones)", "All bones", "All bones except digits" };

    public void Dispose()
    {
    }
}

using System;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;

namespace RagdollSystem.Gui;

public partial class MainWindow
{
    private static readonly string[] guidedCollapseModeNames = { "Relaxation", "Knee Power Loss" };
    private static readonly string[] guidedRelaxationArchetypeNames = { "StiffHold", "UniformCollapse" };
    private static readonly string[] guidedCollapseDirectionNames = { "None", "Random", "Forward", "Backward", "Sideways" };

    private void DrawGuidedCollapseSection()
    {
        if (!ImGui.CollapsingHeader("Guided Collapse##guidedcollapse"))
            return;

        ImGui.Text("Guided Collapse");
        HelpMarker("Optional procedural death-collapse controller layered on top of ragdoll activation. Requires Enable Ragdoll.");

        var guided = config.GuidedCollapse;
        var enabled = guided.Enabled;
        if (ImGui.Checkbox("Enable Guided Collapse##guidedcollapse", ref enabled))
        {
            guided.Enabled = enabled;
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Restore Defaults##guidedcollapse"))
        {
            guided.ResetDefaults();
            config.Save();
        }
        HelpMarker("Reset all Guided Collapse mode and tuning parameters to built-in defaults.");

        if (guided.Enabled && !config.EnableRagdoll)
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.75f, 0.25f, 1f), "Enable Ragdoll above; Guided Collapse only arms during ragdoll activation.");

        if (guided.Enabled && config.RagdollActivationDelay > 0.001f)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.75f, 0.25f, 1f), "Activation Delay is not zero; guided collapse captures the delayed death-animation pose.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Set 0##guidedcollapseDelay"))
            {
                config.RagdollActivationDelay = 0f;
                config.Save();
            }
        }

        using (ImRaii.Disabled(!guided.Enabled))
        {
            var mode = Math.Clamp(guided.Mode, 0, guidedCollapseModeNames.Length - 1);
            if (ImGui.Combo("Mode##guidedcollapse", ref mode, guidedCollapseModeNames, guidedCollapseModeNames.Length))
            {
                guided.Mode = mode;
                config.Save();
            }

            ImGui.Separator();
            if (guided.Mode == 0)
                DrawGuidedRelaxationSettings(guided.Relaxation);
            else
                DrawGuidedKneePowerLossSettings(guided.KneePowerLoss);
        }
    }

    private void DrawGuidedRelaxationSettings(GuidedCollapseRelaxationSettings s)
    {
        ImGui.Text("Relaxation");
        HelpMarker("Validated hold-then-fade active-ragdoll family. Good for power-off/limp deaths; does not try to kneel.");

        var archetype = Math.Clamp(s.Archetype, 0, guidedRelaxationArchetypeNames.Length - 1);
        if (ImGui.Combo("Relaxation mode##guidedrelax", ref archetype, guidedRelaxationArchetypeNames, guidedRelaxationArchetypeNames.Length))
        { s.Archetype = archetype; config.Save(); }

        var strength = Math.Clamp(s.Strength, 0.5f, 40f);
        if (ImGui.SliderFloat("Strength (Hz)##guidedrelax", ref strength, 0.5f, 40f, "%.1f"))
        { s.Strength = strength; config.Save(); }

        var hold = Math.Clamp(s.Hold, 0f, 5f);
        if (ImGui.SliderFloat("Hold (s)##guidedrelax", ref hold, 0f, 5f, "%.2f"))
        { s.Hold = hold; config.Save(); }

        var fade = Math.Clamp(s.Fade, 0.05f, 8f);
        if (ImGui.SliderFloat("Fade (s)##guidedrelax", ref fade, 0.05f, 8f, "%.2f"))
        { s.Fade = fade; config.Save(); }

        var brakeStrength = Math.Clamp(s.BrakeStrength, 0f, 1f);
        if (ImGui.SliderFloat("Eccentric brake##guidedrelax", ref brakeStrength, 0f, 1f, "%.2f"))
        { s.BrakeStrength = brakeStrength; config.Save(); }
        HelpMarker("After the hold fades, joints keep a velocity-resisting brake instead of going slack instantly — the body sinks, gets braked, sinks, rather than free-falling to limp. 0 = old instant-limp; ~0.3 = noticeable controlled settle.");

        using (ImRaii.Disabled(s.BrakeStrength <= 0f))
        {
            var brakeFade = Math.Clamp(s.BrakeFade, 0.05f, 4f);
            if (ImGui.SliderFloat("Brake fade (s)##guidedrelax", ref brakeFade, 0.05f, 4f, "%.2f"))
            { s.BrakeFade = brakeFade; config.Save(); }
            HelpMarker("Seconds the eccentric brake decays over after the hold is gone.");
        }

        var hinge = Math.Clamp(s.HingeSoften, 0f, 1f);
        if (ImGui.SliderFloat("Hinge strength##guidedrelax", ref hinge, 0f, 1f, "%.2f"))
        { s.HingeSoften = hinge; config.Save(); }

        var direction = Math.Clamp(s.Direction, 0, guidedCollapseDirectionNames.Length - 1);
        if (ImGui.Combo("Topple direction##guidedrelax", ref direction, guidedCollapseDirectionNames, guidedCollapseDirectionNames.Length))
        { s.Direction = direction; config.Save(); }

        var topple = config.RagdollRelaxationTopple;
        if (ImGui.Checkbox("Whole-body topple##guidedrelax", ref topple))
        { config.RagdollRelaxationTopple = topple; config.Save(); }
        HelpMarker("Drive a whole-body center-of-mass topple fused with the eccentric brake, instead of only a one-shot shove. Falls in 'Topple direction'.");

        var asym = Math.Clamp(config.RagdollCollapseAsymmetry, 0f, 1f);
        if (ImGui.SliderFloat("Asymmetry##guidedrelax", ref asym, 0f, 1f, "%.2f"))
        { config.RagdollCollapseAsymmetry = asym; config.Save(); }
        HelpMarker("Real collapses are never symmetric. 0 = symmetric (robotic), ~0.35 = natural, 1 = strongly one-sided.");

        var staged = config.RagdollStagedFailure;
        if (ImGui.Checkbox("Staged muscle failure##guidedrelax", ref staged))
        { config.RagdollStagedFailure = staged; config.Save(); }
        HelpMarker("Let muscle groups fail in sequence — legs give first, trunk holds a beat longer, arms trail last.");

        using (ImRaii.Disabled(!config.RagdollRelaxationTopple))
        {
            var momentum = Math.Clamp(config.RagdollToppleMomentumBias, 0f, 1f);
            if (ImGui.SliderFloat("Momentum steering##guidedrelax", ref momentum, 0f, 1f, "%.2f"))
            { config.RagdollToppleMomentumBias = momentum; config.Save(); }
            HelpMarker("Bias the topple toward the body's actual horizontal motion at the handoff, so a moving corpse falls the way it was going.");
        }

        using (ImRaii.Disabled(s.Direction == 0 || config.RagdollRelaxationTopple))
        {
            var impulse = Math.Clamp(s.Impulse, 0f, 8f);
            if (ImGui.SliderFloat("Topple impulse##guidedrelax", ref impulse, 0f, 8f, "%.2f"))
            { s.Impulse = impulse; config.Save(); }
            HelpMarker("Simple one-shot velocity shove at death. Only used when Whole-body topple is OFF.");
        }
    }

    private void DrawGuidedKneePowerLossSettings(GuidedCollapseKneePowerLossSettings s)
    {
        ImGui.Text("Knee Power Loss");
        HelpMarker("Directed collapse pattern: optional entry conditioning, then leg-extensor failure and forward torso release.");

        var entry = s.EntryConditioningEnabled;
        if (ImGui.Checkbox("Entry conditioning##guidedknee", ref entry))
        { s.EntryConditioningEnabled = entry; config.Save(); }

        using (ImRaii.Disabled(!s.EntryConditioningEnabled))
        {
            var stanceThreshold = Math.Clamp(s.EntryStanceThreshold, 0.05f, 1.0f);
            if (ImGui.SliderFloat("Trigger stance width##guidedknee", ref stanceThreshold, 0.05f, 1.0f, "%.2f"))
            { s.EntryStanceThreshold = stanceThreshold; config.Save(); }

            var readyStance = Math.Clamp(s.EntryReadyStance, 0.05f, 1.2f);
            if (ImGui.SliderFloat("Ready stance width##guidedknee", ref readyStance, 0.05f, 1.2f, "%.2f"))
            { s.EntryReadyStance = readyStance; config.Save(); }

            var readyKnee = Math.Clamp(s.EntryReadyKneeAngle, 1f, 60f);
            if (ImGui.SliderFloat("Ready knee angle##guidedknee", ref readyKnee, 1f, 60f, "%.1f"))
            { s.EntryReadyKneeAngle = readyKnee; config.Save(); }

            var minDur = Math.Clamp(s.EntryMinDuration, 0.05f, 1.0f);
            if (ImGui.SliderFloat("Entry min (s)##guidedknee", ref minDur, 0.05f, 1.0f, "%.2f"))
            {
                s.EntryMinDuration = minDur;
                if (s.EntryMaxDuration < s.EntryMinDuration) s.EntryMaxDuration = s.EntryMinDuration;
                config.Save();
            }

            var maxDur = Math.Clamp(s.EntryMaxDuration, s.EntryMinDuration, 1.5f);
            if (ImGui.SliderFloat("Entry max (s)##guidedknee", ref maxDur, s.EntryMinDuration, 1.5f, "%.2f"))
            { s.EntryMaxDuration = maxDur; config.Save(); }
        }

        if (ImGui.CollapsingHeader("Entry Advanced##guidedknee"))
        {
            var targetStart = Math.Clamp(s.EntryTargetStanceStart, 0.05f, 1.2f);
            if (ImGui.SliderFloat("Target stance start##guidedknee", ref targetStart, 0.05f, 1.2f, "%.2f"))
            { s.EntryTargetStanceStart = targetStart; config.Save(); }

            var targetEnd = Math.Clamp(s.EntryTargetStanceEnd, 0.05f, 1.2f);
            if (ImGui.SliderFloat("Target stance end##guidedknee", ref targetEnd, 0.05f, 1.2f, "%.2f"))
            { s.EntryTargetStanceEnd = targetEnd; config.Save(); }

            var downStart = Math.Clamp(s.EntryPelvisDownStart, 0f, 2f);
            if (ImGui.SliderFloat("Pelvis down start##guidedknee", ref downStart, 0f, 2f, "%.2f"))
            { s.EntryPelvisDownStart = downStart; config.Save(); }

            var downEnd = Math.Clamp(s.EntryPelvisDownEnd, 0f, 2f);
            if (ImGui.SliderFloat("Pelvis down end##guidedknee", ref downEnd, 0f, 2f, "%.2f"))
            { s.EntryPelvisDownEnd = downEnd; config.Save(); }
        }

        ImGui.Separator();

        var flexDegrees = Math.Clamp(s.KneeFlexDegrees, 0f, 90f);
        if (ImGui.SliderFloat("Knee flex target##guidedknee", ref flexDegrees, 0f, 90f, "%.0f deg"))
        { s.KneeFlexDegrees = flexDegrees; config.Save(); }

        var buckleFlex = Math.Clamp(s.KneeBuckleFlexForce, 0f, 500f);
        if (ImGui.SliderFloat("Buckle knee force##guidedknee", ref buckleFlex, 0f, 500f, "%.0f"))
        { s.KneeBuckleFlexForce = buckleFlex; config.Save(); }

        var torsoFlex = Math.Clamp(s.KneeTorsoFlexForce, 0f, 500f);
        if (ImGui.SliderFloat("Torso knee force##guidedknee", ref torsoFlex, 0f, 500f, "%.0f"))
        { s.KneeTorsoFlexForce = torsoFlex; config.Save(); }

        var footSupport = Math.Clamp(s.BuckleFootSupportForce, 0f, 5000f);
        if (ImGui.SliderFloat("Buckle foot support##guidedknee", ref footSupport, 0f, 5000f, "%.0f"))
        { s.BuckleFootSupportForce = footSupport; config.Save(); }

        if (ImGui.CollapsingHeader("Support Advanced##guidedknee"))
        {
            var footProxy = s.FootProxyEnabled;
            if (ImGui.Checkbox("Foot contact proxy##guidedknee", ref footProxy))
            { s.FootProxyEnabled = footProxy; config.Save(); }
            HelpMarker("Applies the temporary foot support servo at a virtual sole point near the ground instead of the ankle body center.");

            using (ImRaii.Disabled(!s.FootProxyEnabled))
            {
                var proxyForward = Math.Clamp(s.FootProxyForwardOffset, -0.05f, 0.25f);
                if (ImGui.SliderFloat("Proxy forward##guidedknee", ref proxyForward, -0.05f, 0.25f, "%.3f"))
                { s.FootProxyForwardOffset = proxyForward; config.Save(); }

                var proxyDown = Math.Clamp(s.FootProxyDownOffset, 0f, 0.16f);
                if (ImGui.SliderFloat("Proxy down##guidedknee", ref proxyDown, 0f, 0.16f, "%.3f"))
                { s.FootProxyDownOffset = proxyDown; config.Save(); }

                var proxyClearance = Math.Clamp(s.FootProxyGroundClearance, 0.004f, 0.08f);
                if (ImGui.SliderFloat("Proxy clearance##guidedknee", ref proxyClearance, 0.004f, 0.08f, "%.3f"))
                { s.FootProxyGroundClearance = proxyClearance; config.Save(); }
            }

            ImGui.Separator();

            var torsoFoot = Math.Clamp(s.TorsoFootSupportForce, 0f, 5000f);
            if (ImGui.SliderFloat("Torso foot support##guidedknee", ref torsoFoot, 0f, 5000f, "%.0f"))
            { s.TorsoFootSupportForce = torsoFoot; config.Save(); }

            var torsoPelvis = Math.Clamp(s.TorsoPelvisForce, 0f, 3000f);
            if (ImGui.SliderFloat("Torso pelvis force##guidedknee", ref torsoPelvis, 0f, 3000f, "%.0f"))
            { s.TorsoPelvisForce = torsoPelvis; config.Save(); }

            var pelvisForce = Math.Clamp(s.BucklePelvisForce, 0f, 3000f);
            if (ImGui.SliderFloat("Buckle pelvis force##guidedknee", ref pelvisForce, 0f, 3000f, "%.0f"))
            { s.BucklePelvisForce = pelvisForce; config.Save(); }
        }

        var chestPitch = Math.Clamp(s.ChestPitchDegrees, -90f, 90f);
        if (ImGui.SliderFloat("Chest pitch##guidedknee", ref chestPitch, -90f, 90f, "%.0f deg"))
        { s.ChestPitchDegrees = chestPitch; config.Save(); }

        if (ImGui.CollapsingHeader("Phase Transition Advanced##guidedknee"))
        {
            ImGui.Separator();

            var buckleMin = Math.Clamp(s.BuckleMinDuration, 0.05f, 1.5f);
            if (ImGui.SliderFloat("Buckle min (s)##guidedknee", ref buckleMin, 0.05f, 1.5f, "%.2f"))
            { s.BuckleMinDuration = buckleMin; config.Save(); }

            var buckleTimeout = Math.Clamp(s.BuckleTimeout, 0.1f, 3f);
            if (ImGui.SliderFloat("Buckle timeout (s)##guidedknee", ref buckleTimeout, 0.1f, 3f, "%.2f"))
            { s.BuckleTimeout = buckleTimeout; config.Save(); }

            var dropToTorso = Math.Clamp(s.BucklePelvisDropToTorso, 0.05f, 1.5f);
            if (ImGui.SliderFloat("Drop to torso##guidedknee", ref dropToTorso, 0.05f, 1.5f, "%.2f"))
            { s.BucklePelvisDropToTorso = dropToTorso; config.Save(); }

            var kneeToTorso = Math.Clamp(s.BuckleKneeAngleToTorso, 1f, 90f);
            if (ImGui.SliderFloat("Knee angle to torso##guidedknee", ref kneeToTorso, 1f, 90f, "%.1f"))
            { s.BuckleKneeAngleToTorso = kneeToTorso; config.Save(); }

            var torsoMin = Math.Clamp(s.TorsoMinDuration, 0.05f, 2f);
            if (ImGui.SliderFloat("Torso min (s)##guidedknee", ref torsoMin, 0.05f, 2f, "%.2f"))
            { s.TorsoMinDuration = torsoMin; config.Save(); }

            var torsoTimeout = Math.Clamp(s.TorsoTimeout, 0.1f, 3f);
            if (ImGui.SliderFloat("Torso timeout (s)##guidedknee", ref torsoTimeout, 0.1f, 3f, "%.2f"))
            { s.TorsoTimeout = torsoTimeout; config.Save(); }
        }
    }
}

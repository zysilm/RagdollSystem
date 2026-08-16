using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace RagdollSystem;

[Serializable]
public class RagdollBoneConfig
{
    public string Name { get; set; } = "";
    public string? SkeletonParent { get; set; }  // real skeleton parent (for computing physics parent chain)
    public bool Enabled { get; set; } = true;     // whether this bone participates in physics
    public float CapsuleRadius { get; set; }
    public float CapsuleHalfLength { get; set; }
    public float Mass { get; set; }
    public float SwingLimit { get; set; }
    public float? SwingMinLimit { get; set; } // hinge-only lower bound; null means migrate/default
    public float? HingeRestAngle { get; set; } // hinge-only passive rest target; null disables/defaults
    public float? HingeRestSpringFreq { get; set; }
    public float? HingeRestMaxForce { get; set; }
    public int JointType { get; set; } // 0=Ball, 1=Hinge
    public float TwistMinAngle { get; set; }
    public float TwistMaxAngle { get; set; }
    public int AnatomicalRole { get; set; } // 0=Generic, see RagdollController.AnatomicalRole
    public int ColliderShape { get; set; } // 0=Capsule, 1=Box
    public float BoxHalfExtentX { get; set; }
    public float BoxHalfExtentY { get; set; }
    public float BoxHalfExtentZ { get; set; }
    public string? Description { get; set; }      // human-readable label for UI
    // Soft body spring settings (for breast/jiggle bones). Split: stiff translation (the socket
    // only carries flesh weight, ~mm of sag), soft rotation (the jiggle swings about the pivot).
    public bool SoftBody { get; set; }             // use soft springs + AngularServo instead of rigid + AngularMotor
    public float SoftSpringFreq { get; set; } = 10f;   // BallSocket spring frequency (Hz); static sag = g/(2πf)²
    public float SoftSpringDamp { get; set; } = 1f;    // BallSocket damping ratio, critical = no translational ring
    public float SoftServoFreq { get; set; } = 3f;     // AngularServo spring frequency (Hz), the jiggle rate
    public float SoftServoDamp { get; set; } = 0.2f;   // AngularServo damping ratio, lower = more swings before settling
}

[Serializable]
public class RagdollBoneProfile
{
    public string Name { get; set; } = "";
    public List<RagdollBoneConfig> Bones { get; set; } = new();
}

[Serializable]
public class GuidedCollapseSettings
{
    public bool Enabled { get; set; } = false;
    public int Mode { get; set; } = 1; // 0=Relaxation, 1=KneePowerLoss
    public GuidedCollapseRelaxationSettings Relaxation { get; set; } = new();
    public GuidedCollapseKneePowerLossSettings KneePowerLoss { get; set; } = new();

    public void ResetDefaults()
    {
        var defaults = new GuidedCollapseSettings();
        Enabled = defaults.Enabled;
        Mode = defaults.Mode;
        Relaxation = defaults.Relaxation;
        KneePowerLoss = defaults.KneePowerLoss;
    }
}

[Serializable]
public class GuidedCollapseRelaxationSettings
{
    public int Archetype { get; set; } = 1;        // 0=StiffHold, 1=UniformCollapse
    public float Strength { get; set; } = 14f;
    public float Hold { get; set; } = 0.3f;
    public float Fade { get; set; } = 0.9f;
    public float HingeSoften { get; set; } = 0.25f;
    public int Direction { get; set; } = 1;        // 0=None,1=Random,2=Forward,3=Backward,4=Sideways
    public float Impulse { get; set; } = 2.0f;
    // Eccentric braking: after the hold fades, joints keep a velocity-resisting brake (the
    // muscle "pays out" under load) instead of going slack instantly, so the body sinks →
    // gets braked → sinks rather than free-falling to limp. BrakeStrength = residual torque
    // ceiling as a fraction of full (0 = old instant-limp behavior, ~0.3 = noticeable brake);
    // BrakeFade = seconds the brake decays over after the hold is gone.
    public float BrakeStrength { get; set; } = 0.3f;
    public float BrakeFade { get; set; } = 0.7f;
}

[Serializable]
public class GuidedCollapseKneePowerLossSettings
{
    public float EntryStrength { get; set; } = 0.65f;
    public float KneeYield { get; set; } = 0.55f;
    public float FootGrip { get; set; } = 0.65f;
    public float ForwardCommitment { get; set; } = 0.55f;
    public float ReleaseTiming { get; set; } = 0.55f;
    public bool EntryConditioningEnabled { get; set; } = true;
    public float EntryStanceThreshold { get; set; } = 0.28f;
    public float EntryReadyStance { get; set; } = 0.30f;
    public float EntryReadyKneeAngle { get; set; } = 10f;
    public float EntryMinDuration { get; set; } = 0.24f;
    public float EntryMaxDuration { get; set; } = 0.42f;
    public float EntryTargetStanceStart { get; set; } = 0.34f;
    public float EntryTargetStanceEnd { get; set; } = 0.50f;
    public float EntryPelvisDownStart { get; set; } = 0.32f;
    public float EntryPelvisDownEnd { get; set; } = 0.60f;
    public float KneeFlexDegrees { get; set; } = 34f;
    // Knee-flex torques act on the lower-leg inertia, which the anthropometric masses cut to
    // ~half (shin 3->1.8, calf 1->0.18 kg). Scaled down ~x0.55 so the knee buckles at the same
    // rate instead of over-driving on the now-lighter leg (was 82 / 42).
    public float KneeBuckleFlexForce { get; set; } = 46f;
    public float KneeTorsoFlexForce { get; set; } = 24f;
    // Foot supports are positional pins that anchor the WHOLE body's pivot over the planted
    // foot; body mass is unchanged (~70 kg), so these stay as-is (lowering them slips the foot).
    public float BuckleFootSupportForce { get; set; } = 1100f;
    public float TorsoFootSupportForce { get; set; } = 650f;
    public bool FootProxyEnabled { get; set; } = true;
    public float FootProxyForwardOffset { get; set; } = 0.10f;
    public float FootProxyDownOffset { get; set; } = 0.035f;
    public float FootProxyGroundClearance { get; set; } = 0.018f;
    // Pelvis-drive torques act on the trunk, which the anthropometric masses made HEAVIER
    // (pelvis 8->9.9, mid-spine 5->7.7 kg). Scaled up ~x1.24 so the torso still pitches
    // forward in step with the buckling legs instead of lagging (was 420 / 220).
    public float BucklePelvisForce { get; set; } = 520f;
    public float TorsoPelvisForce { get; set; } = 275f;
    public float ChestPitchDegrees { get; set; } = 41f;
    public bool UseSemanticControls { get; set; } = false;
    public float BuckleMinDuration { get; set; } = 0.24f;
    public float BuckleTimeout { get; set; } = 0.95f;
    public float BucklePelvisDropToTorso { get; set; } = 0.30f;
    public float BuckleKneeAngleToTorso { get; set; } = 22f;
    public float TorsoMinDuration { get; set; } = 0.55f;
    public float TorsoTimeout { get; set; } = 0.90f;
}

public enum RagdollNpcCollisionMode
{
    BoneCapsule = 0,
    ConvexHull = 1,
    Mesh = 2,
    AnimatedMesh = 3,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // General
    public bool ShowMainWindow { get; set; } = false;
    public float SidebarWidth { get; set; } = 130f;

    // Ragdoll physics
    public bool EnableRagdoll { get; set; } = true;
    public float RagdollActivationDelay { get; set; } = 1.0f;
    // Extend terrain detection: also build ground collision patches under nearby
    // enemies (not just the death spot) so a victory-sequence grab that drags the
    // body onto an enemy doesn't fall through the floor on release. Costs extra
    // raycasts/triangles at activation, so default off.
    public bool ExtendTerrainDetection { get; set; } = false;
    public float RagdollGravity { get; set; } = 9.8f;
    public float RagdollDamping { get; set; } = 0.97f;
    // Velocity iterations per SUBSTEP (not per frame). Total solver work is this times
    // RagdollSolverSubsteps. Paired with 16 substeps below, 4 iterations is the same total
    // work as the old flat 8-iteration/1-substep solve while converging joint limits far
    // more finely (see RagdollSolverSubsteps).
    public int RagdollSolverIterations { get; set; } = 4;
    // Velocity-solve substeps per fixed timestep. 1 = legacy single-substep behavior. Raising
    // this re-integrates poses and re-solves constraints at a finer sub-step, which is BEPU's
    // recommended lever for making a stiff limit wall (see RagdollLimitSpringFrequency)
    // well-conditioned instead of pumping energy.
    public int RagdollSolverSubsteps { get; set; } = 16;
    // The same two knobs for an NPC corpse, which gets a much smaller solver budget than the
    // player so a wave of simultaneous deaths doesn't spike.
    public int NpcRagdollSolverIterations { get; set; } = 2;
    public int NpcRagdollSolverSubsteps { get; set; } = 8;
    // Spring frequency (Hz) of the joint LIMIT walls (swing cones + twist ranges), not the
    // positional joints. Higher = firmer wall so joints don't blow past their range under
    // momentum; too high relative to the 60Hz step can over-drive the solver into jitter.
    public float RagdollLimitSpringFrequency { get; set; } = 90f;
    // Soft-edge swing limits (ball joints): replace the hard wall on swing cones with a
    // low-frequency, overdamped spring so a joint sliding toward its range edge decelerates
    // and settles instead of pinning at the extreme angle. Hinges (knee/elbow) keep the hard
    // wall — a soft knee reads as hyperextension.
    public bool RagdollSoftLimits { get; set; } = true;
    public float RagdollSoftLimitFrequency { get; set; } = 12f;
    public float RagdollSoftLimitDamping { get; set; } = 4f;
    // Spring frequency (Hz) of the POSITIONAL joints (the BallSocket/Weld that hold bones
    // together), as opposed to the limit walls above. Higher = bones separate less under
    // large impulses ("rubber-band" stretch).
    public float RagdollJointSpringFrequency { get; set; } = 45f;
    // Positional joint stiffness for the FOOT specifically (calf->foot BallSocket). The foot
    // takes the hardest ground-impact impulses, so it rubber-bands first; give it a firmer
    // spring than the body default. 0 = fall back to RagdollJointSpringFrequency.
    public float RagdollFootJointSpringFrequency { get; set; } = 60f;
    // Animation-driven handoff: when the ragdoll activates after the death-animation delay,
    // seed each physics body with the velocity the animation was carrying instead of starting
    // at zero. Removes the "freeze" hitch when an in-motion death animation hands off to
    // physics, and gives the topple its initial momentum.
    public bool RagdollCarryAnimationVelocity { get; set; } = true;
    // Scales the carried handoff velocity (1 = exact animation speed). Lower if a fast death
    // animation throws the corpse too hard at handoff.
    public float RagdollHandoffVelocityScale { get; set; } = 1.0f;
    // Stage a hard landing instead of merely simulating it: a brief freeze and a heavier heave
    // on the limbs at impact, since physics alone reads as too light without it.
    public bool RagdollImpactWeight { get; set; } = true;
    // Make the body itself read heavy: fall harder than true gravity, and give the limbs more
    // rotational inertia than a thin capsule implies. Changes trajectories (how far a kicked
    // corpse flies, how fast it tumbles), unlike RagdollImpactWeight which only affects landing.
    public bool RagdollHeavyBody { get; set; } = true;
    public bool RagdollRelaxationTopple { get; set; } = true;
    // Collapse asymmetry: pick a random lead side each death — its leg buckles first and the
    // whole-body topple leans + twists toward it — instead of a flat, mirror-image fall.
    // 0 = perfectly symmetric, ~0.35 = natural lopsided collapse, 1 = strongly one-sided.
    public float RagdollCollapseAsymmetry { get; set; } = 0.35f;
    // Staged muscle failure: let muscle groups fail in sequence (legs first, trunk a beat
    // later, arms trailing) instead of one shared fade curve.
    public bool RagdollStagedFailure { get; set; } = true;
    // Momentum-steered topple: bias the fall direction toward the body's actual horizontal
    // motion at handoff, so a moving corpse falls the way it was going.
    public float RagdollToppleMomentumBias { get; set; } = 0.5f;
    public bool RagdollSelfCollision { get; set; } = true; // Body parts collide with each other (arms vs torso, etc)
    public float RagdollFriction { get; set; } = 1.0f; // Surface friction (0=ice, 1=grippy). Lower = limbs slide more realistically.

    // Anthropometric segment masses: each physics bone's mass is resolved as a body-mass
    // fraction x RagdollBodyMass instead of the hand-picked per-bone Mass values.
    public bool RagdollAnthropometricMass { get; set; } = true;
    public float RagdollBodyMass { get; set; } = 70f; // Total body mass (kg) anthropometric fractions scale against.
    // Asymmetric swing-twist range of motion from a clinical/ISB anatomical ROM table instead
    // of the hand-set per-bone twist values. Blocks knee/elbow backward hyperextension.
    // Default off: only useful in specific setups; the twist governors and limit tables stay
    // active regardless.
    public bool RagdollAnatomicalRom { get; set; } = false;
    // Passive hinge rest bias — a soft spring on the knee/elbow hinge pulling it toward
    // straight, so a limb resting on the ground (a supine corpse) returns to straight instead
    // of staying visibly bent.
    public bool RagdollAnatomicalHingeRestBias { get; set; } = true;

    // Point-of-collapse bone list for a limb-hide effect (root bone names of severed parts).
    // Always empty in this standalone build — there is no dismemberment feature to populate
    // it — kept only because the ported RagdollController reads it as an input.
    public List<string> DismemberPocBones { get; set; } = new();

    // Death collapse — physics-driven guided collapse on death (relaxation family + directed
    // knee power-loss).
    public GuidedCollapseSettings GuidedCollapse { get; set; } = new();

    // Blade capsule half-length used to size the (inert in this build) live-combat weapon
    // collision geometry inside RagdollController. No weapon-drop simulation is wired up in
    // this standalone build.
    public float WeaponDropHalfLength { get; set; } = 0.4f;

    // Hair physics — the BEPU strand rig (jointed rigid-body strands), anchored to the head
    // ragdoll body. Works for any hairstyle — built from the hair partial-skeleton bone tree.
    public bool RagdollHairPhysics { get; set; } = false;
    // Strand-vs-corpse contact. Off by default: strands spawn overlapping the head/body
    // capsules and contact resolution on overlapping spawns can fling the ragdoll.
    public bool RagdollHairCollision { get; set; } = false;
    public float RagdollHairRigSegmentMass { get; set; } = 0.02f;        // per-segment mass (very light)
    public float RagdollHairRigThickness { get; set; } = 0.008f;         // strand box half-thickness (m)
    public float RagdollHairRigSwingLimit { get; set; } = 0.6f;          // per-joint swing ROM (radians)
    public float RagdollHairRigInitialSwingFactor { get; set; } = 0.28f; // spawn ROM fraction (holds style, relaxes to full)
    public float RagdollHairRigPoseGuideForce { get; set; } = 4f;        // servo force holding the style at spawn, fades out
    public float RagdollHairRigSettleSeconds { get; set; } = 1.0f;       // time to relax ROM to full + fade the pose guide

    // Soft tissue — mod-skeleton jiggle bones. Bones whose names match one of the
    // comma-separated prefixes are auto-registered as SoftBody jiggle bodies at ragdoll
    // activation. Vanilla skeletons have no matching bones, so this is inert without a body
    // mod installed.
    public bool RagdollSoftTissueModBones { get; set; } = true;
    public string RagdollSoftTissueBonePrefixes { get; set; } = "iv_, ya_";
    // Bone coverage: 0 = Standard (prefix-matched mod-skeleton bones except fingers/toes),
    // 1 = All bones, 2 = All bones except digits.
    public int RagdollSoftTissueScope { get; set; } = 0;
    public bool RagdollSoftTissueCollision { get; set; } = false;
    // Soft tissue — squash & stretch (EXPERIMENTAL). Writes per-bone scale into the skeleton
    // pose so soft bones visibly compress on impact and flatten against the ground at rest.
    public bool RagdollSquashStretch { get; set; } = false;
    public float RagdollSquashIntensity { get; set; } = 0.5f; // 0..1, scales max compression
    // Uniformly lifts the whole rig at activation if any bone's capsule/box would start
    // underground. Off by default — it fights Ragdoll Follow (the two disagree about where the
    // corpse root actually is right after a lift, causing a visible pop). Rare underground-start
    // cases are left to damped contact recovery instead.
    public bool RagdollGroundPenetrationLift { get; set; } = false;

    // Debug
    public bool RagdollDebugOverlay { get; set; } = false;
    public bool RagdollVerboseLog { get; set; } = false;
    // Follows the ragdoll root to keep a flung corpse from being culled/unloaded on long
    // falls. Local player moves render-only (DrawObject.Position); NPC phantoms move full
    // position.
    public bool RagdollFollowPosition { get; set; } = true;

    // Bone configs (Advanced)
    public List<RagdollBoneConfig> RagdollBoneConfigs { get; set; } = new();
    public List<RagdollBoneProfile> RagdollBoneProfiles { get; set; } = new();

    // NPC death ragdoll
    public bool EnableNpcDeathRagdoll { get; set; } = false;
    public float NpcRagdollActivationDelay { get; set; } = 0.5f;

    // Auto-cleanup (standalone-plugin-only: combat sim relies on its own combat/despawn
    // lifecycle instead of a timed expiry)
    public float RagdollDuration { get; set; } = 30.0f;

    // Max concurrent NPC ragdolls
    public int MaxNpcRagdolls { get; set; } = 5;

    // NPC collision volumes for ragdoll interaction with nearby actors
    public bool RagdollNpcCollision { get; set; } = true;
    // Let nearby NPCs treat settled ragdoll bodies as a low, walkable surface. Experimental
    // and opt-in.
    public bool RagdollNpcCorpseTraversal { get; set; } = true;
    /// <summary>Derive structural collision centers, axes, lengths and cross-sections from the
    /// character's weighted body mesh. Joint topology remains anatomical and invariant;
    /// invalid measurements fall back to deterministic built-in geometry per segment.</summary>
    public bool RagdollCharacterSurfaceProfiles { get; set; } = true;
    public bool RagdollNpcCollisionAutoSize { get; set; } = true;
    public float RagdollNpcCollisionScale { get; set; } = 0.0001f;
    public bool RagdollNpcCollisionConvexHull { get; set; } = false;
    // Bone capsule: the per-bone capsule proxies that already exist for the ragdoll — no hull
    // or mesh built at activation. Convex-hull/mesh are more faithful but cost more to build.
    public RagdollNpcCollisionMode RagdollNpcCollisionMode { get; set; } = RagdollNpcCollisionMode.BoneCapsule;
    /// <summary>Whether NPC collision is actually in force right now. Plain passthrough in
    /// this standalone build — combat sim's combat-recipe override layer doesn't exist here.</summary>
    public bool NpcCollisionActive => RagdollNpcCollision;
    public bool RagdollNpcSettleCollision { get; set; } = true;

    // Inert gates kept only because the ported RagdollController checks them (both always
    // false in this standalone build — there is no combat-companion system here).
    public bool EnableCombatCompanions { get; set; } = false;
    public bool PartyCompanionDeathRagdoll { get; set; } = false;


    // Physics-dropped gear (GearDropController.cs, ported verbatim from source's
    // DismembermentController.cs with the AnimationController dependency removed).
    // Only KoStripPhysicsDrop is exposed in the GUI (see MainWindow.ArmorDetachment.cs) — a
    // simple rigid-body tumble for hats/accessories. Everything below it (garment-tube cloth
    // draping, skirt physics, cloth-hold presets, advanced clothing physics) is the full source
    // feature set kept for build correctness and possible future use, but deliberately left off
    // by default and unexposed in the GUI: it is tightly interleaved with the rigid-drop path
    // inside the 7.8k-line controller (shared per-clone state, shared spawn/tick loop) in ways
    // that don't cleanly separate, so surgically removing it risked introducing a subtle physics
    // bug that isn't easy to verify without extensive in-game testing. Keeping it compiled-but-
    // inert was judged safer than a risky prune.
    // Physically drop hats / accessories (separate models, not fused with skin) as falling rigid
    // bodies instead of just hiding them. Head + accessory slots.
    public bool KoStripPhysicsDrop { get; set; } = true;

    // Physically drop supported clothing (Body / Legs) as falling shells. Still includes the body skin
    // baked into those equipment models, so it remains opt-in.
    public bool KoStripPhysicsDropClothing { get; set; } = true;

    // Garment polish layered on top of clothing physics drop: short visual body follow, body/ground
    // friction damping, and delayed cloth collapse.
    public bool KoStripAdvancedClothPhysics { get; set; } = true;

    // Experimental: drive the upper garment (Body slot) with a ring-tube physics model instead of the
    // chain-of-boxes rig. The tube wraps the corpse capsules, so the shirt slides down off the body
    // instead of folding. Host ragdoll only; falls back to the chain rig when unavailable.
    public bool KoStripGarmentTubeModel { get; set; } = false;

    // Draw the garment tube's ring bodies as a wireframe overlay (tuning aid). Not saved-critical.
    public bool KoStripGarmentTubeDebugDraw { get; set; } = false;

    // Give a coat's j_sk_* columns real bodies hanging off the hem ring, instead of riding it rigidly.
    // They then swing and fold, and — since the rig already collides with the body — drape over the legs
    // and pool on the ground rather than passing through them. Costs roughly one body and one joint per
    // skirt bone (~18 on a typical coat); only reachable when the tube model is on, which already brings
    // a rig of its own.
    public bool KoStripGarmentSkirtPhysics { get; set; } = true;
    public float KoStripSkirtSegmentMass { get; set; } = 0.06f;
    // Radians a segment may swing away from its parent. Too tight reads as a board; too loose lets a
    // panel fold back through the leg it is hanging on.
    public float KoStripSkirtSwingLimit { get; set; } = 0.9f;
    // How much of that swing is allowed at birth. The rig relaxes from this to the full range over the
    // first second, so a skirt does not burst out of its rest shape on the frame it is created.
    public float KoStripSkirtInitialSwing { get; set; } = 0.3f;

    // Pieces still attached to the body travel with it when the body is moved as a whole. Nothing but
    // contact holds them on, so without this they are simply left where they were. Pieces that have
    // already come away stay put; which is which is decided by whether the piece still wraps the bones
    // it was built around, not by how near the body it happens to lie.
    public bool KoStripGarmentFollowsBody { get; set; } = true;

    // Friction the tube uses against the corpse (higher = clings/slides slower). Defaults match the
    // values the tube shipped with (Math.Clamp(RagdollFriction, 0.45, 0.9) at RagdollFriction=1.0).
    public const float KoStripGarmentTubeBodyFrictionDefault = 0.9f;
    public float KoStripGarmentTubeBodyFriction { get; set; } = KoStripGarmentTubeBodyFrictionDefault;

    // Friction the tube uses against the ground once it slides off the body. Default matches the
    // value the tube shipped with (MathF.Max(RagdollFriction, 3.5) at RagdollFriction=1.0).
    public const float KoStripGarmentTubeGroundFrictionDefault = 3.5f;
    public float KoStripGarmentTubeGroundFriction { get; set; } = KoStripGarmentTubeGroundFrictionDefault;

    // How long the tube stays visually bound to the body pose before handoff to physics. Default matches
    // the value the tube shipped with (ClothHoldMinFrames = 18 frames @ 60fps).
    public const float KoStripGarmentTubeHoldSecondsDefault = 0.3f;
    public float KoStripGarmentTubeHoldSeconds { get; set; } = KoStripGarmentTubeHoldSecondsDefault;

    // Manual fallback duration (seconds) for the "still attached" visual hold with Advanced clothing
    // settle on. Auto mode is the default path; this only applies when Auto cloth hold is disabled.
    public const float KoStripClothHoldSecondsDefault = 0.3f;
    public float KoStripClothHoldSeconds { get; set; } = KoStripClothHoldSecondsDefault;

    // Auto cloth hold: release the garment on an event (body settled, or slid down to the floor)
    // rather than the fixed KoStripClothHoldSeconds timer.
    public bool KoStripClothHoldAuto { get; set; } = true;

    // Auto-hold feel: 0 Quick, 1 Natural, 2 Clingy, 3 Slide-to-floor, 4 Visual-only. Default Slide-to-floor.
    public int KoStripClothHoldPreset { get; set; } = 1;

    // Visual-only preset tuning: how far (metres) and how fast (m/s) the garment slides down the body
    // before it freezes and stays visual. Only used by the Visual-only preset — Slide-to-floor keeps its
    // own fixed 0.8m / 0.20 m/s behaviour. Raise the distance if the garment stops short of the ground
    // in a standing KO; raise the speed if the slide looks too slow.
    public const float KoStripClothVisualOnlySlideDistanceDefault = 0.8f;
    public float KoStripClothVisualOnlySlideDistance { get; set; } = KoStripClothVisualOnlySlideDistanceDefault;
    public const float KoStripClothVisualOnlySlideSpeedDefault = 0.07f;
    public float KoStripClothVisualOnlySlideSpeed { get; set; } = KoStripClothVisualOnlySlideSpeedDefault;

    // Per-slot "collapse on drop" toggles for the physics-drop pieces. When a slot is enabled the
    // dropped piece deflates/flattens like cloth; when disabled it keeps its full rigid shape (better
    // for armor / rigid gear). Indexed via GearKeepModelSlot (0 Head,1 Body,2 Hands,3 Legs,4 Feet,
    // 5 Ears,6 Neck,7 Wrists,8 RFinger,9 LFinger).
    public bool KoStripCollapseHead { get; set; } = true;
    public bool KoStripCollapseBody { get; set; } = true;
    public bool KoStripCollapseHands { get; set; } = false;
    public bool KoStripCollapseLegs { get; set; } = true;
    public bool KoStripCollapseFeet { get; set; } = false;
    public bool KoStripCollapseEars { get; set; } = false;
    public bool KoStripCollapseNeck { get; set; } = false;
    public bool KoStripCollapseWrists { get; set; } = false;
    public bool KoStripCollapseRFinger { get; set; } = false;
    public bool KoStripCollapseLFinger { get; set; } = false;

    /// <summary>Whether the dropped piece for the given GearKeepModelSlot should collapse/deflate.
    /// Unknown slots default to collapsing (matches the historic all-gear-deflates behavior).</summary>
    public bool IsKoStripCollapseEnabled(int gearKeepModelSlot) => gearKeepModelSlot switch
    {
        0 => KoStripCollapseHead,
        1 => KoStripCollapseBody,
        2 => KoStripCollapseHands,
        3 => KoStripCollapseLegs,
        4 => KoStripCollapseFeet,
        5 => KoStripCollapseEars,
        6 => KoStripCollapseNeck,
        7 => KoStripCollapseWrists,
        8 => KoStripCollapseRFinger,
        9 => KoStripCollapseLFinger,
        _ => true,
    };

    public void ResetKoStripCollapseDefaults()
    {
        KoStripCollapseHead = true;
        KoStripCollapseBody = true;
        KoStripCollapseHands = false;
        KoStripCollapseLegs = true;
        KoStripCollapseFeet = false;
        KoStripCollapseEars = false;
        KoStripCollapseNeck = false;
        KoStripCollapseWrists = false;
        KoStripCollapseRFinger = false;
        KoStripCollapseLFinger = false;
    }

    // Armor detachment ("Strip KO") — visually unequips the selected gear slots when the
    // player is knocked out. Purely visual by default (via Glamourer, or a direct draw-object
    // write as fallback); KoStripPhysicsDrop/KoStripPhysicsDropClothing below opt individual
    // slots into physically falling and tumbling instead.
    public bool KoStripEnabled { get; set; } = false;
    // Progressive on-hit strip is not wired up in this standalone build (no live-combat hit
    // pipeline to trigger it from) — kept only so KoStripController compiles unchanged.
    public bool KoStripOnHitEnabled { get; set; } = false;
    public bool KoStripSyncWithRagdoll { get; set; } = false;
    public bool KoStripHead { get; set; } = true;
    public bool KoStripBody { get; set; } = true;
    public bool KoStripHands { get; set; } = false;
    public bool KoStripLegs { get; set; } = true;
    public bool KoStripFeet { get; set; } = false;
    public bool KoStripEars { get; set; } = false;
    public bool KoStripNeck { get; set; } = false;
    public bool KoStripWrists { get; set; } = false;
    public bool KoStripRFinger { get; set; } = false;
    public bool KoStripLFinger { get; set; } = false;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;
        MigrateSkirtParentChains();
        RenameLegacyBoneProfiles();
        SeedBuiltInBoneProfiles();
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }

    private void MigrateSkirtParentChains()
    {
        var changed = MigrateBoneList(RagdollBoneConfigs);
        foreach (var profile in RagdollBoneProfiles)
            changed |= MigrateBoneList(profile.Bones);
        if (changed) Save();
    }

    private static bool MigrateBoneList(List<RagdollBoneConfig> bones)
    {
        var changed = false;
        foreach (var bone in bones)
        {
            var remapped = RemapLegacySkirtParent(bone.Name, bone.SkeletonParent);
            if (remapped == bone.SkeletonParent) continue;
            bone.SkeletonParent = remapped;
            changed = true;
        }
        return changed;
    }

    private static string? RemapLegacySkirtParent(string boneName, string? oldParent)
    {
        if (oldParent == null || !boneName.StartsWith("j_sk_")) return oldParent;
        var parts = boneName.Split('_');
        if (parts.Length != 5) return oldParent;
        var pos = parts[2];
        var tier = parts[3];
        var side = parts[4];
        if (tier == "b" && oldParent == "j_sebo_b") return $"j_sk_{pos}_a_{side}";
        if (tier == "c" && oldParent == "j_sebo_c") return $"j_sk_{pos}_b_{side}";
        return oldParent;
    }

    private static readonly Dictionary<string, string> LegacyBoneProfileNameMap = new()
    {
        { "Thickness", "Default" },
        { "Flatter", "Thinner Volumes I" },
        { "Complete Flat", "Thinner Volumes II" },
    };

    private void RenameLegacyBoneProfiles()
    {
        var changed = false;
        foreach (var profile in RagdollBoneProfiles)
        {
            if (!LegacyBoneProfileNameMap.TryGetValue(profile.Name, out var newName)) continue;
            profile.Name = newName;
            changed = true;
        }
        if (changed) Save();
    }

    private void SeedBuiltInBoneProfiles()
    {
        List<RagdollBoneProfile>? builtIns;
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("RagdollSystem.Resources.BuiltInBoneProfiles.json");
            if (stream == null) return;
            using var reader = new System.IO.StreamReader(stream);
            var json = reader.ReadToEnd();
            builtIns = System.Text.Json.JsonSerializer.Deserialize<List<RagdollBoneProfile>>(json);
        }
        catch
        {
            return;
        }

        if (builtIns == null) return;

        var changed = false;
        foreach (var seed in builtIns)
        {
            if (RagdollBoneProfiles.Any(p => p.Name.Equals(seed.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            RagdollBoneProfiles.Add(seed);
            changed = true;
        }
        if (changed) Save();
    }
}

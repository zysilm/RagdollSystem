using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using BepuSimulation = BepuPhysics.Simulation;

namespace RagdollSystem.Animation;

public unsafe class RagdollController : IDisposable
{
    private readonly BoneTransformService boneService;
    private readonly Configuration config;
    private readonly IPluginLog log;

    // Physics simulation
    private BufferPool? bufferPool;
    private BepuSimulation? simulation;

    // State
    private bool isActive;
    private nint targetCharacterAddress;
    private float elapsed;
    private float activationDelay;
    private bool physicsStarted;

    // Skeleton world transform
    private Vector3 skelWorldPos;
    private Quaternion skelWorldRot;
    private Quaternion skelWorldRotInv;

    // Bone-to-body mapping
    private readonly List<RagdollBone> ragdollBones = new();

    // Ground height
    private float groundY;
    private float realGroundY;

    // Diagnostic frame counter
    private int frameCount;

    // Animation freeze state
    private float savedOverallSpeed = 1.0f;
    private readonly HashSet<int> ragdollBoneIndices = new();

    // Ancestor bone index — n_hara must follow j_kosi to prevent mesh tearing
    private int nHaraIndex = -1;
    // Head bone index (j_kao)
    private int kaoBodyBoneIndex = -1;
    // Hair physics simulator
    private HairPhysicsSimulator? hairPhysics;

    public bool IsActive => isActive;
    public nint TargetCharacterAddress => targetCharacterAddress;

    // Joint type
    public enum JointType { Ball, Hinge }

    // Ragdoll bone definition
    public struct RagdollBoneDef
    {
        public string Name;
        public string? ParentName;
        public float CapsuleRadius;
        public float CapsuleHalfLength;
        public float Mass;
        public float SwingLimit;
        public JointType Joint;
        public float TwistMinAngle;
        public float TwistMaxAngle;
        public bool SoftBody;
        public float SoftSpringFreq;
        public float SoftSpringDamp;
        public float SoftServoFreq;
        public float SoftServoDamp;
    }

    // Runtime bone with physics body
    private struct RagdollBone
    {
        public int BoneIndex;
        public int ParentBoneIndex;
        public BodyHandle BodyHandle;
        public string Name;
        public Quaternion CapsuleToBoneOffset;
        public float SegmentHalfLength;
    }

    /// <summary>Debug draw data for a single ragdoll capsule body.</summary>
    public struct DebugCapsule
    {
        public Vector3 Position;
        public Quaternion Orientation;
        public float Radius;
        public float HalfLength;
        public string Name;
        public JointType Joint;
        public float SwingLimit;
    }

    /// <summary>Debug data for visualizing joint rotation limits.</summary>
    public struct DebugJointVis
    {
        public bool Valid;
        public Vector3 JointPosition;
        public Vector3 ParentAxis;
        public Vector3 ChildAxis;
        public Vector3 ParentRight;
        public Vector3 ParentForward;
        public JointType Joint;
        public float SwingLimit;
        public float TwistMinAngle;
        public float TwistMaxAngle;
    }

    // Complete bone catalog with default parameters
    public static readonly RagdollBoneConfig[] AllBoneDefaults = new[]
    {
        // === SPINE CHAIN ===
        new RagdollBoneConfig { Name = "j_kosi",    SkeletonParent = null,       Enabled = true,  CapsuleRadius = 0.105f, CapsuleHalfLength = 0.06f, Mass = 8.0f,  SwingLimit = 0.2f,                JointType = 0, TwistMinAngle = 0f,     TwistMaxAngle = 0f,    Description = "Pelvis" },
        new RagdollBoneConfig { Name = "j_sebo_a",  SkeletonParent = "j_kosi",   Enabled = true,  CapsuleRadius = 0.10f,  CapsuleHalfLength = 0.05f, Mass = 10.0f, SwingLimit = 0.2f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Lower Spine" },
        new RagdollBoneConfig { Name = "j_sebo_b",  SkeletonParent = "j_sebo_a", Enabled = true,  CapsuleRadius = 0.09f,  CapsuleHalfLength = 0.05f, Mass = 5.0f,  SwingLimit = 0.15f,               JointType = 0, TwistMinAngle = -0.15f, TwistMaxAngle = 0.15f, Description = "Mid Spine" },
        new RagdollBoneConfig { Name = "j_sebo_c",  SkeletonParent = "j_sebo_b", Enabled = true,  CapsuleRadius = 0.09f,  CapsuleHalfLength = 0.05f, Mass = 6.0f,  SwingLimit = 0.15f,               JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Chest" },
        new RagdollBoneConfig { Name = "j_kubi",    SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.04f,  CapsuleHalfLength = 0.03f, Mass = 2.0f,  SwingLimit = 0.25f,               JointType = 0, TwistMinAngle = -0.3f,  TwistMaxAngle = 0.3f,  Description = "Neck" },
        new RagdollBoneConfig { Name = "j_kao",     SkeletonParent = "j_kubi",   Enabled = true,  CapsuleRadius = 0.08f,  CapsuleHalfLength = 0.04f, Mass = 3.5f,  SwingLimit = 0.25f,               JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Head" },

        // === CLOTH/SKIRT BONES ===
        new RagdollBoneConfig { Name = "j_sk_b_a_l", SkeletonParent = "j_sebo_a", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back A L" },
        new RagdollBoneConfig { Name = "j_sk_b_a_r", SkeletonParent = "j_sebo_a", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back A R" },
        new RagdollBoneConfig { Name = "j_sk_f_a_l", SkeletonParent = "j_sebo_a", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front A L" },
        new RagdollBoneConfig { Name = "j_sk_f_a_r", SkeletonParent = "j_sebo_a", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front A R" },
        new RagdollBoneConfig { Name = "j_sk_s_a_l", SkeletonParent = "j_sebo_a", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side A L" },
        new RagdollBoneConfig { Name = "j_sk_s_a_r", SkeletonParent = "j_sebo_a", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side A R" },
        new RagdollBoneConfig { Name = "j_sk_b_b_l", SkeletonParent = "j_sebo_b", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back B L" },
        new RagdollBoneConfig { Name = "j_sk_b_b_r", SkeletonParent = "j_sebo_b", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back B R" },
        new RagdollBoneConfig { Name = "j_sk_f_b_l", SkeletonParent = "j_sebo_b", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front B L" },
        new RagdollBoneConfig { Name = "j_sk_f_b_r", SkeletonParent = "j_sebo_b", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front B R" },
        new RagdollBoneConfig { Name = "j_sk_s_b_l", SkeletonParent = "j_sebo_b", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side B L" },
        new RagdollBoneConfig { Name = "j_sk_s_b_r", SkeletonParent = "j_sebo_b", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side B R" },
        new RagdollBoneConfig { Name = "j_sk_b_c_l", SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back C L" },
        new RagdollBoneConfig { Name = "j_sk_b_c_r", SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Back C R" },
        new RagdollBoneConfig { Name = "j_sk_f_c_l", SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front C L" },
        new RagdollBoneConfig { Name = "j_sk_f_c_r", SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Front C R" },
        new RagdollBoneConfig { Name = "j_sk_s_c_l", SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side C L" },
        new RagdollBoneConfig { Name = "j_sk_s_c_r", SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.03f, Mass = 0.1f,  SwingLimit = 0.3f,  JointType = 0, TwistMinAngle = -0.2f,  TwistMaxAngle = 0.2f,  Description = "Cloth Side C R" },

        // === WEAPON HOLSTER/SHEATHE === (disabled by default)
        new RagdollBoneConfig { Name = "j_buki_kosi_l",  SkeletonParent = "j_kosi",   Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Hip Sheathe" },
        new RagdollBoneConfig { Name = "j_buki_kosi_r",  SkeletonParent = "j_kosi",   Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Hip Sheathe" },
        new RagdollBoneConfig { Name = "j_buki2_kosi_l", SkeletonParent = "j_kosi",   Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Hip Holster" },
        new RagdollBoneConfig { Name = "j_buki2_kosi_r", SkeletonParent = "j_kosi",   Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Hip Holster" },
        new RagdollBoneConfig { Name = "j_buki_sebo_l",  SkeletonParent = "j_sebo_c", Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Back Scabbard" },
        new RagdollBoneConfig { Name = "j_buki_sebo_r",  SkeletonParent = "j_sebo_c", Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 1.5f,  SwingLimit = 0.1f,  JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Back Scabbard" },

        // === BREAST === (disabled by default)
        new RagdollBoneConfig { Name = "j_mune_l",  SkeletonParent = "j_sebo_b", Enabled = false, CapsuleRadius = 0.06f,  CapsuleHalfLength = 0.02f, Mass = 0.1f,  SwingLimit = 0.25f,               JointType = 1, TwistMinAngle = 0f,     TwistMaxAngle = 0f,    Description = "Left Breast",  SoftBody = true, SoftSpringFreq = 1f, SoftSpringDamp = 0.05f, SoftServoFreq = 4f, SoftServoDamp = 0.35f },
        new RagdollBoneConfig { Name = "j_mune_r",  SkeletonParent = "j_sebo_b", Enabled = false, CapsuleRadius = 0.06f,  CapsuleHalfLength = 0.02f, Mass = 0.1f,  SwingLimit = 0.25f,               JointType = 1, TwistMinAngle = 0f,     TwistMaxAngle = 0f,    Description = "Right Breast",  SoftBody = true, SoftSpringFreq = 1f, SoftSpringDamp = 0.05f, SoftServoFreq = 4f, SoftServoDamp = 0.35f },

        // === CLAVICLE ===
        new RagdollBoneConfig { Name = "j_sako_l",  SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.04f, Mass = 0.5f,  SwingLimit = 0.15f,               JointType = 0, TwistMinAngle = -0.15f, TwistMaxAngle = 0.15f, Description = "Left Clavicle" },
        new RagdollBoneConfig { Name = "j_sako_r",  SkeletonParent = "j_sebo_c", Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.04f, Mass = 0.5f,  SwingLimit = 0.15f,               JointType = 0, TwistMinAngle = -0.15f, TwistMaxAngle = 0.15f, Description = "Right Clavicle" },

        // === ARM CHAIN ===
        new RagdollBoneConfig { Name = "j_ude_a_l", SkeletonParent = "j_sako_l", Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.08f, Mass = 2.0f,  SwingLimit = 1.8f,                JointType = 0, TwistMinAngle = -0.8f,  TwistMaxAngle = 0.8f,  Description = "Left Upper Arm" },
        new RagdollBoneConfig { Name = "j_ude_a_r", SkeletonParent = "j_sako_r", Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.08f, Mass = 2.0f,  SwingLimit = 1.8f,                JointType = 0, TwistMinAngle = -0.8f,  TwistMaxAngle = 0.8f,  Description = "Right Upper Arm" },
        new RagdollBoneConfig { Name = "j_ude_b_l", SkeletonParent = "j_ude_a_l",Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.07f, Mass = 1.2f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Forearm" },
        new RagdollBoneConfig { Name = "j_ude_b_r", SkeletonParent = "j_ude_a_r",Enabled = true,  CapsuleRadius = 0.025f, CapsuleHalfLength = 0.07f, Mass = 1.2f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Forearm" },
        new RagdollBoneConfig { Name = "j_te_l",    SkeletonParent = "j_ude_b_l",Enabled = true,  CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 0.5f,  SwingLimit = 0.4f,                JointType = 0, TwistMinAngle = -0.15f, TwistMaxAngle = 0.15f, Description = "Left Hand" },
        new RagdollBoneConfig { Name = "j_te_r",    SkeletonParent = "j_ude_b_r",Enabled = true,  CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.03f, Mass = 0.5f,  SwingLimit = 0.4f,                JointType = 0, TwistMinAngle = -0.15f, TwistMaxAngle = 0.15f, Description = "Right Hand" },

        // === LEG CHAIN ===
        new RagdollBoneConfig { Name = "j_asi_a_l", SkeletonParent = "j_kosi",   Enabled = true,  CapsuleRadius = 0.045f, CapsuleHalfLength = 0.12f, Mass = 10.0f, SwingLimit = 1.3f,                JointType = 0, TwistMinAngle = -0.5f,  TwistMaxAngle = 0.5f,  Description = "Left Thigh" },
        new RagdollBoneConfig { Name = "j_asi_a_r", SkeletonParent = "j_kosi",   Enabled = true,  CapsuleRadius = 0.045f, CapsuleHalfLength = 0.12f, Mass = 10.0f, SwingLimit = 1.3f,                JointType = 0, TwistMinAngle = -0.5f,  TwistMaxAngle = 0.5f,  Description = "Right Thigh" },
        new RagdollBoneConfig { Name = "j_asi_b_l", SkeletonParent = "j_asi_a_l",Enabled = true,  CapsuleRadius = 0.035f, CapsuleHalfLength = 0.11f, Mass = 3.0f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Shin (Knee)" },
        new RagdollBoneConfig { Name = "j_asi_b_r", SkeletonParent = "j_asi_a_r",Enabled = true,  CapsuleRadius = 0.035f, CapsuleHalfLength = 0.11f, Mass = 3.0f,  SwingLimit = MathF.PI / 2,        JointType = 1, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Shin (Knee)" },
        new RagdollBoneConfig { Name = "j_asi_c_l", SkeletonParent = "j_asi_b_l",Enabled = true,  CapsuleRadius = 0.03f,  CapsuleHalfLength = 0.04f, Mass = 1.0f,  SwingLimit = 0.1f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Calf" },
        new RagdollBoneConfig { Name = "j_asi_c_r", SkeletonParent = "j_asi_b_r",Enabled = true,  CapsuleRadius = 0.03f,  CapsuleHalfLength = 0.04f, Mass = 1.0f,  SwingLimit = 0.1f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Calf" },
        new RagdollBoneConfig { Name = "j_asi_d_l", SkeletonParent = "j_asi_c_l",Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.0f,  Mass = 1.0f,  SwingLimit = 0.29f,               JointType = 0, TwistMinAngle = -0.64f, TwistMaxAngle = 0.65f, Description = "Left Foot (Ankle)" },
        new RagdollBoneConfig { Name = "j_asi_d_r", SkeletonParent = "j_asi_c_r",Enabled = true,  CapsuleRadius = 0.01f,  CapsuleHalfLength = 0.0f,  Mass = 1.0f,  SwingLimit = 0.3f,                JointType = 0, TwistMinAngle = -0.65f, TwistMaxAngle = 0.65f, Description = "Right Foot (Ankle)" },
        new RagdollBoneConfig { Name = "j_asi_e_l", SkeletonParent = "j_asi_d_l",Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.02f, Mass = 0.2f,  SwingLimit = 0.3f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Left Toes" },
        new RagdollBoneConfig { Name = "j_asi_e_r", SkeletonParent = "j_asi_d_r",Enabled = false, CapsuleRadius = 0.02f,  CapsuleHalfLength = 0.02f, Mass = 0.2f,  SwingLimit = 0.3f,                JointType = 0, TwistMinAngle = -0.1f,  TwistMaxAngle = 0.1f,  Description = "Right Toes" },
    };

    public static readonly RagdollBoneDef[] DefaultBoneDefs = BuildDefaultBoneDefs();

    private static RagdollBoneDef[] BuildDefaultBoneDefs()
    {
        return BuildBoneDefsFromConfigs(AllBoneDefaults);
    }

    public static RagdollBoneDef[] BuildBoneDefsFromConfigs(RagdollBoneConfig[] configs)
    {
        var defaultParents = new Dictionary<string, string?>();
        foreach (var d in AllBoneDefaults)
            defaultParents[d.Name] = d.SkeletonParent;

        var enabledNames = new HashSet<string>();
        foreach (var c in configs)
            if (c.Enabled) enabledNames.Add(c.Name);

        var configByName = new Dictionary<string, RagdollBoneConfig>();
        foreach (var c in configs)
            configByName[c.Name] = c;

        var result = new List<RagdollBoneDef>();
        foreach (var c in configs)
        {
            if (!c.Enabled) continue;

            string? physicsParent = null;
            var skelParent = c.SkeletonParent;
            if (skelParent == null) defaultParents.TryGetValue(c.Name, out skelParent);
            var current = skelParent;
            while (current != null)
            {
                if (enabledNames.Contains(current))
                {
                    physicsParent = current;
                    break;
                }
                string? nextParent = null;
                if (configByName.TryGetValue(current, out var parentConfig))
                    nextParent = parentConfig.SkeletonParent;
                if (nextParent == null)
                    defaultParents.TryGetValue(current, out nextParent);
                current = nextParent;
            }

            result.Add(new RagdollBoneDef
            {
                Name = c.Name,
                ParentName = physicsParent,
                CapsuleRadius = c.CapsuleRadius,
                CapsuleHalfLength = c.CapsuleHalfLength,
                Mass = c.Mass,
                SwingLimit = c.SwingLimit,
                Joint = (JointType)c.JointType,
                TwistMinAngle = c.TwistMinAngle,
                TwistMaxAngle = c.TwistMaxAngle,
                SoftBody = c.SoftBody,
                SoftSpringFreq = c.SoftSpringFreq,
                SoftSpringDamp = c.SoftSpringDamp,
                SoftServoFreq = c.SoftServoFreq,
                SoftServoDamp = c.SoftServoDamp,
            });
        }
        return result.ToArray();
    }

    private RagdollBoneDef[] GetBoneDefs()
    {
        if (config.RagdollBoneConfigs.Count == 0)
            return DefaultBoneDefs;
        return BuildBoneDefsFromConfigs(config.RagdollBoneConfigs.ToArray());
    }

    public RagdollController(BoneTransformService boneService, Configuration config, IPluginLog log)
    {
        this.boneService = boneService;
        this.config = config;
        this.log = log;

        boneService.OnRenderFrame += OnRenderFrame;
    }

    public void Activate(nint characterAddress, float? delayOverride = null)
    {
        if (isActive) Deactivate();

        targetCharacterAddress = characterAddress;
        activationDelay = delayOverride ?? config.RagdollActivationDelay;
        elapsed = 0f;
        physicsStarted = false;
        isActive = true;

        log.Info($"RagdollController: Activated for 0x{characterAddress:X} (delay={activationDelay:F1}s)");
    }

    public void Deactivate()
    {
        if (!isActive) return;
        isActive = false;

        // Restore animation speed
        if (targetCharacterAddress != nint.Zero && physicsStarted)
        {
            try
            {
                var character = (Character*)targetCharacterAddress;
                character->Timeline.OverallSpeed = savedOverallSpeed;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "RagdollController: Failed to restore animation speed");
            }
        }

        targetCharacterAddress = nint.Zero;
        physicsStarted = false;
        ragdollBoneIndices.Clear();
        nHaraIndex = -1;
        kaoBodyBoneIndex = -1;
        hairPhysics?.Reset();
        hairPhysics = null;

        DestroySimulation();
        log.Info("RagdollController: Deactivated");
    }

    private void OnRenderFrame()
    {
        if (!isActive) return;

        try
        {
            elapsed += 1.0f / 60.0f;

            if (!physicsStarted)
            {
                if (elapsed < activationDelay) return;
                if (!InitializePhysics()) { Deactivate(); return; }
                physicsStarted = true;
                frameCount = 0;
            }

            StepAndApply();
        }
        catch (Exception ex)
        {
            log.Error(ex, "RagdollController: Error in render frame");
            Deactivate();
        }
    }

    // --- Coordinate conversion ---
    private Vector3 ModelToWorld(Vector3 modelPos)
        => skelWorldPos + Vector3.Transform(modelPos, skelWorldRot);

    private Vector3 WorldToModel(Vector3 worldPos)
        => Vector3.Transform(worldPos - skelWorldPos, skelWorldRotInv);

    private Quaternion ModelRotToWorld(Quaternion modelRot)
        => Quaternion.Normalize(skelWorldRot * modelRot);

    private Quaternion WorldRotToModel(Quaternion worldRot)
        => Quaternion.Normalize(skelWorldRotInv * worldRot);

    private static Quaternion RotationFromYToDirection(Vector3 dir)
    {
        var dirN = Vector3.Normalize(dir);
        var dot = Vector3.Dot(Vector3.UnitY, dirN);
        if (dot > 0.9999f) return Quaternion.Identity;
        if (dot < -0.9999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, dirN));
        var angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));
        return Quaternion.CreateFromAxisAngle(axis, angle);
    }

    private static Quaternion CreateTwistBasis(Vector3 twistAxis, Vector3 referenceDir)
    {
        var z = Vector3.Normalize(twistAxis);
        var y = Vector3.Normalize(Vector3.Cross(z, referenceDir));
        var x = Vector3.Cross(y, z);
        var m = new Matrix4x4(
            x.X, y.X, z.X, 0,
            x.Y, y.Y, z.Y, 0,
            x.Z, y.Z, z.Z, 0,
            0, 0, 0, 1);
        return Quaternion.CreateFromRotationMatrix(m);
    }

    private Vector3 ComputeHingeAxis(Vector3 segmentDir)
    {
        var segN = Vector3.Normalize(segmentDir);
        var hingeAxis = Vector3.Cross(segN, Vector3.UnitY);
        if (hingeAxis.LengthSquared() < 0.001f)
        {
            var forward = Vector3.Transform(Vector3.UnitZ, skelWorldRot);
            hingeAxis = Vector3.Cross(segN, forward);
        }
        return Vector3.Normalize(hingeAxis);
    }

    private bool InitializePhysics()
    {
        var skelNullable = boneService.TryGetSkeleton(targetCharacterAddress);
        if (skelNullable == null) return false;
        var skel = skelNullable.Value;
        var BoneDefs = GetBoneDefs();

        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return false;

        skelWorldPos = new Vector3(
            skeleton->Transform.Position.X,
            skeleton->Transform.Position.Y,
            skeleton->Transform.Position.Z);
        skelWorldRot = new Quaternion(
            skeleton->Transform.Rotation.X,
            skeleton->Transform.Rotation.Y,
            skeleton->Transform.Rotation.Z,
            skeleton->Transform.Rotation.W);
        skelWorldRotInv = Quaternion.Inverse(skelWorldRot);

        // Resolve bone indices
        var nameToIndex = new Dictionary<string, int>();
        ragdollBones.Clear();

        foreach (var def in BoneDefs)
        {
            var idx = boneService.ResolveBoneIndex(skel, def.Name);
            if (idx < 0)
            {
                log.Warning($"RagdollController: Bone '{def.Name}' not found, skipping");
                continue;
            }
            nameToIndex[def.Name] = idx;
        }

        // Raycast for ground height
        groundY = skelWorldPos.Y;
        if (BGCollisionModule.RaycastMaterialFilter(
                new Vector3(skelWorldPos.X, skelWorldPos.Y + 2.0f, skelWorldPos.Z),
                new Vector3(0, -1, 0),
                out var hitInfo,
                50f))
        {
            groundY = hitInfo.Point.Y;
        }

        // Create BEPU simulation
        var connectedPairs = config.RagdollSelfCollision ? new HashSet<(int, int)>() : null;
        bufferPool = new BufferPool();
        simulation = BepuSimulation.Create(
            bufferPool,
            new RagdollNarrowPhaseCallbacks { ConnectedPairs = connectedPairs!, Friction = config.RagdollFriction },
            new RagdollPoseIntegratorCallbacks(
                new Vector3(0, -config.RagdollGravity, 0),
                config.RagdollDamping),
            new SolveDescription(config.RagdollSolverIterations, 1));

        // Safety net box
        var groundThickness = 10f;
        var safetyBoxIndex = simulation.Shapes.Add(new Box(1000, groundThickness, 1000));
        simulation.Statics.Add(new StaticDescription(
            new Vector3(0, groundY - groundThickness / 2f - 2f, 0),
            Quaternion.Identity,
            safetyBoxIndex));

        // Build terrain mesh from raycasts
        {
            var terrainRadius = 4.0f;
            var terrainStep = 0.5f;
            var gridSize = (int)(terrainRadius * 2 / terrainStep) + 1;
            var heights = new float[gridSize, gridSize];
            var ox = skelWorldPos.X - terrainRadius;
            var oz = skelWorldPos.Z - terrainRadius;

            for (int gz = 0; gz < gridSize; gz++)
            for (int gx = 0; gx < gridSize; gx++)
            {
                var wx = ox + gx * terrainStep;
                var wz = oz + gz * terrainStep;
                if (BGCollisionModule.RaycastMaterialFilter(
                        new Vector3(wx, skelWorldPos.Y + 2.0f, wz),
                        new Vector3(0, -1, 0), out var gridHit, 50f))
                    heights[gx, gz] = gridHit.Point.Y;
                else
                    heights[gx, gz] = groundY;
            }

            var triCount = (gridSize - 1) * (gridSize - 1) * 2;
            bufferPool.Take<Triangle>(triCount, out var triangles);
            int ti = 0;
            for (int gz = 0; gz < gridSize - 1; gz++)
            for (int gx = 0; gx < gridSize - 1; gx++)
            {
                var x0 = ox + gx * terrainStep;
                var x1 = x0 + terrainStep;
                var z0 = oz + gz * terrainStep;
                var z1 = z0 + terrainStep;
                var v00 = new Vector3(x0, heights[gx, gz], z0);
                var v10 = new Vector3(x1, heights[gx + 1, gz], z0);
                var v01 = new Vector3(x0, heights[gx, gz + 1], z1);
                var v11 = new Vector3(x1, heights[gx + 1, gz + 1], z1);

                triangles[ti++] = new Triangle(v00, v10, v01);
                triangles[ti++] = new Triangle(v10, v11, v01);
            }

            var terrainMesh = new BepuPhysics.Collidables.Mesh(triangles, Vector3.One, bufferPool);
            var terrainIndex = simulation.Shapes.Add(terrainMesh);
            simulation.Statics.Add(new StaticDescription(
                Vector3.Zero, Quaternion.Identity, terrainIndex));
        }

        // --- Pass 1: Collect bone world positions and rotations ---
        var pose = skel.Pose;
        var boneWorldPositions = new Dictionary<string, Vector3>();
        var boneWorldRotations = new Dictionary<string, Quaternion>();

        foreach (var def in BoneDefs)
        {
            if (!nameToIndex.TryGetValue(def.Name, out var idx)) continue;
            ref var mt = ref pose->ModelPose.Data[idx];
            var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            var modelRot = new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W);
            boneWorldPositions[def.Name] = ModelToWorld(modelPos);
            boneWorldRotations[def.Name] = ModelRotToWorld(modelRot);
        }

        var boneToFirstChild = new Dictionary<string, string>();
        foreach (var def in BoneDefs)
        {
            if (def.ParentName != null && !boneToFirstChild.ContainsKey(def.ParentName))
                boneToFirstChild[def.ParentName] = def.Name;
        }

        var skelFirstChild = new Dictionary<int, int>();
        for (int i = 0; i < skel.BoneCount && i < skel.ParentCount; i++)
        {
            var parentIdx = skel.HavokSkeleton->ParentIndices[i];
            if (parentIdx >= 0 && !skelFirstChild.ContainsKey(parentIdx))
                skelFirstChild[parentIdx] = i;
        }

        realGroundY = groundY;

        // --- Pass 2: Create physics bodies ---
        foreach (var def in BoneDefs)
        {
            if (!nameToIndex.TryGetValue(def.Name, out var boneIdx)) continue;
            if (!boneWorldPositions.TryGetValue(def.Name, out var boneWorldPos)) continue;
            var boneWorldRot = boneWorldRotations[def.Name];

            Vector3 capsuleCenter;
            float segmentHalfLength;
            float effectiveHalfLength = def.CapsuleHalfLength;
            Quaternion capsuleWorldRot;

            if (boneToFirstChild.TryGetValue(def.Name, out var childName) &&
                boneWorldPositions.TryGetValue(childName, out var childWorldPos))
            {
                var segment = childWorldPos - boneWorldPos;
                var segLen = segment.Length();

                if (segLen > 0.01f)
                {
                    var maxHalf = MathF.Max(0.02f, segLen * 0.45f);
                    if (effectiveHalfLength > maxHalf)
                        effectiveHalfLength = maxHalf;

                    var segDir = segment / segLen;
                    capsuleCenter = boneWorldPos + effectiveHalfLength * segDir;
                    segmentHalfLength = effectiveHalfLength;
                    capsuleWorldRot = RotationFromYToDirection(segment);
                }
                else
                {
                    capsuleCenter = boneWorldPos;
                    segmentHalfLength = 0f;
                    capsuleWorldRot = boneWorldRot;
                }
            }
            else if (skelFirstChild.TryGetValue(boneIdx, out var skelChildIdx) &&
                     skelChildIdx < skel.BoneCount)
            {
                ref var childMt = ref pose->ModelPose.Data[skelChildIdx];
                var childModelPos = new Vector3(childMt.Translation.X, childMt.Translation.Y, childMt.Translation.Z);
                var skelChildWorldPos = ModelToWorld(childModelPos);
                var toSkelChild = skelChildWorldPos - boneWorldPos;
                var toSkelChildLen = toSkelChild.Length();

                if (toSkelChildLen > 0.01f)
                {
                    var maxHalf = MathF.Max(0.02f, toSkelChildLen * 0.45f);
                    if (effectiveHalfLength > maxHalf)
                        effectiveHalfLength = maxHalf;

                    var dir = toSkelChild / toSkelChildLen;
                    capsuleCenter = boneWorldPos + effectiveHalfLength * dir;
                    segmentHalfLength = effectiveHalfLength;
                    capsuleWorldRot = RotationFromYToDirection(toSkelChild);
                }
                else
                {
                    capsuleCenter = boneWorldPos;
                    segmentHalfLength = 0f;
                    capsuleWorldRot = boneWorldRot;
                }
            }
            else
            {
                capsuleCenter = boneWorldPos;
                segmentHalfLength = 0f;
                capsuleWorldRot = boneWorldRot;
            }

            var capsuleToBoneOffset = Quaternion.Normalize(
                Quaternion.Inverse(capsuleWorldRot) * boneWorldRot);

            // Clamp capsule center above ground
            var capsuleYAxis = Vector3.Transform(Vector3.UnitY, capsuleWorldRot);
            var capsuleBottomExtent = MathF.Abs(capsuleYAxis.Y) * effectiveHalfLength + def.CapsuleRadius;
            var minCenterY = groundY + capsuleBottomExtent + 0.005f;
            if (capsuleCenter.Y < minCenterY)
                capsuleCenter.Y = minCenterY;

            var capsuleLength = effectiveHalfLength * 2;
            var capsule = new Capsule(def.CapsuleRadius, capsuleLength);
            var shapeIndex = simulation.Shapes.Add(capsule);

            var bodyDesc = BodyDescription.CreateDynamic(
                new RigidPose(capsuleCenter, capsuleWorldRot),
                capsule.ComputeInertia(def.Mass),
                new CollidableDescription(shapeIndex, 0.04f),
                new BodyActivityDescription(0.01f));

            var bodyHandle = simulation.Bodies.Add(bodyDesc);

            int parentBoneIdx = -1;
            if (def.ParentName != null && nameToIndex.TryGetValue(def.ParentName, out var pIdx))
                parentBoneIdx = pIdx;

            ragdollBones.Add(new RagdollBone
            {
                BoneIndex = boneIdx,
                ParentBoneIndex = parentBoneIdx,
                BodyHandle = bodyHandle,
                Name = def.Name,
                CapsuleToBoneOffset = capsuleToBoneOffset,
                SegmentHalfLength = segmentHalfLength,
            });
        }

        // --- Pass 3: Add constraints ---
        var boneIdxToBodyHandle = new Dictionary<int, BodyHandle>();
        foreach (var rb in ragdollBones)
            boneIdxToBodyHandle[rb.BoneIndex] = rb.BodyHandle;

        var jointSpring = new SpringSettings(30, 1);
        var limitSpring = new SpringSettings(60, 1);
        var motorDamping = 0.01f;

        for (int i = 0; i < ragdollBones.Count; i++)
        {
            var rb = ragdollBones[i];
            if (rb.ParentBoneIndex < 0) continue;
            if (!boneIdxToBodyHandle.TryGetValue(rb.ParentBoneIndex, out var parentHandle)) continue;

            if (connectedPairs != null)
            {
                var lo = Math.Min(rb.BodyHandle.Value, parentHandle.Value);
                var hi = Math.Max(rb.BodyHandle.Value, parentHandle.Value);
                connectedPairs.Add((lo, hi));

                var parentRb = ragdollBones.Find(r => r.BoneIndex == rb.ParentBoneIndex);
                if (parentRb.ParentBoneIndex >= 0 && boneIdxToBodyHandle.TryGetValue(parentRb.ParentBoneIndex, out var grandparentHandle))
                {
                    lo = Math.Min(rb.BodyHandle.Value, grandparentHandle.Value);
                    hi = Math.Max(rb.BodyHandle.Value, grandparentHandle.Value);
                    connectedPairs.Add((lo, hi));
                }
            }

            RagdollBoneDef boneDef = default;
            foreach (var def in BoneDefs)
                if (def.Name == rb.Name) { boneDef = def; break; }

            var anchorWorld = boneWorldPositions[rb.Name];

            var childBodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);
            var parentBodyRef = simulation.Bodies.GetBodyReference(parentHandle);

            var childLocalAnchor = Vector3.Transform(
                anchorWorld - childBodyRef.Pose.Position,
                Quaternion.Inverse(childBodyRef.Pose.Orientation));
            var parentLocalAnchor = Vector3.Transform(
                anchorWorld - parentBodyRef.Pose.Position,
                Quaternion.Inverse(parentBodyRef.Pose.Orientation));

            var segDirWorld = Vector3.Transform(Vector3.UnitY, childBodyRef.Pose.Orientation);
            if (segDirWorld.LengthSquared() < 0.001f)
                segDirWorld = Vector3.UnitY;

            if (boneDef.Joint == JointType.Hinge)
            {
                var hingeAxisWorld = ComputeHingeAxis(segDirWorld);
                var hingeAxisLocalChild = Vector3.Normalize(Vector3.Transform(
                    hingeAxisWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)));
                var hingeAxisLocalParent = Vector3.Normalize(Vector3.Transform(
                    hingeAxisWorld, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));

                simulation.Solver.Add(rb.BodyHandle, parentHandle,
                    new Hinge
                    {
                        LocalOffsetA = childLocalAnchor,
                        LocalHingeAxisA = hingeAxisLocalChild,
                        LocalOffsetB = parentLocalAnchor,
                        LocalHingeAxisB = hingeAxisLocalParent,
                        SpringSettings = jointSpring,
                    });

                if (boneDef.SwingLimit > 0)
                {
                    var parentSegDir = Vector3.Transform(Vector3.UnitY, parentBodyRef.Pose.Orientation);
                    var forwardWorld = Vector3.Normalize(Vector3.Cross(hingeAxisWorld, parentSegDir));

                    if (Vector3.Dot(forwardWorld, segDirWorld) < 0)
                        forwardWorld = -forwardWorld;

                    var swingAxisLocalParent = Vector3.Normalize(Vector3.Transform(
                        forwardWorld, Quaternion.Inverse(parentBodyRef.Pose.Orientation)));
                    var swingAxisLocalChild = Vector3.Normalize(Vector3.Transform(
                        segDirWorld, Quaternion.Inverse(childBodyRef.Pose.Orientation)));

                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new SwingLimit
                        {
                            AxisLocalA = swingAxisLocalChild,
                            AxisLocalB = swingAxisLocalParent,
                            MaximumSwingAngle = boneDef.SwingLimit,
                            SpringSettings = limitSpring,
                        });

                    if (boneDef.TwistMinAngle != 0 || boneDef.TwistMaxAngle != 0)
                    {
                        var twistBasis = CreateTwistBasis(hingeAxisWorld, forwardWorld);
                        var twistBasisLocalChild = Quaternion.Normalize(
                            Quaternion.Inverse(childBodyRef.Pose.Orientation) * twistBasis);
                        var twistBasisLocalParent = Quaternion.Normalize(
                            Quaternion.Inverse(parentBodyRef.Pose.Orientation) * twistBasis);

                        simulation.Solver.Add(rb.BodyHandle, parentHandle,
                            new TwistLimit
                            {
                                LocalBasisA = twistBasisLocalChild,
                                LocalBasisB = twistBasisLocalParent,
                                MinimumAngle = boneDef.TwistMinAngle,
                                MaximumAngle = boneDef.TwistMaxAngle,
                                SpringSettings = limitSpring,
                            });
                    }
                }
            }
            else
            {
                simulation.Solver.Add(rb.BodyHandle, parentHandle,
                    new BallSocket
                    {
                        LocalOffsetA = childLocalAnchor,
                        LocalOffsetB = parentLocalAnchor,
                        SpringSettings = boneDef.SoftBody
                            ? new SpringSettings(boneDef.SoftSpringFreq, boneDef.SoftSpringDamp)
                            : jointSpring,
                    });

                if (boneDef.SwingLimit > 0)
                {
                    var axisChildLocal = Vector3.Transform(segDirWorld,
                        Quaternion.Inverse(childBodyRef.Pose.Orientation));
                    var axisParentLocal = Vector3.Transform(segDirWorld,
                        Quaternion.Inverse(parentBodyRef.Pose.Orientation));

                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new SwingLimit
                        {
                            AxisLocalA = axisChildLocal,
                            AxisLocalB = axisParentLocal,
                            MaximumSwingAngle = boneDef.SwingLimit,
                            SpringSettings = limitSpring,
                        });
                }

                if (boneDef.TwistMinAngle != 0 || boneDef.TwistMaxAngle != 0)
                {
                    var refDir = Vector3.Cross(segDirWorld, Vector3.UnitY);
                    if (refDir.LengthSquared() < 0.001f)
                        refDir = Vector3.Cross(segDirWorld, Vector3.UnitX);
                    refDir = Vector3.Normalize(refDir);

                    var twistBasis = CreateTwistBasis(segDirWorld, refDir);

                    simulation.Solver.Add(rb.BodyHandle, parentHandle,
                        new TwistLimit
                        {
                            LocalBasisA = Quaternion.Normalize(Quaternion.Inverse(childBodyRef.Pose.Orientation) * twistBasis),
                            LocalBasisB = Quaternion.Normalize(Quaternion.Inverse(parentBodyRef.Pose.Orientation) * twistBasis),
                            MinimumAngle = boneDef.TwistMinAngle,
                            MaximumAngle = boneDef.TwistMaxAngle,
                            SpringSettings = limitSpring,
                        });
                }
            }

            if (boneDef.SoftBody)
            {
                simulation.Solver.Add(rb.BodyHandle, parentHandle,
                    new AngularServo
                    {
                        TargetRelativeRotationLocalA = Quaternion.Identity,
                        ServoSettings = new ServoSettings(float.MaxValue, 0f, float.MaxValue),
                        SpringSettings = new SpringSettings(boneDef.SoftServoFreq, boneDef.SoftServoDamp),
                    });
            }
            else
            {
                simulation.Solver.Add(rb.BodyHandle, parentHandle,
                    new AngularMotor
                    {
                        TargetVelocityLocalA = Vector3.Zero,
                        Settings = new MotorSettings(float.MaxValue, motorDamping),
                    });
            }
        }

        // Build ragdoll bone index set
        ragdollBoneIndices.Clear();
        foreach (var rb in ragdollBones)
            ragdollBoneIndices.Add(rb.BoneIndex);

        // Freeze animation
        var character = (Character*)targetCharacterAddress;
        savedOverallSpeed = character->Timeline.OverallSpeed;
        character->Timeline.OverallSpeed = 0f;

        // Resolve special bones
        nHaraIndex = boneService.ResolveBoneIndex(skel, "n_hara");
        kaoBodyBoneIndex = boneService.ResolveBoneIndex(skel, "j_kao");

        // Initialize hair physics
        if (config.RagdollHairPhysics && kaoBodyBoneIndex >= 0)
        {
            hairPhysics = new HairPhysicsSimulator(config, log);
            hairPhysics.Initialize(skel.CharBase, kaoBodyBoneIndex);
        }

        log.Info($"RagdollController: Physics initialized — {ragdollBones.Count} bodies, ground={groundY:F3}");
        return ragdollBones.Count > 0;
    }

    private void StepAndApply()
    {
        if (simulation == null) return;

        var skelNullable = boneService.TryGetSkeleton(targetCharacterAddress);
        if (skelNullable == null) return;
        var skel = skelNullable.Value;
        var BoneDefs = GetBoneDefs();

        // Keep animation frozen
        var character = (Character*)targetCharacterAddress;
        character->Timeline.OverallSpeed = 0f;

        // Update skeleton transform
        var skeleton = skel.CharBase->Skeleton;
        if (skeleton != null)
        {
            var newSkelPos = new Vector3(
                skeleton->Transform.Position.X,
                skeleton->Transform.Position.Y,
                skeleton->Transform.Position.Z);

            var skelDist = (newSkelPos - skelWorldPos).Length();
            if (skelDist > 0.1f)
            {
                if (BGCollisionModule.RaycastMaterialFilter(
                        new Vector3(newSkelPos.X, newSkelPos.Y + 2.0f, newSkelPos.Z),
                        new Vector3(0, -1, 0),
                        out var hitInfo,
                        50f))
                {
                    realGroundY = hitInfo.Point.Y;
                    groundY = realGroundY;
                }
            }

            skelWorldPos = newSkelPos;
            skelWorldRot = new Quaternion(
                skeleton->Transform.Rotation.X,
                skeleton->Transform.Rotation.Y,
                skeleton->Transform.Rotation.Z,
                skeleton->Transform.Rotation.W);
            skelWorldRotInv = Quaternion.Inverse(skelWorldRot);
        }

        // Step physics
        simulation.Timestep(1.0f / 60.0f);

        var pose = skel.Pose;

        // Save original positions/rotations for delta tracking
        var result = new BoneModificationResult(skel.BoneCount);
        for (int i = 0; i < skel.BoneCount; i++)
        {
            ref var m = ref pose->ModelPose.Data[i];
            result.OriginalPositions[i] = new Vector3(m.Translation.X, m.Translation.Y, m.Translation.Z);
            result.OriginalRotations[i] = new Quaternion(m.Rotation.X, m.Rotation.Y, m.Rotation.Z, m.Rotation.W);
        }

        frameCount++;

        // --- Pass 1: Read physics bodies ---
        var boneCount = ragdollBones.Count;
        var worldPositions = new Vector3[boneCount];
        var worldRotations = new Quaternion[boneCount];
        var boneValid = new bool[boneCount];

        for (int i = 0; i < boneCount; i++)
        {
            var rb = ragdollBones[i];
            if (rb.BoneIndex < 0 || rb.BoneIndex >= skel.BoneCount) continue;

            var bodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);

            if (float.IsNaN(bodyRef.Pose.Position.X) || float.IsNaN(bodyRef.Pose.Position.Y) ||
                float.IsNaN(bodyRef.Pose.Position.Z) || float.IsNaN(bodyRef.Pose.Orientation.W))
            {
                log.Warning($"RagdollController: NaN detected in body '{rb.Name}', deactivating");
                Deactivate();
                return;
            }

            worldRotations[i] = Quaternion.Normalize(bodyRef.Pose.Orientation * rb.CapsuleToBoneOffset);

            if (rb.SegmentHalfLength > 0)
            {
                var capsuleYAxis = Vector3.Transform(Vector3.UnitY, bodyRef.Pose.Orientation);
                worldPositions[i] = bodyRef.Pose.Position - rb.SegmentHalfLength * capsuleYAxis;
            }
            else
            {
                worldPositions[i] = bodyRef.Pose.Position;
            }

            boneValid[i] = true;
        }

        // --- Pass 2: Write to ModelPose ---
        for (int i = 0; i < boneCount; i++)
        {
            if (!boneValid[i]) continue;
            var rb = ragdollBones[i];

            var modelPos = WorldToModel(worldPositions[i]);
            var modelRot = WorldRotToModel(worldRotations[i]);

            boneService.WriteBoneTransform(skel, rb.BoneIndex, modelPos, modelRot, result);
        }

        // Propagate ragdoll movement to non-ragdoll descendant bones
        for (int i = 0; i < skel.BoneCount && i < skel.ParentCount; i++)
        {
            if (ragdollBoneIndices.Contains(i)) continue;

            var parentIdx = skel.HavokSkeleton->ParentIndices[i];
            if (parentIdx < 0 || !result.HasAccumulated[parentIdx]) continue;

            var parentDelta = result.AccumulatedDeltas[parentIdx];
            var parentOrigPos = result.OriginalPositions[parentIdx];
            ref var parentModel = ref pose->ModelPose.Data[parentIdx];
            var parentNewPos = new Vector3(parentModel.Translation.X, parentModel.Translation.Y, parentModel.Translation.Z);

            var relPos = result.OriginalPositions[i] - parentOrigPos;
            relPos = Vector3.Transform(relPos, parentDelta);
            var newPos = parentOrigPos + relPos + (parentNewPos - parentOrigPos);
            var newRot = Quaternion.Normalize(parentDelta * result.OriginalRotations[i]);

            boneService.WriteBoneTransform(skel, i, newPos, newRot, result);
        }

        // Propagate j_kao changes to face/hair partial skeletons
        if (kaoBodyBoneIndex >= 0 && result.HasAccumulated[kaoBodyBoneIndex])
        {
            boneService.PropagateToPartialSkeletons(skel, kaoBodyBoneIndex, "j_kao", result);
        }

        // Apply hair physics
        if (hairPhysics != null && kaoBodyBoneIndex >= 0)
        {
            hairPhysics.StepAndApply(
                skel.CharBase, kaoBodyBoneIndex,
                skelWorldPos, skelWorldRot, skelWorldRotInv,
                1.0f / 60.0f);
        }
    }

    public DebugJointVis GetDebugJointVis(string boneName)
    {
        var result = new DebugJointVis { Valid = false };
        if (!isActive || simulation == null) return result;

        RagdollBone? childBone = null;
        for (int i = 0; i < ragdollBones.Count; i++)
        {
            if (ragdollBones[i].Name == boneName)
            {
                childBone = ragdollBones[i];
                break;
            }
        }
        if (childBone == null || childBone.Value.ParentBoneIndex < 0) return result;

        RagdollBone? parentBone = null;
        foreach (var rb in ragdollBones)
        {
            if (rb.BoneIndex == childBone.Value.ParentBoneIndex)
            {
                parentBone = rb;
                break;
            }
        }
        if (parentBone == null) return result;

        var BoneDefs = GetBoneDefs();
        RagdollBoneDef boneDef = default;
        foreach (var def in BoneDefs)
            if (def.Name == boneName) { boneDef = def; break; }

        var childBody = simulation.Bodies.GetBodyReference(childBone.Value.BodyHandle);
        var parentBody = simulation.Bodies.GetBodyReference(parentBone.Value.BodyHandle);

        var childAxis = Vector3.Transform(Vector3.UnitY, childBody.Pose.Orientation);
        var parentAxis = Vector3.Transform(Vector3.UnitY, parentBody.Pose.Orientation);

        var jointPos = childBody.Pose.Position - childAxis * childBone.Value.SegmentHalfLength;

        var up = MathF.Abs(Vector3.Dot(parentAxis, Vector3.UnitY)) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var parentRight = Vector3.Normalize(Vector3.Cross(parentAxis, up));
        var parentForward = Vector3.Normalize(Vector3.Cross(parentRight, parentAxis));

        result.Valid = true;
        result.JointPosition = jointPos;
        result.ParentAxis = parentAxis;
        result.ChildAxis = childAxis;
        result.ParentRight = parentRight;
        result.ParentForward = parentForward;
        result.Joint = boneDef.Joint;
        result.SwingLimit = boneDef.SwingLimit;
        result.TwistMinAngle = boneDef.TwistMinAngle;
        result.TwistMaxAngle = boneDef.TwistMaxAngle;
        return result;
    }

    public List<DebugCapsule> GetDebugCapsules()
    {
        var result = new List<DebugCapsule>();
        if (!isActive || simulation == null) return result;

        var BoneDefs = GetBoneDefs();
        foreach (var rb in ragdollBones)
        {
            var bodyRef = simulation.Bodies.GetBodyReference(rb.BodyHandle);

            RagdollBoneDef boneDef = default;
            foreach (var def in BoneDefs)
                if (def.Name == rb.Name) { boneDef = def; break; }

            result.Add(new DebugCapsule
            {
                Position = bodyRef.Pose.Position,
                Orientation = bodyRef.Pose.Orientation,
                Radius = boneDef.CapsuleRadius,
                HalfLength = boneDef.CapsuleHalfLength,
                Name = rb.Name,
                Joint = boneDef.Joint,
                SwingLimit = boneDef.SwingLimit,
            });
        }
        return result;
    }

    public List<DebugCapsule> GetDebugCapsulesFromSkeleton(nint characterAddress)
    {
        var result = new List<DebugCapsule>();
        var skelNullable = boneService.TryGetSkeleton(characterAddress);
        if (skelNullable == null) return result;
        var skel = skelNullable.Value;

        var skeleton = skel.CharBase->Skeleton;
        if (skeleton == null) return result;
        var skelPos = new Vector3(skeleton->Transform.Position.X, skeleton->Transform.Position.Y, skeleton->Transform.Position.Z);
        var skelRot = new Quaternion(skeleton->Transform.Rotation.X, skeleton->Transform.Rotation.Y, skeleton->Transform.Rotation.Z, skeleton->Transform.Rotation.W);

        var BoneDefs = GetBoneDefs();
        var pose = skel.Pose;

        foreach (var def in BoneDefs)
        {
            var idx = boneService.ResolveBoneIndex(skel, def.Name);
            if (idx < 0 || idx >= skel.BoneCount) continue;

            ref var mt = ref pose->ModelPose.Data[idx];
            var modelPos = new Vector3(mt.Translation.X, mt.Translation.Y, mt.Translation.Z);
            var modelRot = new Quaternion(mt.Rotation.X, mt.Rotation.Y, mt.Rotation.Z, mt.Rotation.W);

            var worldPos = skelPos + Vector3.Transform(modelPos, skelRot);
            var worldRot = Quaternion.Normalize(skelRot * modelRot);

            result.Add(new DebugCapsule
            {
                Position = worldPos,
                Orientation = worldRot,
                Radius = def.CapsuleRadius,
                HalfLength = def.CapsuleHalfLength,
                Name = def.Name,
                Joint = def.Joint,
                SwingLimit = def.SwingLimit,
            });
        }
        return result;
    }

    private void DestroySimulation()
    {
        ragdollBones.Clear();
        simulation?.Dispose();
        simulation = null;
        bufferPool?.Clear();
        bufferPool = null;
    }

    public void Dispose()
    {
        Deactivate();
        boneService.OnRenderFrame -= OnRenderFrame;
    }
}

// --- BEPU Callbacks ---

struct RagdollNarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public float Friction;
    public HashSet<(int, int)> ConnectedPairs;

    public void Initialize(BepuSimulation simulation) { }

    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        if (a.Mobility == CollidableMobility.Static || b.Mobility == CollidableMobility.Static)
            return true;

        if (ConnectedPairs != null && a.Mobility == CollidableMobility.Dynamic && b.Mobility == CollidableMobility.Dynamic)
        {
            var idA = a.BodyHandle.Value;
            var idB = b.BodyHandle.Value;
            var lo = Math.Min(idA, idB);
            var hi = Math.Max(idA, idB);
            if (ConnectedPairs.Contains((lo, hi)))
                return false;
            return true;
        }

        return false;
    }

    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
        => true;

    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
        out PairMaterialProperties pairMaterial) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial.FrictionCoefficient = Friction;
        pairMaterial.MaximumRecoveryVelocity = 2f;
        pairMaterial.SpringSettings = new SpringSettings(30, 1);
        return true;
    }

    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
        ref ConvexContactManifold manifold) => true;

    public void Dispose() { }
}

struct RagdollPoseIntegratorCallbacks : IPoseIntegratorCallbacks
{
    private Vector3 gravity;
    private float linearDamping;
    private Vector3Wide gravityDt;
    private Vector<float> dampingDt;

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;

    public RagdollPoseIntegratorCallbacks(Vector3 gravity, float linearDamping)
    {
        this.gravity = gravity;
        this.linearDamping = linearDamping;
        this.gravityDt = default;
        this.dampingDt = default;
    }

    public void Initialize(BepuSimulation simulation) { }

    public void PrepareForIntegration(float dt)
    {
        gravityDt.X = new Vector<float>(gravity.X * dt);
        gravityDt.Y = new Vector<float>(gravity.Y * dt);
        gravityDt.Z = new Vector<float>(gravity.Z * dt);
        dampingDt = new Vector<float>(MathF.Pow(linearDamping, dt * 60f));
    }

    public void IntegrateVelocity(
        Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
        BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex,
        Vector<float> dt, ref BodyVelocityWide velocity)
    {
        velocity.Linear.X = (velocity.Linear.X + gravityDt.X) * dampingDt;
        velocity.Linear.Y = (velocity.Linear.Y + gravityDt.Y) * dampingDt;
        velocity.Linear.Z = (velocity.Linear.Z + gravityDt.Z) * dampingDt;
        velocity.Angular.X *= dampingDt;
        velocity.Angular.Y *= dampingDt;
        velocity.Angular.Z *= dampingDt;
    }
}

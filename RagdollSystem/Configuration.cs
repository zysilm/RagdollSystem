using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace RagdollSystem;

[Serializable]
public class RagdollBoneConfig
{
    public string Name { get; set; } = "";
    public string? SkeletonParent { get; set; }
    public bool Enabled { get; set; } = true;
    public float CapsuleRadius { get; set; }
    public float CapsuleHalfLength { get; set; }
    public float Mass { get; set; }
    public float SwingLimit { get; set; }
    public int JointType { get; set; } // 0=Ball, 1=Hinge
    public float TwistMinAngle { get; set; }
    public float TwistMaxAngle { get; set; }
    public string? Description { get; set; }
    public bool SoftBody { get; set; }
    public float SoftSpringFreq { get; set; } = 6f;
    public float SoftSpringDamp { get; set; } = 0.4f;
    public float SoftServoFreq { get; set; } = 4f;
    public float SoftServoDamp { get; set; } = 0.35f;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // General
    public bool ShowMainWindow { get; set; } = false;

    // Ragdoll physics
    public bool EnableRagdoll { get; set; } = true;
    public float RagdollActivationDelay { get; set; } = 1.0f;
    public float RagdollGravity { get; set; } = 9.8f;
    public float RagdollDamping { get; set; } = 0.97f;
    public int RagdollSolverIterations { get; set; } = 8;
    public bool RagdollSelfCollision { get; set; } = true;
    public float RagdollFriction { get; set; } = 1.0f;

    // Hair physics
    public bool RagdollHairPhysics { get; set; } = false;
    public float RagdollHairGravityStrength { get; set; } = 0.5f;
    public float RagdollHairDamping { get; set; } = 0.92f;
    public float RagdollHairStiffness { get; set; } = 0.1f;

    // Debug
    public bool RagdollDebugOverlay { get; set; } = false;
    public bool RagdollVerboseLog { get; set; } = false;

    // Bone configs (Advanced)
    public List<RagdollBoneConfig> RagdollBoneConfigs { get; set; } = new();

    // NPC death ragdoll
    public bool EnableNpcDeathRagdoll { get; set; } = false;
    public float NpcRagdollActivationDelay { get; set; } = 0.5f;

    // Auto-cleanup
    public float RagdollDuration { get; set; } = 30.0f;

    // Max concurrent NPC ragdolls
    public int MaxNpcRagdolls { get; set; } = 5;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}

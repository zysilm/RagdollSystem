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
public class RagdollBoneProfile
{
    public string Name { get; set; } = "";
    public List<RagdollBoneConfig> Bones { get; set; } = new();
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
    public bool RagdollFollowPosition { get; set; } = false;

    // Bone configs (Advanced)
    public List<RagdollBoneConfig> RagdollBoneConfigs { get; set; } = new();
    public List<RagdollBoneProfile> RagdollBoneProfiles { get; set; } = new();

    // NPC death ragdoll
    public bool EnableNpcDeathRagdoll { get; set; } = false;
    public float NpcRagdollActivationDelay { get; set; } = 0.5f;

    // Auto-cleanup
    public float RagdollDuration { get; set; } = 30.0f;

    // Max concurrent NPC ragdolls
    public int MaxNpcRagdolls { get; set; } = 5;

    // NPC collision volumes for ragdoll interaction with nearby actors
    public bool RagdollNpcCollision { get; set; } = true;
    public float RagdollNpcCollisionScale { get; set; } = 0.0001f;
    public bool RagdollNpcSettleCollision { get; set; } = true;

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

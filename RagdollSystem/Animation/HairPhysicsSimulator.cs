using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace RagdollSystem.Animation;

/// <summary>
/// Per-bone pendulum gravity simulation for hair bone chains during ragdoll.
/// Runs after PropagateToPartialSkeletons applies rigid j_kao transform.
/// Rotation-only: bone positions orbit parents, own translation unchanged.
/// </summary>
public unsafe class HairPhysicsSimulator
{
    private struct HairBoneState
    {
        public Vector3 AngularVelocity;
        public bool Initialized;
    }

    private struct HairChainState
    {
        public int PartialSkeletonIndex;
        public int BoneCount;
        public HairBoneState[] BoneStates;
    }

    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly List<HairChainState> hairChains = new();
    private int frameCount;

    private const float HeadRadius = 0.12f;

    public HairPhysicsSimulator(Configuration config, IPluginLog log)
    {
        this.config = config;
        this.log = log;
    }

    public void Initialize(CharacterBase* charBase, int kaoBodyBoneIndex)
    {
        hairChains.Clear();

        var skeleton = charBase->Skeleton;
        if (skeleton == null) return;

        log.Info($"HairPhysics: Character has {skeleton->PartialSkeletonCount} partial skeletons");
        for (int ps = 1; ps < skeleton->PartialSkeletonCount; ps++)
        {
            var partial = &skeleton->PartialSkeletons[ps];
            var pose = partial->GetHavokPose(0);
            if (pose == null || pose->Skeleton == null) continue;
            if (pose->ModelInSync == 0) continue;

            if (!IsHairPartialSkeleton(pose)) continue;

            var boneCount = pose->ModelPose.Length;
            hairChains.Add(new HairChainState
            {
                PartialSkeletonIndex = ps,
                BoneCount = boneCount,
                BoneStates = new HairBoneState[boneCount],
            });

            var skelName = pose->Skeleton->Bones.Length > 1
                ? pose->Skeleton->Bones[1].Name.String : "?";
            log.Info($"HairPhysics: Found hair skeleton at partial {ps}, {boneCount} bones (first child: {skelName})");
        }

        log.Info($"HairPhysics: Initialized with {hairChains.Count} hair chain(s)");
    }

    public void StepAndApply(
        CharacterBase* charBase,
        int kaoBodyBoneIndex,
        Vector3 skelWorldPos,
        Quaternion skelWorldRot,
        Quaternion skelWorldRotInv,
        float dt)
    {
        if (hairChains.Count == 0) return;
        frameCount++;

        var skeleton = charBase->Skeleton;
        if (skeleton == null) return;

        var gravityDirModel = Vector3.Normalize(Vector3.Transform(-Vector3.UnitY, skelWorldRotInv));

        Vector3 headModelPos = Vector3.Zero;
        var bodyPose = skeleton->PartialSkeletons[0].GetHavokPose(0);
        if (bodyPose != null && kaoBodyBoneIndex >= 0 && kaoBodyBoneIndex < bodyPose->ModelPose.Length)
        {
            ref var kaoMt = ref bodyPose->ModelPose.Data[kaoBodyBoneIndex];
            headModelPos = new Vector3(kaoMt.Translation.X, kaoMt.Translation.Y, kaoMt.Translation.Z);
        }

        var gravityStrength = config.RagdollHairGravityStrength;
        var damping = config.RagdollHairDamping;
        var stiffness = config.RagdollHairStiffness;

        for (int c = 0; c < hairChains.Count; c++)
        {
            var chain = hairChains[c];
            var partial = &skeleton->PartialSkeletons[chain.PartialSkeletonIndex];
            var pose = partial->GetHavokPose(0);
            if (pose == null || pose->Skeleton == null) continue;

            var boneCount = Math.Min(chain.BoneCount, pose->ModelPose.Length);
            var parentCount = pose->Skeleton->ParentIndices.Length;

            var accDelta = new Quaternion[boneCount];
            for (int b = 0; b < boneCount; b++) accDelta[b] = Quaternion.Identity;

            for (int b = 1; b < boneCount && b < parentCount; b++)
            {
                var parentIdx = pose->Skeleton->ParentIndices[b];
                if (parentIdx < 0 || parentIdx >= boneCount) continue;

                ref var bm = ref pose->ModelPose.Data[b];
                var bonePos = new Vector3(bm.Translation.X, bm.Translation.Y, bm.Translation.Z);
                var boneRot = new Quaternion(bm.Rotation.X, bm.Rotation.Y, bm.Rotation.Z, bm.Rotation.W);

                if (accDelta[parentIdx] != Quaternion.Identity)
                {
                    ref var parentMt = ref pose->ModelPose.Data[parentIdx];
                    var parentPos = new Vector3(parentMt.Translation.X, parentMt.Translation.Y, parentMt.Translation.Z);
                    var pDelta = accDelta[parentIdx];

                    var relPos = bonePos - parentPos;
                    bonePos = parentPos + Vector3.Transform(relPos, pDelta);
                    boneRot = Quaternion.Normalize(pDelta * boneRot);

                    bm.Translation.X = bonePos.X;
                    bm.Translation.Y = bonePos.Y;
                    bm.Translation.Z = bonePos.Z;
                    bm.Rotation.X = boneRot.X;
                    bm.Rotation.Y = boneRot.Y;
                    bm.Rotation.Z = boneRot.Z;
                    bm.Rotation.W = boneRot.W;
                }

                ref var parentMt2 = ref pose->ModelPose.Data[parentIdx];
                var parentPos2 = new Vector3(parentMt2.Translation.X, parentMt2.Translation.Y, parentMt2.Translation.Z);
                var currentDir = bonePos - parentPos2;
                var boneLen = currentDir.Length();

                if (boneLen < 0.001f)
                {
                    accDelta[b] = accDelta[parentIdx];
                    continue;
                }
                currentDir /= boneLen;

                ref var state = ref chain.BoneStates[b];
                if (!state.Initialized)
                {
                    state.AngularVelocity = Vector3.Zero;
                    state.Initialized = true;
                }

                var rotToGravity = RotationBetween(currentDir, gravityDirModel);
                ToAxisAngle(rotToGravity, out var axis, out var angle);
                angle *= gravityStrength;

                var angAccel = axis * angle * 30.0f;
                state.AngularVelocity += angAccel * dt;
                state.AngularVelocity *= damping;
                state.AngularVelocity *= (1.0f - stiffness);

                var velMag = state.AngularVelocity.Length();
                if (velMag > 10.0f)
                    state.AngularVelocity *= 10.0f / velMag;

                if (velMag > 0.0001f)
                {
                    var velAxis = state.AngularVelocity / velMag;
                    var velRot = Quaternion.CreateFromAxisAngle(velAxis, velMag);

                    var newDir = Vector3.Transform(currentDir, velRot);
                    var newDirLen = newDir.Length();
                    if (newDirLen > 0.001f)
                        newDir /= newDirLen;
                    else
                        newDir = currentDir;

                    var newBonePos = parentPos2 + newDir * boneLen;
                    if (ClampToHeadSphere(ref newBonePos, headModelPos, HeadRadius))
                    {
                        var clampedDir = newBonePos - parentPos2;
                        var clampedLen = clampedDir.Length();
                        if (clampedLen > 0.001f)
                            newDir = clampedDir / clampedLen;

                        var toHead = headModelPos - newBonePos;
                        var toHeadLen = toHead.Length();
                        if (toHeadLen > 0.001f)
                        {
                            toHead /= toHeadLen;
                            var velToward = Vector3.Dot(state.AngularVelocity, toHead);
                            if (velToward > 0)
                                state.AngularVelocity -= toHead * velToward;
                        }
                    }

                    var ownDelta = RotationBetween(currentDir, newDir);
                    var newRot = Quaternion.Normalize(ownDelta * boneRot);

                    accDelta[b] = Quaternion.Normalize(ownDelta * accDelta[parentIdx]);

                    bm.Rotation.X = newRot.X;
                    bm.Rotation.Y = newRot.Y;
                    bm.Rotation.Z = newRot.Z;
                    bm.Rotation.W = newRot.W;
                }
                else
                {
                    accDelta[b] = accDelta[parentIdx];
                }
            }

            hairChains[c] = chain;
        }
    }

    public void Reset()
    {
        hairChains.Clear();
    }

    private static bool IsHairPartialSkeleton(FFXIVClientStructs.Havok.Animation.Rig.hkaPose* pose)
    {
        var bones = pose->Skeleton->Bones;
        if (bones.Length < 2) return false;

        var rootName = bones[0].Name.String;
        if (rootName != "j_kao") return false;

        for (int i = 1; i < bones.Length; i++)
        {
            var name = bones[i].Name.String;
            if (name == null) continue;
            if (name.StartsWith("j_kami_")) return true;
            if (name.StartsWith("j_ex_h") && name.Contains("_ke_")) return true;
        }
        return false;
    }

    private static bool ClampToHeadSphere(ref Vector3 bonePos, Vector3 headPos, float radius)
    {
        var offset = bonePos - headPos;
        var dist = offset.Length();
        if (dist < radius && dist > 0.001f)
        {
            bonePos = headPos + (offset / dist) * radius;
            return true;
        }
        return false;
    }

    private static Quaternion RotationBetween(Vector3 from, Vector3 to)
    {
        var dot = Vector3.Dot(from, to);
        if (dot > 0.9999f) return Quaternion.Identity;
        if (dot < -0.9999f)
        {
            var perp = Vector3.Cross(from, Vector3.UnitX);
            if (perp.LengthSquared() < 0.001f)
                perp = Vector3.Cross(from, Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(perp), MathF.PI);
        }
        var axis = Vector3.Cross(from, to);
        var axisLen = axis.Length();
        if (axisLen < 0.0001f) return Quaternion.Identity;
        axis /= axisLen;
        var angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));
        return Quaternion.CreateFromAxisAngle(axis, angle);
    }

    private static void ToAxisAngle(Quaternion q, out Vector3 axis, out float angle)
    {
        q = Quaternion.Normalize(q);
        if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        angle = 2.0f * MathF.Acos(Math.Clamp(q.W, -1f, 1f));
        var sinHalf = MathF.Sqrt(1.0f - q.W * q.W);
        if (sinHalf > 0.0001f)
            axis = new Vector3(q.X / sinHalf, q.Y / sinHalf, q.Z / sinHalf);
        else
            axis = Vector3.UnitY;
    }
}

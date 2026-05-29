using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using RagdollSystem.Animation;

namespace RagdollSystem.Gui;

public class RagdollDebugOverlay
{
    private readonly RagdollSystemPlugin plugin;
    private readonly MainWindow mainWindow;
    private readonly Configuration config;
    private readonly IGameGui gameGui;

    public RagdollDebugOverlay(RagdollSystemPlugin plugin, MainWindow mainWindow, Configuration config, IGameGui gameGui)
    {
        this.plugin = plugin;
        this.mainWindow = mainWindow;
        this.config = config;
        this.gameGui = gameGui;
    }

    public void Draw()
    {
        var ragdoll = plugin.PlayerRagdoll;
        if (!config.RagdollDebugOverlay || ragdoll == null || !ragdoll.IsActive) return;

        var capsules = ragdoll.GetDebugCapsules();
        if (capsules.Count == 0) return;

        var drawList = ImGui.GetForegroundDrawList();
        var editingBone = mainWindow.EditingBoneName;

        foreach (var cap in capsules)
        {
            var isEditing = editingBone != null && cap.Name == editingBone;
            var alpha = isEditing ? 1.0f : 0.8f;
            var thickness = isEditing ? 3.0f : 1.5f;
            var colorCapsule = ImGui.GetColorU32(isEditing
                ? new Vector4(1f, 1f, 0.2f, alpha)
                : new Vector4(0.2f, 0.8f, 0.3f, alpha));
            var jointColor = ImGui.GetColorU32(
                cap.Joint == RagdollController.JointType.Hinge
                    ? new Vector4(1f, 0.5f, 0.1f, alpha)
                    : new Vector4(0.3f, 0.5f, 1f, alpha));
            var colorLabel = ImGui.GetColorU32(isEditing
                ? new Vector4(1f, 1f, 0.2f, 1f)
                : new Vector4(0.7f, 0.9f, 0.7f, 0.9f));

            var yAxis = Vector3.Transform(Vector3.UnitY, cap.Orientation);
            var xAxis = Vector3.Transform(Vector3.UnitX, cap.Orientation);
            var zAxis = Vector3.Transform(Vector3.UnitZ, cap.Orientation);
            var top = cap.Position + yAxis * cap.HalfLength;
            var bottom = cap.Position - yAxis * cap.HalfLength;

            DrawCircle3D(drawList, top, xAxis, zAxis, cap.Radius, colorCapsule, thickness);
            DrawCircle3D(drawList, bottom, xAxis, zAxis, cap.Radius, colorCapsule, thickness);

            for (var i = 0; i < 4; i++)
            {
                var angle = i * MathF.PI / 2;
                var offset = (xAxis * MathF.Cos(angle) + zAxis * MathF.Sin(angle)) * cap.Radius;
                if (gameGui.WorldToScreen(top + offset, out var s1) &&
                    gameGui.WorldToScreen(bottom + offset, out var s2))
                    drawList.AddLine(s1, s2, colorCapsule, thickness);
            }

            DrawCircle3D(drawList, cap.Position, xAxis, zAxis, cap.Radius * 0.5f, jointColor, thickness + 0.5f);

            if (cap.SwingLimit > 0.01f && isEditing)
            {
                var coneRadius = MathF.Min(cap.HalfLength * MathF.Tan(cap.SwingLimit), 0.3f);
                DrawCircle3D(drawList, top + yAxis * 0.05f, xAxis, zAxis, coneRadius, jointColor, 1.5f);
                for (var i = 0; i < 4; i++)
                {
                    var angle = i * MathF.PI / 2;
                    var edgePoint = top + yAxis * 0.05f + (xAxis * MathF.Cos(angle) + zAxis * MathF.Sin(angle)) * coneRadius;
                    if (gameGui.WorldToScreen(cap.Position, out var sc) &&
                        gameGui.WorldToScreen(edgePoint, out var se))
                        drawList.AddLine(sc, se, jointColor, 1f);
                }
            }

            if (gameGui.WorldToScreen(cap.Position, out var labelPos))
            {
                var label = isEditing ? $">> {cap.Name} <<" : cap.Name;
                var textSize = ImGui.CalcTextSize(label);
                drawList.AddText(labelPos - textSize * 0.5f, colorLabel, label);
            }
        }

        var editParam = mainWindow.EditingParameter;
        if (editingBone != null && editParam != MainWindow.EditParam.None)
        {
            var jv = ragdoll.GetDebugJointVis(editingBone);
            if (jv.Valid)
                DrawJointLimits(drawList, jv, editParam);
        }
    }

    private void DrawJointLimits(ImDrawListPtr drawList, RagdollController.DebugJointVis jv, MainWindow.EditParam editParam)
    {
        var colorLimit = ImGui.GetColorU32(new Vector4(1f, 0.3f, 0.3f, 0.9f));
        var colorChild = ImGui.GetColorU32(new Vector4(0.2f, 1f, 0.5f, 1f));
        var colorAxis = ImGui.GetColorU32(new Vector4(1f, 1f, 0.3f, 0.8f));

        if (editParam == MainWindow.EditParam.Swing)
        {
            if (jv.Joint == RagdollController.JointType.Ball)
                DrawSwingCone(drawList, jv, colorLimit, colorChild, colorAxis);
            else
                DrawSwingArc(drawList, jv, colorLimit, colorChild, colorAxis);
        }
        else if (editParam == MainWindow.EditParam.TwistMin || editParam == MainWindow.EditParam.TwistMax)
        {
            DrawTwistArc(drawList, jv, colorLimit, colorChild, colorAxis);
        }
    }

    private void DrawSwingCone(ImDrawListPtr drawList, RagdollController.DebugJointVis jv,
        uint colorLimit, uint colorChild, uint colorAxis)
    {
        var coneLength = 0.15f;
        var coneRadius = MathF.Min(coneLength * MathF.Tan(jv.SwingLimit), 0.4f);
        var coneTip = jv.JointPosition;
        var coneBase = coneTip + jv.ParentAxis * coneLength;

        DrawCircle3D(drawList, coneBase, jv.ParentRight, jv.ParentForward, coneRadius, colorLimit, 2f, 24);
        for (var i = 0; i < 8; i++)
        {
            var angle = i * MathF.PI / 4;
            var edgePoint = coneBase + (jv.ParentRight * MathF.Cos(angle) + jv.ParentForward * MathF.Sin(angle)) * coneRadius;
            if (gameGui.WorldToScreen(coneTip, out var st) && gameGui.WorldToScreen(edgePoint, out var se))
                drawList.AddLine(st, se, colorLimit, 1.5f);
        }

        DrawWorldLine(drawList, coneTip, coneTip + jv.ParentAxis * (coneLength * 1.3f), colorAxis, 1f);
        DrawWorldLine(drawList, coneTip, coneTip + jv.ChildAxis * coneLength, colorChild, 2.5f);
    }

    private void DrawSwingArc(ImDrawListPtr drawList, RagdollController.DebugJointVis jv,
        uint colorLimit, uint colorChild, uint colorAxis)
    {
        var arcRadius = 0.12f;
        var center = jv.JointPosition;
        DrawArc3D(drawList, center, jv.ParentRight, jv.ParentAxis,
            arcRadius, -jv.SwingLimit, jv.SwingLimit, colorLimit, 2f, 20);

        for (var sign = -1; sign <= 1; sign += 2)
        {
            var angle = sign * jv.SwingLimit;
            var endPoint = center + (jv.ParentRight * MathF.Cos(angle) + jv.ParentAxis * MathF.Sin(angle)) * arcRadius;
            DrawWorldLine(drawList, center, endPoint, colorLimit, 1.5f);
        }

        DrawWorldLine(drawList, center, center + jv.ChildAxis * arcRadius, colorChild, 2.5f);
        DrawWorldLine(drawList, center, center + jv.ParentAxis * arcRadius, colorAxis, 1f);
    }

    private void DrawTwistArc(ImDrawListPtr drawList, RagdollController.DebugJointVis jv,
        uint colorLimit, uint colorChild, uint colorAxis)
    {
        var arcRadius = 0.1f;
        var center = jv.JointPosition;
        var up = MathF.Abs(Vector3.Dot(jv.ChildAxis, Vector3.UnitY)) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var twistRight = Vector3.Normalize(Vector3.Cross(jv.ChildAxis, up));
        var twistForward = Vector3.Normalize(Vector3.Cross(twistRight, jv.ChildAxis));

        DrawArc3D(drawList, center, twistRight, twistForward,
            arcRadius, jv.TwistMinAngle, jv.TwistMaxAngle, colorLimit, 2f, 20);

        var minPoint = center + (twistRight * MathF.Cos(jv.TwistMinAngle) + twistForward * MathF.Sin(jv.TwistMinAngle)) * arcRadius;
        var maxPoint = center + (twistRight * MathF.Cos(jv.TwistMaxAngle) + twistForward * MathF.Sin(jv.TwistMaxAngle)) * arcRadius;
        DrawWorldLine(drawList, center, minPoint, colorLimit, 1.5f);
        DrawWorldLine(drawList, center, maxPoint, colorLimit, 1.5f);
        DrawWorldLine(drawList, center, center + jv.ChildAxis * (arcRadius * 1.5f), colorAxis, 1f);
        DrawWorldLine(drawList, center, center + jv.ParentAxis * arcRadius, colorChild, 1f);
    }

    private void DrawArc3D(ImDrawListPtr drawList, Vector3 center, Vector3 right, Vector3 forward,
        float radius, float fromAngle, float toAngle, uint color, float thickness, int segments)
    {
        Vector2 prevScreen = default;
        var prevValid = false;
        var step = (toAngle - fromAngle) / segments;

        for (var i = 0; i <= segments; i++)
        {
            var angle = fromAngle + i * step;
            var worldPos = center + (right * MathF.Cos(angle) + forward * MathF.Sin(angle)) * radius;
            if (gameGui.WorldToScreen(worldPos, out var screenPos))
            {
                if (prevValid)
                    drawList.AddLine(prevScreen, screenPos, color, thickness);
                prevScreen = screenPos;
                prevValid = true;
            }
            else
            {
                prevValid = false;
            }
        }
    }

    private void DrawCircle3D(ImDrawListPtr drawList, Vector3 center, Vector3 right, Vector3 forward,
        float radius, uint color, float thickness = 1.5f, int segments = 12)
    {
        Vector2 prevScreen = default;
        var prevValid = false;

        for (var i = 0; i <= segments; i++)
        {
            var angle = i * 2f * MathF.PI / segments;
            var worldPos = center + (right * MathF.Cos(angle) + forward * MathF.Sin(angle)) * radius;
            if (gameGui.WorldToScreen(worldPos, out var screenPos))
            {
                if (prevValid)
                    drawList.AddLine(prevScreen, screenPos, color, thickness);
                prevScreen = screenPos;
                prevValid = true;
            }
            else
            {
                prevValid = false;
            }
        }
    }

    private void DrawWorldLine(ImDrawListPtr drawList, Vector3 from, Vector3 to, uint color, float thickness)
    {
        if (gameGui.WorldToScreen(from, out var a) && gameGui.WorldToScreen(to, out var b))
            drawList.AddLine(a, b, color, thickness);
    }
}

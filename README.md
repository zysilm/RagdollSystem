# Ragdoll System

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for FFXIV that applies ragdoll physics to characters when they die in-game. Bodies collapse under gravity, joints respect rotation limits, and the result settles against the terrain.

This is a standalone extraction of the ragdoll subsystem from [FFXIV-CombatSimulator](https://github.com/zysilm/FFXIV-CombatSimulator) — useful on its own if you don't want the combat simulation features.

## Installation

Open Dalamud Settings in-game (`/xlsettings`) and follow these steps:

1. Click the **Experimental** tab.
2. Paste the following URL into the empty box at the bottom of **Custom Plugin Repositories**:
   ```
   https://raw.githubusercontent.com/zysilm/FFXIV-CombatSimulator/main/pluginmaster.json
   ```
3. Click the **+** button, then click **Save**.
4. Open the Plugin Installer (`/xlplugins`) and search for **Ragdoll System**.

> The plugin manifest currently ships in the FFXIV-CombatSimulator repository's `pluginmaster.json`, which lists both plugins side by side. Installing **Ragdoll System** from that repo does not install Combat Simulator.

## Features

- **Death-triggered ragdoll** — Detects HP transitions from >0 to 0 each frame and activates a physics simulation on the corpse. Player revives (HP back to >0) automatically clean up the ragdoll.
- **Per-bone physics** — 42 configurable bones across spine, arms, legs, clavicles, and cloth/skirt chains, with optional weapon-holster, breast, and toe bones. Each bone exposes capsule radius/half-length, mass, joint type (ball or hinge), swing limit, and twist range.
- **Hair physics** — Optional per-bone pendulum simulation for hair chains, with adjustable gravity, damping, and stiffness. Runs as a separate pass after the main ragdoll updates.
- **NPC ragdoll (opt-in)** — Nearby battle NPCs can also ragdoll on death. Concurrent count is capped (default 5, oldest removed first) and non-humanoid skeletons are handled gracefully — the system uses whichever default bones are actually present.
- **Tunable simulation** — Gravity, linear damping, solver iterations, surface friction, and self-collision are all live-editable. Activation delay and total duration are per-side (player vs NPC).
- **Auto-cleanup** — Ragdolls expire after a configurable duration (default 30s) and are wiped on territory change.

Built on [BepuPhysics2](https://github.com/bepu/bepuphysics2). All effects are client-side only — no data is sent to the server.

## Usage

1. Open the plugin window with `/ragdoll` (or via the Dalamud plugin installer's settings button).
2. Adjust settings across the three tabs and die in-game (or use the test button) to see the ragdoll trigger.

The settings window is organized into:

| Tab | Contents |
|---|---|
| **General** | Master enable, activation delay, duration, gravity, damping, solver iterations, friction, self-collision, hair physics, debug toggles, and **Test: Activate / Deactivate** buttons for previewing on a living player. |
| **NPC** | Opt-in NPC death ragdoll, NPC activation delay, max concurrent NPC ragdolls. |
| **Ragdoll (Adv)** | Per-bone editor — enable/disable individual bones, edit capsule volume, mass, joint type, swing limit, and twist range. Quick toggle for sheathed-weapon bones. |

### Commands

| Command | Description |
|---------|-------------|
| `/ragdoll` | Toggle the settings window. |

## Building from Source

```bash
git clone https://github.com/zysilm/RagdollSystem.git
dotnet build RagdollSystem/RagdollSystem.csproj -c Release
```

Requirements:

- .NET 10.0 SDK
- A local Dalamud install (the CI workflow downloads the latest Dalamud distribution to `$DALAMUD_HOME`; if building locally you need Dalamud available in the standard XIVLauncher path or `DALAMUD_HOME` set).
- `Dalamud.NET.Sdk 15.0.0` and `BepuPhysics 2.5.0-beta.22` (restored automatically via NuGet).

The build output lives at `RagdollSystem/bin/Release/RagdollSystem/`. The `release.yaml` workflow handles tagging and publishing release zips automatically on pushes to `main`.

## Relationship to FFXIV-CombatSimulator

[FFXIV-CombatSimulator](https://github.com/zysilm/FFXIV-CombatSimulator) bundles this ragdoll system as one feature among combat simulation, NPC AI, and camera controls. Ragdoll System is the same physics core extracted into a standalone plugin so users who only want death physics don't need the full combat simulator installed. The two can coexist; if you have Combat Simulator installed, you don't also need Ragdoll System.

## Credits

- Physics powered by [BEPUphysics2](https://github.com/bepu/bepuphysics2) by Ross Nordby
- Built on [Dalamud](https://github.com/goatcorp/Dalamud) by goatcorp

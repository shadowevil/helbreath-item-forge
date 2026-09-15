# Helbreath Item Forge

Avalonia desktop GUI (published as `helbreath_item_forge`) that bakes 3D item models into Helbreath
sprites: worn on the player in every action / direction / frame for both sexes, on the equip doll, in the
inventory, and on the ground. Every placement is visible and hand-adjustable in a live 3D view, and every
bake is repeatable byte-for-byte.

The full design is [item-forge-plan.md](item-forge-plan.md).

## Status

Phase 1 of 8: workspace, Workbench-styled shell, card gallery, card Details editor (name, item type, model
file with SHA-256, notes), and the MCP control surface. No rendering yet.

## Build

Prerequisite: the .NET 8 SDK (or a newer SDK with the net8.0 targeting pack).

    dotnet build ItemForge.sln

## Publish (portable win-x64, framework-dependent)

    dotnet publish src/ItemForge.App/ItemForge.App.csproj /p:PublishProfile=win-x64

Output lands in `src/ItemForge.App/bin/publish/`. Requires the .NET 8 runtime on the target machine.

## Workspace

The app works inside a **workspace**: the folder holding `forge-workspace.json` (this repo). It finds it by
walking up from the exe folder, or File > Open Workspace... picks one.

    forge-workspace.json    marker + settings shared by every card
    cards/<id>.json         one model card per item (committed)
    models/                 .glb / .gltf inputs (gitignored - third-party licensed)
    reference/              copied Helbreath sprites used as backdrops (phase 3)
    output/                 bake output (gitignored, regenerable)

## Layout

    ItemForge.sln
    src/ItemForge.Core/     cards, workspace, model binding - no UI
    src/ItemForge.App/      the Avalonia GUI + MCP server
    item-forge-plan.md      the design: requirements, formats, phases, findings
    item-forge-mcp.md       agent guide for driving the app over MCP

# Helbreath Item Forge - project instructions

A C# Avalonia GUI that bakes 3D item models into Helbreath sprites. The design is `item-forge-plan.md` in
this repo - read it before changing behaviour. Work is tracked in chat and that plan; there is no kanban
board for this repo. Nothing about the forge is written into the Helbreath repo (`../Helbreath`): it is read
for reference only (user direction 2026-09-15, after forge files sat unstaged in that tree).

## User rules (binding, set 2026-09-15)

- **The tool reads 3D models only.** It never opens a `.hba`. Helbreath character sprites are COPIED into
  `reference/` and used as backdrops only.
- **A bake renders the 3D model only**, never the character sprite behind it.
- **Pivots are derived, never hand-set**: the bake writes a placement json per character action set, and
  both output layouts take their rects and pivots from it.
- **Every adjustment belongs to one card** (one model bound to one item).
- **Four presentations per card**: Worn (animated on the player), Equip (static on the doll, with
  cut-outs), Inventory, Ground.
- **Output is raw PNG + frame data in BOTH the 1999 legacy layout and Helbreath's current layout.** Importing
  into `items.hba` is a separate step outside this tool.
- **Follow the HBA Workbench's design standards exactly** (`../hba-workshop`): its colour tokens, Inter,
  button / tree / dialog styles, frameless shell and cursors. `App.axaml` here carries its tokens unchanged.
  One deliberate exception: **no activity bar** - the forge has a single view (user decision 2026-09-15).
- **Stay lean** (user rule, 2026-09-15): this is a 3D processor that bakes models into 2D sprites following
  the Helbreath models, with slight adjustments. A card keeps what describes how the item is represented -
  name, item type, model file, notes. Anything that feeds nothing (informational fields, extra navigation,
  convenience features) is bloat: don't add it.
- **Keep a full MCP server, like the Workbench's** (user decision 2026-09-15).

## Engineering rules

- **Every feature must be drivable and observable over MCP** (the Workshop rule) and verified over MCP, not
  by mouse / keyboard injection. `item-forge-mcp.md` is the agent guide - update it with every tool change.
- **UI and MCP share one code path**: menus, buttons and tools all call the same `*Core` operations in
  `MainWindow`, so an agent and a person always get the same behaviour.
- **Repeatability**: card files are the source of truth, models are bound by path + SHA-256, and output
  must be byte-identical for the same card + model.
- **Console output is ASCII** (the Windows console here is cp1252). UI strings may use `\u` escapes.
- **Close the app with the `quit` tool**, never `taskkill` - a running copy may be the user's.
- Code style follows the Workbench's C#: PascalCase, 4-space indent, Allman braces, `_camelCase` fields,
  comments that explain why.

## Build, run, MCP

    dotnet build ItemForge.sln
    src/ItemForge.App/bin/Debug/net8.0/helbreath_item_forge.exe --mcp

MCP endpoint `http://127.0.0.1:4001/mcp` (4000 = Workbench, 8787 = kanban, 8791 = Helbreath client bridge).
Port and token live in `settings.json` next to the exe.

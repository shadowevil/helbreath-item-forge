# Item Forge - MCP Agent Guide

How an AI agent drives **Helbreath Item Forge** through its built-in MCP server: browse and edit model
cards, bind models, and take screenshots, with no mouse or keyboard. The server design is the HBA
Workbench's (`../hba-workshop/hba-workbench-mcp.md`); only the port, name and tools differ.

---

## 1. Connect

The app hosts a **local-only MCP server** (loopback `127.0.0.1`, bearer-token gated). The app must be
**running** with integration **enabled**.

**Enable it** - in the app: `Tools > Claude Code Integration > Observe` (read-only) or `Full`
(read/write). The same menu has **Copy connect command**, **Copy .mcp.json config**, **Copy server URL**
and **Copy bearer token**.

**Register with the agent** (once):

```bash
claude mcp add --transport http helbreath-item-forge http://127.0.0.1:4001/mcp \
  --header "Authorization: Bearer <TOKEN>"
```

or in a project's `.mcp.json`:

```json
{
  "mcpServers": {
    "helbreath-item-forge": {
      "type": "http",
      "url": "http://127.0.0.1:4001/mcp",
      "headers": { "Authorization": "Bearer <TOKEN>" }
    }
  }
}
```

Default port is `4001` (4000 is the Workbench). Port and token live in `settings.json` next to the exe
(`IntegrationPort` / `IntegrationToken`).

**Launch from the agent** with the server on for that run:

```bash
helbreath_item_forge.exe --mcp [cards\<id>.json]
```

The app is **single-instance**: a second launch forwards its card file to the running window and prints
the running instance's URL + token to stdout.

## 2. Permission modes

| Mode | Allows |
|---|---|
| **Off** | Server stopped. |
| **Observe** | Read, navigate, screenshots returned inline. No content changes, no disk writes. |
| **Full** | Everything: create / edit / save / duplicate / delete cards, settings, screenshots to disk, quit. |

Tools above the current mode are hidden from `tools/list` and refused by `tools/call`. `--mcp` turns on
Observe for that run; Full must be chosen in the menu (it persists).

## 3. Tools

| Tool | Mode | Args | What it does |
|---|---|---|---|
| `ping` | Observe | - | Version + current mode. |
| `get_state` | Observe | - | **Primary eyes**: workspace, card count, view, open card (id, name, dirty), integration, windows, settings. |
| `list_cards` | Observe | - | Re-reads `cards/`; every card's summary incl. `modelStatus` (None / Ok / Missing / Changed). |
| `get_card` | Observe | `id` | Summary + full card JSON (the unsaved working copy when open) + `isOpen` / `dirty`. |
| `screenshot` | Observe (Full for `destFile`) | `target?`, `destFile?` | PNG of `main` / `all` / a title substring; inline, or written to an absolute `.png` path. |
| `show_gallery` | Observe | - | Show the card gallery. |
| `open_card` | Observe | `id` | Open a card in the editor. Refused while another card is dirty. |
| `set_card_tab` | Observe | `tab` | `Details` or `Model`; the presentation tabs are locked until their phase. |
| `get_model_info` | Observe | `id?` | Loaded geometry (triangles, bounds, texture, load ms), the base setup, the live view and the last render's stats. |
| `set_model_view` | Observe | `direction?`, `zoom?`, `mode?`, `reset?` | Point the live 3D view: facing 0..7, whole-pixel zoom 1..16, camera `game` or `free`, or reset it. View state only. |
| `render_model` | Observe (Full for `destFile`) | `id?`, `direction?`, `size?`, `supersample?`, `destFile?` | Render the model alone through the game camera; returns cost, opaque box and pivot, PNG inline or to disk. |
| `close_card` | Observe (Full to discard) | `discard?` | Close the open card; refused when dirty unless `discard:true`. |
| `create_card` | Full | `name`, `open?` | New `cards/<id>.json`; id derived from the name. |
| `update_card` | Full | `id?`, `name?`, `notes?`, `itemType?`, `modelPath?` | Edit the working copy (becomes dirty). Validates all fields before applying any. `modelPath` binds + hashes; `""` clears. |
| `update_model_setup` | Full | `id?`, `fit?`, `scale?`, `offsetX/Y?`, `rotationX/Y/Z?`, `lightYaw?`, `lightPitch?`, `ambient?` | Edit the base fix-up every presentation inherits. Validates before applying. |
| `save_card` | Full | - | Atomic write of the open card. |
| `duplicate_card` | Full | `id`, `name` | Copy a card under a new name. |
| `delete_card` | Full | `id` | Move to `cards/.trash/`. |
| `set_setting` | Full | `key`, `value` | `theme` (System / Light / Dark) or `workspaceDir`. |
| `quit` | Full | `discard?` | Close the app cleanly. **Use this, never taskkill.** |

Every reply is JSON text; failures are `{ok:false, error}` with `isError:true`.

## 4. How to work

1. **Perceive first**: `get_state` / `list_cards` / `get_card` give exact values - screenshots are for
   checking the look, not for reading state.
2. **Edit, then save**: `update_card` only changes the working copy, exactly like typing in the Details tab.
   Nothing reaches disk until `save_card`. Confirm with `get_card` (`dirty:false`) or by reading the file.
3. **The Model tab is the renderer**: `render_model` puts the same pixels on disk that the live view shows,
   through one shared game camera - orthographic, so the tight opaque box it reports IS the sprite pivot
   (`pivotX` / `pivotY`, relative to the anchor). A bake renders the MODEL ONLY; the character sprite is an
   editing backdrop and never reaches an output pixel.
4. **Two cameras, one of which bakes**: the Model tab can orbit freely (`set_model_view mode:free`, or
   middle-drag in the app) to inspect a model, but `render_model` and every bake always use the GAME camera -
   fixed elevation, eight facings. A free look changes nothing about the output, which is what makes the
   pixel-exact preview in game mode trustworthy.
5. **Every render states its cost** (`stats.cost`: triangles drawn, supersample, ms, size). Live view renders
   at supersample 1, `render_model` defaults to 4.
6. **Model binding is by path + SHA-256**: a model inside the workspace is stored relative
   (`models/foo.glb`), so the card works on any machine; `modelStatus: Changed` means the file no longer
   matches the recorded hash.

# item forge - bake 3D item models into Helbreath sprites

Status: phases 1-2 BUILT (2026-09-15), the tool trimmed to the lean scope; phases 3-8 not started.

Moved here from the Helbreath repo (`design/plans/partial/item-forge.md`) on 2026-09-15 so forge work stays
out of that tree. Source paths below (`player_renderer.cpp`, `in_game_screen.cpp`, `entity_action.h`,
`reference/...`, `design/...`, `claude_docs/...`) are relative to `../Helbreath`.

**Item Forge** (repo `helbreath-item-forge`, exe `helbreath_item_forge`) is a standalone C# GUI tool that turns
a 3D item model (GLB) into every 2D sprite the game needs for that item - worn on the player in every
action/direction/frame for both sexes, on the equip doll, in the inventory, and on the ground - with
every placement visible and hand-adjustable, and every bake exactly repeatable.

## 1. Requirements (user-stated, 2026-09-15)

- **A GUI**, following the HBA Workbench's design standards exactly.
- **It reads 3D models only.** It never opens a `.hba`. The character art it places items against is a
  one-time COPY of the existing sprites into the tool's own directory, used as a backdrop only.
- **Model cards.** The main view is a gallery of cards; one card = one model bound to one item. Every
  adjustment belongs to that card and never leaks into another.
- **Four presentations per card, all adjustable** (scale, size, position, rotation, ...):
  1. **Worn** - on the player, animated: every action x direction x frame, male and female;
  2. **Equip** - the equip-doll (character-sheet) figure: a STATIC model, no animation, adjusted by
     positioning, with cut-outs from that positioning;
  3. **Inventory** - the bag icon;
  4. **Ground** - the dropped-item sprite.
- **Adjusting is intuitive**: a live 3D view of the model with the sprite behind it; Bake produces the
  proper render.
- **The body cuts into the model where it should** (hand over the hilt, arm across the blade) - section 5.
- **A bake renders the 3D model only** - never the character sprite.
- **Pivots are derived automatically** from a placement json written per character action set.
- **Output is raw PNG (+ frame data)** in TWO layouts: the 1999 legacy layout and this project's current
  layout. Getting the art into `items.hba` is a separate step (Workbench import), not the tool's job.
- Repeatable: the same card + model gives byte-identical output.
- **Lean** (user rule, 2026-09-15): a 3D processor that bakes models into 2D sprites following the Helbreath
  models, with slight adjustments - nothing more. A feature or field that feeds nothing is bloat. Cut on
  review of phase 1: the card's item model name and item ids, the Workbench's activity bar (one view only),
  keyframe interpolation and onion skin, the separate CLI and the determinism check. Kept: a full MCP server.
- Work is tracked in chat and this plan, not on a kanban board.

## 2. Architecture

- **Own repo** `repos4/helbreath-item-forge`, a sibling of `hba-workshop`. It does NOT depend on `hba-lib` -
  the tool never touches archives.
- **Stack**: net8.0, Avalonia 11.2.1 + Fonts.Inter, SkiaSharp 2.88.8 (PNG encode/decode), SharpGLTF
  (GLB load). Matches the Workbench's versions so the shell, styles and canvas port across.
- **Projects**:
  - `ItemForge.Core` - card/pose data model, GLB loader, software rasterizer, occlusion, post-process,
    exporters, 1999 pose measurement. No UI.
  - `ItemForge.App` - the Avalonia GUI. Batch bakes run in the app (Bake all) or over MCP; there is no CLI.
- **Renderer is a software rasterizer**, not the GPU: output must not vary by driver, and sprites are a
  few dozen pixels so bake speed is a non-issue. Render supersampled, downsample, then the pixel-art pass.
- **The live 3D editing view uses the SAME renderer**. One renderer means where the model is placed in the
  preview is exactly where it bakes - no preview/bake drift. The preview skips supersampling and the
  pixel-art pass; Bake adds them. The first model is 1,076 triangles, so interactive frame rates are
  expected; if a heavy model measures too slow, a GPU preview becomes an option only once it is proven to
  place identically.
- **One game camera**, shared by all cards: orthographic, a fixed elevation angle and pixels-per-unit,
  tuned once against the 1999 art so every item is viewed the way the sprites were drawn. The character's
  facing turns the model 45 degrees per direction inside that camera; a card may override the camera only
  as an explicit, visible exception.
- **MCP server** on its own port (proposed 4001; 4000 = Workbench, 8787 = kanban, 8791 = client
  bridge), same hand-rolled HttpListener / JSON-RPC / bearer-token design and Off / Observe / Full modes as
  the Workbench. Workshop rule carried over: every feature drivable and verifiable over MCP.
- **Every bake reports its cost** (Helbreath binding rule for derived bakes): frames rendered, ms total
  and per frame, supersample factor.

## 3. Repo layout (proposed)

```
helbreath-item-forge/
  src/ItemForge.Core/  src/ItemForge.App/
  reference/characters/   copied body / underwear / hair sprites (player + equip doll), PNG + frames json
  reference/legacy/       copied 1999 item sprites (e.g. Msw/Wsw look 3, pack, ground, equip) for seeding
  models/                 GLB inputs (section 10: license)
  cards/<card>.json       one per item
  output/<card>/legacy/   output/<card>/current/
```

## 4. The model card

`cards/<card>.json` holds everything that makes the bake repeatable:

- identity: card name, item type (one-handed sword, two-handed, bow, staff, axe, shield, ...; picks the pose
  baseline), notes. Which game items use the art is the import step's business, not the card's;
- model: GLB path + content hash (a changed model is flagged, not silently re-baked), grip point and grip
  axis on the model, base orientation fix-up, material switches (ignore emissive, normal map on/off,
  flat roughness), light direction / ambient;
- `worn`, `equip`, `inventory`, `ground` blocks (sections 5-6), each with its own transform, occluders
  and cut overrides;
- output: supersample factor, pixel-art switches (alpha threshold, outline, colour reduction).

## 5. Worn presentation (animated, on the player)

**Frame space** (from the client, verified by research 2026-09-15): 2 sexes x 8 directions (0 = N,
clockwise) x 12 actions = 96 frames per direction, 768 per sex, 1,536 total. Every layer is blitted at the
same foot anchor; each frame has its own tight rect + pivot (`dest = anchor + pivot`, above = negative).

**Per-frame pose**: hand position (x, y relative to the foot anchor, sub-pixel), weapon orientation
(yaw / pitch / roll), and a visible flag. Each frame is set directly - no keyframe interpolation and no
onion skin (cut 2026-09-15) - with playback at the action's real frame period to check the motion.

**Layering, so a card's tweaks stay on the card**:
1. a *pose baseline* per sex per item type, seeded by measuring the 1999 sprites;
2. the card's own overrides on top - a global offset / rotation / scale, per-direction offsets, and
   per-frame edits. Editing a card never changes the baseline; re-seeding a baseline is an explicit action.

**Seeding from 1999** (user choice): measure hilt position and blade angle per frame from the chosen 1999
weapon look (principal axis of the opaque pixels; hilt = end nearest the hand). Measured 2026-09-15 on
longsword look 3: all 7 slots have real art for both sexes (8 frames, hit = 4), frames are thin elongated
shapes (e.g. 12x42, 47x27), and the sexes differ - male peace-stand carries the blade upright past the
head (pivot y -66 vs body top -56), female carries it horizontal (40x12). Actions 1999 never drew seed
from the nearest drawn pose and are then hand-placed.

**Behind / in front**: the client puts main-hand and two-handed weapons behind the body at N, W, NW and in
front otherwise (`player_renderer.cpp`, the weapon_behind_body rule). The editor previews with that rule;
it is not baked into pixels.

**Backdrop**: body + underwear + hair composite for the selected sex / action / direction / frame,
optional ghost of the 1999 weapon frame, foot-anchor crosshair.

### Occlusion - the body cutting into the model

Applies to **Worn and Equip** (user requirement 2026-09-15: the hand or other body parts must pass in
front of part of the item). The item is one flat layer drawn wholly in front of or wholly behind the body,
so the only way a hand can wrap a hilt is for the item's own pixels to be CUT where the hand is, letting
the body show through. The bake cuts them, from two sources combined:

1. **Occluder proxies in the 3D view** - simple depth-only shapes (capsule / box / sphere): a hand
   capsule parented to the grip, and optional forearm / torso / leg shapes a card adds where needed. They
   write depth but no colour, so any part of the model behind a proxy is not drawn. Because the hand proxy
   is attached to the grip it follows every pose automatically; per-frame tweaks are ordinary per-frame edits.
2. **The backdrop sprite's own silhouette** - a cut is applied only where the body frame (or doll
   figure) is actually opaque. So the hole takes the shape of the drawn hand, not of the capsule: the proxy
   decides WHERE the body is in front, the sprite decides the exact pixels.

Final cut per pixel = (a proxy is nearer the camera than the model) AND (the backdrop body has a pixel
there). A per-frame **paint override** (add / erase cut, brush) covers the cases neither source gets
right. The editor shows the cut live as a tinted overlay, and the Bake preview shows the result with the
backdrop on or off.

Limits to keep in view:

- Cutting only works when the item layer is drawn IN FRONT of the body. At the facings where the client
  draws the weapon behind (N, W, NW) the body already covers it; the reverse - item in front of the body
  at those facings - cannot be done by cutting. The placement json carries a per-frame `draw` value; a
  later client change could honour a per-frame override instead of the facing rule. That is client work,
  coordinated separately, and out of scope for the tool.
- The cut silhouette comes from the bare body frame. Worn arm armour / gloves sit over roughly the same
  hand shape, so the result should hold, but it is to be checked on armoured characters.
- Hypothesis to verify once the 1999 weapon art is copied in: the 1999 artists cut the hilt pixels under
  the hand by hand the same way, which would make this the authentic look rather than an invention.

## 6. Equip, Inventory and Ground presentations

Each is a **single static frame** (Equip: one per sex; Inventory and Ground: one, gender-neutral) with its
own fixed camera, its own transform on the card (scale, size, position, rotation) and a backdrop.

| presentation | 1999 source | current (items.hba) | how the client places it | backdrop |
|---|---|---|---|---|
| Equip | `item-equipM.pak` / `item-equipW.pak` | `sprites/item_atlases/02` | doll anchor + (slot anchor - (171, 290)) + pivot | `equip_doll_{male,female}` body / underwear / hair |
| Inventory | `item-pack.pak` (sprite, frame) | `sprites/item_atlases/01`, trimmed | panel + the item's bag position + pivot, no centring | bag slot |
| Ground | `item-ground.pak` (sprite, frame) | `sprites/item_atlases/00`, trimmed | 1999: tile + pivot. Remake: centred on the tile centre, pivot IGNORED | map tile |
| Worn | `Msw` / `Wsw` style looks (section 7) | `sprites/items/<model>/00..23` | foot anchor + pivot | body / underwear / hair |

- **Equip is static and uses the section 5 occlusion** - proxies placed against the doll figure plus the
  doll's silhouette, and the paint override. No playback.
- **One (sprite, frame) pair picks all three icon views in 1999**; the remake's `items.json` `displays[]`
  gives `ground_frame`, `pack_frame`, `equip_frame` per sex. Icons (pack, ground) are gender-neutral;
  doll and worn art follow the wearer's sex.
- **Doll slot anchors** (1999 table, reused by the remake): left hand (90, 170), right hand / two-handed
  (57, 186), female right hand (60, 191); doll body drawn at (171, 290).
- **Reference sizes, LongSword**: pack 45x79 pivot (-25, -44) in 1999, trimmed to 27x67 (-15, -35) now;
  ground 32x24 (-19, -13), trimmed to 25x20 (-12, -10); doll male 53x69 (-5, -62), female 48x61
  (-4, -55), identical in both. Pivots are about minus half the size, i.e. art centred on the anchor.
- **Output per presentation**: the model-only frame PNG + a placement json relative to its backdrop anchor
  (doll anchor, bag slot origin, tile centre), in both layouts - legacy as stBrush
  `{x, y, w, h, pivotX, pivotY}` with the 1999 padding convention, current as a tight-cropped frame with the
  padding folded into the pivot. These are single frames meant to be APPENDED to the shared atlases;
  repacking an atlas and pointing `items.json` at the new frame index is the import step, not the tool's.
- **Colour**: 1999 tinted item art at runtime (per-channel additive offset by `m_cItemColor`); colour
  variants were never separate frames. The remake has no item colour at all. So the tool always bakes
  NEUTRAL art and never bakes a tint.

## 7. Output formats

**A bake renders the 3D model ONLY** (user rule, 2026-09-15). The character sprite, doll, bag slot or tile
behind the model is an editing backdrop and never reaches an output pixel. Output frames are the model
alone on transparent, tightly cropped, with occlusion cuts applied.

**Pivots are derived, never hand-set** (user rule, 2026-09-15). For every character action set (one sex x
one action) the bake writes a placement json alongside the PNGs, giving for each direction x frame where
the item frame sits relative to the character sprite:

```json
{
  "card": "dark_angel_sword", "sex": "male", "action": "stand_peace",
  "frames_per_direction": 8,
  "frames": [
    { "direction": 0, "frame": 0,
      "item_rect":   { "w": 12, "h": 42 },
      "pivot":       { "x": 0, "y": -66 },
      "body_offset": { "x": 14, "y": -10 },
      "draw": "behind",
      "cut_pixels": 9,
      "visible": true }
  ]
}
```

- `pivot` - item frame top-left relative to the foot anchor, exactly the client's `dest = anchor + pivot`
  convention; this is what goes into the frame tables below.
- `body_offset` - the same position relative to the character body frame's top-left for that
  direction x frame (body pivot taken from the copied reference art), so the placement can be checked or
  re-applied against the character sprite without re-deriving it.
- `draw` - behind / front as the client's facing rule resolves it (informational; not baked in).
- `cut_pixels` - how many model pixels occlusion removed, so an unexpected cut is visible in review.

Equip, Inventory and Ground write the same shape with one frame, relative to their own backdrop anchor.

**How 3D placement becomes a pivot** (asked 2026-09-15 - it is exact, not estimated):

1. The backdrop sprite frame is drawn at `foot anchor + body pivot`. In the 3D view it is a plane in the game
   camera with the foot anchor at the world origin, at the camera's fixed pixels-per-unit.
2. The game camera is orthographic, so projection is linear: every 3D point lands on one exact pixel
   position relative to the foot anchor - the same pixel space the sprite frames live in.
3. Bake renders the model alone through that camera and takes the tight bounding box of its final opaque
   pixels (after the pixel-art pass, so an outline is included). The box's top-left relative to the anchor
   IS `pivot`; `body_offset = pivot - body pivot`.
4. It inverts: a typed pixel position maps back to a 3D position on the chosen depth plane, so dragging in 3D
   and typing pixel values always agree. Depth toward the camera never moves a pivot; it only drives
   occlusion.
5. The one quantization is snapping to whole pixels, on a grid anchored at the foot anchor, so the same pose
   always rounds the same way and bakes stay byte-identical.

Both output layouts below take their frame rects and pivots from these files, so the numbers are computed
once and cannot disagree between the legacy and current outputs.

**Legacy (1999 layout)** - one weapon look = 56 sprites = 7 slots x 8 directions, sprite =
`slot * 8 + direction`, per sex. Slots as drawn by the 1999 client (`reference/original/Client/Game.cpp`):

| slot | action | drawn by |
|---|---|---|
| 0 | stand, peace | `CGame::DrawObject_OnStop` |
| 1 | stand, combat | `CGame::DrawObject_OnStop` |
| 2 | walk, peace | `CGame::DrawObject_OnMove` |
| 3 | walk, combat | `CGame::DrawObject_OnMove` |
| 4 | attack | `CGame::DrawObject_OnAttack`, `OnAttackMove` |
| 5 | being hit (4 frames) | `CGame::DrawObject_OnDamage`, `OnDamageMove` |
| 6 | run | `CGame::DrawObject_OnRun` |

1999 draws no weapon for peace attack, magic, pickup, dying or dead. Written as one PNG per sprite plus a
frame json of `{x, y, w, h, pivotX, pivotY}` (the 1999 stBrush fields), under `legacy/m/` and `legacy/w/`.

**Current (this project)** - 24 sheets `00..23` (00-11 male actions, 12-23 female, action order of
`entity_action.h`), each sheet direction-major with one direction per row. Written as `NN.png` plus
`NN.frames.json` in the Workbench's `export_frames_json` shape
`{sheetW, sheetH, frames:[{index, x, y, w, h, pivotX, pivotY}]}`, so it imports with
`import_frames_json` unchanged. Actions the card hides get the client's placeholder (fewer than 8 frames
= skipped by the client).

## 8. UI (Workbench shell)

Frameless window, 34px title bar (icon, File / View / Tools menus, caption buttons), sidebar, editor, 24px
status bar with progress; the Workbench's `Hba*` colour tokens (dark + light), Inter, its button / tree /
dialog styles and cursors. No activity bar - the forge has one view (user decision 2026-09-15). Views:

- **Cards** (sidebar list; View > Card Gallery): gallery of card tiles with a live thumbnail; new / duplicate
  / delete.
- **Card editor**: tabs Worn / Equip / Inventory / Ground / Model. The centre of every tab is the **live
  3D view**: the item model in 3D, with the sprite for the current context drawn behind it as a backdrop
  plane at game scale (character frame, doll figure, bag slot, map tile).
  - Direct manipulation follows BLENDER (user direction, 2026-09-15): middle-drag orbits, shift-middle
    pans, the wheel zooms, Ctrl+wheel scales, and G / R / S start a modal move / rotate / scale with X / Y / Z
    to constrain an axis, Ctrl to snap, Shift for fine and Esc to cancel - with a numeric inspector on the
    right that mirrors every drag and accepts typed values. Undo / redo per card.
  - TWO cameras: **game** (the bake camera - fixed elevation, eight facings) where the viewport renders at
    game scale and magnifies by whole pixels, so the preview IS the bake output; and **free**, which orbits
    for inspection only and is never used by a render or a bake. The viewport fills the panel and draws the
    sprite frame as a guide rather than clipping to it.
  - Occluders: add / select / move proxy shapes in the same view; cut shown as a tinted overlay; brush
    for the per-frame paint override.
  - Worn: sidebar tree sex -> action -> direction; bottom frame strip + play at the action's real frame
    period.
  - Equip: male / female toggle, one static frame, no strip.
  - Model: grip point / axis picked by clicking on the model, material switches, light direction.
  - **Bake** button on the card: renders the proper output (supersample + pixel-art pass) of the model
    alone and shows it next to the live view at 1:1 and zoomed - optionally overlaid on the backdrop at
    its derived pivot, as a check only - so the result is verified before anything is written.
- **Bake all**: every card or a selection, output switches, cost report.

## 9. Phases

1. **BUILT 2026-09-15** (commit `75af234` in `helbreath-item-forge`): repo scaffold, Workbench-styled shell, card
   gallery + Details editor, card file load / save (atomic, unknown fields preserved, delete to `.trash`), model
   binding by path + SHA-256, MCP server on port 4001 with 15 tools and single-instance forwarding - verified by
   38 MCP checks plus a second-launch forward test. Trimmed the same day to the lean scope (section 1): item
   model / item ids removed, weapon class renamed item type, activity bar removed.
2. **BUILT 2026-09-15**: GLB loader (SharpGLTF; node transforms baked in, textures decoded and capped at
   1024), the orthographic game camera (8 facings, pixels per unit), the software rasterizer (depth buffer,
   bilinear texture, two-sided lambert, supersample + box downsample, tight opaque box -> pivot) and the
   Model tab - the live view at whole-pixel zoom showing the bake's own pixels, with scale / orientation /
   light on the card. Five MCP tools (get_model_info, set_card_tab, set_model_view, render_model,
   update_model_setup), 20 in all. The grip point moves to phase 4, where the hand proxy first needs it.
3. Copy reference art in (player + doll characters, 1999 item sprites); backdrop compositor.
4. Occlusion (proxies, silhouette cut, paint override) + Equip editor - the static case proves placement
   and cutting before animation is added.
5. 1999 measurement + pose baselines; Worn editor.
6. Inventory and Ground editors.
7. Exporters (legacy + current), placement json, Bake all, cost report.
8. First real card (Dark Angel Sword on the longsword model), imported to `items.hba` via the Workbench and
   checked in the client over the agent bridge.

## 10. Open questions and findings

- **License**: the first model (`low_poly_dark_angel_sword.glb`) is "Sketchfab Standard", not CC. That
  generally allows use inside a product but not redistributing the model file - keep `models/` out of any
  public remote.
- **Finding**: the remake's longsword art (retired assetc recipe) maps the hit action to a placeholder,
  but 1999 draws the sword in slot 5 while being hit - so the remake's sword vanishes on hit.
- **Finding**: `design/decisions/worn-equipment.md` says the art bakes the weapon's side per facing; the
  client now decides it in code (weapon_behind_body). A one-sentence fix was written and then backed out of
  the Helbreath tree on 2026-09-15 to keep forge work off its branch; still to be applied there.
- **Finding**: the remake draws ground items centred on the tile and ignores the ground frame's pivot
  (`in_game_screen.cpp`, ground item draw); 1999 honoured it. The placement json still records a pivot so
  either behaviour can be served.
- **Finding**: item colour (1999 `m_cItemColor` tint) does not exist anywhere in the remake - not in
  `items.json`, the client or the server ground-item structs.
- **Import gap**: the Workbench's `replace_frame` refuses a different frame size, and `claude_docs/assets.md`
  records no supported re-bake path. Getting baked frames with new rects into `items.hba` needs a verified
  Workbench route (whole-sheet replace + `import_frames_json`, or atlas append) before phase 8.

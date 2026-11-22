# PoseKit usage guide

Full guide to the PoseKit pose editor, the in-game (VR) editor and the pose loader. For building from source, see the [main README](../README.md).

GlovesTan3 drives the hand from a `HandPoseItem` placed as a direct child of the held object. That item holds `HandPose` skeletons: every hand joint, positioned in the gun's space. The mod only ships poses for its own AKS-74n. PoseKit lets you make poses for any gun.

| Part | What it does |
| --- | --- |
| Browser editor | 3D pose editor, served by the plugin at `http://127.0.0.1:48261/` (`editor/index.html` in the source; it needs internet for three.js). |
| VR editor | In-game panel (wrist menu **PoseKit: VR editor**) for posing without leaving VR. |
| Plugin | Loads pose sets and injects them into matching guns, parts, pumps and magazines. It also captures in-game poses, dumps the hand rig and glove, and serves models to the editor. |

## Workflow

1. **In game:** spawn the gloves (Wrist menu → Custom buttons → *Tan Gloves?*).
2. Press **PoseKit: Dump rig** (or `F8`). This writes `BepInEx/config/GlovesTan3Poses/rig.json`, the real GlovesTan3 skeleton.
3. Hold the gun so the physical fingers wrap it. Then press **PoseKit: Capture L / Capture R** (`F5` / `F6`). Holding the foregrip captures a foregrip pose.
   Each capture is saved in `GlovesTan3Poses/captures/` with the gun's colliders, readable meshes and key transforms (PoseOverride, trigger, safety…).
4. **In the editor:** import `rig.json` and the capture (or drag them onto the viewport). Then refine each slot:
   * finger curl and spread sliders, plus *All fingers*;
   * click a joint to rotate it with the gizmo, or type Unity local euler angles;
   * click the square at the hand root to move or rotate the whole hand (`W` / `E`);
   * *Mirror from other hand*, *Copy pose from…*, and the *Trigger preview* slider (blends Fire start → Fire end like the mod does).
5. Fill in the gun's **Item ID** (from the capture or the spawner). Then click **Export for game** and save the `.json` into `BepInEx/config/GlovesTan3Poses/`.
6. In game press **PoseKit: Rescan Folders** (`F7`) and grab the gun again.

## VR editor (in game)

Everything can be done without leaving VR. Open the panel with the wrist menu button **PoseKit: VR editor**, or `F11`. It's a Sodalite lockable panel: grab it to move it, and lock it in the air like the item spawner. Spawn the GlovesTan3 gloves first.

**How it works:** hold the gun with the hand you're posing, and press panel buttons with the other hand. Every change shows up on that hand straight away. **Save** writes the pose file (`Save *` means there are unsaved changes).

| Row | Buttons |
| --- | --- |
| Target & slot | **◀ Target ▶**: Gun, then each grabbable part (slide, bolt, charging handle… racking parts first), then Magazine, then **Empty hand** (the glove when it holds nothing; see Empty hand). **◀ Slot ▶**: Idle / Foregrip / Fire start / Fire end, L and R (parts have Idle only; magazines have Idle and Retention). A `*` on the slot means it has a pose. |
| Pose | **Capture hand** · **Reset** (neutral fingers) · **Copy Idle** · **Mirror L<>R** · **Clear slot** · **Undo** |
| Presets | Open · Relaxed · Grip · Fist · Point · Trigger |
| Finger | All (the four fingers) · Thumb · Index · Middle · Ring · Pinky |
| Knuckle | Whole · Base · Mid · Tip · **Step** (1 / 5 / 15 degrees or mm) · **Tip follows** (bending Mid also bends the tip ⅔ as much) |
| Rotate | Curl − / + · Spread − / + · Twist − / + (Whole finger: curl bends every knuckle; spread/twist turn the base) |
| Move hand | Left / Right / Down / Up / Back / Forward, along the gun's axes |
| Turn hand | Pitch / Yaw / Roll ±, around the wrist |
| Session | **Use held gun** (switch to the gun you're holding; saves the previous one) · **Save** · **Revert** (back to the saved file) · **Auto copy** (add similarly named guns) · **Freeze gun** (see Ghost hands) · **Hide panel** |

Buttons are colour-coded by group: blue = target & slot, teal = whole-pose actions, purple = presets, orange = finger, gold = knuckle & options, red = rotate, green = move hand, cyan = turn hand, grey = session. The selected finger and knuckle (and "Tip follows" when on) show in a lighter shade of their colour.

Placing the hand naturally:

1. Pick the slot, then **Clear slot**.
2. Grip the gun (or the slide, bolt or magazine) the way you want, and let GlovesTan3's physical fingers settle.
3. Press **Capture hand**. Gripping a part or a magazine switches the target to it automatically, and gripping the foregrip picks the Foregrip slot.
4. Refine with presets, fingers and the move/turn rows, then **Save**.

A slot that's empty when you press an editing button starts from Idle, or else from your hand's current grip.

## Loading models from H3VR

While the game is running, the plugin serves data to the editor on `http://127.0.0.1:48261` (this PC only, and only to the editor page it serves).

1. Start H3VR, then open the editor in your default browser in one of these ways:
   * the wrist menu button **PoseKit: Open editor**, or `F10`;
   * set `[Editor bridge] OpenEditorOnStart = true` to have it open by itself when the game starts;
   * or go to **http://127.0.0.1:48261/** yourself, in any browser (Firefox, Chrome, Edge).

    The plugin serves the editor itself, and it connects to the game automatically. Use this rather than opening `editor/index.html` from disk: the game refuses every other page, including the editor opened from disk. The editor file on disk still works for editing and file import/export without the game link.
2. Search any item the game knows about, including modded guns from OtherLoader, and click **Load**. The plugin loads its prefab without spawning it and sends back the model and markers. If the pose set has no Item ID yet, it's set too.
3. **Get rig** pulls the GlovesTan3 skeleton. **Capture L / R** captures whatever that hand is holding. Both need the gloves spawned.

### Sending poses to the game live

With the game link connected, there's no need to export files or reload:

* **Send to game** applies the pose set immediately and saves it as `GlovesTan3Poses/<name>.json`, so it's there next launch too.
* **Live sync** pushes every edit as you make it (sliders, gizmo, keyboard, presets, captures). It sends at most about 12 updates per second, and nothing is saved until you press Send to game. Hold the gun in VR while someone (or you, on desktop) drags the sliders, and the hand updates in place.

What happens in game:

* A gun or magazine that already has these poses is updated in place. GlovesTan3 re-reads the poses every frame, so you see it while holding the gun.
* If slots were added or removed, its `HandPoseItem` is rebuilt, and any hand locked onto the old one is moved to the new one.
* Objects that had no poses yet pick them up the next time you hold them.
* Renaming the pose set is fine: anything sharing its item IDs or magazine types is updated.
* Live updates that weren't saved last until the game restarts or you press **Rescan Folders**.

The bridge only accepts requests from the editor page it serves. Other websites can't read from it or send to it, and it never sends folder paths. It only writes pose sets into the pose folder, and a pose set can't take the name of PoseKit's own files there. Set `[Editor bridge] AllowLiveImport = false` to make it read-only.

### Sync with GitHub repo

**Sync with GitHub repo** (game link panel) downloads the pose `.json` files from the main folder of a public GitHub repository. The default is `LionHeartChiptuner/PoseKit-Poses`, and you can type another `owner/name` or paste its URL. It only reads from GitHub and needs no account.

* Your browser fetches the files. The game's own networking can't make secure (TLS 1.2) connections to GitHub.
* A review list shows every pose set. **New** ones are ticked. Ones that would **replace** a pose set you already have (same name or same guns) are left unticked, so your own edits aren't overwritten unless you choose to. Files that aren't valid pose sets are skipped.
* **Download ticked** sends each one to the game like *Send to game*: it applies straight away and is saved into `GlovesTan3Poses`.
* Unauthenticated GitHub requests are limited to 60 per hour; the editor says when the limit is reached.

#### Test poses (pull requests)

**Sync test poses (pull requests)**, under the sync button, does the same for poses that haven't been merged yet: it reads the repository's open pull requests (up to 30) and downloads the pose `.json` files each one adds or changes in the main folder, straight from that pull request's branch. Each row in the review list shows which PR it came from (number, author and title). The same ticking rule applies: new guns are ticked, ones that would replace your poses aren't. Test poses can still change before they're merged; sync the main repo again later to get the final versions. Pull requests whose fork was deleted are skipped. It only reads from GitHub. Each open pull request costs one extra request against GitHub's limit of 60 per hour.

#### Update check when the game starts

When H3VR starts, PoseKit checks the same repository for pose files you don't have yet, or newer versions of ones you synced. If it finds any, a window appears in front of you in game, listing what's new or updated:

* **Sync now** downloads them into your pose folder and applies them straight away. It never touches poses you made or edited yourself.
* **Sync + replace mine** (shown only when the repo has poses for guns you've posed yourself) also takes the repo's version of those. Yours are copied to `GlovesTan3Poses\backup\<date>` first.
* **Not now** asks again next time you start the game.
* **Skip this update** doesn't ask again until the repo changes.

Files that aren't pose sets (such as `rig.json`) are ignored. It only reads from GitHub. In `gt3posekit.loader.cfg` under `[Pose repo]`: `CheckOnStart` (default on) and `Repo` (default `LionHeartChiptuner/PoseKit-Poses`). What was last synced is remembered in `GlovesTan3Poses\repo-sync-state.dat`.

### Ghost hands

A see-through copy of the GlovesTan3 glove shows, in game, the slot you're editing, so you can check poses at true scale in VR while working in the browser.

* **What's shown:** the target and slot selected in whichever editor you used last, the browser editor or the VR editor. From the browser it's your current, unsaved pose. The ghost is display-only: nothing is applied or saved until you use Send to game or Live sync.
* **Where:** on the matching weapon (by item ID or object name) nearest the VR editor panel, or nearest your head when the panel isn't open. The gun doesn't need to be held: lay it on a table next to the panel. Part, Pump and Magazine targets appear on that part, the pump, or the nearest matching magazine.
* **Toggle:** **Ghost hands in game** in the browser editor's game link panel. In `gt3posekit.loader.cfg` under `[Ghost hands]`: `Enabled`, `Opacity` (default 0.35), and `ColourLeft` / `ColourRight` (`#RRGGBB`).
* **Freeze gun in place:** **Freeze that gun in place** in the browser editor, or **Freeze gun** in the VR editor. The weapon showing the ghost loses gravity and stays exactly where it is whenever nobody holds it, so you can let go of it mid-air and pose around it. Grabbing it works as normal; letting go freezes it again where you released it. A magazine loaded in a gun freezes the gun. Turning it off, or the ghost moving to another weapon, gives the weapon its normal physics back. Config default: `FreezeWeapon` (off).
* Needs the gloves spawned, since the ghost uses the real glove model. Empty slots show no ghost.

Parts whose meshes the game has locked (Read/Write disabled) show up as solid bounding boxes, so the gun's shape is still visible.

Without the link, **PoseKit: Export held model** (`F9`) writes `GlovesTan3Poses/models/<ItemID>.json`, which you can import like any other file.

Notes:

* If Chrome asks to let the page access devices on your local network, allow it, since that is how it reaches the game.
* To change the port, edit `[Editor bridge] Port` in `gt3posekit.loader.cfg` and open the editor again with **PoseKit: Open editor** (or at `http://127.0.0.1:<port>/`). Set `Enabled = false` to turn the bridge off.

## Auto copy to similar guns

Under **Weapon**, **Auto copy to similar guns** gives the current poses to every firearm in the game with a very similar name. It does this by adding them to the pose set's item IDs (needs the game link). Then **Send to game**, or let Live sync apply it. **Review…** shows each candidate with its score so you can tick or untick guns, or lower the threshold for near misses.

How names are compared (item IDs and display names, best of both):

| Example | Score | Copied? |
| --- | --- | --- |
| G17 ↔ G18, Glock17 ↔ Glock19, P226 ↔ P228 | 0.90 (only a number differs, by ≤ 2) | yes |
| G17 ↔ G17L, MP5 ↔ MP5K, M4 ↔ M4A1 | 0.85 (same name plus a suffix) | yes |
| G17 ↔ G26, M4 ↔ M14, M1911 ↔ M1903, AK47 ↔ AK74 | ≤ 0.71 | no |

There's also an optional in-game fallback, off by default: `[Auto copy] InGameFallback = true` in `gt3posekit.loader.cfg`. With it on, any gun without poses silently borrows the poses of the most similarly named gun that has them.

## Glove model

**Get rig** (game link) also loads the real GlovesTan3 glove mesh. It's skinned to the rig joints, so it bends with every pose you make. Offline, **PoseKit: Dump rig** writes `glove.json` next to `rig.json`; import both.

* The plugin bakes the glove as it currently looks in game and sends its texture. It uses the model's own bone weights when the mesh is readable. Otherwise it estimates them from each vertex's distance to the bones, and the editor says "bending estimated from bones".
* **View → Glove model** toggles it. **Bones inside the glove** shows the skeleton too. Joints draw small and on top of the glove so you can still click them.
* The other hand's ghost shows its glove semi-transparent.
* The glove isn't kept in browser storage when that's full. Save the project, or press Get rig again after reopening the editor.

## Moving the hand

The viewport toolbar has three gizmo modes:

| Mode | Key | What the gizmo does |
| --- | --- | --- |
| **Rotate joint** | `E` | rotates the selected joint |
| **Move hand** | `W` | moves the whole hand; arrow keys / PgUp / PgDn nudge it in mm |
| **Turn hand** | `R` | turns the whole hand around the selected joint, or around the wrist when nothing is selected |

Move and Turn work from any joint. The selected joint is the pivot, so you can, for example, turn the hand around the index fingertip resting on the trigger. The game's hand rig has its root frame about 14 cm behind the wrist, near the controller. That frame is the small square, and you no longer need to grab it.

## Finger controls

Every finger joint is controlled by **curl**, **spread** and **twist** (for the thumb: curl, **swing** and **roll**). These values are read back from the joint's actual rotation, so the sliders, the gizmo, the typed angles and the keyboard always agree. Changing one axis never resets the others.

* **Presets**: Open, Relaxed, Grip, Fist, Point, Trigger. Each sets every knuckle's curl (and the thumb's swing) as a starting point.
* **Finger sliders** curl a whole finger. They shift every knuckle by a weighted amount (the tip bends 80% as much), so your per-knuckle tweaks are kept. **All fingers** does the same for every finger except the thumb.
* **▸** next to a finger expands it to show each knuckle (Base / Middle / Tip, plus the knuckle root on 4-joint rigs) with its own curl, spread and twist. Click a knuckle name to select that joint.
* Selecting any finger joint also shows its curl / spread / twist under **Selection**.
* Double-click any slider to reset that axis to 0.
* **Limit to natural range** clamps every axis to a plausible range for that knuckle (on by default). The gizmo isn't clamped.
* **Tip follows middle knuckle** bends the last joint to about ⅔ of the middle one, like a real finger.

Keyboard (with a joint selected; the step is set by *Keyboard step*, and `Shift` multiplies it by 5):

| Selected | ↑ / ↓ | ← / → | PgUp / PgDn |
| --- | --- | --- | --- |
| finger joint | extend / curl | spread | twist |
| hand root | forward / back (mm) | left / right | up / down |

`[` and `]` jump to the parent / child joint, so you can walk down a finger.

## Pose slots

These are the slots GlovesTan3 1.1.1 actually uses:

| Slot | Used when |
| --- | --- |
| `IdleL` / `IdleR` | holding the gun with that hand |
| `ForeL` / `ForeR` | holding its foregrip / alternate grip |
| `FireStartL/R` → `FireEndL/R` | blended by trigger pull (if *Blend trigger finger* is on) |

A side with no `Idle` pose falls back to GlovesTan3's physical fingers.

### Mag release

The gun's **Mag release** slots (L/R) hold the pose for pressing the magazine release, for example the thumb on the button.

* When the magazine leaves a held gun by the button, the holding hand blends into this pose and back over about half a second. The blend takes 0.08 s in, holds for 0.22 s, and takes 0.18 s out.
* It doesn't play if the other hand pulls the magazine out.
* GlovesTan3 has no working slot for this, so PoseKit plays it by blending the hand's current Idle / Fire poses for that side, then restores them.
* **VR editor:** while the Mag release slot is selected, the gun you're holding shows that pose continuously, so you can see what you're editing.
* Capture works as usual: select the Mag release slot, place your thumb, press **Capture hands**.

### Retention reload (magazine)

The magazine's **Retention** slots (L/R) hold the pose for carrying two magazines in one hand, for example the fresh magazine gripped normally and the spent one tucked against the palm with your little finger.

* It needs **Mag palming** turned on in H3VR's options (Options → Guns). Then, while holding a magazine, grab a second one with the same hand and the two stack together.
* While the held magazine has a second one palmed with it, the hand blends into this pose (0.1 s) and back when the stack splits.
* GlovesTan3 has no slot for this, so PoseKit blends the magazine's Idle pose for that side, then restores it.
* **VR editor:** choose the **Magazine** target, then the Retention slot with **◀ Slot ▶**. While it's selected, the magazine you're holding shows that pose continuously.
* **Capture:** hold the two palmed magazines and press **Capture hands**. The capture goes to Retention automatically. The browser editor also picks Retention for a palmed capture.
* A side with no Retention pose keeps its normal magazine pose.
* **Where the palmed magazine sits:** H3VR puts the second magazine right beside the held one, at the same angle. You can move and turn it per hand. On the Retention slot, the **Copy Idle** button becomes **Adjust: hand / palmed mag**. With *palmed mag* selected, the **Move** and **Turn** rows move and turn the palmed magazine (in the held magazine's axes) instead of the hand, by the current step. **Clear slot** puts it back where H3VR puts it. Hold the two palmed magazines while you adjust to see the result live. It's saved with the pose set (`magazine.palmed` in the file).
* **Ghost palmed magazine:** while a Retention slot is selected (with ghost hands on), a see-through copy of the magazine shows where the palmed magazine will sit, in a lighter shade of the hand's ghost colour. It updates as you move and turn it, and shows even before the Retention hand pose exists. It hides while a real magazine is palmed with the one you're posing on, since the real one already shows the spot.

### Support grip (pistols and other guns without a foregrip)

On guns with no foregrip, the **Fore / Support** slot is the two-handed support grip. It follows H3VR's own two-handed stabilization:

* While one hand holds the gun, bring your empty free hand right next to it, palm within 8 cm of the shooting hand's palm (the `Range` setting). No button is needed. H3VR must also be stabilizing the gun, which it does up to 15 cm.
* That hand then shows the gun's Fore pose for its side, snapped onto the gun.
* Move the hand away and it lets go. Pressing grip on the gun still grabs it and swaps hands, as normal.
* To pose it, stabilize as above and capture: **Capture hands** in VR, or Capture L/R. It goes into Fore L/R.
* Pistols (handguns, revolvers, derringers) always get it, even when H3VR gives them a `_ForeGrip` grab point for two-handed holds. Other guns get it only when they have no forward grip. Rifles with a foregrip or a pump are unaffected: there, Fore is the foregrip as before.
* A hand holding a flashlight or magazine still stabilizes in H3VR, but keeps its own pose.
* Settings in `gt3posekit.loader.cfg`: `[Support grip] Range` (default 0.08 m, up to 0.15; smaller is less sensitive; it lets go at 1.25× that) and `Enabled`.

### Slide racking and other parts

Use the **Editing** picker to choose a grabbable part of the gun: slide, bolt handle, charging handle, pump and so on. The part list comes from a model loaded from the game or from a capture. Each part has its own **Grabbing** pose for each hand.

* The pose is stored relative to the part, so in game the hand travels with the slide as you rack it. The small axes show the part's frame.
* To capture one, grab the slide or handle in game and press **Capture L/R**. The capture is assigned to that part automatically and converted into its frame, even if the slide was pulled back at the time.
* *Copy pose from…* can copy a pose from another target (for example the gun's Idle). The hand stays in the same place on the gun.
### Pump

Guns with a pump (shotgun `TubeFedShotgunHandle`, or a moving fore-handle such as `ClosedBoltForeHandle`) get a **Pump** target, in both the browser and VR editors.

* Its left and right poses are stored relative to the pump, so in game the hand slides with it as you pump.
* **Capture** while gripping the pump goes to Pump automatically.
* The loader finds the gun's pump by itself: shotgun pumps first, then moving fore-handles. In game, the pump's Foregrip slots reuse the same pose, because GlovesTan3 treats a held pump as a foregrip.
* Plain fixed forward grips still use the gun's **Foregrip** slots.
* Projects from the earlier build that posed the pump as an ordinary part are moved into Pump automatically when the gun's model is loaded.

**Pump-action guns: a foregrip hold is the pump.**

* **In game:** if a pump gun has no Pump poses, its Foregrip poses are used on the pump (converted relative to it), so they slide with it.
* **In the editors:** the gun's Foregrip slots show **→ Pump** and open the Pump target. Existing Foregrip poses on a pump gun are moved into Pump, keeping the hand in the same place.
* **VR Capture hands** records every hand at once into whatever it's holding (grip → Gun, pump → Pump, slide/bolt → that part, magazine → Magazine), so the selected slot's side doesn't matter.
* **One-handed by the foregrip:** H3VR treats this as an "alternate hold" of the gun itself, not of the pump, and GlovesTan3 then uses the gun's Foregrip slots. On a pump gun, those slots are filled from the Pump pose, placed where the pump rests, so the hand stays on the pump instead of jumping to Idle (the trigger). Capturing during an alternate hold goes to Pump, or to Foregrip on guns without a pump.

GlovesTan3 picks a hand's poses once, when the grab starts, and keeps them while the hand stays near the gun. To stop a hand getting stuck on the wrong set (for example the gun's Idle pose while you work the pump), the plugin checks each frame what each glove's hand actually holds. If that object has poses attached directly to it, the plugin switches the glove to them. Hands are matched to gloves the same way GlovesTan3 does it.

### Magazines

Choose **Magazine** in the picker. These poses are used whenever you hold a matching magazine, whether loose or pulled from the gun.

* Matching uses **magazine types** (every magazine of that type, filled in automatically when you load a gun's model) and/or magazine **item IDs**.
* Load a magazine's model from the game link (category *Magazine*, or *Load a compatible magazine's model*). You can also capture while holding one.

In game, the loader adds a `HandPoseItem` under the slide or handle's `FVRInteractiveObject.GameObject`, or under the magazine. That is exactly where GlovesTan3 looks when you grab it.

### Empty hand

Your own poses for the glove when it's holding nothing, in place of GlovesTan3's built-in idle hand. They switch with what your hand does on the controller, blending over a few hundredths of a second:

| Gesture | When |
| --- | --- |
| **Idle** | grip not pressed |
| **Grip** (a fist) | grip pressed, finger on the trigger, thumb resting on the stick, pad or buttons |
| **Point** | grip pressed, finger off the trigger, thumb resting |
| **Thumbs up** | grip pressed, finger on the trigger, thumb lifted |
| **Finger gun** | grip pressed, finger off the trigger, thumb lifted |

"Finger on the trigger" uses trigger touch where the controller has it (Index, Quest/Rift), otherwise a slight pull. "Thumb resting" uses stick, pad or face-button touch, or finger tracking on Index controllers. A grip press near an object still grabs it as normal.

They're made in the VR editor (not the browser editor yet), and need no gun:

1. Open the VR editor and press **Target ▶** until it shows **Empty hand**. Without a gun held, it's the only target.
2. Pick a gesture and hand with **◀ Slot ▶** (Idle, Grip, Point, Thumbs up, Finger gun; L and R). While you edit a slot, that hand shows it whatever your fingers do, so you can see it.
3. Shape it with presets, finger controls and the move/turn rows. **Capture hands** sets both Idle slots to GlovesTan3's own idle pose to start from.
4. **Save** writes `empty-hand.json` in the pose folder. **Clear slot** goes back to that slot's default, and **Revert** goes back to the saved file.

A gesture you haven't posed uses a built-in default (fist, point, thumbs up, finger gun) made from your Idle pose, shown as "built-in default" on the panel. An Idle you haven't posed is GlovesTan3's own idle pose.

The feature turns on for a hand as soon as any of its empty-hand slots has a pose. From then on that hand shows these poses instead of GlovesTan3's physics hand, so it no longer curls and opens freely with your grip and trigger when empty (its fingers still push things). Clear every slot for that hand to get the physics hand back, or turn it off without deleting anything with `[Empty hand] Enabled` in `gt3posekit.loader.cfg`.

### Glove skins

Put a new material on the GlovesTan3 gloves, for example one painted in Substance 3D Painter. The gloves use the Alloy/Core shader with a base colour and a normal map, but their packed map (Metal(R) AO(G) Spec(B) Rough(A)) is empty, which is why they look flat. PoseKit swaps in a new material instead of editing GlovesTan3's.

A skin is a folder in `GlovesTan3Poses/skins/<name>/`. Choose it with `[Glove skin] Skin` in `gt3posekit.loader.cfg`, or cycle through skins with the wrist menu button **PoseKit: Next glove skin**. **PoseKit: Rescan Folders** re-reads the files, so you can export again and reload without restarting.

There are two ways to make a skin:

1. **Texture files (no Unity needed).** PNG or JPG files, recognised by name:

   | Name contains | Map |
   |---|---|
   | `basecolor`, `albedo`, `diffuse` | base colour |
   | `packed`, `alloy`, `materialmap` | Alloy packed map: R metallic, G AO, B specularity, A roughness |
   | `normal` | normal map (OpenGL / Unity style) |
   | `emission`, `emissive` | emission |

   PoseKit builds a new material that starts as a copy of the glove's own (same shader and settings) and replaces whichever maps you provide. A folder with just a packed map gives the stock gloves their missing metal, AO, specularity and roughness, and keeps the original colour and normals. Add `"fresh": true` to `skin.json` to start from the shader's defaults instead. An optional `skin.json` sets a different shader, a tint, the normal strength, extra shader values or keywords. `skins/README.txt` describes it.
2. **A ready-made material.** Build an asset bundle in Unity 5.6 containing your material and its shader, and put it in the folder as `*.bundle`, `*.assetbundle` or a file named `skin`. PoseKit uses that material as it is.

**Skins from other mods.** Skin makers can release skins as their own mod. They put them in a folder named `GlovesTanSkins`, one subfolder per skin (same contents as above), for example `GlovesTanSkins/Camo/camo.bundle` at the root of their Thunderstore zip. PoseKit finds every `GlovesTanSkins` folder under `BepInEx/plugins` when the game starts, when you press **PoseKit: Rescan Folders**, and with the wrist button **PoseKit: Search for glove skins**, which also says how many skins it found and from which mods. They then show up in **Next glove skin** by their subfolder name. If two skins share a name, the mod's folder name is added in brackets. The skin mod needs no code, but players need PoseKit to wear the skins. The skin bundles shouldn't also be set up for OtherLoader, because Unity can't load one bundle twice.

When the gloves first appear, PoseKit writes `skins/glove-material.txt`, listing GlovesTan3's own shader and texture slots. `skins/applied-material.txt` lists the material the current skin actually put on the gloves. Each time a skin is applied, the BepInEx log says which slot each texture went into, or which maps the shader has no slot for.

## File formats

All positions and rotations use Unity coordinates, in metres, relative to the gun's root transform with its scale removed.

* `gt3poses`: exported pose set; the loader reads this. Matches guns by `match.itemIds` (FVRObject.ItemID) or `match.objectNames`. Version 2 adds `parts[]` (`path`, `type`, `poses`, in part space), `pump` (`poses`, in pump space) and `magazine` (`itemIds`, `magazineTypes`, `poses`, in magazine space).
* `gt3rig`: hand skeleton (`joints[i].parent`, with `local` relative to the parent joint or to the hand root frame).
* `gt3capture`: one hand's joints on a held object, plus its rig and reference geometry.
* `gt3model`: an item's reference geometry (meshes, locked parts as `meshbox` prims, colliders, markers).
* `gt3proj`: editor project (everything above plus slider state). The editor also autosaves it in the browser.

Hotkeys and mesh export limits are set in `BepInEx/config/gt3posekit.loader.cfg`.

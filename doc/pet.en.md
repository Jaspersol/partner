# Desktop pet

[简体中文](./pet.md) | [繁體中文](./pet.zh-TW.md) | **English**

Back to the [documentation index](./README.en.md).

A Live2D character that sits on the desktop. It takes no part in the conversation: it reads the assistant replies that are **already finalised** in the same session log, scores them by keyword, and plays fixed scenes. The system prompt is unchanged, the model is never asked for an emotion tag, and no extra model request is made.

## 1. What it is

- **A separate small window** — transparent, borderless, always on top, out of the taskbar, not resizable. 300×360 by default; once the model loads it resizes to the model canvas × the scale setting.
- **Read-only on the session** — polls `GET /api/pet/feed` once a second (every 250 ms while speaking) for replies after the last `seq` it saw. History is not replayed, so opening the pet does not set off a backlog of scenes.
- **Streaming text is ignored** — only a finalised assistant reply is scored; the half-written line does not count.
- **Shares your settings** — scale, idle range, sleep minutes, greeting times, emotion cooldown, expression hold, action mapping and the keyword dictionary all live under Settings → Desktop pet. Saving updates the pet window live; no restart.
- **What it does not do** — no speech bubbles, no conversation, no model requests of its own.

## 2. Installing the Cubism Core

The Core is Live2D's runtime. You download it from Live2D's own site and accept their agreement there.

Only **Cubism 5 SDK for Web R5** works, as the file `CubismSdkForWeb-5-r.5.zip`. Older Cores are incompatible with easy-live2d 1.x, so the import is strict:

| Check | Requirement |
|---|---|
| Whole-file SHA-256 | `67064a7fb1812cf502f5c4a03bfe12cc638c75a621bb4acf06bb28763df06ba0` |
| Entries extracted | The two whose paths end in `/Core/live2dcubismcore.min.js` and `/Core/LICENSE.md` |

The SDK's top-level folder name is not fixed, so entries are matched by suffix, not by full path. A hash mismatch is refused outright, which keeps an old Core or an edited script out of the page's `script-src`.

1. Under Settings → Desktop pet → Cubism Core, follow the download link to Live2D's site and fetch the R5 zip yourself.
2. Tick "I downloaded this from Live2D and accept their agreement"; the import button stays disabled otherwise.
3. Enter the zip's absolute path and press Import.
4. On success the page shows "R5 installed" and the first 8 hex digits of the hash.

Files land in `%LOCALAPPDATA%\Partner\pet\core\`; models in `%LOCALAPPDATA%\Partner\pet\models\<id>\`. A model id is the ASCII-fied folder name plus the first 8 hex of a hash over its file contents, so importing the same model twice does not leave a second copy.

## 3. Importing a model

Models are **not distributed with the app** and remain the copyright of their authors. You can import either a folder or a zip.

- **Path** — a folder or a zip. A zip whose Chinese file names carry no UTF-8 flag is decoded as GBK, so older packs still work.
- **Several model3 files** — when a pack holds more than one `*.model3.json`, the page lists the candidates (relative paths) and you pick one. A single one imports directly.
- **Only referenced files are copied** — the moc3, textures, physics, pose, expressions, motions and sounds listed in `model3.json`'s `FileReferences`. Unreferenced art and loose `exp_*.json` in the same folder stay behind.
- **Validation** — every path must be relative, free of `..`, inside the model root once normalised, and not a symlink or junction; extensions are limited to `.json .moc3 .png .jpg .webp .wav .mp3 .ogg .flac` (`.js` is refused); 200 MB total and 2000 files are the caps.
- **Re-importing** — identical content returns the existing entry instead of a second copy.
- The imported list shows name (the folder name), motion count, expression count, whether the model has hit areas, and size on disk. You can make one current, or delete it — including the current one, after which the pet shows nothing until another is chosen.

For a model without `HitAreas`, the head and face click zones are cut from the model's bounding box by vertical proportion; the sliders are on the Action mapping tab.

The window is cropped to the figure's own opaque extent rather than the whole model canvas. Authored canvases leave a lot of empty margin (one measured model drew its figure in 44%×47% of the canvas), so opening a window at the canvas size makes the pet look small and puts the zone proportions over empty space. After load the app renders one frame, reads the alpha channel back to find the figure's box, and opens the window at that box; `100%` means the figure is 360 logical pixels tall.

## 4. Scenes

The scenes are fixed. Only two kinds of trigger exist: local interaction or a timer, and a keyword match in a reply. Each scene is an action combination plus an expression; actions resolve through the current model's mapping first, fall back to procedural parameter tracks, and skip when the parameter does not exist.

In Procedural action tuning, map standard channels to actual parameter IDs per model. The cdi3 names are suggestions, not automatic guesses or proof that a runtime parameter exists. Model and per-action gains default to 1, range from -4 to 4, and multiply; a negative value reverses the track. Model motions still take priority. Extra tracks use actual parameter IDs: their first and last values must be zero, times must increase strictly, and the last key must match the action duration. Values are clamped to the model parameter range. Save and open the pet, then read action diagnostics to see the loaded configuration's motion, procedural or skipped resolution, parameter ranges and missing IDs. Diagnostics are static candidates: motion priority can still refuse a motion and cause a fallback. Diagnostics and preview do not include unsaved edits. Automatic displacement calibration is not implemented.

| Scene | Trigger | Actions / expression | Priority |
|---|---|---|---|
| First load | after the model is ready | greet + blink / smile | 2 |
| Quiet | nothing else running | ambient micro-motion / default | 0 |
| Idle | quiet for `rand(IdleMin, IdleMax)` seconds | greet 45%, stretch 45%, blink 10% → back to quiet | 1 |
| Head tap | single click on the head | the model's own `TapHead`/`Tap` group when it has one; otherwise one of: head-pat reaction / head tilt / head shake. Expression either shy or angry | 4 |
| Face tap | single click on the face | the model's own `TapFace`/`TapHead`/`Tap` group when it has one; otherwise blink + slight lean back. Expression: smile or shy | 4 |
| Body tap | single click on the body | the model's own `TapBody`/`Tap` group when it has one; otherwise body sway or shrink. Expression: shy or angry | 4 |
| Hover | 600 ms of hovering | gaze following is always on; this scene only adds a slight tilt / default | overlay |
| Drag | drag starts | drag spring-back (speed-driven) / default; past the speed threshold it becomes surprise | 5 |
| Release after a fast drag | release speed past the threshold | head shake + body sway + spring-back / surprise | 5 |
| Double click | double click | greet + wave / smile | 4 |
| Voice or TTS | currently speaking | lip-sync + a slight nod every 3–5 s / current emotion or smile | overlay |
| Scheduled greeting | local time hits one of the greeting times (once per entry per day; wakes first if asleep) | greet + wave / smile | 2 |
| Idle timeout | no interaction and no new reply for `SleepAfterMinutes` | stretch → sleep / default or sleepy | 1 |
| Tap while asleep | any click while asleep | wake first (blink + eyes open), then the scene that tap would have played | 4 |
| Emotion: smile / angry / cry / shy | a reply matched, or the preview button in settings | wave or nod / pout / sob / head tilt or shrink; matching expression | 3 |

The rules behind that table:

- A new scene must have priority **≥** the current one to pre-empt it; otherwise it is dropped. Emotion scenes are the exception: one is queued and runs when the current scene ends.
- Emotion scenes have a cooldown (`EmotionCooldownSeconds`). The same emotion triggering again only refreshes the expression hold; the motion is not replayed.
- While asleep only a click wakes the pet, and the wake scene plays *then* the tap's own scene, so the first click is never a non-event. A new reply or a scheduled greeting wakes it first and then runs its scene. Dragging still works and does not wake it.
- While asleep only the eyes follow the cursor: the head stays put and there is no speaking nod, because the held `sleep` track owns the sleeping pose and anything else writing those parameters fights it.
- The cursor look turns the head at most 40% of the parameter's half range (±12° on a ±30° parameter); the eyeballs follow fully. Outside the window the look fades with the cursor's distance and is gone one window width away. While a procedural track or a model motion drives the head, the look's head share drops to 20% and returns within 0.3 s afterwards.
- Procedural tracks, the look and the drag recoil are offsets on top of the model's own motion, blink and breath, written before physics, so hair and physics smoothing chains follow them. This needs a patch to easy-live2d 1.1.0 (`ui/patches/easy-live2d+1.1.0.patch`, applied by patch-package on `npm install`).
- Sleep does not currently slow breathing. easy-live2d 1.1.0 advances Framework `CubismBreath` inside its effects update; the existing pre-physics hook sees already-combined parameters and cannot safely change breathing speed independently of motion or physics.
- An expression returns to default after `ExpressionHoldSeconds`.
- The random source and the clock are injected, so tests can pin both.

## 5. The emotion keyword dictionary

Under Settings → Desktop pet → Emotion dictionary each of the four emotions has an editable table, alongside the negation prefixes, the minimum weight and "restore defaults". The same `emotion.ts` scores both the pet and the try-out box on that tab, so what the box shows is what the pet does.

How a match is decided:

1. Preprocess: drop code blocks, inline code, URLs and Markdown link targets; lowercase English; keep at most 4000 characters.
2. Keywords are searched longest first, and spans already taken by a longer keyword are not counted again — so `不开心` never also counts as `开心`. English needs word boundaries; Chinese and emoji match as substrings.
3. A negation prefix (`不 没 别 不太 并不 not no never`) within the two characters before a hit voids that hit, unless "prefix + keyword" is itself in the dictionary.
4. For each emotion the **highest weight among its hits** is its primary score, and the highest primary score wins. Ties compare that emotion's summed hit weights; a remaining tie goes to the emotion whose last hit is later in the text.
5. A primary score below `MinWeight`, or no hit at all, triggers nothing.

The default dictionary (weights 1–5; editable on the tab):

| Emotion | Keywords (weight) |
|---|---|
| Smile | 哈哈(3) 嘻嘻(3) 好笑(3) 开心(3) 高兴(3) 太好了(4) 恭喜(4) 棒(2) 不错(1) 喜欢(2) 有趣(2) 😄(4) 😊(3) 🎉(4) haha(3) great(2) awesome(3) glad(2) |
| Angry | 生气(4) 气死(5) 可恶(4) 讨厌(3) 烦(2) 不能接受(3) 过分(3) 哼(3) 😠(4) 😡(5) angry(4) annoying(3) |
| Cry | 难过(4) 伤心(4) 哭(4) 呜呜(4) 抱歉(2) 遗憾(3) 可惜(2) 不开心(3) 失败(2) 😢(4) 😭(5) sorry(2) sad(4) unfortunately(2) |
| Shy | 害羞(4) 不好意思(3) 脸红(4) 过奖(3) 哪里哪里(3) 被夸(3) 讨厌啦(4) ///(3) 😳(4) ☺️(3) blush(4) flattered(3) |

`讨厌啦` (shy) is longer than `讨厌` (angry), so longest-first matching keeps it out of angry.

Example: 「哈哈，这个太好笑了，不过被你夸得有点害羞」 → smile {哈哈 3, 好笑 3}, primary 3; shy {害羞 4}, primary 4 → the shy scene runs.

## 6. Licence

Live2D's code **cannot** be relicensed as MIT. This repository's `LICENSE` covers our own code only.

| Object | Licence | Notes |
|---|---|---|
| Cubism Core (`live2dcubismcore.min.js`) | Live2D Proprietary Software License | The user downloads it from Live2D and accepts the agreement locally |
| Cubism Framework (bundled inside easy-live2d) | Live2D Open Software License | Ships inside the pet frontend bundle; the copyright notice is preserved in the built chunk by a build banner |
| easy-live2d, Pixi.js, @pixi/sound and their transitive dependencies | MIT / ISC / BSD-3-Clause | See [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) |
| Model files | Copyright of each model's author | Not distributed; importing happens on your machine, and neither the repository nor the installer carries a model |

What we do about it:

- The texts of both Live2D agreements live in `third-party/` and are copied into the installer's `notices/licenses/` at build time; `THIRD-PARTY-NOTICES.md` ships with the installer too.
- The build adds a banner to the pet chunk (`Copyright(c) Live2D Inc.` plus the licence URL), because a minifier strips comments that are not `/*!`, and the Open Software License forbids removing the copyright notice.
- Settings → Desktop pet ends with the Live2D attribution and links to both agreements.
- Importing the Core requires ticking the agreement first; importing a model does not, because that file is yours.

## 7. Known limits

- **The mouth has no audio** — TTS plays through NAudio on the .NET side and the WebView never sees the stream, so lip-sync is a 6–9 Hz random envelope while `speaking` is true, not real phonemes. The backend reports speaking state from playback; that says it is talking, not which syllable.
- **Actions fall back to procedural tracks** — you cannot assume a model carries the motions or hit areas a scene wants. The sample model ships two motion groups, no `Idle` group, no `HitAreas`, and no physics or pose, so most scenes land on parameter keyframes (`ParamAngleX/Y/Z`, `ParamBodyAngle*`, `ParamEye*`, `ParamMouthForm`, …). A parameter the model lacks is skipped rather than forced, and no error is raised. The Action mapping tab lets you pin each entry; leaving it on "auto" is what makes it guess by name.
- **Sound is opt-in** — motion sound is off by default; when on it follows `Config.MotionSound`.
- **The window has little power** — it may move, resize, read the cursor and toggle click-through. It cannot close itself or start with Windows. Closing the main window, or Quit in the tray, ends it too. Tray Quit hides the icon before backend cleanup finishes and leaves the tray preference unchanged.
- **One screen at a time** — position restores from `pet-window.json`, and a position outside every visible area is moved to the main display's bottom right. Manual smoke at 125% / 150% scaling and on a secondary monitor with negative coordinates is still outstanding.
- **Click-through by pixel** — every 50 ms the alpha under the cursor decides whether the window passes clicks through; dragging forces it off. After a model switch, the new input layer waits for any old window command and synchronizes click-through to the new settings; failed commands are retried on a later poll. A model exported without `premultipliedAlpha` can show dark edges.
- **Cost** — capped at 30 fps and the ticker stops while the window is hidden. Heavy models (many large textures) drop frames while being dragged.
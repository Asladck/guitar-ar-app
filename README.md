# Guitar AR

A mobile augmented-reality app (Android + iOS) that hangs a guitar on a person in front of the camera,
or puts it on a real surface around you.
Built with Unity 6 LTS, Universal Render Pipeline and AR Foundation (ARCore on Android, ARKit on iOS).

## What's here

| Path | What it is |
| --- | --- |
| `Packages/manifest.json` | Unity packages: AR Foundation, ARCore, ARKit, URP, Input System, uGUI |
| `ProjectSettings/ProjectVersion.txt` | Editor version (Unity 6000.0 LTS) |
| `Assets/Scripts/ProceduralGuitar.cs` | Builds a simple guitar from primitives (placeholder until a real model is imported) |
| `Assets/Scripts/GuitarPlacer.cs` | Tap a detected plane to place the guitar; tap again to move it |
| `Assets/Scripts/GuitarWearer.cs` | Shared logic for wearing the guitar on a torso: pose, smoothing, pop-in animation |
| `Assets/Scripts/GuitarBodyAttacher.cs` | Back camera: hangs the guitar on a person found by ARKit body tracking |
| `Assets/Scripts/GuitarSelfieAttacher.cs` | Front camera: hangs the guitar on you, estimated from your face |
| `Assets/Scripts/GuitarModeSwitcher.cs` | On-screen buttons switching between *on a person*, *selfie* and *on a surface* |
| `Assets/Editor/GuitarARProjectSetup.cs` | **Guitar AR → Set Up Project** menu: URP + AR background, player settings, AR scene |
| `.claude/skills/` | Unity's agent skills (`npx skills add Unity-Technologies/skills`) |

The project was hand-authored, so Unity generates the rest of `ProjectSettings/`, the `.meta`
files and `Library/` the first time it opens.

## First-time setup

1. Install **Unity Hub** and **Unity 6000.0 LTS** (6000.0.58f2 or newer) with the
   **Android Build Support** (incl. OpenJDK + SDK/NDK) and/or **iOS Build Support** modules.
2. In Unity Hub: **Add → Add project from disk** and pick this folder. If Hub asks about the
   editor version, pick your installed 6000.0.x.
3. Wait for packages to import. If the Package Manager offers newer AR Foundation / ARCore /
   ARKit 6.0.x patch versions, accept them.
4. Run **Guitar AR → Set Up Project** from the top menu. This creates and assigns the URP asset
   with the AR Background renderer feature, sets Android/iOS player settings and creates
   `Assets/Scenes/GuitarAR.unity`.
5. **Edit → Project Settings → XR Plug-in Management**: tick **ARCore** on the Android tab and
   **ARKit** on the iOS tab.
6. Commit the generated files (`Assets/**/*.meta`, `ProjectSettings/*`, `Assets/Settings`,
   `Assets/Scenes`, ...). `Library/` and `Temp/` are git-ignored.

## Run on a phone

- **Android:** File → Build Profiles → Android → Switch Platform, plug in a phone with
  developer mode on and ARCore support, then **Build And Run**.
- **iOS from the command line:** close Unity, connect the iPhone and run
  `Tools/build-ios.sh <device-udid> <apple-team-id>`. It exports the Xcode project, builds it and installs the app.
- **iOS:** switch to iOS, **Build**, open the generated Xcode project on a Mac, set your signing
  team and run on an ARKit-capable iPhone.

The app starts in **on a person** mode: point the camera at someone so their whole body is in
frame, and the guitar appears on their torso. Body tracking needs an iPhone with an A12 chip or
newer; it isn't available on Android.

**Selfie** mode uses the front camera: look into it and the guitar appears below your face. ARKit
can't track bodies with the front camera, so the torso is estimated from the face (needs Face
Tracking ticked under XR Plug-in Management → Apple ARKit).

In **on a surface** mode, move the phone slowly over a
floor or table until the translucent plane appears, then tap it.

ARKit can't track bodies, track faces and detect planes in the same session, so only one mode runs at a time.

## Next steps

- Replace the procedural guitar with a real 3D model (assign it to *Guitar Prefab* on
  `XR Origin → GuitarPlacer`).
- Pinch to scale / twist to rotate, and tap strings to play notes.

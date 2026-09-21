# Roblox and Blender animation workflows

DesktopUseAgent keeps Blender and Roblox Studio independent: Blender owns authored Actions and exports; Roblox owns references, runtime binding, playtests, and sequences. Use semantic adapter operations first, then normal DesktopUseAgent UI/vision fallback only where the applications do not expose a semantic path.

## Animation specification

`blender.animation_apply` accepts `{ armature, spec }`. A spec has `name`, `fps`, `duration`, optional `loop`, `priority`, `rootMotion`, `interpolation`, `poses`, and `markers`. Each pose supplies `time` (seconds) or `frame` and a batch of named bone transforms. Markers are named gameplay synchronization points; use them instead of sleeps.

Typical flow: inspect the rig with `blender.animation_inspect`; apply one complete spec; render deterministic QA frames with `blender.animation_preview`; validate required bones and the exported output with `blender.asset_validate`; export with `blender.export_for_roblox`.

## Studio integration

Keep animation IDs in `ReplicatedStorage.DesktopUseAgentAnimationManifest`, created by `roblox.animation_configure`; do not scatter IDs in gameplay code. Use `roblox.animation_marker_add` and `roblox.animation_bind` to register marker-driven callbacks. `roblox.sequence_apply` stores a typed cinematic spec (animation, camera, tween, VFX, sound, UI, visibility, callback tracks) as centralized sequence metadata.

For each runtime change, start playtest, use `roblox.playtest_inspect` and `roblox.output_read`, then capture the Studio viewport using the normal `vision.capture_window` fallback. Validate state/diagnostics deterministically before visual review. Publishing/upload remains confirmation-gated by existing Studio controls.

## Recovery

All bridge commands return the existing request operation ID and structured errors. Animation apply/configure operations are idempotent by action/manifest name: reapplying replaces generated curves or metadata. Inspect before retrying a timed-out operation; do not assume a failed request did not reach the target.

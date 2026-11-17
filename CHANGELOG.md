# Changelog

## 2.0.0

- Preserve the horizontal movement bonus when walking or sprinting loses ground contact on descending stairs, slopes and ramps. Fall/landing states inherit the locomotion bonus until support returns or an excluded movement mode interrupts it.
- Add F12 jump-distance scaling: enabled by default, adjustable from 0% (no extra horizontal jump speed) to 100% (full movement bonus). Intentional jumps retain their setting through descent. Vertical input and gravity remain native.
- Freeze jump settings at raid entry, alongside the existing bonus snapshot.
- Set per-level HP defaults to head 1, each arm 2, each leg 3, stomach 4, thorax 5. Other bonus defaults are unchanged. Existing explicit settings are preserved.
- Give the client plugin, client assembly and server metadata version 2.0.0.


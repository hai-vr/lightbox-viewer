# Usage

## Testing materials with Lightbox Viewer

To begin, choose an object, and click the three vertical dots `⋮` next to the Transform component.

![image](https://user-images.githubusercontent.com/60819407/168523708-b1f94066-af60-49f2-9d04-73763eba20dc.png)

Then, press *Activate LightboxViewer*. You can now move the scene camera to reframe, move the object around, and do your lighting tests live.

While this mode is active, the editor may slow down. Press *Activate LightboxViewer* again when not in use to improve the performance of the editor.

https://user-images.githubusercontent.com/60819407/168524490-2a707241-5886-419c-8a1f-c371dca79da2.mp4

## Testing in Play mode

*Lightbox Viewer* can be used in Play mode. Press *Activate LightboxViewer* before entering Play mode.

https://user-images.githubusercontent.com/60819407/168525049-d6769b9d-0645-4e65-8258-a441367b67ed.mp4

## Post Processing

If Post Processing is not installed, you can press *Install Post-processing*. This lets you test color grading, bloom, and other effects.

https://user-images.githubusercontent.com/60819407/168525248-84a4dc17-df70-46e4-8365-e3ff81c4c6d3.mp4

# Advanced usage

Lightbox Viewer is shipped with some default lightboxes that you can test your content with.

These lightboxes is contained in a scene called `Lightbox.unity`.

## Mode of operation

When activating LightboxViewer, the following happens:

- The lightbox scene is loaded as an additive scene on top of the current scene.
- Light probes are re-applied (tetrahedralized).

When capturing lightboxes, the following happens:

- If there is a reference camera in the Advanced settings, this camera is temporaily copied, otherwise a new one is temporaily created.
- The camera copies the scene camera settings.
  - If there is a camera in the Advanced settings, the near and far clip plane are copied from that reference camera.
  - If not, the camera copies the near and far clip plane of the scene camera.
- All light sources in the scene are temporaily disabled, except those in the lightbox scene.
- All reflection probes in the scene are temporaily disabled, except those in the lightbox scene.
- All children of the object called `Lightboxes` in the lightbox scene are disabled.

For each child of the object called `Lightboxes`:

- That child lightbox is enabled. This effectively enables anything inside of it that can influence the render, such as real time lights or post-processing volumes.
- The object to be viewed is moved to that lightbox.
- The camera takes a picture.
- That child is disabled again.

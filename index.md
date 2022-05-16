# Usage

## Testing materials with Lightbox Viewer

To begin, choose an object, and click the three vertical dots `⋮` next to the Transform component.

![image](https://user-images.githubusercontent.com/60819407/168523708-b1f94066-af60-49f2-9d04-73763eba20dc.png)

Then, press *Activate LightboxViewer*. You can now move the scene camera to reframe, move the object around, and do your lighting tests live.

While this mode is active, the editor may slow down. Press *Activate LightboxViewer* again when not in use to improve the performance of the editor.

<video controls width="816" autostart="false">
    <source src="https://hai-vr.github.io/lightbox-viewer/videos/sx_2022-05-16_07-18-28_Sda2clkyuk.mp4" type="video/mp4">
</video>

## Testing in Play mode

*Lightbox Viewer* can be used in Play mode. Press *Activate LightboxViewer* before entering Play mode.

<video controls width="816" autostart="false">
    <source src="https://hai-vr.github.io/lightbox-viewer/videos/sx_2022-05-16_07-24-50_VDkm4dNnOs.mp4" type="video/mp4">
</video>

## Post Processing

If Post Processing is not installed, you can press *Install Post-processing*. This lets you test color grading, bloom, and other effects.

<video controls width="816" autostart="false">
    <source src="https://hai-vr.github.io/lightbox-viewer/videos/sx_2022-05-16_07-27-19_5hudaArHip.mp4" type="video/mp4">
</video>

# Advanced usage

Lightbox Viewer is shipped with some default lightboxes that you can test your content with.

These lightboxes is contained in a scene called `Lightbox.unity`.

## Camera roll

Camera Roll lets you roll the camera. This can be used to test how some shaders behave, specifically in regards to some matcap shaders that can look strange in VR when tilting the head sideways.

By default, the *Counter-rotate* option is enabled, which keeps the preview upright despite the camera rolling.

Press *Reset* to restore the view upright.

<video controls width="816" autostart="false">
    <source src="https://hai-vr.github.io/lightbox-viewer/videos/sx_2022-05-16_07-52-09_k7AkO3iYda.mp4" type="video/mp4">
</video>

## Disabling a lightbox

After pressing *Activate LightboxViewer*, the lightbox scene will show up in at the bottom of the hierarchy.

Expand the `Lightboxes` object. If you tag one of them as `EditorOnly`, the lightbox will no longer show up.

<video controls width="816" autostart="false">
    <source src="https://hai-vr.github.io/lightbox-viewer/videos/sx_2022-05-16_08-03-21_RQ8duK7m1k.mp4" type="video/mp4">
</video>

# Create your own lightbox scene

To create your own lightbox, you need to understand how Lightbox Viewer operates.

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

**Each child of the object called `Lightboxes` generates a picture:**

- That child lightbox is enabled. **This effectively enables anything inside of it that can influence the render, such as real time lights or post-processing volumes.**
- The object to be viewed is moved to that lightbox.
- The camera takes a picture.
- That child is disabled again.

If the child is tagged as `EditorOnly`, it will not be used.

With that in mind, you can now create a custom scene. Each lightbox is defined in the *Lightboxes* object.

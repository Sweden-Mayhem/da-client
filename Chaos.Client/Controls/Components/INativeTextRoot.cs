namespace Chaos.Client.Controls.Components;

/// <summary>
///     Marker for a container that paints its descendants' <c>RenderNative</c> text in TTF at native resolution via a
///     separate pass (so the in-place, would-be-upscaled glyphs are suppressed). Implemented by the magnifying
///     <see cref="ScaleHost" /> (in-world menus) and by the lobby's root panel (the 640x480 login screen, which is
///     letterbox-upscaled). A RenderNative label/textbox with NO such ancestor draws in place (already native resolution).
/// </summary>
public interface INativeTextRoot;

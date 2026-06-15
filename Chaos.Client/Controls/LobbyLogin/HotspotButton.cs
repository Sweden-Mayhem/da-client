#region
using Chaos.Client.Controls.Components;
#endregion

namespace Chaos.Client.Controls.LobbyLogin;

/// <summary>
///     A transparent clickable region for the custom login screen. The button artwork is painted into the
///     background image, so this control draws nothing of its own, it only provides a hit-target over the
///     drawn button. (UIButton with no textures renders nothing.)
/// </summary>
public sealed class HotspotButton : UIButton;

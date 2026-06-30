namespace Chaos.Client.Definitions;

/// <summary>
///     Helpers for the server-masked item name. Found gear arrives unidentified and the server sends its name as
///     "Unidentified &lt;type&gt;" (server-side <c>UnidentifiedItem.MaskName</c>); the client keys the tooltip lookup and
///     the slot "?" badge off that prefix. Keep <see cref="UnidentifiedPrefix" /> in step with the server - if the
///     wording drifts, unidentified items stop showing the "?" and their tooltips go blank.
/// </summary>
public static class ItemNaming
{
    /// <summary>The prefix every server-masked unidentified item name starts with.</summary>
    public const string UnidentifiedPrefix = "Unidentified ";

    /// <summary>True when <paramref name="name" /> is a server-masked unidentified item name.</summary>
    public static bool IsUnidentified(string? name)
        => name is not null && name.StartsWith(UnidentifiedPrefix, StringComparison.Ordinal);
}

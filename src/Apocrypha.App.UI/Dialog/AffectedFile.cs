using Apocrypha.Abstractions.Loadouts;
using Apocrypha.Sdk.Games;
using NexusMods.Paths;

namespace Apocrypha.App.UI.Dialog;

/// <summary>Immutable presentation of the actual synchronization override entries.</summary>
public sealed record AffectedFile(string Path, ulong? Bytes, string Classification)
{
    public string Display => $"{Path}  •  {(Bytes is { } n ? $"{n:N0} bytes" : "Size unavailable")}  •  {Classification}";
    public static string Export(IEnumerable<AffectedFile> files) => "Path\tBytes\tClassification" + (files.Any() ? "\n" : "") +
        string.Join("\n", files.Select(f => $"{f.Path}\t{f.Bytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}\t{f.Classification}"));

    public static AffectedFile[] FromEntries(LoadoutItemWithTargetPath.ReadOnly[] entries, ISet<GamePath> originalPaths) => entries.Select(entry =>
        new AffectedFile(entry.TargetPath.Item3.ToString(), LoadoutFile.Size.TryGetValue(entry, out var size) ? size.Value : null,
            originalPaths.Contains(entry.TargetPath) ? "Modified or unverified original" : "Additional / unrecognized"))
        .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
}

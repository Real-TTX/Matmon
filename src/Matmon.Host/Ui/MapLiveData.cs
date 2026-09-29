using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Matmon.Core.Domain;
using Matmon.Host.Services;

namespace Matmon.Host.Ui;

/// <summary>
/// The JSON a wallboard polls to refresh itself in place, shared by the in-app viewer and the public board.
/// <para>
/// This replaces a 30-second full-page meta-refresh, which had a failure mode worth spelling out: reloading
/// resets the slide carousel to slide 1, so on a board with more than one slide every slide after the first
/// was effectively unreachable on a TV.
/// </para>
/// Only VALUES travel here. Anything structural - a tile added, removed, moved or resized - is baked into the
/// server-rendered markup and cannot be patched, so the payload carries a <see cref="MapLiveSnapshot.Revision"/>
/// over exactly that shape and the client does one full reload when it changes.
/// </summary>
public static class MapLiveData
{
    public static MapLiveSnapshot Build(MapDisplayViewModel display) => new(
        ComputeRevision(display),
        display.Slides
            .Select(slide => new MapLiveSlide(
                slide.Slide.Id,
                slide.Tiles.Select(BuildTile).ToArray()))
            .ToArray());

    private static MapLiveTile BuildTile(MapDisplayTileViewModel tile) => new(
        tile.Tile.Id,
        tile.StateKey,
        tile.StateLabel,
        tile.Value,
        tile.Subtitle,
        tile.ProgressPercent,
        tile.ProgressLabel,
        tile.GraphLinePath,
        tile.GraphAreaPath,
        tile.GraphBarPath,
        tile.Series?.Select(line => new MapLiveSeries(line.Label, line.Color, line.LinePath, line.Value, line.Points)).ToArray(),
        tile.Rows?.Select(row => new MapLiveRow(row.Label, row.Detail, row.Value, row.Tone, row.TimeText)).ToArray(),
        tile.Sla is { } sla ? new MapLiveSla(sla.Percent, sla.Label) : null,
        tile.Pins?.Select(pin => new MapLivePin(pin.Label, pin.Tone, pin.Value)).ToArray(),
        tile.Axis is { } axis ? new MapLiveAxis(axis.Top, axis.Middle, axis.Bottom, axis.StartMs, axis.EndMs) : null);

    /// <summary>
    /// A short hash over everything the markup bakes in: which tiles exist, on which slide, of what kind, and
    /// where. A state change must NOT move this (that is the whole point of patching), so nothing
    /// value-shaped goes in.
    /// </summary>
    private static string ComputeRevision(MapDisplayViewModel display)
    {
        var builder = new StringBuilder();
        builder.Append(display.Map.Columns).Append('x').Append(display.Map.Rows)
            .Append('|').Append(display.Map.LogicalWidth).Append('x').Append(display.Map.LogicalHeight)
            .Append('|').Append(display.Map.TilePadding).Append('|').Append(display.Map.OuterMargin);

        foreach (var slide in display.Slides)
        {
            builder.Append(';').Append(slide.Slide.Id.ToString("N"));
            foreach (var tile in slide.Tiles)
            {
                builder.Append(',')
                    .Append(tile.Tile.Id.ToString("N")).Append(':')
                    .Append((int)tile.Tile.Kind).Append(':')
                    .Append(tile.Tile.Column).Append('/').Append(tile.Tile.Row).Append('/')
                    .Append(tile.Tile.ColumnSpan).Append('/').Append(tile.Tile.RowSpan).Append(':')
                    // Pin COUNT, not pin state: adding a pin changes the markup, a pin turning red does not.
                    .Append(tile.Pins?.Count ?? 0).Append(':')
                    .Append(tile.Rows?.Count ?? 0).Append(':')
                    // Series COUNT, same reasoning: a sensor appearing under the target changes the markup,
                    // its line moving does not.
                    .Append(tile.Series?.Count ?? 0);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }

    /// <summary>Progress as the CSS custom property expects it - invariant culture, because a German server
    /// would otherwise emit "42,5" and the browser would read the whole declaration as invalid.</summary>
    public static string? FormatPercent(double? value) =>
        value is { } percent ? Math.Clamp(percent, 0, 100).ToString("0.##", CultureInfo.InvariantCulture) : null;
}

public sealed record MapLiveSnapshot(string Revision, IReadOnlyList<MapLiveSlide> Slides);

public sealed record MapLiveSlide(Guid Id, IReadOnlyList<MapLiveTile> Tiles);

public sealed record MapLiveTile(
    Guid Id,
    string StateKey,
    string StateLabel,
    string Value,
    string Subtitle,
    double? ProgressPercent,
    string ProgressLabel,
    string? GraphLinePath,
    string? GraphAreaPath,
    string? GraphBarPath,
    /// <summary>A multi-graph's lines. Their PATHS travel, unlike everything else here, because a line IS
    /// the value - there is nothing else about it to patch. The series COUNT is part of the revision hash,
    /// so a sensor appearing or disappearing under the target still forces one honest reload.</summary>
    IReadOnlyList<MapLiveSeries>? Series,
    IReadOnlyList<MapLiveRow>? Rows,
    MapLiveSla? Sla,
    IReadOnlyList<MapLivePin>? Pins,
    /// <summary>A multi-graph's Y labels and window: the scale follows the data, so it moves with the lines.</summary>
    MapLiveAxis? Axis = null);

/// <summary>Label and Color travel too: the legend is ordered by the LAST reading, so when another sensor
/// becomes the highest the positions swap - patched by position alone, a name ended up next to someone
/// else's value and line.</summary>
public sealed record MapLiveSeries(string Label, string Color, string? LinePath, string? Value, string? Points);

public sealed record MapLiveAxis(string Top, string Middle, string Bottom, long StartMs, long EndMs);

public sealed record MapLiveRow(string Label, string? Detail, string? Value, string Tone, string? TimeText);

public sealed record MapLiveSla(double? Percent, string Label);

/// <param name="Label">Carried so a row/pin can be matched by position AND sanity-checked by name - a pin
/// list only changes order when the configuration changes, which bumps the revision anyway.</param>
public sealed record MapLivePin(string Label, string Tone, string? Value);

using Matmon.Core.Domain;

namespace Matmon.Host.Services;

/// <summary>
/// Builds a first map from a part of the tree (or a tag) so nobody starts on an empty canvas: an overview row (a heading,
/// a honeycomb of every sensor, the open alerts) and one summary tile per machine. The result is an ordinary map - the
/// designer opens on it and everything can be moved, removed or added.
/// </summary>
public sealed class MapGenerator(IMonitoringWorkspaceStore store)
{
    /// <summary>More machines than this get no tile of their own - the honeycomb still shows all of their sensors.</summary>
    public const int MaxMachineTiles = 24;

    private const int Columns = 12;

    public MonitoringMap Generate(string name, string targetToken, bool overview, bool tilePerMachine)
    {
        var sensors = store.ResolveTargetSensors(targetToken);
        if (sensors.Count == 0)
        {
            throw new InvalidOperationException("There are no sensors under that element or tag yet.");
        }

        var elements = store.GetAllElements().ToDictionary(element => element.Id);
        var tiles = new List<MonitoringMapTile>();
        var row = 1;

        if (overview)
        {
            tiles.Add(new MonitoringMapTile { Kind = MonitoringMapTileKind.Heading, Text = name, Title = name, Column = 1, Row = row, ColumnSpan = Columns, RowSpan = 1, ShowCard = false });
            row += 1;
            // Three rows: an alert feed is never shorter (MonitoringMapTileConstraints) - a shorter request is grown on
            // save and would run into the row below.
            tiles.Add(Targeted(MonitoringMapTileKind.Honeycomb, "All sensors", targetToken, 1, row, 6, 3));
            tiles.Add(Targeted(MonitoringMapTileKind.AlertFeed, "Open alerts", targetToken, 7, row, 6, 3));
            row += 3;
        }

        if (tilePerMachine)
        {
            var machines = sensors
                .Select(sensor => NearestContainer(sensor, elements))
                .Where(container => container is not null)
                .DistinctBy(container => container!.Id)
                .OrderBy(container => container!.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxMachineTiles)
                .ToList();
            var column = 1;
            foreach (var machine in machines)
            {
                tiles.Add(Targeted(MonitoringMapTileKind.Status, machine!.Name, machine.Id.ToString(), column, row, 3, 2));
                column += 3;
                if (column > Columns)
                {
                    column = 1;
                    row += 2;
                }
            }
        }

        if (tiles.Count == 0)
        {
            tiles.Add(Targeted(MonitoringMapTileKind.Honeycomb, "All sensors", targetToken, 1, 1, 6, 2));
        }

        return store.CreateMap(new MonitoringMap
        {
            Name = name,
            Columns = Columns,
            Rows = 8,
            Slides = [new MonitoringMapSlide { Name = "Overview", Tiles = tiles }]
        });
    }

    private static MonitoringMapTile Targeted(MonitoringMapTileKind kind, string title, string token, int column, int row, int columnSpan, int rowSpan) => new()
    {
        Kind = kind,
        Title = title,
        ElementId = MonitoringTargetResolver.ElementId(token),
        TargetTag = MonitoringTargetResolver.TagName(token),
        Column = column,
        Row = row,
        ColumnSpan = columnSpan,
        RowSpan = rowSpan,
        ListLimit = 8
    };

    /// <summary>The machine a sensor belongs to: its nearest host, else the folder or probe it sits in.</summary>
    private static MonitoringElement? NearestContainer(SensorElement sensor, IReadOnlyDictionary<Guid, MonitoringElement> elements)
    {
        MonitoringElement? first = null;
        var parentId = sensor.ParentId;
        for (var depth = 0; parentId is Guid id && depth < 16 && elements.TryGetValue(id, out var element); depth++)
        {
            first ??= element;
            if (element is HostElement)
            {
                return element;
            }

            parentId = element.ParentId;
        }

        return first;
    }
}

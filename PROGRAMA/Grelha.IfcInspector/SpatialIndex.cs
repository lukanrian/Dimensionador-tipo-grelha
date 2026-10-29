using Xbim.Common.Geometry;

internal sealed class SpatialIndex<T>(IEnumerable<(T Item, XbimPoint3D Point)> points, double cellSize)
{
    private readonly Dictionary<(long X, long Y, long Z), List<(T Item, XbimPoint3D Point)>> cells = points
        .GroupBy(entry => Cell(entry.Point, cellSize))
        .ToDictionary(group => group.Key, group => group.ToList());

    public IEnumerable<(T Item, double Distance)> FindWithin(XbimPoint3D point, double radius, Func<T, bool> accept)
    {
        var center = Cell(point, cellSize);
        var reach = (long)Math.Ceiling(radius / cellSize);

        for (var x = center.X - reach; x <= center.X + reach; x++)
        for (var y = center.Y - reach; y <= center.Y + reach; y++)
        for (var z = center.Z - reach; z <= center.Z + reach; z++)
        {
            if (!cells.TryGetValue((x, y, z), out var bucket)) continue;
            foreach (var candidate in bucket)
            {
                if (!accept(candidate.Item)) continue;
                var dx = point.X - candidate.Point.X;
                var dy = point.Y - candidate.Point.Y;
                var dz = point.Z - candidate.Point.Z;
                var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (distance <= radius) yield return (candidate.Item, distance);
            }
        }
    }

    public bool TryNearest(XbimPoint3D point, double radius, Func<T, bool> accept, out T item, out double distance)
    {
        var center = Cell(point, cellSize);
        var reach = (long)Math.Ceiling(radius / cellSize);
        var bestDistance = radius;
        T? bestItem = default;
        var found = false;

        for (var x = center.X - reach; x <= center.X + reach; x++)
        for (var y = center.Y - reach; y <= center.Y + reach; y++)
        for (var z = center.Z - reach; z <= center.Z + reach; z++)
        {
            if (!cells.TryGetValue((x, y, z), out var bucket)) continue;
            foreach (var candidate in bucket)
            {
                if (!accept(candidate.Item)) continue;
                var dx = point.X - candidate.Point.X;
                var dy = point.Y - candidate.Point.Y;
                var dz = point.Z - candidate.Point.Z;
                var candidateDistance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (candidateDistance > bestDistance) continue;
                bestDistance = candidateDistance;
                bestItem = candidate.Item;
                found = true;
            }
        }

        item = bestItem!;
        distance = bestDistance;
        return found;
    }

    private static (long X, long Y, long Z) Cell(XbimPoint3D point, double cellSize) =>
        ((long)Math.Floor(point.X / cellSize), (long)Math.Floor(point.Y / cellSize), (long)Math.Floor(point.Z / cellSize));
}

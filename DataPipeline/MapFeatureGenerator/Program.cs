using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using CommandLine;
using Mapster.Common;
using Mapster.Common.MemoryMappedTypes;
using OSMDataParser;
using OSMDataParser.Elements;

namespace MapFeatureGenerator;

public static class Program
{
    private static MapData LoadOsmFile(ReadOnlySpan<char> osmFilePath)
    {
        var nodes = new ConcurrentDictionary<long, AbstractNode>();
        var ways = new ConcurrentBag<Way>();

        Parallel.ForEach(new PBFFile(osmFilePath), (blob, _) =>
        {
            switch (blob.Type)
            {
                case BlobType.Primitive:
                    {
                        var primitiveBlock = blob.ToPrimitiveBlock();
                        foreach (var primitiveGroup in primitiveBlock)
                            switch (primitiveGroup.ContainedType)
                            {
                                case PrimitiveGroup.ElementType.Node:
                                    foreach (var node in primitiveGroup) nodes[node.Id] = (AbstractNode)node;
                                    break;

                                case PrimitiveGroup.ElementType.Way:
                                    foreach (var way in primitiveGroup) ways.Add((Way)way);
                                    break;
                            }

                        break;
                    }
            }
        });

        var tiles = new Dictionary<int, List<long>>();
        foreach (var (id, node) in nodes)
        {
            var tileId = TiligSystem.GetTile(new Coordinate(node.Latitude, node.Longitude));
            if (tiles.TryGetValue(tileId, out var nodeIds))
            {
                nodeIds.Add(id);
            }
            else
            {
                tiles[tileId] = new List<long>
                {
                    id
                };
            }
        }

        return new MapData
        {
            Nodes = nodes.ToImmutableDictionary(),
            Tiles = tiles.ToImmutableDictionary(),
            Ways = ways.ToImmutableArray()
        };
    }

    private static void CreateMapDataFile(ref MapData mapData, string filePath)
    {
        var usedNodes = new HashSet<long>();

        long featureIdCounter = -1;
        var featureIds = new List<long>();
        // var geometryTypes = new List<GeometryType>();
        // var coordinates = new List<(long id, (int offset, List<Coordinate> coordinates) values)>();

        var labels = new List<int>();
        // var propKeys = new List<(long id, (int offset, IEnumerable<string> keys) values)>();
        // var propValues = new List<(long id, (int offset, IEnumerable<string> values) values)>();

        using var fileWriter = new BinaryWriter(File.OpenWrite(filePath));
        var offsets = new Dictionary<int, long>(mapData.Tiles.Count);

        // Write FileHeader
        fileWriter.Write((long)1); // FileHeader: Version
        fileWriter.Write(mapData.Tiles.Count); // FileHeader: TileCount

        // Write TileHeaderEntry
        foreach (var tile in mapData.Tiles)
        {
            fileWriter.Write(tile.Key); // TileHeaderEntry: ID
            fileWriter.Write((long)0); // TileHeaderEntry: OffsetInBytes
        }

        foreach (var (tileId, _) in mapData.Tiles)
        {
            // FIXME: Not thread safe
            usedNodes.Clear();

            // FIXME: Not thread safe
            featureIds.Clear();
            labels.Clear();

            var totalCoordinateCount = 0;
            var totalPropertyCount = 0;

            var featuresData = new Dictionary<long, FeatureData>();

            foreach (var way in mapData.Ways)
            {
                var featureId = Interlocked.Increment(ref featureIdCounter);

                var featureData = new FeatureData
                {
                    Id = featureId,
                    Coordinates = (totalCoordinateCount, new List<Coordinate>()),

                    //folosesc dictionare pentru a reduce complexitatea

                    Property = (totalPropertyCount, new Dictionary<string,string>(way.Tags.Count)),
                    
                };

                var geometryType = GeometryType.Polyline;

                labels.Add(-1);
                foreach (var tag in way.Tags)
                {
                    if (tag.Key == "name")
                    {
                        labels[^1] = totalPropertyCount * 2 + featureData.Property.property.Count * 2 + 1;
                    }
                   
                    featureData.Property.property.Add(tag.Key,tag.Value);
                }

                foreach (var nodeId in way.NodeIds)
                {
                    var node = mapData.Nodes[nodeId];
                    if (TiligSystem.GetTile(new Coordinate(node.Latitude, node.Longitude)) != tileId)
                    {
                        continue;
                    }

                    usedNodes.Add(nodeId);

                    foreach (var (key, value) in node.Tags)
                    {
                        if (!featureData.Property.property.ContainsKey(key))
                        {
                            featureData.Property.property.Add(key,value);
                            
                        }
                    }

                    featureData.Coordinates.coordinates.Add(new Coordinate(node.Latitude, node.Longitude));
                }

                // This feature is not located within this tile, skip it
                if (featureData.Coordinates.coordinates.Count == 0)
                {
                    // Remove the last item since we added it preemptively
                    labels.RemoveAt(labels.Count - 1);
                    continue;
                }

                if (featureData.Coordinates.coordinates[0] == featureData.Coordinates.coordinates[^1])
                {
                    geometryType = GeometryType.Polygon;
                }
                featureData.GeometryType = (byte)geometryType;

                totalPropertyCount += featureData.Property.property.Count;
                totalCoordinateCount += featureData.Coordinates.coordinates.Count;

                if (featureData.Property.property.Keys.Count != featureData.Property.property.Values.Count)
                {
                    throw new InvalidDataContractException("Property keys and values should have the same count");
                }

                featureIds.Add(featureId);
                featuresData.Add(featureId, featureData);
            }

            foreach (var (nodeId, node) in mapData.Nodes.Where(n => !usedNodes.Contains(n.Key)))
            {
                if (TiligSystem.GetTile(new Coordinate(node.Latitude, node.Longitude)) != tileId)
                {
                    continue;
                }

                var featureId = Interlocked.Increment(ref featureIdCounter);

                
                var featureProp = new Dictionary<string, string>();

                labels.Add(-1);
                for (var i = 0; i < node.Tags.Count; ++i)
                {
                    var tag = node.Tags[i];
                    if (tag.Key == "name")
                    {
                        labels[^1] = totalPropertyCount * 2 + featureProp.Count * 2 + 1;
                    }
                    if (!featureProp.ContainsKey(tag.Key))
                    {

                    featureProp.Add(tag.Key, tag.Value);
                    }
                 
                if (featureProp.Keys.Count != featureProp.Values.Count)
                {
                    throw new InvalidDataContractException("Property keys and values should have the same count");
                }
                }

              
                var fData = new FeatureData
                {
                    Id = featureId,
                    GeometryType = (byte)GeometryType.Point,
                    Coordinates = (totalCoordinateCount, new List<Coordinate>
                    {
                        new Coordinate(node.Latitude, node.Longitude)
                    }),
                    Property = (totalPropertyCount, featureProp),
                    
                };
                featuresData.Add(featureId, fData);
                featureIds.Add(featureId);

                totalPropertyCount += featureProp.Count;
                ++totalCoordinateCount;
            }

            offsets.Add(tileId, fileWriter.BaseStream.Position);

            // Write TileBlockHeader
            fileWriter.Write(featureIds.Count); // TileBlockHeader: FeatureCount
            fileWriter.Write(totalCoordinateCount); // TileBlockHeader: CoordinateCount
            fileWriter.Write(totalPropertyCount * 2); // TileBlockHeader: StringCount
            fileWriter.Write(0); //TileBlockHeader: CharactersCount

            // Take note of the offset within the file for this field
            var coPosition = fileWriter.BaseStream.Position;
            // Write a placeholder value to reserve space in the file
            fileWriter.Write((long)0); // TileBlockHeader: CoordinatesOffsetInBytes (placeholder)

            // Take note of the offset within the file for this field
            var soPosition = fileWriter.BaseStream.Position;
            // Write a placeholder value to reserve space in the file
            fileWriter.Write((long)0); // TileBlockHeader: StringsOffsetInBytes (placeholder)

            // Take note of the offset within the file for this field
            var choPosition = fileWriter.BaseStream.Position;
            // Write a placeholder value to reserve space in the file
            fileWriter.Write((long)0); // TileBlockHeader: CharactersOffsetInBytes (placeholder)

            // Write MapFeatures
            for (var i = 0; i < featureIds.Count; ++i)
            {
                var featureData = featuresData[featureIds[i]];

                fileWriter.Write(featureIds[i]); // MapFeature: Id
                fileWriter.Write(labels[i]); // MapFeature: LabelOffset
                fileWriter.Write(featureData.GeometryType); // MapFeature: GeometryType
                fileWriter.Write(featureData.Coordinates.offset); // MapFeature: CoordinateOffset
                fileWriter.Write(featureData.Coordinates.coordinates.Count); // MapFeature: CoordinateCount
                fileWriter.Write(featureData.Property.offset * 2); // MapFeature: PropertiesOffset 
                fileWriter.Write(featureData.Property.property.Count); // MapFeature: PropertyCount
            }

            // Record the current position in the stream
            var currentPosition = fileWriter.BaseStream.Position;
            // Seek back in the file to the position of the field
            fileWriter.BaseStream.Position = coPosition;
            // Write the recorded 'currentPosition'
            fileWriter.Write(currentPosition); // TileBlockHeader: CoordinatesOffsetInBytes
            // And seek forward to continue updating the file
            fileWriter.BaseStream.Position = currentPosition;
            foreach (var t in featureIds)
            {
                var featureData = featuresData[t];

                foreach (var c in featureData.Coordinates.coordinates)
                {
                    fileWriter.Write(c.Latitude); // Coordinate: Latitude
                    fileWriter.Write(c.Longitude); // Coordinate: Longitude
                }
            }

            // Record the current position in the stream
            currentPosition = fileWriter.BaseStream.Position;
            // Seek back in the file to the position of the field
            fileWriter.BaseStream.Position = soPosition;
            // Write the recorded 'currentPosition'
            fileWriter.Write(currentPosition); // TileBlockHeader: StringsOffsetInBytes
            // And seek forward to continue updating the file
            fileWriter.BaseStream.Position = currentPosition;

            var stringOffset = 0;
            foreach (var t in featureIds)
            {
                var featureData = featuresData[t];
                foreach(var (key,value) in featureData.Property.property)
                {
                    ReadOnlySpan<char> k = key;
                    ReadOnlySpan<char> v = value;

                    fileWriter.Write(stringOffset); // StringEntry: Offset
                    fileWriter.Write(key.Length); // StringEntry: Length
                    stringOffset += key.Length;
                    
                    fileWriter.Write(stringOffset); // StringEntry: Offset
                    fileWriter.Write(value.Length); // StringEntry: Length
                    stringOffset += value.Length;
                }
               
            }

            // Record the current position in the stream
            currentPosition = fileWriter.BaseStream.Position;
            // Seek back in the file to the position of the field
            fileWriter.BaseStream.Position = choPosition;
            // Write the recorded 'currentPosition'
            fileWriter.Write(currentPosition); // TileBlockHeader: CharactersOffsetInBytes
            // And seek forward to continue updating the file
            fileWriter.BaseStream.Position = currentPosition;
            foreach (var t in featureIds)
            {
                var featureData = featuresData[t];
                foreach (var (key, value) in featureData.Property.property)
                {
                    ReadOnlySpan<char> k = key;
                    foreach (var c in k)
                    {
                        fileWriter.Write((short)c);
                    }
                    
                    ReadOnlySpan<char> v = value;
                    foreach (var c in v)
                    {
                        fileWriter.Write((short)c);
                    }
                }
            }
        }

        // Seek to the beginning of the file, just before the first TileHeaderEntry
        fileWriter.Seek(Marshal.SizeOf<FileHeader>(), SeekOrigin.Begin);
        foreach (var (tileId, offset) in offsets)
        {
            fileWriter.Write(tileId);
            fileWriter.Write(offset);
        }

        fileWriter.Flush();
    }

    public static void Main(string[] args)
    {
        Options? arguments = null;
        var argParseResult =
            Parser.Default.ParseArguments<Options>(args).WithParsed(options => { arguments = options; });

        if (argParseResult.Errors.Any())
        {
            Environment.Exit(-1);
        }

        var mapData = LoadOsmFile(arguments!.OsmPbfFilePath);
        CreateMapDataFile(ref mapData, arguments!.OutputFilePath!);
    }

    public class Options
    {
        [Option('i', "input", Required = true, HelpText = "Input osm.pbf file")]
        public string? OsmPbfFilePath { get; set; }

        [Option('o', "output", Required = true, HelpText = "Output binary file")]
        public string? OutputFilePath { get; set; }
    }

    private readonly struct MapData
    {
        public ImmutableDictionary<long, AbstractNode> Nodes { get; init; }
        public ImmutableDictionary<int, List<long>> Tiles { get; init; }
        public ImmutableArray<Way> Ways { get; init; }
    }

    private struct FeatureData
    {
        public long Id { get; init; }

        public byte GeometryType { get; set; }
        public (int offset, List<Coordinate> coordinates) Coordinates { get; init; }
        public (int offset, Dictionary<string, string> property) Property { get; init; }

    }
}

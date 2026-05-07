using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Загружает OBJ-модель раны и извлекает центры маркеров, которые экспортированы
    /// как отдельные объекты вида marker1, marker1.001 и т.п.
    /// </summary>
    public sealed class WoundModelLoaderService
    {
        private const double MetersToMillimeters = 1000.0;

        public WoundModelData Load(string objPath)
        {
            if (string.IsNullOrWhiteSpace(objPath))
                throw new ArgumentException("Путь к OBJ-модели не задан.", nameof(objPath));

            if (!File.Exists(objPath))
                throw new FileNotFoundException("OBJ-модель раны не найдена.", objPath);

            var sidecarPath = Path.ChangeExtension(objPath, ".markers.json");
            var binding = LoadBinding(sidecarPath);
            var unitScale = GetUnitScale(binding.Units);
            var parsedObj = ParseObj(objPath);
            var materialTextureMap = LoadMaterialTextureMap(objPath, parsedObj.MaterialLibraries);

            var mesh = BuildMainMesh(parsedObj, unitScale);
            if (mesh.Vertices.Length == 0 || mesh.TriangleIndices.Length == 0)
                throw new InvalidOperationException("OBJ-модель не содержит основной сетки для деформации.");

            string? diffuseTexturePath = null;
            if (!string.IsNullOrWhiteSpace(mesh.DominantMaterialName) &&
                materialTextureMap.TryGetValue(mesh.DominantMaterialName!, out var dominantTexturePath) &&
                File.Exists(dominantTexturePath))
            {
                diffuseTexturePath = dominantTexturePath;
            }
            else
            {
                diffuseTexturePath = materialTextureMap.Values.FirstOrDefault(File.Exists);
            }

            var modelMarkerCenters = BuildMarkerCenters(parsedObj, unitScale);
            var markerBindings = BuildMarkerBindings(modelMarkerCenters, binding.ModelToCameraMarkerIds);
            var missingBindings = modelMarkerCenters.Keys
                .Where(name => !binding.ModelToCameraMarkerIds.TryGetValue(name, out var id) || !id.HasValue)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var declaredUnits = string.IsNullOrWhiteSpace(binding.Units) ? "meters" : binding.Units;

            return new WoundModelData(
                objPath,
                sidecarPath,
                mesh.Vertices,
                mesh.TriangleIndices,
                mesh.TextureCoordinates,
                mesh.TriangleMaterialNames,
                materialTextureMap,
                diffuseTexturePath,
                declaredUnits,
                unitScale,
                modelMarkerCenters,
                markerBindings,
                missingBindings);
        }

        /// <summary>
        /// Пересобирает привязки к ArUco-ID без повторного чтения OBJ (геометрия и центры маркеров неизменны).
        /// </summary>
        public WoundModelData WithUpdatedMarkerBindings(
            WoundModelData data,
            IReadOnlyDictionary<string, int?> modelToCameraMarkerIds)
        {
            var markerBindings = BuildMarkerBindings(data.ModelMarkerCentersMm, modelToCameraMarkerIds);
            var missingBindings = data.ModelMarkerCentersMm.Keys
                .Where(name => !modelToCameraMarkerIds.TryGetValue(name, out var id) || !id.HasValue)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new WoundModelData(
                data.SourcePath,
                data.SidecarPath,
                data.ReferenceVerticesMm,
                data.TriangleIndices,
                data.TextureCoordinates,
                data.TriangleMaterialNames,
                data.MaterialTexturePaths,
                data.DiffuseTexturePath,
                data.DeclaredUnits,
                data.DeclaredUnitScaleMm,
                data.ModelMarkerCentersMm,
                markerBindings,
                missingBindings);
        }

        /// <summary>
        /// Сохраняет sidecar JSON с полем <c>modelToCameraMarkerIds</c> (camelCase, как при ручном редактировании).
        /// </summary>
        public void SaveMarkerSidecar(
            string sidecarPath,
            IReadOnlyDictionary<string, int?> modelToCameraMarkerIds,
            string? unitsOverride = null)
        {
            if (string.IsNullOrWhiteSpace(sidecarPath))
                throw new ArgumentException("Путь к sidecar не задан.", nameof(sidecarPath));

            var units = unitsOverride;
            if (string.IsNullOrWhiteSpace(units) && File.Exists(sidecarPath))
            {
                try
                {
                    var existing = JsonConvert.DeserializeObject<WoundMarkerBindingFile>(File.ReadAllText(sidecarPath));
                    if (!string.IsNullOrWhiteSpace(existing?.Units))
                        units = existing.Units;
                }
                catch
                {
                    // оставляем null — подставим ниже
                }
            }

            if (string.IsNullOrWhiteSpace(units))
                units = "meters";

            var dict = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in modelToCameraMarkerIds)
                dict[kv.Key] = kv.Value;

            var json = JsonConvert.SerializeObject(
                new { units, modelToCameraMarkerIds = dict },
                Formatting.Indented);

            var directory = Path.GetDirectoryName(Path.GetFullPath(sidecarPath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(sidecarPath, json);
        }

        private static WoundMarkerBindingFile LoadBinding(string sidecarPath)
        {
            if (!File.Exists(sidecarPath))
                return new WoundMarkerBindingFile();

            var json = File.ReadAllText(sidecarPath);
            var binding = JsonConvert.DeserializeObject<WoundMarkerBindingFile>(json);
            return binding ?? new WoundMarkerBindingFile();
        }

        private static double GetUnitScale(string? units)
        {
            if (string.IsNullOrWhiteSpace(units))
                return MetersToMillimeters;

            var normalized = units.Trim().ToLowerInvariant();
            if (normalized == "mm" || normalized == "millimeter" || normalized == "millimeters")
                return 1.0;

            return MetersToMillimeters;
        }

        private static ParsedObjModel ParseObj(string objPath)
        {
            var vertices = new List<Point3D>();
            var textureCoordinates = new List<Point>();
            var materialLibraries = new List<string>();
            var objects = new Dictionary<string, ObjObject>(StringComparer.OrdinalIgnoreCase);
            var currentObject = GetOrCreateObject(objects, "default");
            string? currentMaterial = null;

            foreach (var rawLine in File.ReadLines(objPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;

                if (line.StartsWith("o ", StringComparison.Ordinal) ||
                    line.StartsWith("g ", StringComparison.Ordinal))
                {
                    var objectName = line.Substring(2).Trim();
                    if (!string.IsNullOrWhiteSpace(objectName))
                        currentObject = GetOrCreateObject(objects, objectName);
                    continue;
                }

                if (line.StartsWith("v ", StringComparison.Ordinal))
                {
                    vertices.Add(ParseVertex(line));
                    continue;
                }

                if (line.StartsWith("vt ", StringComparison.Ordinal))
                {
                    textureCoordinates.Add(ParseTextureCoordinate(line));
                    continue;
                }

                if (line.StartsWith("mtllib ", StringComparison.Ordinal))
                {
                    var materialLibrary = line.Substring(7).Trim();
                    if (!string.IsNullOrWhiteSpace(materialLibrary))
                        materialLibraries.Add(materialLibrary);
                    continue;
                }

                if (line.StartsWith("usemtl ", StringComparison.Ordinal))
                {
                    currentMaterial = line.Substring(7).Trim();
                    continue;
                }

                if (line.StartsWith("f ", StringComparison.Ordinal))
                {
                    var face = ParseFace(line, vertices.Count, textureCoordinates.Count, currentMaterial);
                    if (face.Vertices.Count >= 3)
                    {
                        currentObject.Faces.Add(face);
                        foreach (var index in face.Vertices)
                        {
                            currentObject.VertexIndices.Add(index.VertexIndex);
                        }
                    }
                }
            }

            return new ParsedObjModel(vertices, textureCoordinates, materialLibraries, objects.Values.ToList());
        }

        private static ObjObject GetOrCreateObject(Dictionary<string, ObjObject> objects, string name)
        {
            if (!objects.TryGetValue(name, out var obj))
            {
                obj = new ObjObject(name);
                objects[name] = obj;
            }

            return obj;
        }

        private static Point3D ParseVertex(string line)
        {
            var parts = SplitObjLine(line);
            if (parts.Length < 4)
                throw new FormatException($"Некорректная строка вершины OBJ: {line}");

            return new Point3D(
                ParseDouble(parts[1]),
                ParseDouble(parts[2]),
                ParseDouble(parts[3]));
        }

        private static Point ParseTextureCoordinate(string line)
        {
            var parts = SplitObjLine(line);
            if (parts.Length < 3)
                throw new FormatException($"Некорректная строка UV OBJ: {line}");

            var u = ParseDouble(parts[1]);
            var v = ParseDouble(parts[2]);
            return new Point(u, 1.0 - v);
        }

        private static ObjFace ParseFace(string line, int vertexCount, int textureCoordinateCount, string? materialName)
        {
            var parts = SplitObjLine(line);
            var vertices = new ObjFaceVertex[parts.Length - 1];
            for (var i = 1; i < parts.Length; i++)
            {
                var token = parts[i];
                var slashParts = token.Split('/');
                if (slashParts.Length == 0 || string.IsNullOrWhiteSpace(slashParts[0]))
                    throw new FormatException($"Некорректная строка грани OBJ: {line}");

                var vertexIndex = ParseObjIndex(slashParts[0], vertexCount);
                var textureIndex = -1;
                if (slashParts.Length > 1 && !string.IsNullOrWhiteSpace(slashParts[1]))
                    textureIndex = ParseObjIndex(slashParts[1], textureCoordinateCount);

                vertices[i - 1] = new ObjFaceVertex(vertexIndex, textureIndex);
            }

            return new ObjFace(vertices, materialName);
        }

        private static int ParseObjIndex(string token, int count)
        {
            var objIndex = int.Parse(token, CultureInfo.InvariantCulture);
            return objIndex < 0
                ? count + objIndex
                : objIndex - 1;
        }

        private static string[] SplitObjLine(string line)
        {
            return line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static double ParseDouble(string value)
        {
            return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static (Point3D[] Vertices, int[] TriangleIndices, Point[] TextureCoordinates, string?[] TriangleMaterialNames, string? DominantMaterialName)
            BuildMainMesh(ParsedObjModel parsedObj, double unitScale)
        {
            var vertices = new List<Point3D>();
            var triangleIndices = new List<int>();
            var textureCoordinates = new List<Point>();
            var triangleMaterialNames = new List<string?>();
            var globalToLocal = new Dictionary<(int VertexIndex, int TextureIndex), int>();
            var materialUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var obj in parsedObj.Objects.Where(obj => !IsMarkerObject(obj.Name)))
            {
                foreach (var face in obj.Faces)
                {
                    if (!string.IsNullOrWhiteSpace(face.MaterialName))
                    {
                        if (!materialUsage.TryGetValue(face.MaterialName!, out var usage))
                            usage = 0;
                        materialUsage[face.MaterialName!] = usage + Math.Max(1, face.Vertices.Count - 2);
                    }

                    for (var i = 1; i < face.Vertices.Count - 1; i++)
                    {
                        triangleIndices.Add(GetLocalIndex(face.Vertices[0]));
                        triangleIndices.Add(GetLocalIndex(face.Vertices[i]));
                        triangleIndices.Add(GetLocalIndex(face.Vertices[i + 1]));
                        triangleMaterialNames.Add(face.MaterialName);
                    }
                }
            }

            var dominantMaterial = materialUsage
                .OrderByDescending(item => item.Value)
                .Select(item => item.Key)
                .FirstOrDefault();

            return (vertices.ToArray(), triangleIndices.ToArray(), textureCoordinates.ToArray(), triangleMaterialNames.ToArray(), dominantMaterial);

            int GetLocalIndex(ObjFaceVertex faceVertex)
            {
                var key = (faceVertex.VertexIndex, faceVertex.TextureIndex);
                if (globalToLocal.TryGetValue(key, out var localIndex))
                    return localIndex;

                var source = parsedObj.Vertices[faceVertex.VertexIndex];
                var scaled = new Point3D(source.X * unitScale, source.Y * unitScale, source.Z * unitScale);
                localIndex = vertices.Count;
                vertices.Add(scaled);
                textureCoordinates.Add(GetTextureCoordinate(faceVertex.TextureIndex));
                globalToLocal[key] = localIndex;
                return localIndex;
            }

            Point GetTextureCoordinate(int textureIndex)
            {
                if (textureIndex >= 0 && textureIndex < parsedObj.TextureCoordinates.Count)
                    return parsedObj.TextureCoordinates[textureIndex];

                return new Point(0.5, 0.5);
            }
        }

        private static Dictionary<string, Point3D> BuildMarkerCenters(ParsedObjModel parsedObj, double unitScale)
        {
            var result = new Dictionary<string, Point3D>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in parsedObj.Objects.Where(obj => IsMarkerObject(obj.Name)))
            {
                if (obj.VertexIndices.Count == 0)
                    continue;

                var sumX = 0.0;
                var sumY = 0.0;
                var sumZ = 0.0;
                foreach (var index in obj.VertexIndices)
                {
                    var vertex = parsedObj.Vertices[index];
                    sumX += vertex.X * unitScale;
                    sumY += vertex.Y * unitScale;
                    sumZ += vertex.Z * unitScale;
                }

                var count = obj.VertexIndices.Count;
                result[obj.Name] = new Point3D(sumX / count, sumY / count, sumZ / count);
            }

            return result;
        }

        private static Dictionary<int, WoundMarkerBinding> BuildMarkerBindings(
            IReadOnlyDictionary<string, Point3D> markerCenters,
            IReadOnlyDictionary<string, int?> mapping)
        {
            var result = new Dictionary<int, WoundMarkerBinding>();
            foreach (var item in mapping)
            {
                if (!item.Value.HasValue)
                    continue;

                if (!markerCenters.TryGetValue(item.Key, out var modelPoint))
                    continue;

                result[item.Value.Value] = new WoundMarkerBinding(item.Key, item.Value.Value, modelPoint);
            }

            return result;
        }

        private static bool IsMarkerObject(string objectName)
        {
            return objectName.StartsWith("marker", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, string> LoadMaterialTextureMap(
            string objPath,
            IReadOnlyList<string> materialLibraries)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (materialLibraries.Count == 0)
                return result;

            foreach (var materialLibrary in materialLibraries)
            {
                var libraryPath = ResolveMaterialLibraryPath(objPath, materialLibrary);
                if (!File.Exists(libraryPath))
                    continue;

                string? currentMaterial = null;
                foreach (var rawLine in File.ReadLines(libraryPath))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    if (line.StartsWith("newmtl ", StringComparison.Ordinal))
                    {
                        currentMaterial = line.Substring(7).Trim();
                        continue;
                    }

                    if (line.StartsWith("map_Kd ", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(currentMaterial))
                    {
                        var texturePathToken = line.Substring(7).Trim();
                        if (texturePathToken.Length == 0)
                            continue;

                        var texturePath = ResolveTexturePath(libraryPath, texturePathToken);
                        result[currentMaterial!] = texturePath;
                    }
                }
            }

            return result;
        }

        private static string ResolveMaterialLibraryPath(string objPath, string materialLibrary)
        {
            if (Path.IsPathRooted(materialLibrary))
                return materialLibrary;

            var objDirectory = Path.GetDirectoryName(Path.GetFullPath(objPath)) ?? Directory.GetCurrentDirectory();
            return Path.Combine(objDirectory, materialLibrary);
        }

        private static string ResolveTexturePath(string materialLibraryPath, string texturePathToken)
        {
            if (Path.IsPathRooted(texturePathToken))
                return texturePathToken;

            var materialDirectory = Path.GetDirectoryName(Path.GetFullPath(materialLibraryPath)) ?? Directory.GetCurrentDirectory();
            return Path.Combine(materialDirectory, texturePathToken);
        }

        private sealed class WoundMarkerBindingFile
        {
            public string? Units { get; set; } = "meters";
            public Dictionary<string, int?> ModelToCameraMarkerIds { get; set; } = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ParsedObjModel
        {
            public ParsedObjModel(
                IReadOnlyList<Point3D> vertices,
                IReadOnlyList<Point> textureCoordinates,
                IReadOnlyList<string> materialLibraries,
                IReadOnlyList<ObjObject> objects)
            {
                Vertices = vertices;
                TextureCoordinates = textureCoordinates;
                MaterialLibraries = materialLibraries;
                Objects = objects;
            }

            public IReadOnlyList<Point3D> Vertices { get; }
            public IReadOnlyList<Point> TextureCoordinates { get; }
            public IReadOnlyList<string> MaterialLibraries { get; }
            public IReadOnlyList<ObjObject> Objects { get; }
        }

        private sealed class ObjObject
        {
            public ObjObject(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public List<ObjFace> Faces { get; } = new List<ObjFace>();
            public HashSet<int> VertexIndices { get; } = new HashSet<int>();
        }

        private sealed class ObjFace
        {
            public ObjFace(IReadOnlyList<ObjFaceVertex> vertices, string? materialName)
            {
                Vertices = vertices;
                MaterialName = materialName;
            }

            public IReadOnlyList<ObjFaceVertex> Vertices { get; }
            public string? MaterialName { get; }
        }

        private readonly struct ObjFaceVertex
        {
            public ObjFaceVertex(int vertexIndex, int textureIndex)
            {
                VertexIndex = vertexIndex;
                TextureIndex = textureIndex;
            }

            public int VertexIndex { get; }
            public int TextureIndex { get; }
        }
    }

    public sealed class WoundModelData
    {
        public WoundModelData(
            string sourcePath,
            string sidecarPath,
            IReadOnlyList<Point3D> referenceVerticesMm,
            IReadOnlyList<int> triangleIndices,
            IReadOnlyList<Point> textureCoordinates,
            IReadOnlyList<string?> triangleMaterialNames,
            IReadOnlyDictionary<string, string> materialTexturePaths,
            string? diffuseTexturePath,
            string? declaredUnits,
            double declaredUnitScaleMm,
            IReadOnlyDictionary<string, Point3D> modelMarkerCentersMm,
            IReadOnlyDictionary<int, WoundMarkerBinding> markerBindingsByCameraId,
            IReadOnlyList<string> unmappedModelMarkers)
        {
            SourcePath = sourcePath;
            SidecarPath = sidecarPath;
            ReferenceVerticesMm = referenceVerticesMm;
            TriangleIndices = triangleIndices;
            TextureCoordinates = textureCoordinates;
            TriangleMaterialNames = triangleMaterialNames;
            MaterialTexturePaths = materialTexturePaths;
            DiffuseTexturePath = diffuseTexturePath;
            DeclaredUnits = string.IsNullOrWhiteSpace(declaredUnits) ? "meters" : declaredUnits;
            DeclaredUnitScaleMm = declaredUnitScaleMm;
            ModelMarkerCentersMm = modelMarkerCentersMm;
            MarkerBindingsByCameraId = markerBindingsByCameraId;
            UnmappedModelMarkers = unmappedModelMarkers;
        }

        public string SourcePath { get; }
        public string SidecarPath { get; }
        public IReadOnlyList<Point3D> ReferenceVerticesMm { get; }
        public IReadOnlyList<int> TriangleIndices { get; }
        public IReadOnlyList<Point> TextureCoordinates { get; }
        public IReadOnlyList<string?> TriangleMaterialNames { get; }
        public IReadOnlyDictionary<string, string> MaterialTexturePaths { get; }
        public string? DiffuseTexturePath { get; }
        public string DeclaredUnits { get; }
        public double DeclaredUnitScaleMm { get; }
        public IReadOnlyDictionary<string, Point3D> ModelMarkerCentersMm { get; }
        public IReadOnlyDictionary<int, WoundMarkerBinding> MarkerBindingsByCameraId { get; }
        public IReadOnlyList<string> UnmappedModelMarkers { get; }
        public int VertexCount => ReferenceVerticesMm.Count;
        public int TriangleCount => TriangleIndices.Count / 3;
        public bool HasUsableMarkerBindings => MarkerBindingsByCameraId.Count >= 3;
    }

    public readonly struct WoundMarkerBinding
    {
        public WoundMarkerBinding(string modelMarkerName, int cameraMarkerId, Point3D modelPointMm)
        {
            ModelMarkerName = modelMarkerName;
            CameraMarkerId = cameraMarkerId;
            ModelPointMm = modelPointMm;
        }

        public string ModelMarkerName { get; }
        public int CameraMarkerId { get; }
        public Point3D ModelPointMm { get; }
    }
}

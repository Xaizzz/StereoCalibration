using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Одна нормализованная команда перемещения из G-кода.
    /// </summary>
    public sealed class GCodeMove
    {
        public GCodeMove(
            Point3D start,
            Point3D end,
            bool isExtrusion,
            bool isTravel,
            double feedRateMmPerMinute,
            double extrusionDelta,
            int sourceLineNumber)
        {
            Start = start;
            End = end;
            IsExtrusion = isExtrusion;
            IsTravel = isTravel;
            FeedRateMmPerMinute = feedRateMmPerMinute;
            ExtrusionDelta = extrusionDelta;
            SourceLineNumber = sourceLineNumber;
            LengthMm = Distance(start, end);
        }

        public Point3D Start { get; }
        public Point3D End { get; }
        public bool IsExtrusion { get; }
        public bool IsTravel { get; }
        public double FeedRateMmPerMinute { get; }
        public double ExtrusionDelta { get; }
        public int SourceLineNumber { get; }
        public double LengthMm { get; }

        public GCodeMove WithPoints(Point3D start, Point3D end)
        {
            return new GCodeMove(
                start,
                end,
                IsExtrusion,
                IsTravel,
                FeedRateMmPerMinute,
                ExtrusionDelta,
                SourceLineNumber);
        }

        private static double Distance(Point3D a, Point3D b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    /// <summary>
    /// 2D-границы траектории в XY.
    /// </summary>
    public readonly struct PathBounds2D
    {
        public PathBounds2D(double minX, double maxX, double minY, double maxY, bool isValid)
        {
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
            IsValid = isValid;
        }

        public double MinX { get; }
        public double MaxX { get; }
        public double MinY { get; }
        public double MaxY { get; }
        public bool IsValid { get; }
        public double Width => IsValid ? Math.Max(0, MaxX - MinX) : 0;
        public double Height => IsValid ? Math.Max(0, MaxY - MinY) : 0;
        public double CenterX => IsValid ? (MinX + MaxX) / 2.0 : 0;
        public double CenterY => IsValid ? (MinY + MaxY) / 2.0 : 0;
    }

    /// <summary>
    /// Результат разбора исходного G-кода.
    /// </summary>
    public sealed class ParsedGCodePath
    {
        public ParsedGCodePath(
            IReadOnlyList<GCodeMove> moves,
            IReadOnlyList<GCodeMove> extrusionMoves,
            PathBounds2D motionBounds,
            PathBounds2D extrusionBounds,
            double minZ,
            double maxZ,
            string sourcePath)
        {
            Moves = moves;
            ExtrusionMoves = extrusionMoves;
            MotionBounds = motionBounds;
            ExtrusionBounds = extrusionBounds;
            MinZ = minZ;
            MaxZ = maxZ;
            SourcePath = sourcePath;
        }

        public IReadOnlyList<GCodeMove> Moves { get; }
        public IReadOnlyList<GCodeMove> ExtrusionMoves { get; }
        public PathBounds2D MotionBounds { get; }
        public PathBounds2D ExtrusionBounds { get; }
        public double MinZ { get; }
        public double MaxZ { get; }
        public string SourcePath { get; }
    }

    /// <summary>
    /// Локальная привязка точки траектории к референсной поверхности.
    /// Хранит барицентрические координаты в треугольнике маркеров и
    /// смещение вдоль нормали поверхности.
    /// </summary>
    public readonly struct SurfaceAnchor
    {
        public SurfaceAnchor(
            int triangleA,
            int triangleB,
            int triangleC,
            double weightA,
            double weightB,
            double weightC,
            double normalOffsetMm)
        {
            TriangleA = triangleA;
            TriangleB = triangleB;
            TriangleC = triangleC;
            WeightA = weightA;
            WeightB = weightB;
            WeightC = weightC;
            NormalOffsetMm = normalOffsetMm;
        }

        public int TriangleA { get; }
        public int TriangleB { get; }
        public int TriangleC { get; }
        public double WeightA { get; }
        public double WeightB { get; }
        public double WeightC { get; }
        public double NormalOffsetMm { get; }
    }

    /// <summary>
    /// Перемещение траектории, закреплённое в локальных координатах поверхности.
    /// </summary>
    public sealed class AnchoredPrintMove
    {
        public AnchoredPrintMove(
            SurfaceAnchor startAnchor,
            SurfaceAnchor endAnchor,
            bool isExtrusion,
            bool isTravel,
            double feedRateMmPerMinute,
            double extrusionDelta,
            int sourceLineNumber)
        {
            StartAnchor = startAnchor;
            EndAnchor = endAnchor;
            IsExtrusion = isExtrusion;
            IsTravel = isTravel;
            FeedRateMmPerMinute = feedRateMmPerMinute;
            ExtrusionDelta = extrusionDelta;
            SourceLineNumber = sourceLineNumber;
        }

        public SurfaceAnchor StartAnchor { get; }
        public SurfaceAnchor EndAnchor { get; }
        public bool IsExtrusion { get; }
        public bool IsTravel { get; }
        public double FeedRateMmPerMinute { get; }
        public double ExtrusionDelta { get; }
        public int SourceLineNumber { get; }
    }

    /// <summary>
    /// Зафиксированная в момент старта печати референсная поверхность и локальная
    /// привязка всей траектории к этой поверхности.
    /// </summary>
    public sealed class SurfacePrintReference
    {
        public SurfacePrintReference(
            IReadOnlyList<int> markerIds,
            IReadOnlyList<Point3D> referenceMarkerPositions,
            IReadOnlyList<AnchoredPrintMove> anchoredMoves,
            Point3D preferredSidePoint)
        {
            MarkerIds = markerIds;
            ReferenceMarkerPositions = referenceMarkerPositions;
            AnchoredMoves = anchoredMoves;
            PreferredSidePoint = preferredSidePoint;
            CreatedAtUtc = DateTime.UtcNow;
        }

        public IReadOnlyList<int> MarkerIds { get; }
        public IReadOnlyList<Point3D> ReferenceMarkerPositions { get; }
        public IReadOnlyList<AnchoredPrintMove> AnchoredMoves { get; }
        public Point3D PreferredSidePoint { get; }
        public DateTime CreatedAtUtc { get; }
    }

    /// <summary>
    /// Локальная привязка точки траектории к треугольнику деформируемого mesh.
    /// </summary>
    public readonly struct MeshSurfaceAnchor
    {
        public MeshSurfaceAnchor(
            int triangleA,
            int triangleB,
            int triangleC,
            double weightA,
            double weightB,
            double weightC,
            double normalOffsetMm)
        {
            TriangleA = triangleA;
            TriangleB = triangleB;
            TriangleC = triangleC;
            WeightA = weightA;
            WeightB = weightB;
            WeightC = weightC;
            NormalOffsetMm = normalOffsetMm;
        }

        public int TriangleA { get; }
        public int TriangleB { get; }
        public int TriangleC { get; }
        public double WeightA { get; }
        public double WeightB { get; }
        public double WeightC { get; }
        public double NormalOffsetMm { get; }
    }

    /// <summary>
    /// Сегмент печати, закреплённый в координатах mesh-модели.
    /// </summary>
    public sealed class MeshAnchoredPrintMove
    {
        public MeshAnchoredPrintMove(
            MeshSurfaceAnchor startAnchor,
            MeshSurfaceAnchor endAnchor,
            bool isExtrusion,
            bool isTravel,
            double feedRateMmPerMinute,
            double extrusionDelta,
            int sourceLineNumber)
        {
            StartAnchor = startAnchor;
            EndAnchor = endAnchor;
            IsExtrusion = isExtrusion;
            IsTravel = isTravel;
            FeedRateMmPerMinute = feedRateMmPerMinute;
            ExtrusionDelta = extrusionDelta;
            SourceLineNumber = sourceLineNumber;
        }

        public MeshSurfaceAnchor StartAnchor { get; }
        public MeshSurfaceAnchor EndAnchor { get; }
        public bool IsExtrusion { get; }
        public bool IsTravel { get; }
        public double FeedRateMmPerMinute { get; }
        public double ExtrusionDelta { get; }
        public int SourceLineNumber { get; }
    }

    /// <summary>
    /// Референс печати, привязанный к треугольникам mesh-модели.
    /// </summary>
    public sealed class WoundMeshPrintReference
    {
        public WoundMeshPrintReference(
            IReadOnlyList<Point3D> referenceVertices,
            IReadOnlyList<int> triangleIndices,
            IReadOnlyList<MeshAnchoredPrintMove> anchoredMoves,
            int supportMarkerCount,
            Point3D preferredSidePoint)
        {
            ReferenceVertices = referenceVertices;
            TriangleIndices = triangleIndices;
            AnchoredMoves = anchoredMoves;
            SupportMarkerCount = supportMarkerCount;
            PreferredSidePoint = preferredSidePoint;
            CreatedAtUtc = DateTime.UtcNow;
        }

        public IReadOnlyList<Point3D> ReferenceVertices { get; }
        public IReadOnlyList<int> TriangleIndices { get; }
        public IReadOnlyList<MeshAnchoredPrintMove> AnchoredMoves { get; }
        public int SupportMarkerCount { get; }
        public Point3D PreferredSidePoint { get; }
        public DateTime CreatedAtUtc { get; }
    }

    /// <summary>
    /// Траектория после проекции на деформируемую поверхность.
    /// </summary>
    public sealed class ProjectedPrintPath
    {
        public ProjectedPrintPath(
            IReadOnlyList<GCodeMove> moves,
            IReadOnlyList<GCodeMove> extrusionMoves,
            PathBounds2D projectedBounds,
            int markerCount)
        {
            Moves = moves;
            ExtrusionMoves = extrusionMoves;
            ProjectedBounds = projectedBounds;
            MarkerCount = markerCount;
        }

        public IReadOnlyList<GCodeMove> Moves { get; }
        public IReadOnlyList<GCodeMove> ExtrusionMoves { get; }
        public PathBounds2D ProjectedBounds { get; }
        public int MarkerCount { get; }
    }

    /// <summary>
    /// Снимок состояния анимации печати для рендера.
    /// </summary>
    public readonly struct PrintPlaybackSnapshot
    {
        public PrintPlaybackSnapshot(
            Point3D nozzlePosition,
            double normalizedProgress,
            int currentMoveIndex,
            int completedExtrusionCount,
            int activeExtrusionIndex,
            double activeExtrusionProgress,
            bool isFinished)
        {
            NozzlePosition = nozzlePosition;
            NormalizedProgress = normalizedProgress;
            CurrentMoveIndex = currentMoveIndex;
            CompletedExtrusionCount = completedExtrusionCount;
            ActiveExtrusionIndex = activeExtrusionIndex;
            ActiveExtrusionProgress = activeExtrusionProgress;
            IsFinished = isFinished;
        }

        public Point3D NozzlePosition { get; }
        public double NormalizedProgress { get; }
        public int CurrentMoveIndex { get; }
        public int CompletedExtrusionCount { get; }
        public int ActiveExtrusionIndex { get; }
        public double ActiveExtrusionProgress { get; }
        public bool IsFinished { get; }
    }
}

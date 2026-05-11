using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Проецирует траекторию печати на деформируемую поверхность mesh-модели раны.
    /// 
    /// Идея аналогична SurfaceProjectionService: при фиксации референса каждая точка
    /// G-code закрепляется локально как (triangle + barycentric + offset по нормали).
    /// При обновлении деформации позиции пересчитываются по текущим вершинам того же mesh.
    /// </summary>
    public sealed class WoundMeshProjectionService
    {
        private const double FitMarginRatio = 0.72;
        private const double MinTriangleArea = 1e-6;
        private const double MinDeterminant = 1e-9;
        private const double BarycentricTolerance = 1e-6;
        private const double PathSurfaceLiftMm = 4.0;
        private const double MaxReasonableCoordinateAbsMm = 20000.0;
        private const double MaxExtrusionSegmentLengthMm = 2.5;
        private const double MaxTravelSegmentLengthMm = 8.0;

        public const int MinMarkersForDeformation = 3;
        public const double SafetyClearanceMm = PathSurfaceLiftMm;

        private const double MinimumFitBoundsSpanMm = 1e-3;

        public bool TryCreateReference(
            ParsedGCodePath sourcePath,
            IReadOnlyList<Point3D> referenceVertices,
            IReadOnlyList<int> triangleIndices,
            int supportMarkerCount,
            Point3D preferredSidePoint,
            out WoundMeshPrintReference printReference,
            IReadOnlyList<Point3D>? markerZoneScenePoints = null)
        {
            printReference = null!;
            if (sourcePath == null ||
                sourcePath.Moves.Count == 0 ||
                referenceVertices == null ||
                referenceVertices.Count < 3 ||
                triangleIndices == null ||
                triangleIndices.Count < 3)
            {
                return false;
            }

            var sourceBounds = sourcePath.ExtrusionBounds.IsValid
                ? sourcePath.ExtrusionBounds
                : sourcePath.MotionBounds;
            if (!sourceBounds.IsValid)
                return false;

            var projectionAxes = ChooseProjectionAxes(referenceVertices);
            var referencePoints2D = referenceVertices
                .Select(point => new SurfacePoint(
                    GetAxisValue(point, projectionAxes.First),
                    GetAxisValue(point, projectionAxes.Second)))
                .ToList();

            var triangles = BuildTrianglesFromMesh(referencePoints2D, triangleIndices);
            if (triangles.Count == 0)
                return false;

            var meshSurfaceBounds = GetSurfaceBounds(referencePoints2D);
            var markerSurfaceBounds = TryGetMarkerProjectedUvBounds(
                markerZoneScenePoints,
                projectionAxes.First,
                projectionAxes.Second);
            SurfaceBounds fitSourceBounds = meshSurfaceBounds;
            SurfaceBounds safeSurfaceBounds;

            if (markerSurfaceBounds.HasValue)
            {
                var projected = markerSurfaceBounds.Value;
                var safeMarker = BuildSafeSurfaceBounds(projected);
                if (SpansArePositive(safeMarker))
                {
                    fitSourceBounds = projected;
                    safeSurfaceBounds = safeMarker;
                }
                else
                {
                    safeSurfaceBounds = BuildSafeSurfaceBounds(meshSurfaceBounds);
                }
            }
            else
            {
                safeSurfaceBounds = BuildSafeSurfaceBounds(meshSurfaceBounds);
            }

            var fitTransform = BuildAutoFitTransform(sourceBounds, fitSourceBounds);
            var sourceMinZ = sourcePath.MinZ;

            var anchoredMoves = new List<MeshAnchoredPrintMove>(sourcePath.Moves.Count);
            foreach (var move in sourcePath.Moves)
            {
                if (!TryBuildAnchoredMoveSegments(
                    move,
                    sourceMinZ,
                    fitTransform,
                    safeSurfaceBounds,
                    triangles,
                    referencePoints2D,
                    anchoredMoves))
                {
                    return false;
                }
            }

            printReference = new WoundMeshPrintReference(
                referenceVertices.ToArray(),
                triangleIndices.ToArray(),
                anchoredMoves,
                supportMarkerCount,
                preferredSidePoint);

            return true;
        }

        public bool TryProjectPath(
            WoundMeshPrintReference printReference,
            IReadOnlyList<Point3D> currentVertices,
            Point3D preferredSidePoint,
            out ProjectedPrintPath projectedPath)
        {
            projectedPath = null!;
            if (printReference == null ||
                printReference.AnchoredMoves.Count == 0 ||
                currentVertices == null ||
                currentVertices.Count != printReference.ReferenceVertices.Count)
            {
                return false;
            }

            var projectedMoves = new List<GCodeMove>(printReference.AnchoredMoves.Count);
            var projectedExtrusionMoves = new List<GCodeMove>();
            foreach (var anchoredMove in printReference.AnchoredMoves)
            {
                var startPoint = EvaluateAnchor(anchoredMove.StartAnchor, currentVertices, preferredSidePoint);
                var endPoint = EvaluateAnchor(anchoredMove.EndAnchor, currentVertices, preferredSidePoint);
                if (!IsFinite(startPoint) || !IsFinite(endPoint))
                    return false;

                var move = new GCodeMove(
                    startPoint,
                    endPoint,
                    anchoredMove.IsExtrusion,
                    anchoredMove.IsTravel,
                    anchoredMove.FeedRateMmPerMinute,
                    anchoredMove.ExtrusionDelta,
                    anchoredMove.SourceLineNumber);

                projectedMoves.Add(move);
                if (move.IsExtrusion)
                    projectedExtrusionMoves.Add(move);
            }

            projectedPath = new ProjectedPrintPath(
                projectedMoves,
                projectedExtrusionMoves,
                BuildBounds(projectedMoves),
                printReference.SupportMarkerCount);
            return true;
        }

        private static bool TryBuildAnchoredMoveSegments(
            GCodeMove sourceMove,
            double sourceMinZ,
            FitTransform transform,
            SurfaceBounds safeSurfaceBounds,
            IReadOnlyList<MeshTriangle> triangles,
            IReadOnlyList<SurfacePoint> referencePoints2D,
            List<MeshAnchoredPrintMove> anchoredMoves)
        {
            var maxSegmentLength = sourceMove.IsExtrusion
                ? MaxExtrusionSegmentLengthMm
                : MaxTravelSegmentLengthMm;
            var segmentCount = Math.Max(1, (int)Math.Ceiling(sourceMove.LengthMm / Math.Max(0.5, maxSegmentLength)));

            if (!TryBuildAnchor(sourceMove.Start, sourceMinZ, transform, safeSurfaceBounds, triangles, referencePoints2D, out var startAnchor))
                return false;

            var extrusionDeltaPerSegment = sourceMove.IsExtrusion
                ? sourceMove.ExtrusionDelta / segmentCount
                : sourceMove.ExtrusionDelta;

            var currentStartAnchor = startAnchor;
            for (var segmentIndex = 1; segmentIndex <= segmentCount; segmentIndex++)
            {
                var t = segmentIndex / (double)segmentCount;
                var currentEndPoint = Lerp(sourceMove.Start, sourceMove.End, t);
                if (!TryBuildAnchor(currentEndPoint, sourceMinZ, transform, safeSurfaceBounds, triangles, referencePoints2D, out var currentEndAnchor))
                    return false;

                anchoredMoves.Add(new MeshAnchoredPrintMove(
                    currentStartAnchor,
                    currentEndAnchor,
                    sourceMove.IsExtrusion,
                    sourceMove.IsTravel,
                    sourceMove.FeedRateMmPerMinute,
                    extrusionDeltaPerSegment,
                    sourceMove.SourceLineNumber));

                currentStartAnchor = currentEndAnchor;
            }

            return true;
        }

        private static bool TryBuildAnchor(
            Point3D sourcePoint,
            double sourceMinZ,
            FitTransform transform,
            SurfaceBounds safeSurfaceBounds,
            IReadOnlyList<MeshTriangle> triangles,
            IReadOnlyList<SurfacePoint> referencePoints2D,
            out MeshSurfaceAnchor anchor)
        {
            anchor = default;
            var projectedUv = new SurfacePoint(
                transform.SurfaceCenterU + (sourcePoint.X - transform.SourceCenterX) * transform.Scale,
                transform.SurfaceCenterV + (sourcePoint.Y - transform.SourceCenterY) * transform.Scale);
            projectedUv = ClampToBounds(projectedUv, safeSurfaceBounds);

            var (triangle, barycentric) = FindBestTriangle(projectedUv, triangles, referencePoints2D);
            var normalOffset = Math.Max(0, sourcePoint.Z - sourceMinZ);
            anchor = new MeshSurfaceAnchor(
                triangle.A,
                triangle.B,
                triangle.C,
                barycentric.A,
                barycentric.B,
                barycentric.C,
                normalOffset);
            return true;
        }

        private static Point3D EvaluateAnchor(
            MeshSurfaceAnchor anchor,
            IReadOnlyList<Point3D> currentVertices,
            Point3D preferredSidePoint)
        {
            var triangle = new MeshTriangle(anchor.TriangleA, anchor.TriangleB, anchor.TriangleC);
            var weights = new BarycentricWeights(anchor.WeightA, anchor.WeightB, anchor.WeightC);
            var basePoint = InterpolatePoint(currentVertices, triangle, weights);
            var normal = GetTriangleNormal(currentVertices, triangle, basePoint, preferredSidePoint);
            var offsetMm = Math.Max(SafetyClearanceMm, anchor.NormalOffsetMm + SafetyClearanceMm);

            var point = new Point3D(
                basePoint.X + normal.X * offsetMm,
                basePoint.Y + normal.Y * offsetMm,
                basePoint.Z + normal.Z * offsetMm);

            var penetrationCheck = Vector3D.DotProduct(point - basePoint, normal);
            if (penetrationCheck < SafetyClearanceMm)
            {
                point = new Point3D(
                    basePoint.X + normal.X * SafetyClearanceMm,
                    basePoint.Y + normal.Y * SafetyClearanceMm,
                    basePoint.Z + normal.Z * SafetyClearanceMm);
            }

            return point;
        }

        private static List<MeshTriangle> BuildTrianglesFromMesh(
            IReadOnlyList<SurfacePoint> points2D,
            IReadOnlyList<int> triangleIndices)
        {
            var result = new List<MeshTriangle>(triangleIndices.Count / 3);
            for (var i = 0; i + 2 < triangleIndices.Count; i += 3)
            {
                var triangle = new MeshTriangle(triangleIndices[i], triangleIndices[i + 1], triangleIndices[i + 2]);
                if (!IsTriangleIndexValid(points2D.Count, triangle))
                    continue;

                var area = Math.Abs(GetTriangleArea(points2D, triangle));
                if (area < MinTriangleArea)
                    continue;

                result.Add(triangle);
            }

            return result;
        }

        private static bool IsTriangleIndexValid(int pointCount, MeshTriangle triangle)
        {
            return triangle.A >= 0 && triangle.A < pointCount &&
                   triangle.B >= 0 && triangle.B < pointCount &&
                   triangle.C >= 0 && triangle.C < pointCount;
        }

        private static SurfaceBounds GetSurfaceBounds(IReadOnlyList<SurfacePoint> points)
        {
            var minU = points.Min(point => point.U);
            var maxU = points.Max(point => point.U);
            var minV = points.Min(point => point.V);
            var maxV = points.Max(point => point.V);
            return new SurfaceBounds(minU, maxU, minV, maxV);
        }

        private static (MeshTriangle Triangle, BarycentricWeights Weights) FindBestTriangle(
            SurfacePoint point,
            IReadOnlyList<MeshTriangle> triangles,
            IReadOnlyList<SurfacePoint> points2D)
        {
            MeshTriangle? bestTriangle = null;
            var bestWeights = default(BarycentricWeights);
            var bestDistanceSq = double.PositiveInfinity;

            foreach (var triangle in triangles)
            {
                if (!TryGetBarycentricWeights(point, points2D, triangle, out var weights))
                    continue;

                if (weights.A >= -BarycentricTolerance &&
                    weights.B >= -BarycentricTolerance &&
                    weights.C >= -BarycentricTolerance)
                {
                    return (triangle, NormalizeWeights(weights));
                }

                var closestPoint = GetClosestPointOnTriangle(point, points2D, triangle);
                var du = closestPoint.U - point.U;
                var dv = closestPoint.V - point.V;
                var distanceSq = du * du + dv * dv;
                if (distanceSq < bestDistanceSq)
                {
                    bestDistanceSq = distanceSq;
                    bestTriangle = triangle;
                    if (!TryGetBarycentricWeights(closestPoint, points2D, triangle, out bestWeights))
                        bestWeights = weights;
                }
            }

            if (bestTriangle.HasValue)
                return (bestTriangle.Value, ClampAndNormalizeWeights(bestWeights));

            return (triangles[0], new BarycentricWeights(1, 0, 0));
        }

        private static bool TryGetBarycentricWeights(
            SurfacePoint point,
            IReadOnlyList<SurfacePoint> points2D,
            MeshTriangle triangle,
            out BarycentricWeights weights)
        {
            var a = points2D[triangle.A];
            var b = points2D[triangle.B];
            var c = points2D[triangle.C];

            var denominator = (b.V - c.V) * (a.U - c.U) + (c.U - b.U) * (a.V - c.V);
            if (Math.Abs(denominator) < MinDeterminant)
            {
                weights = default;
                return false;
            }

            var wa = ((b.V - c.V) * (point.U - c.U) + (c.U - b.U) * (point.V - c.V)) / denominator;
            var wb = ((c.V - a.V) * (point.U - c.U) + (a.U - c.U) * (point.V - c.V)) / denominator;
            var wc = 1.0 - wa - wb;
            weights = new BarycentricWeights(wa, wb, wc);
            return true;
        }

        private static SurfacePoint GetClosestPointOnTriangle(
            SurfacePoint point,
            IReadOnlyList<SurfacePoint> points,
            MeshTriangle triangle)
        {
            var a = points[triangle.A];
            var b = points[triangle.B];
            var c = points[triangle.C];
            var closestAB = GetClosestPointOnSegment(point, a, b);
            var closestBC = GetClosestPointOnSegment(point, b, c);
            var closestCA = GetClosestPointOnSegment(point, c, a);

            var distanceAB = DistanceSquared(point, closestAB);
            var distanceBC = DistanceSquared(point, closestBC);
            var distanceCA = DistanceSquared(point, closestCA);

            if (distanceAB <= distanceBC && distanceAB <= distanceCA)
                return closestAB;

            return distanceBC <= distanceCA ? closestBC : closestCA;
        }

        private static SurfacePoint GetClosestPointOnSegment(SurfacePoint point, SurfacePoint start, SurfacePoint end)
        {
            var du = end.U - start.U;
            var dv = end.V - start.V;
            var lengthSq = du * du + dv * dv;
            if (lengthSq < MinDeterminant)
                return start;

            var t = ((point.U - start.U) * du + (point.V - start.V) * dv) / lengthSq;
            t = Math.Max(0, Math.Min(1, t));
            return new SurfacePoint(start.U + du * t, start.V + dv * t);
        }

        private static SurfaceBounds BuildSafeSurfaceBounds(SurfaceBounds surfaceBounds)
        {
            var width = surfaceBounds.MaxU - surfaceBounds.MinU;
            var height = surfaceBounds.MaxV - surfaceBounds.MinV;
            var insetU = Math.Max(0, width * (1.0 - FitMarginRatio) / 2.0);
            var insetV = Math.Max(0, height * (1.0 - FitMarginRatio) / 2.0);
            return new SurfaceBounds(
                surfaceBounds.MinU + insetU,
                surfaceBounds.MaxU - insetU,
                surfaceBounds.MinV + insetV,
                surfaceBounds.MaxV - insetV);
        }

        private static SurfaceBounds? TryGetMarkerProjectedUvBounds(
            IReadOnlyList<Point3D>? markerZoneScenePoints,
            Axis firstAxis,
            Axis secondAxis)
        {
            if (markerZoneScenePoints == null || markerZoneScenePoints.Count < 3)
                return null;

            var projected = new List<SurfacePoint>(markerZoneScenePoints.Count);
            foreach (var point in markerZoneScenePoints)
            {
                projected.Add(new SurfacePoint(
                    GetAxisValue(point, firstAxis),
                    GetAxisValue(point, secondAxis)));
            }

            return GetSurfaceBounds(projected);
        }

        private static bool SpansArePositive(SurfaceBounds bounds)
        {
            return bounds.MaxU - bounds.MinU > MinimumFitBoundsSpanMm &&
                   bounds.MaxV - bounds.MinV > MinimumFitBoundsSpanMm;
        }

        private static SurfacePoint ClampToBounds(SurfacePoint point, SurfaceBounds bounds)
        {
            return new SurfacePoint(
                Math.Max(bounds.MinU, Math.Min(bounds.MaxU, point.U)),
                Math.Max(bounds.MinV, Math.Min(bounds.MaxV, point.V)));
        }

        private static FitTransform BuildAutoFitTransform(PathBounds2D sourceBounds, SurfaceBounds surfaceBounds)
        {
            var sourceWidth = Math.Max(1e-6, sourceBounds.Width);
            var sourceHeight = Math.Max(1e-6, sourceBounds.Height);
            var surfaceWidth = Math.Max(1e-6, surfaceBounds.MaxU - surfaceBounds.MinU);
            var surfaceHeight = Math.Max(1e-6, surfaceBounds.MaxV - surfaceBounds.MinV);
            var scale = Math.Min(surfaceWidth / sourceWidth, surfaceHeight / sourceHeight) * FitMarginRatio;
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 1e-6)
                scale = 1.0;

            return new FitTransform(
                scale,
                sourceBounds.CenterX,
                sourceBounds.CenterY,
                (surfaceBounds.MinU + surfaceBounds.MaxU) / 2.0,
                (surfaceBounds.MinV + surfaceBounds.MaxV) / 2.0);
        }

        private static (Axis First, Axis Second) ChooseProjectionAxes(IReadOnlyList<Point3D> points3D)
        {
            var spreadX = points3D.Max(point => point.X) - points3D.Min(point => point.X);
            var spreadY = points3D.Max(point => point.Y) - points3D.Min(point => point.Y);
            var spreadZ = points3D.Max(point => point.Z) - points3D.Min(point => point.Z);

            var spreads = new[]
            {
                (Axis.X, spreadX),
                (Axis.Y, spreadY),
                (Axis.Z, spreadZ)
            }
            .OrderByDescending(axisSpread => axisSpread.Item2)
            .ToArray();

            return (spreads[0].Item1, spreads[1].Item1);
        }

        private static double GetAxisValue(Point3D point, Axis axis)
        {
            return axis switch
            {
                Axis.X => point.X,
                Axis.Y => point.Y,
                Axis.Z => point.Z,
                _ => point.X
            };
        }

        private static Point3D InterpolatePoint(
            IReadOnlyList<Point3D> points,
            MeshTriangle triangle,
            BarycentricWeights weights)
        {
            var a = points[triangle.A];
            var b = points[triangle.B];
            var c = points[triangle.C];
            return new Point3D(
                a.X * weights.A + b.X * weights.B + c.X * weights.C,
                a.Y * weights.A + b.Y * weights.B + c.Y * weights.C,
                a.Z * weights.A + b.Z * weights.B + c.Z * weights.C);
        }

        private static Vector3D GetTriangleNormal(
            IReadOnlyList<Point3D> points,
            MeshTriangle triangle,
            Point3D basePoint,
            Point3D preferredSidePoint)
        {
            var a = points[triangle.A];
            var b = points[triangle.B];
            var c = points[triangle.C];
            var ab = b - a;
            var ac = c - a;
            var normal = Vector3D.CrossProduct(ab, ac);
            if (normal.Length < 1e-6)
                normal = new Vector3D(0, 0, 1);
            else
                normal.Normalize();

            var preferredDirection = preferredSidePoint - basePoint;
            if (preferredDirection.Length > 1e-6 &&
                Vector3D.DotProduct(normal, preferredDirection) < 0)
            {
                normal = -normal;
            }

            return normal;
        }

        private static bool IsFinite(Point3D point)
        {
            return IsFiniteValue(point.X) &&
                   IsFiniteValue(point.Y) &&
                   IsFiniteValue(point.Z) &&
                   Math.Abs(point.X) <= MaxReasonableCoordinateAbsMm &&
                   Math.Abs(point.Y) <= MaxReasonableCoordinateAbsMm &&
                   Math.Abs(point.Z) <= MaxReasonableCoordinateAbsMm;
        }

        private static bool IsFiniteValue(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static Point3D Lerp(Point3D start, Point3D end, double alpha)
        {
            return new Point3D(
                start.X + (end.X - start.X) * alpha,
                start.Y + (end.Y - start.Y) * alpha,
                start.Z + (end.Z - start.Z) * alpha);
        }

        private static PathBounds2D BuildBounds(IReadOnlyList<GCodeMove> moves)
        {
            if (moves.Count == 0)
                return new PathBounds2D(0, 0, 0, 0, false);

            double minX = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity;
            double maxY = double.NegativeInfinity;

            foreach (var move in moves)
            {
                Extend(move.Start);
                Extend(move.End);
            }

            return new PathBounds2D(minX, maxX, minY, maxY, true);

            void Extend(Point3D point)
            {
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
            }
        }

        private static double DistanceSquared(SurfacePoint a, SurfacePoint b)
        {
            var du = a.U - b.U;
            var dv = a.V - b.V;
            return du * du + dv * dv;
        }

        private static BarycentricWeights NormalizeWeights(BarycentricWeights weights)
        {
            var sum = weights.A + weights.B + weights.C;
            if (Math.Abs(sum) < MinDeterminant)
                return new BarycentricWeights(1, 0, 0);

            return new BarycentricWeights(weights.A / sum, weights.B / sum, weights.C / sum);
        }

        private static BarycentricWeights ClampAndNormalizeWeights(BarycentricWeights weights)
        {
            var clamped = new BarycentricWeights(
                Math.Max(0, weights.A),
                Math.Max(0, weights.B),
                Math.Max(0, weights.C));
            return NormalizeWeights(clamped);
        }

        private static double GetTriangleArea(IReadOnlyList<SurfacePoint> points, MeshTriangle triangle)
        {
            var a = points[triangle.A];
            var b = points[triangle.B];
            var c = points[triangle.C];
            return ((b.U - a.U) * (c.V - a.V) - (b.V - a.V) * (c.U - a.U)) / 2.0;
        }

        private enum Axis
        {
            X,
            Y,
            Z
        }

        private readonly struct SurfacePoint
        {
            public SurfacePoint(double u, double v)
            {
                U = u;
                V = v;
            }

            public double U { get; }
            public double V { get; }
        }

        private readonly struct SurfaceBounds
        {
            public SurfaceBounds(double minU, double maxU, double minV, double maxV)
            {
                MinU = minU;
                MaxU = maxU;
                MinV = minV;
                MaxV = maxV;
            }

            public double MinU { get; }
            public double MaxU { get; }
            public double MinV { get; }
            public double MaxV { get; }
        }

        private readonly struct FitTransform
        {
            public FitTransform(double scale, double sourceCenterX, double sourceCenterY, double surfaceCenterU, double surfaceCenterV)
            {
                Scale = scale;
                SourceCenterX = sourceCenterX;
                SourceCenterY = sourceCenterY;
                SurfaceCenterU = surfaceCenterU;
                SurfaceCenterV = surfaceCenterV;
            }

            public double Scale { get; }
            public double SourceCenterX { get; }
            public double SourceCenterY { get; }
            public double SurfaceCenterU { get; }
            public double SurfaceCenterV { get; }
        }

        private readonly struct MeshTriangle
        {
            public MeshTriangle(int a, int b, int c)
            {
                A = a;
                B = b;
                C = c;
            }

            public int A { get; }
            public int B { get; }
            public int C { get; }
        }

        private readonly struct BarycentricWeights
        {
            public BarycentricWeights(double a, double b, double c)
            {
                A = a;
                B = b;
                C = c;
            }

            public double A { get; }
            public double B { get; }
            public double C { get; }
        }
    }
}

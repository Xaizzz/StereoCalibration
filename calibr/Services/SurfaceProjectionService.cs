using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Деформирует печатные траектории по динамической поверхности маркеров.
    ///
    /// Ключевая идея: при старте печати фиксируется референсная поверхность и
    /// каждая точка G-code получает локальную привязку (triangle + barycentric +
    /// offset по нормали). На последующих кадрах точки не "рескейлятся заново",
    /// а деформируются по текущему положению тех же маркеров.
    /// </summary>
    public sealed class SurfaceProjectionService
    {
        private const double FitMarginRatio = 0.72;
        private const double MinTriangleArea = 1e-3;
        private const double MinDeterminant = 1e-9;
        private const double BarycentricTolerance = 1e-6;
        private const double PathSurfaceLiftMm = 4.0;
        private const double MaxReasonableCoordinateAbsMm = 20000.0;
        private const double MissingMarkerInfluenceSmoothingMm = 90.0;
        private const double MaxExtrusionSegmentLengthMm = 2.5;
        private const double MaxTravelSegmentLengthMm = 8.0;

        public const int MinMarkersForDeformation = 6;
        public const double SafetyClearanceMm = PathSurfaceLiftMm;

        public bool TryCreateReference(
            ParsedGCodePath sourcePath,
            IReadOnlyList<KeyValuePair<int, Point3D>> orderedMarkers,
            Point3D preferredSidePoint,
            out SurfacePrintReference printReference)
        {
            printReference = null!;

            if (sourcePath == null ||
                sourcePath.Moves.Count == 0 ||
                orderedMarkers == null ||
                orderedMarkers.Count < 3)
            {
                return false;
            }

            var sourceBounds = sourcePath.ExtrusionBounds.IsValid
                ? sourcePath.ExtrusionBounds
                : sourcePath.MotionBounds;
            if (!sourceBounds.IsValid)
                return false;

            var markerIds = orderedMarkers.Select(marker => marker.Key).ToList();
            var referencePoints3D = orderedMarkers.Select(marker => marker.Value).ToList();
            var projectionAxes = ChooseProjectionAxes(referencePoints3D);
            var referencePoints2D = referencePoints3D
                .Select(point => new SurfacePoint(
                    GetAxisValue(point, projectionAxes.First),
                    GetAxisValue(point, projectionAxes.Second)))
                .ToList();

            var triangles = BuildDelaunayTriangles(referencePoints2D);
            if (triangles.Count == 0)
                return false;

            var surfaceBounds = GetSurfaceBounds(referencePoints2D);
            var transform = BuildAutoFitTransform(sourceBounds, surfaceBounds);
            var safeSurfaceBounds = BuildSafeSurfaceBounds(surfaceBounds);
            var sourceMinZ = sourcePath.MinZ;

            var anchoredMoves = new List<AnchoredPrintMove>(sourcePath.Moves.Count);
            foreach (var move in sourcePath.Moves)
            {
                if (!TryBuildAnchoredMoveSegments(
                    move,
                    sourceMinZ,
                    transform,
                    safeSurfaceBounds,
                    triangles,
                    referencePoints2D,
                    anchoredMoves))
                {
                    return false;
                }
            }

            printReference = new SurfacePrintReference(
                markerIds,
                referencePoints3D,
                anchoredMoves,
                preferredSidePoint);
            return true;
        }

        public bool TryProjectPath(
            SurfacePrintReference printReference,
            IReadOnlyList<KeyValuePair<int, Point3D>> currentMarkers,
            Point3D preferredSidePoint,
            out ProjectedPrintPath projectedPath)
        {
            projectedPath = null!;
            if (printReference == null ||
                printReference.AnchoredMoves.Count == 0 ||
                printReference.MarkerIds.Count < 3 ||
                currentMarkers == null ||
                currentMarkers.Count < 3)
            {
                return false;
            }

            var currentMarkerMap = currentMarkers.ToDictionary(marker => marker.Key, marker => marker.Value);
            var currentPoints = new List<Point3D>(printReference.MarkerIds.Count);
            var visibleReferenceIndices = new List<int>(printReference.MarkerIds.Count);
            var hasCurrentMarker = new bool[printReference.MarkerIds.Count];
            for (var i = 0; i < printReference.MarkerIds.Count; i++)
            {
                var markerId = printReference.MarkerIds[i];
                if (currentMarkerMap.TryGetValue(markerId, out var currentMarker))
                {
                    currentPoints.Add(currentMarker);
                    visibleReferenceIndices.Add(i);
                    hasCurrentMarker[i] = true;
                }
                else
                {
                    currentPoints.Add(printReference.ReferenceMarkerPositions[i]);
                }
            }

            if (visibleReferenceIndices.Count >= 3 &&
                visibleReferenceIndices.Count < printReference.MarkerIds.Count)
            {
                for (var i = 0; i < printReference.MarkerIds.Count; i++)
                {
                    if (hasCurrentMarker[i])
                        continue;

                    currentPoints[i] = EstimateMissingMarkerPosition(
                        i,
                        printReference.ReferenceMarkerPositions,
                        currentPoints,
                        visibleReferenceIndices);
                }
            }

            var projectedMoves = new List<GCodeMove>(printReference.AnchoredMoves.Count);
            var projectedExtrusionMoves = new List<GCodeMove>();
            foreach (var anchoredMove in printReference.AnchoredMoves)
            {
                var startPoint = EvaluateAnchor(anchoredMove.StartAnchor, currentPoints, preferredSidePoint);
                var endPoint = EvaluateAnchor(anchoredMove.EndAnchor, currentPoints, preferredSidePoint);

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
                printReference.MarkerIds.Count);
            return true;
        }

        private static bool TryBuildAnchor(
            Point3D sourcePoint,
            double sourceMinZ,
            FitTransform transform,
            SurfaceBounds safeSurfaceBounds,
            IReadOnlyList<SurfaceTriangle> triangles,
            IReadOnlyList<SurfacePoint> referencePoints2D,
            out SurfaceAnchor anchor)
        {
            var projectedUv = new SurfacePoint(
                transform.SurfaceCenterU + (sourcePoint.X - transform.SourceCenterX) * transform.Scale,
                transform.SurfaceCenterV + (sourcePoint.Y - transform.SourceCenterY) * transform.Scale);
            projectedUv = ClampToBounds(projectedUv, safeSurfaceBounds);

            var (triangle, barycentric) = FindBestTriangle(projectedUv, triangles, referencePoints2D);
            var normalOffset = Math.Max(0, sourcePoint.Z - sourceMinZ);
            anchor = new SurfaceAnchor(
                triangle.A,
                triangle.B,
                triangle.C,
                barycentric.A,
                barycentric.B,
                barycentric.C,
                normalOffset);
            return true;
        }

        private static bool TryBuildAnchoredMoveSegments(
            GCodeMove sourceMove,
            double sourceMinZ,
            FitTransform transform,
            SurfaceBounds safeSurfaceBounds,
            IReadOnlyList<SurfaceTriangle> triangles,
            IReadOnlyList<SurfacePoint> referencePoints2D,
            List<AnchoredPrintMove> anchoredMoves)
        {
            var maxSegmentLength = sourceMove.IsExtrusion
                ? MaxExtrusionSegmentLengthMm
                : MaxTravelSegmentLengthMm;
            var segmentCount = Math.Max(1, (int)Math.Ceiling(sourceMove.LengthMm / Math.Max(0.5, maxSegmentLength)));

            if (!TryBuildAnchor(sourceMove.Start, sourceMinZ, transform, safeSurfaceBounds, triangles, referencePoints2D, out var currentStartAnchor))
                return false;

            var extrusionDeltaPerSegment = sourceMove.IsExtrusion
                ? sourceMove.ExtrusionDelta / segmentCount
                : sourceMove.ExtrusionDelta;

            for (var segmentIndex = 1; segmentIndex <= segmentCount; segmentIndex++)
            {
                var t = segmentIndex / (double)segmentCount;
                var currentEndPoint = Lerp(sourceMove.Start, sourceMove.End, t);
                if (!TryBuildAnchor(currentEndPoint, sourceMinZ, transform, safeSurfaceBounds, triangles, referencePoints2D, out var currentEndAnchor))
                    return false;

                anchoredMoves.Add(new AnchoredPrintMove(
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

        private static Point3D EvaluateAnchor(
            SurfaceAnchor anchor,
            IReadOnlyList<Point3D> currentPoints,
            Point3D preferredSidePoint)
        {
            var triangle = new SurfaceTriangle(anchor.TriangleA, anchor.TriangleB, anchor.TriangleC);
            var weights = new BarycentricWeights(anchor.WeightA, anchor.WeightB, anchor.WeightC);
            var basePoint = InterpolatePoint(currentPoints, triangle, weights);
            var normal = GetTriangleNormal(currentPoints, triangle, basePoint, preferredSidePoint);

            // Жесткий no-penetration: всегда остаемся на стороне нормали с минимальным
            // безопасным зазором.
            var offsetMm = Math.Max(SafetyClearanceMm, anchor.NormalOffsetMm + SafetyClearanceMm);
            var deformedPoint = new Point3D(
                basePoint.X + normal.X * offsetMm,
                basePoint.Y + normal.Y * offsetMm,
                basePoint.Z + normal.Z * offsetMm);

            var penetrationCheck = Vector3D.DotProduct(deformedPoint - basePoint, normal);
            if (penetrationCheck < SafetyClearanceMm)
            {
                deformedPoint = new Point3D(
                    basePoint.X + normal.X * SafetyClearanceMm,
                    basePoint.Y + normal.Y * SafetyClearanceMm,
                    basePoint.Z + normal.Z * SafetyClearanceMm);
            }

            return deformedPoint;
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

        private static Point3D EstimateMissingMarkerPosition(
            int missingReferenceIndex,
            IReadOnlyList<Point3D> referencePoints,
            IReadOnlyList<Point3D> currentPoints,
            IReadOnlyList<int> visibleReferenceIndices)
        {
            if (visibleReferenceIndices.Count == 0)
                return referencePoints[missingReferenceIndex];

            var referencePoint = referencePoints[missingReferenceIndex];
            var weightedDisplacement = new Vector3D();
            var weightSum = 0.0;
            var smoothingSquared = MissingMarkerInfluenceSmoothingMm * MissingMarkerInfluenceSmoothingMm;

            for (var i = 0; i < visibleReferenceIndices.Count; i++)
            {
                var visibleIndex = visibleReferenceIndices[i];
                var referenceVisible = referencePoints[visibleIndex];
                var currentVisible = currentPoints[visibleIndex];

                var distanceSquared = DistanceSquared(referencePoint, referenceVisible);
                var weight = 1.0 / (distanceSquared + smoothingSquared);
                weightedDisplacement += (currentVisible - referenceVisible) * weight;
                weightSum += weight;
            }

            if (weightSum < MinDeterminant)
                return referencePoint;

            return referencePoint + weightedDisplacement / weightSum;
        }

        private static double DistanceSquared(Point3D a, Point3D b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        private static Point3D Lerp(Point3D start, Point3D end, double alpha)
        {
            return new Point3D(
                start.X + (end.X - start.X) * alpha,
                start.Y + (end.Y - start.Y) * alpha,
                start.Z + (end.Z - start.Z) * alpha);
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

        private static SurfacePoint ClampToBounds(SurfacePoint point, SurfaceBounds bounds)
        {
            return new SurfacePoint(
                Math.Max(bounds.MinU, Math.Min(bounds.MaxU, point.U)),
                Math.Max(bounds.MinV, Math.Min(bounds.MaxV, point.V)));
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
                ExtendBounds(move.Start);
                ExtendBounds(move.End);
            }

            return new PathBounds2D(minX, maxX, minY, maxY, true);

            void ExtendBounds(Point3D point)
            {
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
            }
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

        private static (SurfaceTriangle Triangle, BarycentricWeights Weights) FindBestTriangle(
            SurfacePoint point,
            IReadOnlyList<SurfaceTriangle> triangles,
            IReadOnlyList<SurfacePoint> points2D)
        {
            SurfaceTriangle? bestTriangle = null;
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
                    {
                        bestWeights = weights;
                    }
                }
            }

            if (bestTriangle.HasValue)
                return (bestTriangle.Value, ClampAndNormalizeWeights(bestWeights));

            return (triangles[0], new BarycentricWeights(1, 0, 0));
        }

        private static SurfacePoint GetClosestPointOnTriangle(
            SurfacePoint point,
            IReadOnlyList<SurfacePoint> points,
            SurfaceTriangle triangle)
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

        private static double DistanceSquared(SurfacePoint a, SurfacePoint b)
        {
            var du = a.U - b.U;
            var dv = a.V - b.V;
            return du * du + dv * dv;
        }

        private static bool TryGetBarycentricWeights(
            SurfacePoint p,
            IReadOnlyList<SurfacePoint> points,
            SurfaceTriangle triangle,
            out BarycentricWeights weights)
        {
            var a = points[triangle.A];
            var b = points[triangle.B];
            var c = points[triangle.C];

            var denominator = (b.V - c.V) * (a.U - c.U) + (c.U - b.U) * (a.V - c.V);
            if (Math.Abs(denominator) < MinDeterminant)
            {
                weights = default;
                return false;
            }

            var wa = ((b.V - c.V) * (p.U - c.U) + (c.U - b.U) * (p.V - c.V)) / denominator;
            var wb = ((c.V - a.V) * (p.U - c.U) + (a.U - c.U) * (p.V - c.V)) / denominator;
            var wc = 1.0 - wa - wb;

            weights = new BarycentricWeights(wa, wb, wc);
            return true;
        }

        private static BarycentricWeights ClampAndNormalizeWeights(BarycentricWeights weights)
        {
            var wa = Math.Max(0, Math.Min(1, weights.A));
            var wb = Math.Max(0, Math.Min(1, weights.B));
            var wc = Math.Max(0, Math.Min(1, weights.C));
            var sum = wa + wb + wc;
            if (sum < MinDeterminant)
                return new BarycentricWeights(1, 0, 0);

            return new BarycentricWeights(wa / sum, wb / sum, wc / sum);
        }

        private static BarycentricWeights NormalizeWeights(BarycentricWeights weights)
        {
            var sum = weights.A + weights.B + weights.C;
            if (Math.Abs(sum) < MinDeterminant)
                return new BarycentricWeights(1, 0, 0);

            return new BarycentricWeights(weights.A / sum, weights.B / sum, weights.C / sum);
        }

        private static Point3D InterpolatePoint(
            IReadOnlyList<Point3D> points3D,
            SurfaceTriangle triangle,
            BarycentricWeights weights)
        {
            var a = points3D[triangle.A];
            var b = points3D[triangle.B];
            var c = points3D[triangle.C];
            return new Point3D(
                a.X * weights.A + b.X * weights.B + c.X * weights.C,
                a.Y * weights.A + b.Y * weights.B + c.Y * weights.C,
                a.Z * weights.A + b.Z * weights.B + c.Z * weights.C);
        }

        private static Vector3D GetTriangleNormal(
            IReadOnlyList<Point3D> points3D,
            SurfaceTriangle triangle,
            Point3D basePoint,
            Point3D preferredSidePoint)
        {
            var a = points3D[triangle.A];
            var b = points3D[triangle.B];
            var c = points3D[triangle.C];

            var ab = b - a;
            var ac = c - a;
            var normal = Vector3D.CrossProduct(ab, ac);
            if (normal.LengthSquared < MinDeterminant)
            {
                var fallback = preferredSidePoint - basePoint;
                if (fallback.LengthSquared < MinDeterminant)
                    return new Vector3D(0, 0, 1);

                fallback.Normalize();
                return fallback;
            }

            normal.Normalize();
            var preferredDirection = preferredSidePoint - basePoint;
            if (preferredDirection.LengthSquared > MinDeterminant &&
                Vector3D.DotProduct(normal, preferredDirection) < 0)
            {
                normal = -normal;
            }

            return normal;
        }

        private static (SurfaceAxis First, SurfaceAxis Second) ChooseProjectionAxes(IReadOnlyList<Point3D> points)
        {
            var ranges = new[]
            {
                (Axis: SurfaceAxis.X, Range: points.Max(point => point.X) - points.Min(point => point.X)),
                (Axis: SurfaceAxis.Y, Range: points.Max(point => point.Y) - points.Min(point => point.Y)),
                (Axis: SurfaceAxis.Z, Range: points.Max(point => point.Z) - points.Min(point => point.Z))
            };

            var selectedAxes = ranges
                .OrderByDescending(range => range.Range)
                .Take(2)
                .Select(range => range.Axis)
                .ToArray();

            return (selectedAxes[0], selectedAxes[1]);
        }

        private static double GetAxisValue(Point3D point, SurfaceAxis axis)
        {
            return axis switch
            {
                SurfaceAxis.X => point.X,
                SurfaceAxis.Y => point.Y,
                SurfaceAxis.Z => point.Z,
                _ => point.X
            };
        }

        private static SurfaceBounds GetSurfaceBounds(IReadOnlyList<SurfacePoint> points)
        {
            return new SurfaceBounds(
                points.Min(point => point.U),
                points.Max(point => point.U),
                points.Min(point => point.V),
                points.Max(point => point.V));
        }

        private static List<SurfaceTriangle> BuildDelaunayTriangles(IReadOnlyList<SurfacePoint> sourcePoints)
        {
            var points = sourcePoints.ToList();
            var bounds = GetSurfaceBounds(points);
            var delta = Math.Max(bounds.MaxU - bounds.MinU, bounds.MaxV - bounds.MinV);
            if (delta <= 1e-6)
                return new List<SurfaceTriangle>();

            var midU = (bounds.MinU + bounds.MaxU) / 2.0;
            var midV = (bounds.MinV + bounds.MaxV) / 2.0;
            var firstSuperIndex = points.Count;
            points.Add(new SurfacePoint(midU - 20 * delta, midV - delta));
            points.Add(new SurfacePoint(midU, midV + 20 * delta));
            points.Add(new SurfacePoint(midU + 20 * delta, midV - delta));

            var triangles = new List<SurfaceTriangle>
            {
                new SurfaceTriangle(firstSuperIndex, firstSuperIndex + 1, firstSuperIndex + 2)
            };

            for (var pointIndex = 0; pointIndex < sourcePoints.Count; pointIndex++)
            {
                var point = points[pointIndex];
                var badTriangles = triangles
                    .Where(triangle => CircumcircleContains(points, triangle, point))
                    .ToList();

                var boundaryEdges = new List<SurfaceEdge>();
                foreach (var triangle in badTriangles)
                {
                    AddOrRemoveBoundaryEdge(boundaryEdges, new SurfaceEdge(triangle.A, triangle.B));
                    AddOrRemoveBoundaryEdge(boundaryEdges, new SurfaceEdge(triangle.B, triangle.C));
                    AddOrRemoveBoundaryEdge(boundaryEdges, new SurfaceEdge(triangle.C, triangle.A));
                }

                foreach (var triangle in badTriangles)
                {
                    triangles.Remove(triangle);
                }

                foreach (var edge in boundaryEdges)
                {
                    var triangle = new SurfaceTriangle(edge.A, edge.B, pointIndex);
                    if (Math.Abs(GetTriangleArea(points, triangle)) < MinTriangleArea)
                        continue;

                    if (GetTriangleArea(points, triangle) < 0)
                        triangle = new SurfaceTriangle(edge.B, edge.A, pointIndex);

                    triangles.Add(triangle);
                }
            }

            return triangles
                .Where(triangle => triangle.A < firstSuperIndex &&
                                   triangle.B < firstSuperIndex &&
                                   triangle.C < firstSuperIndex)
                .ToList();
        }

        private static void AddOrRemoveBoundaryEdge(List<SurfaceEdge> edges, SurfaceEdge edge)
        {
            var existingIndex = edges.FindIndex(existing => existing.Equals(edge));
            if (existingIndex >= 0)
            {
                edges.RemoveAt(existingIndex);
            }
            else
            {
                edges.Add(edge);
            }
        }

        private static bool CircumcircleContains(
            IReadOnlyList<SurfacePoint> points,
            SurfaceTriangle triangle,
            SurfacePoint point)
        {
            var a = points[triangle.A];
            var b = points[triangle.B];
            var c = points[triangle.C];

            var ax = a.U - point.U;
            var ay = a.V - point.V;
            var bx = b.U - point.U;
            var by = b.V - point.V;
            var cx = c.U - point.U;
            var cy = c.V - point.V;

            var determinant =
                (ax * ax + ay * ay) * (bx * cy - cx * by) -
                (bx * bx + by * by) * (ax * cy - cx * ay) +
                (cx * cx + cy * cy) * (ax * by - bx * ay);

            var orientation = GetTriangleArea(points, triangle);
            return orientation > 0
                ? determinant > 1e-6
                : determinant < -1e-6;
        }

        private static double GetTriangleArea(IReadOnlyList<SurfacePoint> points, SurfaceTriangle triangle)
        {
            var a = points[triangle.A];
            var b = points[triangle.B];
            var c = points[triangle.C];
            return (b.U - a.U) * (c.V - a.V) - (b.V - a.V) * (c.U - a.U);
        }

        private enum SurfaceAxis
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

        private readonly struct SurfaceTriangle
        {
            public SurfaceTriangle(int a, int b, int c)
            {
                A = a;
                B = b;
                C = c;
            }

            public int A { get; }
            public int B { get; }
            public int C { get; }
        }

        private readonly struct SurfaceEdge : IEquatable<SurfaceEdge>
        {
            public SurfaceEdge(int a, int b)
            {
                A = a;
                B = b;
            }

            public int A { get; }
            public int B { get; }

            public bool Equals(SurfaceEdge other)
            {
                return (A == other.A && B == other.B) ||
                       (A == other.B && B == other.A);
            }

            public override bool Equals(object? obj)
            {
                return obj is SurfaceEdge other && Equals(other);
            }

            public override int GetHashCode()
            {
                var min = Math.Min(A, B);
                var max = Math.Max(A, B);
                return HashCode.Combine(min, max);
            }
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
    }
}

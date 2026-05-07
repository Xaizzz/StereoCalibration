using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Быстрый TPS/RBF-деформатор сетки. Тяжёлая часть (матрица обратного
    /// преобразования и базис вершин) считается один раз для референсных маркеров.
    /// </summary>
    public sealed class RbfDeformationService
    {
        private const int PolynomialTermCount = 4;
        private const double Epsilon = 1e-10;
        private const double KernelRegularization = 1e-6;
        private const double PolynomialRegularization = 1e-8;

        private Point3D[] _referenceVertices = Array.Empty<Point3D>();
        private Point3D[] _referenceControlPoints = Array.Empty<Point3D>();
        private NormalizedPoint[] _normalizedControlPoints = Array.Empty<NormalizedPoint>();
        private double[,] _inverseSystem = new double[0, 0];
        private double[,] _vertexBasis = new double[0, 0];
        private Point3D _normalizationOrigin;
        private double _normalizationScale = 1.0;
        private int _systemSize;

        public bool IsPrepared => _referenceVertices.Length > 0 && _referenceControlPoints.Length >= 3;
        public int ControlPointCount => _referenceControlPoints.Length;
        public int VertexCount => _referenceVertices.Length;

        public void Prepare(
            IReadOnlyList<Point3D> referenceVertices,
            IReadOnlyList<Point3D> referenceControlPoints)
        {
            if (referenceVertices == null)
                throw new ArgumentNullException(nameof(referenceVertices));
            if (referenceControlPoints == null)
                throw new ArgumentNullException(nameof(referenceControlPoints));
            if (referenceVertices.Count == 0)
                throw new ArgumentException("Сетка модели не содержит вершин.", nameof(referenceVertices));
            if (referenceControlPoints.Count < 3)
                throw new ArgumentException("Для RBF-деформации нужно минимум 3 опорных маркера.", nameof(referenceControlPoints));

            _referenceVertices = referenceVertices.ToArray();
            _referenceControlPoints = referenceControlPoints.ToArray();
            _systemSize = _referenceControlPoints.Length + PolynomialTermCount;
            BuildNormalization();
            _normalizedControlPoints = _referenceControlPoints
                .Select(NormalizePoint)
                .ToArray();

            _inverseSystem = Invert(BuildSystemMatrix());
            _vertexBasis = BuildVertexBasis();
        }

        public Point3D[] Apply(IReadOnlyList<Point3D> currentControlPoints)
        {
            if (!IsPrepared)
                return Array.Empty<Point3D>();

            if (currentControlPoints == null)
                throw new ArgumentNullException(nameof(currentControlPoints));
            if (currentControlPoints.Count != _referenceControlPoints.Length)
                throw new ArgumentException("Количество текущих маркеров не совпадает с референсным набором.", nameof(currentControlPoints));

            var coeffX = SolveCoefficients(currentControlPoints, axis: 0);
            var coeffY = SolveCoefficients(currentControlPoints, axis: 1);
            var coeffZ = SolveCoefficients(currentControlPoints, axis: 2);
            var result = new Point3D[_referenceVertices.Length];

            for (var vertexIndex = 0; vertexIndex < _referenceVertices.Length; vertexIndex++)
            {
                result[vertexIndex] = EvaluatePointWithPrecomputedBasis(
                    _referenceVertices[vertexIndex],
                    vertexIndex,
                    coeffX,
                    coeffY,
                    coeffZ);
            }

            return result;
        }

        /// <summary>
        /// Деформирует произвольные точки в той же RBF-модели.
        /// Полезно для диагностики: можно проверять точность «приклейки» маркеров.
        /// </summary>
        public Point3D[] ApplyToPoints(
            IReadOnlyList<Point3D> sourcePoints,
            IReadOnlyList<Point3D> currentControlPoints)
        {
            if (!IsPrepared)
                return Array.Empty<Point3D>();

            if (sourcePoints == null)
                throw new ArgumentNullException(nameof(sourcePoints));
            if (currentControlPoints == null)
                throw new ArgumentNullException(nameof(currentControlPoints));
            if (currentControlPoints.Count != _referenceControlPoints.Length)
                throw new ArgumentException("Количество текущих маркеров не совпадает с референсным набором.", nameof(currentControlPoints));

            var coeffX = SolveCoefficients(currentControlPoints, axis: 0);
            var coeffY = SolveCoefficients(currentControlPoints, axis: 1);
            var coeffZ = SolveCoefficients(currentControlPoints, axis: 2);
            var result = new Point3D[sourcePoints.Count];
            for (var i = 0; i < sourcePoints.Count; i++)
            {
                result[i] = EvaluatePoint(
                    sourcePoints[i],
                    coeffX,
                    coeffY,
                    coeffZ);
            }

            return result;
        }

        private void BuildNormalization()
        {
            var minX = _referenceControlPoints.Min(point => point.X);
            var maxX = _referenceControlPoints.Max(point => point.X);
            var minY = _referenceControlPoints.Min(point => point.Y);
            var maxY = _referenceControlPoints.Max(point => point.Y);
            var minZ = _referenceControlPoints.Min(point => point.Z);
            var maxZ = _referenceControlPoints.Max(point => point.Z);

            _normalizationOrigin = new Point3D(
                (minX + maxX) / 2.0,
                (minY + maxY) / 2.0,
                (minZ + maxZ) / 2.0);

            var dx = maxX - minX;
            var dy = maxY - minY;
            var dz = maxZ - minZ;
            var diagonal = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            _normalizationScale = diagonal > Epsilon ? 1.0 / diagonal : 1.0;
        }

        private double[,] BuildSystemMatrix()
        {
            var count = _normalizedControlPoints.Length;
            var matrix = new double[_systemSize, _systemSize];

            for (var row = 0; row < count; row++)
            {
                var rowPoint = _normalizedControlPoints[row];
                for (var col = 0; col < count; col++)
                {
                    matrix[row, col] = Kernel(Distance(rowPoint, _normalizedControlPoints[col]));
                }

                matrix[row, row] += KernelRegularization;
                matrix[row, count] = 1.0;
                matrix[row, count + 1] = rowPoint.X;
                matrix[row, count + 2] = rowPoint.Y;
                matrix[row, count + 3] = rowPoint.Z;

                matrix[count, row] = 1.0;
                matrix[count + 1, row] = rowPoint.X;
                matrix[count + 2, row] = rowPoint.Y;
                matrix[count + 3, row] = rowPoint.Z;
            }

            for (var i = count; i < _systemSize; i++)
            {
                matrix[i, i] = PolynomialRegularization;
            }

            return matrix;
        }

        private double[,] BuildVertexBasis()
        {
            var count = _normalizedControlPoints.Length;
            var basis = new double[_referenceVertices.Length, _systemSize];
            for (var vertexIndex = 0; vertexIndex < _referenceVertices.Length; vertexIndex++)
            {
                var point = NormalizePoint(_referenceVertices[vertexIndex]);
                for (var controlIndex = 0; controlIndex < count; controlIndex++)
                {
                    basis[vertexIndex, controlIndex] = Kernel(Distance(point, _normalizedControlPoints[controlIndex]));
                }

                basis[vertexIndex, count] = 1.0;
                basis[vertexIndex, count + 1] = point.X;
                basis[vertexIndex, count + 2] = point.Y;
                basis[vertexIndex, count + 3] = point.Z;
            }

            return basis;
        }

        private double[] SolveCoefficients(IReadOnlyList<Point3D> currentControlPoints, int axis)
        {
            var rhs = new double[_systemSize];
            for (var i = 0; i < _referenceControlPoints.Length; i++)
            {
                rhs[i] = GetAxis(currentControlPoints[i], axis) - GetAxis(_referenceControlPoints[i], axis);
            }

            var result = new double[_systemSize];
            for (var row = 0; row < _systemSize; row++)
            {
                var value = 0.0;
                for (var col = 0; col < _systemSize; col++)
                {
                    value += _inverseSystem[row, col] * rhs[col];
                }

                result[row] = value;
            }

            return result;
        }

        private Point3D EvaluatePointWithPrecomputedBasis(
            Point3D sourcePoint,
            int vertexIndex,
            IReadOnlyList<double> coeffX,
            IReadOnlyList<double> coeffY,
            IReadOnlyList<double> coeffZ)
        {
            var dx = 0.0;
            var dy = 0.0;
            var dz = 0.0;
            for (var basisIndex = 0; basisIndex < _systemSize; basisIndex++)
            {
                var basis = _vertexBasis[vertexIndex, basisIndex];
                dx += basis * coeffX[basisIndex];
                dy += basis * coeffY[basisIndex];
                dz += basis * coeffZ[basisIndex];
            }

            return new Point3D(sourcePoint.X + dx, sourcePoint.Y + dy, sourcePoint.Z + dz);
        }

        private Point3D EvaluatePoint(
            Point3D sourcePoint,
            IReadOnlyList<double> coeffX,
            IReadOnlyList<double> coeffY,
            IReadOnlyList<double> coeffZ)
        {
            var normalized = NormalizePoint(sourcePoint);
            var count = _normalizedControlPoints.Length;
            var dx = 0.0;
            var dy = 0.0;
            var dz = 0.0;

            for (var controlIndex = 0; controlIndex < count; controlIndex++)
            {
                var basis = Kernel(Distance(normalized, _normalizedControlPoints[controlIndex]));
                dx += basis * coeffX[controlIndex];
                dy += basis * coeffY[controlIndex];
                dz += basis * coeffZ[controlIndex];
            }

            var poly0 = 1.0;
            var poly1 = normalized.X;
            var poly2 = normalized.Y;
            var poly3 = normalized.Z;
            var polynomialBaseIndex = count;

            dx += poly0 * coeffX[polynomialBaseIndex];
            dx += poly1 * coeffX[polynomialBaseIndex + 1];
            dx += poly2 * coeffX[polynomialBaseIndex + 2];
            dx += poly3 * coeffX[polynomialBaseIndex + 3];

            dy += poly0 * coeffY[polynomialBaseIndex];
            dy += poly1 * coeffY[polynomialBaseIndex + 1];
            dy += poly2 * coeffY[polynomialBaseIndex + 2];
            dy += poly3 * coeffY[polynomialBaseIndex + 3];

            dz += poly0 * coeffZ[polynomialBaseIndex];
            dz += poly1 * coeffZ[polynomialBaseIndex + 1];
            dz += poly2 * coeffZ[polynomialBaseIndex + 2];
            dz += poly3 * coeffZ[polynomialBaseIndex + 3];

            return new Point3D(sourcePoint.X + dx, sourcePoint.Y + dy, sourcePoint.Z + dz);
        }

        private NormalizedPoint NormalizePoint(Point3D point)
        {
            return new NormalizedPoint(
                (point.X - _normalizationOrigin.X) * _normalizationScale,
                (point.Y - _normalizationOrigin.Y) * _normalizationScale,
                (point.Z - _normalizationOrigin.Z) * _normalizationScale);
        }

        private static double GetAxis(Point3D point, int axis)
        {
            return axis switch
            {
                0 => point.X,
                1 => point.Y,
                2 => point.Z,
                _ => point.X
            };
        }

        private static double Kernel(double radius)
        {
            if (radius <= Epsilon)
                return 0.0;

            var radiusSquared = radius * radius;
            return radiusSquared * Math.Log(radius);
        }

        private static double Distance(NormalizedPoint a, NormalizedPoint b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double[,] Invert(double[,] source)
        {
            var size = source.GetLength(0);
            var augmented = new double[size, size * 2];

            for (var row = 0; row < size; row++)
            {
                for (var col = 0; col < size; col++)
                {
                    augmented[row, col] = source[row, col];
                }

                augmented[row, size + row] = 1.0;
            }

            for (var pivotIndex = 0; pivotIndex < size; pivotIndex++)
            {
                var pivotRow = pivotIndex;
                var pivotAbs = Math.Abs(augmented[pivotRow, pivotIndex]);
                for (var row = pivotIndex + 1; row < size; row++)
                {
                    var candidateAbs = Math.Abs(augmented[row, pivotIndex]);
                    if (candidateAbs > pivotAbs)
                    {
                        pivotAbs = candidateAbs;
                        pivotRow = row;
                    }
                }

                if (pivotAbs < Epsilon)
                    throw new InvalidOperationException("RBF-матрица вырождена: проверьте расположение модельных маркеров.");

                if (pivotRow != pivotIndex)
                {
                    SwapRows(augmented, pivotRow, pivotIndex);
                }

                var pivot = augmented[pivotIndex, pivotIndex];
                for (var col = 0; col < size * 2; col++)
                {
                    augmented[pivotIndex, col] /= pivot;
                }

                for (var row = 0; row < size; row++)
                {
                    if (row == pivotIndex)
                        continue;

                    var factor = augmented[row, pivotIndex];
                    if (Math.Abs(factor) < Epsilon)
                        continue;

                    for (var col = 0; col < size * 2; col++)
                    {
                        augmented[row, col] -= factor * augmented[pivotIndex, col];
                    }
                }
            }

            var inverse = new double[size, size];
            for (var row = 0; row < size; row++)
            {
                for (var col = 0; col < size; col++)
                {
                    inverse[row, col] = augmented[row, size + col];
                }
            }

            return inverse;
        }

        private static void SwapRows(double[,] matrix, int firstRow, int secondRow)
        {
            var columnCount = matrix.GetLength(1);
            for (var col = 0; col < columnCount; col++)
            {
                var temp = matrix[firstRow, col];
                matrix[firstRow, col] = matrix[secondRow, col];
                matrix[secondRow, col] = temp;
            }
        }

        private readonly struct NormalizedPoint
        {
            public NormalizedPoint(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public double X { get; }
            public double Y { get; }
            public double Z { get; }
        }
    }
}

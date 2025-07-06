using OpenCvSharp;
using StereoCalibration.Interfaces;
using StereoCalibration.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Реализация сервиса 3D триангуляции
    /// Инкапсулирует алгоритмы вычисления 3D координат из стерео изображений
    /// </summary>
    public class TriangulationService : ITriangulationService
    {
        /// <summary>
        /// Триангуляция точек из стерео изображений
        /// </summary>
        public List<Point3f> TriangulatePoints(
            Point2f[] leftPoints,
            Point2f[] rightPoints,
            CalibrationResult calibrationResult)
        {
            if (leftPoints == null || rightPoints == null || calibrationResult == null ||
                leftPoints.Length != rightPoints.Length || leftPoints.Length == 0 ||
                !calibrationResult.IsValid())
            {
                return new List<Point3f>();
            }

            try
            {
                Debug.WriteLine($"Начало триангуляции {leftPoints.Length} точек");

                // Создание матриц из данных калибровки
                var cameraMatrix1 = ArrayToMat(calibrationResult.CameraMatrix1);
                var cameraMatrix2 = ArrayToMat(calibrationResult.CameraMatrix2);
                var distCoeffs1 = VectorToMat(calibrationResult.DistCoeffs1);
                var distCoeffs2 = VectorToMat(calibrationResult.DistCoeffs2);
                var R = ArrayToMat(calibrationResult.R);
                var T = VectorToMat(calibrationResult.T);

                // Исправление искажений точек
                var undistortedLeft = new Point2f[leftPoints.Length];
                var undistortedRight = new Point2f[rightPoints.Length];

                Cv2.UndistortPoints(leftPoints, undistortedLeft, cameraMatrix1, distCoeffs1);
                Cv2.UndistortPoints(rightPoints, undistortedRight, cameraMatrix2, distCoeffs2);

                // Создание проекционных матриц
                var P1 = new Mat();
                var P2 = new Mat();

                // P1 = [I | 0] для первой камеры
                Mat.Eye(3, 4, MatType.CV_64FC1).CopyTo(P1);
                cameraMatrix1.CopyTo(P1[new Rect(0, 0, 3, 3)]);

                // P2 = K2 * [R | T] для второй камеры
                var RT = new Mat();
                Cv2.HConcat(new Mat[] { R, T }, RT);
                P2 = cameraMatrix2 * RT;

                // Триангуляция
                var points4D = new Mat();
                Cv2.TriangulatePoints(P1, P2, undistortedLeft, undistortedRight, points4D);

                // Преобразование в однородные координаты
                var result = new List<Point3f>();
                for (int i = 0; i < points4D.Cols; i++)
                {
                    var x = points4D.At<double>(0, i);
                    var y = points4D.At<double>(1, i);
                    var z = points4D.At<double>(2, i);
                    var w = points4D.At<double>(3, i);

                    if (Math.Abs(w) > 1e-6) // Избегаем деления на ноль
                    {
                        result.Add(new Point3f((float)(x / w), (float)(y / w), (float)(z / w)));
                    }
                    else
                    {
                        result.Add(new Point3f(0, 0, 0));
                    }
                }

                // Освобождение ресурсов
                cameraMatrix1.Dispose();
                cameraMatrix2.Dispose();
                distCoeffs1.Dispose();
                distCoeffs2.Dispose();
                R.Dispose();
                T.Dispose();
                P1.Dispose();
                P2.Dispose();
                RT.Dispose();
                points4D.Dispose();

                Debug.WriteLine($"Триангуляция завершена. Получено {result.Count} 3D точек");
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка триангуляции: {ex.Message}");
                return new List<Point3f>();
            }
        }

        /// <summary>
        /// Вычисление расстояния между двумя 3D точками
        /// </summary>
        public double CalculateDistance(Point3f point1, Point3f point2)
        {
            try
            {
                var dx = point2.X - point1.X;
                var dy = point2.Y - point1.Y;
                var dz = point2.Z - point1.Z;

                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка вычисления расстояния: {ex.Message}");
                return 0.0;
            }
        }

        /// <summary>
        /// Фильтрация 3D точек по глубине
        /// </summary>
        public List<Point3f> FilterPointsByDepth(List<Point3f> points, double minDepth, double maxDepth)
        {
            if (points == null || points.Count == 0)
                return new List<Point3f>();

            try
            {
                return points.Where(p => p.Z >= minDepth && p.Z <= maxDepth).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка фильтрации точек: {ex.Message}");
                return points;
            }
        }

        /// <summary>
        /// Фильтрация выбросов методом межквартильного размаха (IQR)
        /// </summary>
        public List<Point3f> FilterOutliers(List<Point3f> points)
        {
            if (points == null || points.Count < 4)
                return points;

            try
            {
                // Фильтрация по каждой оси отдельно
                var filteredByX = FilterOutliersByAxis(points, p => p.X);
                var filteredByY = FilterOutliersByAxis(filteredByX, p => p.Y);
                var filteredByZ = FilterOutliersByAxis(filteredByY, p => p.Z);

                return filteredByZ;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка фильтрации выбросов: {ex.Message}");
                return points;
            }
        }

        /// <summary>
        /// Фильтрация выбросов по одной оси
        /// </summary>
        private List<Point3f> FilterOutliersByAxis(List<Point3f> points, Func<Point3f, float> selector)
        {
            if (points.Count < 4)
                return points;

            var values = points.Select(selector).OrderBy(x => x).ToArray();
            var q1 = values[values.Length / 4];
            var q3 = values[3 * values.Length / 4];
            var iqr = q3 - q1;
            var lowerBound = q1 - 1.5f * iqr;
            var upperBound = q3 + 1.5f * iqr;

            return points.Where(p =>
            {
                var value = selector(p);
                return value >= lowerBound && value <= upperBound;
            }).ToList();
        }

        /// <summary>
        /// Вычисление среднего расстояния между точками
        /// </summary>
        public double CalculateAverageDistance(List<Point3f> points)
        {
            if (points == null || points.Count < 2)
                return 0.0;

            try
            {
                double totalDistance = 0.0;
                int count = 0;

                for (int i = 0; i < points.Count - 1; i++)
                {
                    for (int j = i + 1; j < points.Count; j++)
                    {
                        totalDistance += CalculateDistance(points[i], points[j]);
                        count++;
                    }
                }

                return count > 0 ? totalDistance / count : 0.0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка вычисления среднего расстояния: {ex.Message}");
                return 0.0;
            }
        }

        /// <summary>
        /// Преобразование двумерного массива в Mat
        /// </summary>
        private Mat ArrayToMat(double[,] array)
        {
            if (array == null)
                return new Mat();

            var rows = array.GetLength(0);
            var cols = array.GetLength(1);
            var mat = new Mat(rows, cols, MatType.CV_64FC1);

            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < cols; j++)
                {
                    mat.Set(i, j, array[i, j]);
                }
            }

            return mat;
        }

        /// <summary>
        /// Преобразование одномерного массива в Mat
        /// </summary>
        private Mat VectorToMat(double[] vector)
        {
            if (vector == null || vector.Length == 0)
                return new Mat();

            var mat = new Mat(vector.Length, 1, MatType.CV_64FC1);
            for (int i = 0; i < vector.Length; i++)
            {
                mat.Set(i, 0, vector[i]);
            }

            return mat;
        }

        /// <summary>
        /// Проверка качества триангуляции
        /// </summary>
        public double EvaluateTriangulationQuality(List<Point3f> points)
        {
            if (points == null || points.Count == 0)
                return 0.0;

            try
            {
                // Простая метрика: процент точек с разумной глубиной
                var validPoints = points.Count(p => p.Z > 0 && p.Z < 10000); // от 0 до 10 метров
                return (double)validPoints / points.Count * 100.0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка оценки качества триангуляции: {ex.Message}");
                return 0.0;
            }
        }
    }
} 
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OpenCvSharp;
using OpenCvSharp.Aruco;
using System.Windows.Media.Media3D;
using StereoCalibration.Models;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Сервис вычисления 3D-положений ArUco-маркеров по двум изображениям стереопары.
    /// 
    /// На вход сервис получает corners/ids, уже найденные в <see cref="StereoCameraService"/>.
    /// Здесь выполняется только геометрическая часть: сопоставление одинаковых ID
    /// между камерами, коррекция дисторсии, триангуляция и передача координат
    /// в систему 3D-сцены через событие <see cref="OnMarkerPositions3DUpdated"/>.
    /// </summary>
    public class ImageProcessingService
    {
        #region События
        /// <summary>
        /// Событие пакетного обновления 3D позиций маркеров за кадр.
        /// Координаты в событии находятся в системе координат первой камеры;
        /// перевод в визуальный базис выполняет <see cref="Scene3DService"/>.
        /// </summary>
        public event Action<IReadOnlyDictionary<int, (double X, double Y, double Z)>>? OnMarkerPositions3DUpdated;
        #endregion

        private readonly Dictionary _dictionary;
        private readonly DetectorParameters _detectorParameters;
        private readonly Dictionary<int, Point3D> _lastAcceptedMarkerPositions = new Dictionary<int, Point3D>();
        private readonly Dictionary<int, int> _lastAcceptedMarkerFrame = new Dictionary<int, int>();
        private int _frameIndex;

        /// <summary>Сбор телеметрии стереотрекинга (JSONL-хост назначает в UI).</summary>
        public IStereoTrackingDiagSink? StereoTrackingDiag { get; set; }

        private const double MinDepthMm = 30.0;
        private const double MaxDepthMm = 5000.0;
        private const double MaxMarkerJumpMm = 220.0;
        private const int MaxFramesForJumpCheck = 8;
        private const double MaxReasonableCoordinateAbsMm = 20000.0;
        
        public ImageProcessingService()
        {
            // Инициализация ArUco детектора
            _dictionary = ArucoDetectionProfile.CreateDictionary();
            _detectorParameters = ArucoDetectionProfile.CreateParameters();
        }
        
        /// <summary>
        /// Находит одинаковые ArUco ID в левой и правой камере и запускает
        /// триангуляцию каждого совпавшего маркера.
        /// 
        /// Если хотя бы одна камера не видит маркеры, отправляется пустой словарь.
        /// Это важно для TTL-логики в <see cref="Scene3DService"/>: сцена понимает,
        /// что новых измерений нет, но может временно удержать старые позиции.
        /// </summary>
        public void TriangulateArucoMarkers(Mat frame1, Mat frame2, 
            Point2f[][]? cornersAruco1, Point2f[][]? cornersAruco2,
            int[]? idsAruco1, int[]? idsAruco2,
            CalibrationResult calibrationResult,
            IReadOnlyCollection<int>? staleIdsCamera1 = null,
            IReadOnlyCollection<int>? staleIdsCamera2 = null)
        {
            if (calibrationResult == null)
                return;

            _frameIndex++;
            var utc = DateTime.UtcNow;
            var staleIds1 = staleIdsCamera1 == null ? new HashSet<int>() : new HashSet<int>(staleIdsCamera1);
            var staleIds2 = staleIdsCamera2 == null ? new HashSet<int>() : new HashSet<int>(staleIdsCamera2);
            EstimateIdOverlap(idsAruco1, idsAruco2, out var uniqCam1, out var uniqCam2, out var bilateralOverlap, out var unpairedSlots);
            void NotifyTracking(StereoTrackingFrameObservation o) =>
                StereoTrackingDiag?.NotifyFrame(o);

            if (cornersAruco1 == null || cornersAruco2 == null ||
                idsAruco1 == null || idsAruco2 == null || idsAruco1.Length == 0 || idsAruco2.Length == 0)
            {
                OnMarkerPositions3DUpdated?.Invoke(new Dictionary<int, (double X, double Y, double Z)>());
                NotifyTracking(new StereoTrackingFrameObservation
                {
                    StereoFrameSeq = _frameIndex,
                    TimestampUtc = utc,
                    HadRawDetections = (idsAruco1?.Length > 0) || (idsAruco2?.Length > 0),
                    UniqueIdsCam1 = uniqCam1,
                    UniqueIdsCam2 = uniqCam2,
                    BilateralOverlapCount = bilateralOverlap,
                    UnpairedMarkerIdSlots = unpairedSlots,
                    StereoPairsRejectedStale = 0,
                    StereoPairsEligibleForTriangulation = 0,
                    StaleMarkersReportedCam1 = staleIds1.Count,
                    StaleMarkersReportedCam2 = staleIds2.Count,
                    TriangulationAcceptedCount = 0,
                    ValidationRejectCoordinates = 0,
                    ValidationRejectDepth = 0,
                    ValidationRejectJump = 0,
                    AcceptedMarkerIds = null,
                    AcceptedZMm = null,
                    TriangulationSolveFailuresApprox = 0
                });
                return;
            }
            
            try
            {
                Debug.WriteLine($"=== НАЧАЛО ТРИАНГУЛЯЦИИ ===");
                Debug.WriteLine($"Найдено маркеров: камера 1 - {idsAruco1.Length}, камера 2 - {idsAruco2.Length}");

                var rejectedByStale = 0;
                var rejCoord = 0;
                var rejDepth = 0;
                var rejJump = 0;
                var triSolveFails = 0;

                // Сопоставление маркеров по ID. Триангуляция возможна только для
                // одного и того же физического маркера, найденного в обеих камерах.
                Dictionary<int, (Point2f[] left, Point2f[] right)> matchedMarkers = new Dictionary<int, (Point2f[], Point2f[])>();
                var rightIndexById = new Dictionary<int, int>();
                for (int j = 0; j < idsAruco2.Length; j++)
                {
                    if (!rightIndexById.ContainsKey(idsAruco2[j]))
                        rightIndexById[idsAruco2[j]] = j;
                }

                for (int i = 0; i < idsAruco1.Length; i++)
                {
                    if (!rightIndexById.TryGetValue(idsAruco1[i], out var rightIndex))
                        continue;

                    var markerId = idsAruco1[i];
                    if (staleIds1.Contains(markerId) || staleIds2.Contains(markerId))
                    {
                        rejectedByStale++;
                        continue;
                    }

                    matchedMarkers[markerId] = (cornersAruco1[i], cornersAruco2[rightIndex]);
                    Debug.WriteLine($"Найдено совпадение маркера ID {markerId}");
                }
                
                Debug.WriteLine($"Сопоставленных маркеров: {matchedMarkers.Count}");
                var triangulatedMarkers = new Dictionary<int, (double X, double Y, double Z)>();
                
                // Подготовка калибровочных матриц. В CalibrationResult данные
                // хранятся как обычные массивы, потому что так их удобно сохранять
                // в JSON. Для OpenCV их нужно временно превратить обратно в Mat.
                Mat cameraMatrix1Mat = CreateMatrixFromArray(calibrationResult.CameraMatrix1);
                Mat cameraMatrix2Mat = CreateMatrixFromArray(calibrationResult.CameraMatrix2);
                Mat distCoeffs1Mat = CreateVectorFromArray(calibrationResult.DistCoeffs1);
                Mat distCoeffs2Mat = CreateVectorFromArray(calibrationResult.DistCoeffs2);
                Mat R_stereo = CreateMatrixFromArray(calibrationResult.R);
                Mat T_stereo = CreateVectorFromArray(calibrationResult.T);
                
                // Триангуляция каждого маркера
                foreach (var marker in matchedMarkers)
                {
                    if (TriangulateMarker(marker.Key, marker.Value.left, marker.Value.right,
                        cameraMatrix1Mat, cameraMatrix2Mat, distCoeffs1Mat, distCoeffs2Mat,
                        calibrationResult, frame1, frame2, out var position))
                    {
                        if (!TryValidateTriangulatedMarker(marker.Key, position, out var failKind))
                        {
                            switch (failKind)
                            {
                                case TriangulationValidationFailureKind.BadCoordinatesSanity:
                                    rejCoord++;
                                    break;
                                case TriangulationValidationFailureKind.DepthRange:
                                    rejDepth++;
                                    break;
                                case TriangulationValidationFailureKind.TemporalJump:
                                    rejJump++;
                                    break;
                            }

                            Debug.WriteLine($"Маркер ID {marker.Key} отброшен после триангуляции: {DescribeValidationFailure(failKind)}");
                            continue;
                        }

                        triangulatedMarkers[marker.Key] = position;
                        _lastAcceptedMarkerPositions[marker.Key] = new Point3D(position.X, position.Y, position.Z);
                        _lastAcceptedMarkerFrame[marker.Key] = _frameIndex;
                    }
                    else
                    {
                        triSolveFails++;
                    }
                }

                OnMarkerPositions3DUpdated?.Invoke(triangulatedMarkers);
                PruneHistory();
                Debug.WriteLine(
                    $"Фильтр триангуляции: stale={rejectedByStale}, invalid.coord={rejCoord}, invalid.depth={rejDepth}, invalid.jump={rejJump}, accepted={triangulatedMarkers.Count}");
                
                Debug.WriteLine($"=== КОНЕЦ ТРИАНГУЛЯЦИИ ===");

                NotifyTracking(new StereoTrackingFrameObservation
                {
                    StereoFrameSeq = _frameIndex,
                    TimestampUtc = utc,
                    HadRawDetections = true,
                    UniqueIdsCam1 = uniqCam1,
                    UniqueIdsCam2 = uniqCam2,
                    BilateralOverlapCount = bilateralOverlap,
                    UnpairedMarkerIdSlots = unpairedSlots,
                    StereoPairsRejectedStale = rejectedByStale,
                    StereoPairsEligibleForTriangulation = matchedMarkers.Count,
                    StaleMarkersReportedCam1 = staleIds1.Count,
                    StaleMarkersReportedCam2 = staleIds2.Count,
                    TriangulationAcceptedCount = triangulatedMarkers.Count,
                    ValidationRejectCoordinates = rejCoord,
                    ValidationRejectDepth = rejDepth,
                    ValidationRejectJump = rejJump,
                    AcceptedMarkerIds = triangulatedMarkers.Count > 0 ? triangulatedMarkers.Keys.OrderBy(static x => x).ToArray() : null,
                    AcceptedZMm = triangulatedMarkers.Count > 0
                        ? triangulatedMarkers.Values.Select(static p => p.Z).ToArray()
                        : null,
                    TriangulationSolveFailuresApprox = triSolveFails
                });

                // Освобождение ресурсов
                cameraMatrix1Mat.Dispose();
                cameraMatrix2Mat.Dispose();
                distCoeffs1Mat.Dispose();
                distCoeffs2Mat.Dispose();
                R_stereo.Dispose();
                T_stereo.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ОШИБКА В ТРИАНГУЛЯЦИИ: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        private static void EstimateIdOverlap(int[]? ids1, int[]? ids2, out int uniq1, out int uniq2,
            out int bilateralOverlapCount, out int unpairedSlots)
        {
            uniq1 = uniq2 = bilateralOverlapCount = unpairedSlots = 0;
            if (ids1 == null || ids2 == null || ids1.Length == 0 || ids2.Length == 0)
                return;

            var s1 = new HashSet<int>();
            foreach (var id in ids1)
                s1.Add(id);
            var s2 = new HashSet<int>();
            foreach (var id in ids2)
                s2.Add(id);

            uniq1 = s1.Count;
            uniq2 = s2.Count;
            bilateralOverlapCount = s1.Count(id => s2.Contains(id));

            var only1 = 0;
            foreach (var id in s1)
            {
                if (!s2.Contains(id))
                    only1++;
            }

            var only2 = 0;
            foreach (var id in s2)
            {
                if (!s1.Contains(id))
                    only2++;
            }

            unpairedSlots = only1 + only2;
        }

        private static string DescribeValidationFailure(TriangulationValidationFailureKind kind) =>
            kind switch
            {
                TriangulationValidationFailureKind.BadCoordinatesSanity => "NaN / бесконечность или вне допустимых координат",
                TriangulationValidationFailureKind.DepthRange => "глубина вне полосы фильтра",
                TriangulationValidationFailureKind.TemporalJump => $"скачок 3D > {MaxMarkerJumpMm:F0} мм",
                _ => "не определено"
            };

        private bool TryValidateTriangulatedMarker(
            int markerId,
            (double X, double Y, double Z) position,
            out TriangulationValidationFailureKind failureKind)
        {
            failureKind = TriangulationValidationFailureKind.None;

            if (double.IsNaN(position.X) || double.IsNaN(position.Y) || double.IsNaN(position.Z) ||
                double.IsInfinity(position.X) || double.IsInfinity(position.Y) || double.IsInfinity(position.Z))
            {
                failureKind = TriangulationValidationFailureKind.BadCoordinatesSanity;
                return false;
            }

            if (Math.Abs(position.X) > MaxReasonableCoordinateAbsMm ||
                Math.Abs(position.Y) > MaxReasonableCoordinateAbsMm ||
                Math.Abs(position.Z) > MaxReasonableCoordinateAbsMm)
            {
                failureKind = TriangulationValidationFailureKind.BadCoordinatesSanity;
                return false;
            }

            if (position.Z < MinDepthMm || position.Z > MaxDepthMm)
            {
                failureKind = TriangulationValidationFailureKind.DepthRange;
                return false;
            }

            if (!_lastAcceptedMarkerPositions.TryGetValue(markerId, out var previousPoint) ||
                !_lastAcceptedMarkerFrame.TryGetValue(markerId, out var previousFrame))
                return true;

            var frameGap = _frameIndex - previousFrame;
            if (frameGap > MaxFramesForJumpCheck)
                return true;

            var dx = position.X - previousPoint.X;
            var dy = position.Y - previousPoint.Y;
            var dz = position.Z - previousPoint.Z;
            var jump = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (jump > MaxMarkerJumpMm)
            {
                failureKind = TriangulationValidationFailureKind.TemporalJump;
                return false;
            }

            return true;
        }

        private void PruneHistory()
        {
            if (_lastAcceptedMarkerFrame.Count == 0)
                return;

            var staleMarkerIds = _lastAcceptedMarkerFrame
                .Where(item => _frameIndex - item.Value > 120)
                .Select(item => item.Key)
                .ToArray();

            foreach (var markerId in staleMarkerIds)
            {
                _lastAcceptedMarkerFrame.Remove(markerId);
                _lastAcceptedMarkerPositions.Remove(markerId);
            }
        }
        
        /// <summary>
        /// Триангулирует один ArUco-маркер по четырём углам в двух камерах.
        /// 
        /// Важная причина такой реализации: нельзя просто усреднить 2D-углы
        /// на изображении и триангулировать один центр. Если табличка наклонена,
        /// средний 2D-центр не совпадает с проекцией реального 3D-центра.
        /// Поэтому сначала триангулируются все четыре угла, затем их 3D-точки
        /// переводятся из однородных координат и усредняются.
        /// </summary>
        private bool TriangulateMarker(int markerId, Point2f[] leftCorners, Point2f[] rightCorners,
            Mat cameraMatrix1, Mat cameraMatrix2, Mat distCoeffs1, Mat distCoeffs2,
            CalibrationResult calibrationResult, Mat frame1, Mat frame2,
            out (double X, double Y, double Z) position)
        {
            position = default;
            bool triangulated = false;
            Debug.WriteLine($"--- Обработка маркера ID {markerId} ---");
            
            // Исправление искажений для всех углов маркера.
            // Нельзя триангулировать средний 2D-центр: при наклоне маркера
            // он не совпадает с проекцией реального 3D-центра.
            Mat leftPointsMat = InputArray.Create(leftCorners).GetMat();
            Mat rightPointsMat = InputArray.Create(rightCorners).GetMat();
            Mat undistortedLeft = new Mat();
            Mat undistortedRight = new Mat();
            
            Cv2.UndistortPoints(leftPointsMat, undistortedLeft, cameraMatrix1, distCoeffs1);
            Cv2.UndistortPoints(rightPointsMat, undistortedRight, cameraMatrix2, distCoeffs2);
            
            // Создание проекционных матриц для нормализованных координат.
            // После UndistortPoints точки уже находятся в нормированной плоскости,
            // поэтому P1 — [I|0], а P2 — [R|T] из стереокалибровки.
            Mat P1 = new Mat(3, 4, MatType.CV_64FC1);
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    P1.Set(i, j, i == j ? 1.0 : 0.0);
                }
                P1.Set(i, 3, 0.0);
            }
            
            Mat P2 = new Mat(3, 4, MatType.CV_64FC1);
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    P2.Set(i, j, calibrationResult.R[i, j]);
                }
                P2.Set(i, 3, calibrationResult.T[i]);
            }
            
            // Триангуляция возвращает 4D-точки в однородных координатах:
            // фактические X/Y/Z получаются делением на W.
            Mat points4D = new Mat();
            var leftPoints = new Point2d[leftCorners.Length];
            var rightPoints = new Point2d[rightCorners.Length];
            for (int i = 0; i < leftCorners.Length; i++)
            {
                var leftNormalized = undistortedLeft.At<Point2f>(i, 0);
                var rightNormalized = undistortedRight.At<Point2f>(i, 0);

                leftPoints[i] = new Point2d(leftNormalized.X, leftNormalized.Y);
                rightPoints[i] = new Point2d(rightNormalized.X, rightNormalized.Y);
            }
            
            Cv2.TriangulatePoints(P1, P2, 
                InputArray.Create(leftPoints), 
                InputArray.Create(rightPoints), 
                points4D);
            
            // Преобразование углов в декартовы координаты и усреднение 3D-центра.
            // Некорректные точки с W≈0 пропускаются, чтобы не получить бесконечность.
            double sumX = 0.0;
            double sumY = 0.0;
            double sumZ = 0.0;
            int validPointCount = 0;

            for (int i = 0; i < leftPoints.Length; i++)
            {
                double cornerX = points4D.At<double>(0, i);
                double cornerY = points4D.At<double>(1, i);
                double cornerZ = points4D.At<double>(2, i);
                double cornerW = points4D.At<double>(3, i);

                if (Math.Abs(cornerW) <= 1e-10)
                    continue;

                sumX += cornerX / cornerW;
                sumY += cornerY / cornerW;
                sumZ += cornerZ / cornerW;
                validPointCount++;
            }

            if (validPointCount > 0)
            {
                double x = sumX / validPointCount;
                double y = sumY / validPointCount;
                double z = sumZ / validPointCount;
                double distance = Math.Sqrt(x * x + y * y + z * z);
                
                Debug.WriteLine($"3D координаты в мм: ({x:F4}, {y:F4}, {z:F4})");
                Debug.WriteLine($"Точное расстояние: {distance:F4} мм");
                
                // Передаем координаты в пакетное обновление 3D сцены после обработки кадра
                position = (x, y, z);
                triangulated = true;
            }
            
            // Освобождение ресурсов
            leftPointsMat.Dispose();
            rightPointsMat.Dispose();
            undistortedLeft.Dispose();
            undistortedRight.Dispose();
            P1.Dispose();
            P2.Dispose();
            points4D.Dispose();

            return triangulated;
        }

        /// <summary>Преобразует двумерный массив из JSON-модели калибровки в OpenCV Mat.</summary>
        private Mat CreateMatrixFromArray(double[,] array)
        {
            int rows = array.GetLength(0);
            int cols = array.GetLength(1);
            Mat mat = new Mat(rows, cols, MatType.CV_64FC1);
            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < cols; j++)
                {
                    mat.Set(i, j, array[i, j]);
                }
            }
            return mat;
        }
        
        /// <summary>Преобразует одномерный массив из JSON-модели калибровки в OpenCV Mat-вектор.</summary>
        private Mat CreateVectorFromArray(double[] array)
        {
            Mat mat = new Mat(array.Length, 1, MatType.CV_64FC1);
            for (int i = 0; i < array.Length; i++)
            {
                mat.Set(i, 0, array[i]);
            }
            return mat;
        }
    }
}
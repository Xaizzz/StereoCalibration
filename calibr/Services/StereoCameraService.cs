using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OpenCvSharp;
using OpenCvSharp.Aruco;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Сервис низкого уровня для работы с двумя физическими камерами как со стереопарой.
    /// 
    /// Отвечает за открытие двух <see cref="VideoCapture"/>, чтение кадров,
    /// сохранение пар изображений для калибровки, детект ArUco и лёгкую стабилизацию
    /// 2D-детекта. Сервис не выполняет 3D-триангуляцию — он возвращает только
    /// corners/ids, которые затем обрабатывает <see cref="ImageProcessingService"/>.
    /// </summary>
    public class StereoCameraService : IDisposable
    {
        private VideoCapture? _capture1;
        private VideoCapture? _capture2;
        private Mat _frame1;
        private Mat _frame2;
        private bool _isRunning;
        
        private readonly Dictionary _dictionary;
        private readonly DetectorParameters _detectorParameters;
        /// <summary>
        /// Краткоживущая память последних уверенных детекций отдельно для каждой камеры.
        /// Она нужна, чтобы один пропущенный кадр не приводил сразу к потере маркера
        /// в триангуляции и 3D-сцене.
        /// </summary>
        private readonly System.Collections.Generic.Dictionary<int, RememberedMarker> _camera1MarkerMemory = new System.Collections.Generic.Dictionary<int, RememberedMarker>();
        private readonly System.Collections.Generic.Dictionary<int, RememberedMarker> _camera2MarkerMemory = new System.Collections.Generic.Dictionary<int, RememberedMarker>();
        private readonly HashSet<int> _staleMarkerIds1 = new HashSet<int>();
        private readonly HashSet<int> _staleMarkerIds2 = new HashSet<int>();
        private int _detectionFrameIndex;

        /// <summary>Сколько кадров можно использовать последнее положение маркера как fallback.</summary>
        private const int DetectionMemoryFrames = 3;
        /// <summary>Отступ вокруг прошлого положения маркера для ROI-поиска.</summary>
        private const int RoiPaddingPixels = 70;
        
        public bool IsRunning => _isRunning;
        
        public delegate void FramesUpdateHandler(Mat frame1, Mat frame2);
        public event FramesUpdateHandler OnFramesUpdate;
        
        public StereoCameraService()
        {
            // Инициализация ArUco детектора
            _dictionary = ArucoDetectionProfile.CreateDictionary();
            _detectorParameters = ArucoDetectionProfile.CreateParameters();
            
            _frame1 = new Mat();
            _frame2 = new Mat();
        }
        
        /// <summary>
        /// Открывает две камеры, проверяет доступность потоков и задаёт рабочее разрешение.
        /// 
        /// Задержки между открытием камер нужны для USB-камер: некоторые драйверы
        /// нестабильно стартуют, если два устройства открыть одновременно.
        /// </summary>
        public bool InitializeCameras(int cam1Index, int cam2Index)
        {
            try
            {
                // Освобождаем предыдущие камеры если есть
                ReleaseCameras();
                
                // Дополнительная задержка
                Thread.Sleep(500);
                
                _capture1 = new VideoCapture(cam1Index);
                Thread.Sleep(300); // Задержка между инициализацией камер
                _capture2 = new VideoCapture(cam2Index);
                
                // Проверяем инициализацию с несколькими попытками
                for (int i = 0; i < 5; i++)
                {
                    if (_capture1.IsOpened() && _capture2.IsOpened())
                        break;
                    Thread.Sleep(200);
                }
                
                if (!_capture1.IsOpened() || !_capture2.IsOpened())
                {
                    return false;
                }
                
                // Установка разрешения
                _capture1.Set(VideoCaptureProperties.FrameWidth, 640);
                _capture1.Set(VideoCaptureProperties.FrameHeight, 480);
                _capture2.Set(VideoCaptureProperties.FrameWidth, 640);
                _capture2.Set(VideoCaptureProperties.FrameHeight, 480);
                
                // Тест чтения кадров
                using (var testFrame1 = new Mat())
                using (var testFrame2 = new Mat())
                {
                    if (!_capture1.Read(testFrame1) || testFrame1.Empty() ||
                        !_capture2.Read(testFrame2) || testFrame2.Empty())
                    {
                        return false;
                    }
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка инициализации камер: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Запуск захвата видео
        /// </summary>
        public void StartCapture()
        {
            _isRunning = true;
        }
        
        /// <summary>
        /// Остановка захвата видео
        /// </summary>
        public void StopCapture()
        {
            _isRunning = false;
        }
        
        /// <summary>
        /// Читает очередную пару кадров из камер.
        /// 
        /// Метод вызывается из цикла UI (`Application.Idle` в `MainForm`), поэтому
        /// внутри нет собственного фонового потока. При успешном чтении кадры остаются
        /// в `_frame1`/`_frame2` и дополнительно отправляются подписчикам.
        /// </summary>
        public void ProcessFrames()
        {
            if (!_isRunning || _capture1 == null || _capture2 == null) return;
            
            if (_capture1.Read(_frame1) && _capture2.Read(_frame2))
            {
                if (!_frame1.Empty() && !_frame2.Empty())
                {
                    OnFramesUpdate?.Invoke(_frame1, _frame2);
                }
            }
        }
        
        /// <summary>
        /// Захват пары изображений
        /// </summary>
        public bool CapturePair(string folder, int imageIndex)
        {
            if (_frame1 == null || _frame1.Empty() || _frame2 == null || _frame2.Empty())
                return false;
            
            try
            {
                // Создаем директории если их нет
                var cam1Dir = Path.Combine("cam1", folder);
                var cam2Dir = Path.Combine("cam2", folder);
                Directory.CreateDirectory(cam1Dir);
                Directory.CreateDirectory(cam2Dir);
                
                // Сохраняем изображения
                var filename1 = Path.Combine(cam1Dir, $"{imageIndex}.png");
                var filename2 = Path.Combine(cam2Dir, $"{imageIndex}.png");
                
                Cv2.ImWrite(filename1, _frame1);
                Cv2.ImWrite(filename2, _frame2);
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка сохранения пары изображений: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Выполняет детекцию ArUco на обоих последних кадрах.
        /// 
        /// Возвращает массивы corners/ids в формате OpenCvSharp, потому что именно
        /// такой формат ожидают `CvAruco.DrawDetectedMarkers` и триангуляция.
        /// Перед возвратом применяется fallback: полный кадр, затем ROI вокруг
        /// прошлого положения и только потом краткая память stale-маркера.
        /// </summary>
        public (Point2f[][]? corners1, Point2f[][]? corners2, int[]? ids1, int[]? ids2) DetectArucoMarkers()
        {
            if (_frame1 == null || _frame1.Empty() || _frame2 == null || _frame2.Empty())
                return (null, null, null, null);

            _detectionFrameIndex++;

            var markers1 = DetectMarkersWithFallback(_frame1, _camera1MarkerMemory);
            var markers2 = DetectMarkersWithFallback(_frame2, _camera2MarkerMemory);

            _staleMarkerIds1.Clear();
            _staleMarkerIds2.Clear();

            Point2f[][] corners1 = new Point2f[markers1.Count][];
            int[] ids1 = new int[markers1.Count];
            for (int i = 0; i < markers1.Count; i++)
            {
                corners1[i] = markers1[i].Corners;
                ids1[i] = markers1[i].Id;
                if (markers1[i].IsStale)
                {
                    _staleMarkerIds1.Add(markers1[i].Id);
                }
            }

            Point2f[][] corners2 = new Point2f[markers2.Count][];
            int[] ids2 = new int[markers2.Count];
            for (int i = 0; i < markers2.Count; i++)
            {
                corners2[i] = markers2[i].Corners;
                ids2[i] = markers2[i].Id;
                if (markers2[i].IsStale)
                {
                    _staleMarkerIds2.Add(markers2[i].Id);
                }
            }

            return (corners1, corners2, ids1, ids2);
        }
        
        /// <summary>
        /// Отрисовка ArUco маркеров на кадрах
        /// </summary>
        public void DrawArucoMarkers(Mat frame1, Mat frame2, Point2f[][]? corners1, Point2f[][]? corners2, int[]? ids1, int[]? ids2)
        {
            if (ids1 != null && corners1 != null && ids1.Length > 0)
            {
                CvAruco.DrawDetectedMarkers(frame1, corners1, ids1);
                DrawStaleMarkers(frame1, corners1, ids1, _staleMarkerIds1);
            }
            
            if (ids2 != null && corners2 != null && ids2.Length > 0)
            {
                CvAruco.DrawDetectedMarkers(frame2, corners2, ids2);
                DrawStaleMarkers(frame2, corners2, ids2, _staleMarkerIds2);
            }
        }
        
        /// <summary>
        /// Получение текущих кадров
        /// </summary>
        public (Mat frame1, Mat frame2) GetCurrentFrames()
        {
            return (_frame1, _frame2);
        }
        
        private void ReleaseCameras()
        {
            _camera1MarkerMemory.Clear();
            _camera2MarkerMemory.Clear();
            _staleMarkerIds1.Clear();
            _staleMarkerIds2.Clear();
            _detectionFrameIndex = 0;

            if (_capture1 != null)
            {
                _capture1.Release();
                _capture1.Dispose();
                _capture1 = null;
            }
            if (_capture2 != null)
            {
                _capture2.Release();
                _capture2.Dispose();
                _capture2 = null;
            }
        }
        
        public void Dispose()
        {
            _isRunning = false;
            ReleaseCameras();
            
            _frame1?.Dispose();
            _frame2?.Dispose();
        }

        /// <summary>
        /// Основной алгоритм устойчивого 2D-детекта для одной камеры.
        /// 
        /// Шаги:
        /// 1. Всегда сначала ищем маркеры на полном кадре.
        /// 2. Все найденные ID записываем в память как свежие.
        /// 3. Для ID, которые были недавно, но пропали, пробуем ROI вокруг
        ///    последнего положения.
        /// 4. Если ROI тоже не помог, добавляем stale-детекцию на 1-3 кадра.
        /// 
        /// Такой порядок безопасен: быстрое движение не ломается ROI-режимом,
        /// потому что полный кадр всегда имеет приоритет.
        /// </summary>
        private List<DetectedMarker> DetectMarkersWithFallback(Mat frame, System.Collections.Generic.Dictionary<int, RememberedMarker> markerMemory)
        {
            var markers = DetectMarkersInFrame(frame);
            var detectedIds = new HashSet<int>();

            foreach (var marker in markers)
            {
                detectedIds.Add(marker.Id);
                markerMemory[marker.Id] = new RememberedMarker(marker.Id, CloneCorners(marker.Corners), _detectionFrameIndex);
            }

            var rememberedMarkers = new List<RememberedMarker>(markerMemory.Values);
            foreach (var rememberedMarker in rememberedMarkers)
            {
                if (detectedIds.Contains(rememberedMarker.Id))
                    continue;

                var missingFrames = _detectionFrameIndex - rememberedMarker.LastSeenFrame;
                if (missingFrames > DetectionMemoryFrames)
                {
                    markerMemory.Remove(rememberedMarker.Id);
                    continue;
                }

                if (TryDetectMarkerInRoi(frame, rememberedMarker, out var roiMarker) && roiMarker != null)
                {
                    markers.Add(roiMarker);
                    detectedIds.Add(roiMarker.Id);
                    markerMemory[roiMarker.Id] = new RememberedMarker(roiMarker.Id, CloneCorners(roiMarker.Corners), _detectionFrameIndex);
                    continue;
                }

                markers.Add(new DetectedMarker(rememberedMarker.Id, CloneCorners(rememberedMarker.Corners), true));
            }

            return markers;
        }

        /// <summary>
        /// Тонкая обёртка над `CvAruco.DetectMarkers`, приводящая результат OpenCV
        /// к внутренней структуре <see cref="DetectedMarker"/>.
        /// </summary>
        private List<DetectedMarker> DetectMarkersInFrame(Mat frame)
        {
            Point2f[][] corners;
            int[] ids;
            Point2f[][] rejectedCandidates;

            CvAruco.DetectMarkers(frame, _dictionary, out corners, out ids, _detectorParameters, out rejectedCandidates);

            var markers = new List<DetectedMarker>();
            if (ids == null || corners == null)
                return markers;

            for (int i = 0; i < ids.Length; i++)
            {
                markers.Add(new DetectedMarker(ids[i], CloneCorners(corners[i]), false));
            }

            return markers;
        }

        /// <summary>
        /// Пытается повторно найти конкретный потерянный маркер в небольшой области
        /// вокруг его последнего положения. Координаты углов ROI переводятся обратно
        /// в координаты полного кадра, чтобы дальнейшая триангуляция ничего не знала
        /// о факте ROI-поиска.
        /// </summary>
        private bool TryDetectMarkerInRoi(Mat frame, RememberedMarker rememberedMarker, out DetectedMarker? detectedMarker)
        {
            detectedMarker = null;

            if (!TryBuildMarkerRoi(frame, rememberedMarker.Corners, out var roi))
                return false;

            using (var roiFrame = new Mat(frame, roi))
            {
                Point2f[][] roiCorners;
                int[] roiIds;
                Point2f[][] rejectedCandidates;

                CvAruco.DetectMarkers(roiFrame, _dictionary, out roiCorners, out roiIds, _detectorParameters, out rejectedCandidates);
                if (roiIds == null || roiCorners == null)
                    return false;

                for (int i = 0; i < roiIds.Length; i++)
                {
                    if (roiIds[i] != rememberedMarker.Id)
                        continue;

                    var fullFrameCorners = OffsetCorners(roiCorners[i], roi.X, roi.Y);
                    detectedMarker = new DetectedMarker(roiIds[i], fullFrameCorners, false);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Строит прямоугольную область интереса вокруг четырёх углов маркера.
        /// Область обрезается границами кадра, чтобы OpenCV не получил неверный Rect.
        /// </summary>
        private static bool TryBuildMarkerRoi(Mat frame, Point2f[] corners, out Rect roi)
        {
            roi = default;
            if (corners == null || corners.Length == 0 || frame.Width <= 0 || frame.Height <= 0)
                return false;

            float minX = corners[0].X;
            float minY = corners[0].Y;
            float maxX = corners[0].X;
            float maxY = corners[0].Y;

            for (int i = 1; i < corners.Length; i++)
            {
                minX = Math.Min(minX, corners[i].X);
                minY = Math.Min(minY, corners[i].Y);
                maxX = Math.Max(maxX, corners[i].X);
                maxY = Math.Max(maxY, corners[i].Y);
            }

            int x = Math.Max(0, (int)Math.Floor(minX) - RoiPaddingPixels);
            int y = Math.Max(0, (int)Math.Floor(minY) - RoiPaddingPixels);
            int right = Math.Min(frame.Width, (int)Math.Ceiling(maxX) + RoiPaddingPixels);
            int bottom = Math.Min(frame.Height, (int)Math.Ceiling(maxY) + RoiPaddingPixels);

            int width = right - x;
            int height = bottom - y;
            if (width <= 10 || height <= 10)
                return false;

            roi = new Rect(x, y, width, height);
            return true;
        }

        private static Point2f[] OffsetCorners(Point2f[] corners, int offsetX, int offsetY)
        {
            var offsetCorners = new Point2f[corners.Length];
            for (int i = 0; i < corners.Length; i++)
            {
                offsetCorners[i] = new Point2f(corners[i].X + offsetX, corners[i].Y + offsetY);
            }

            return offsetCorners;
        }

        private static Point2f[] CloneCorners(Point2f[] corners)
        {
            var clonedCorners = new Point2f[corners.Length];
            for (int i = 0; i < corners.Length; i++)
            {
                clonedCorners[i] = corners[i];
            }

            return clonedCorners;
        }

        /// <summary>
        /// Помечает stale-маркеры маленькой оранжевой точкой. Текст специально не
        /// рисуется, чтобы не перекрывать изображение на основном экране.
        /// </summary>
        private static void DrawStaleMarkers(Mat frame, Point2f[][] corners, int[] ids, HashSet<int> staleMarkerIds)
        {
            if (staleMarkerIds.Count == 0)
                return;

            for (int i = 0; i < ids.Length; i++)
            {
                if (!staleMarkerIds.Contains(ids[i]))
                    continue;

                var center = GetMarkerCenter(corners[i]);
                Cv2.Circle(frame, new OpenCvSharp.Point((int)center.X, (int)center.Y), 4, new Scalar(0, 165, 255), -1);
            }
        }

        private static Point2f GetMarkerCenter(Point2f[] corners)
        {
            float sumX = 0;
            float sumY = 0;
            for (int i = 0; i < corners.Length; i++)
            {
                sumX += corners[i].X;
                sumY += corners[i].Y;
            }

            return new Point2f(sumX / corners.Length, sumY / corners.Length);
        }

        /// <summary>
        /// Снимок последнего уверенного положения ArUco-маркера в одной камере.
        /// </summary>
        private class RememberedMarker
        {
            public RememberedMarker(int id, Point2f[] corners, int lastSeenFrame)
            {
                Id = id;
                Corners = corners;
                LastSeenFrame = lastSeenFrame;
            }

            public int Id { get; }
            public Point2f[] Corners { get; }
            public int LastSeenFrame { get; }
        }

        /// <summary>
        /// Внутреннее представление результата детекции, включая признак stale.
        /// </summary>
        private class DetectedMarker
        {
            public DetectedMarker(int id, Point2f[] corners, bool isStale)
            {
                Id = id;
                Corners = corners;
                IsStale = isStale;
            }

            public int Id { get; }
            public Point2f[] Corners { get; }
            public bool IsStale { get; }
        }
    }
}
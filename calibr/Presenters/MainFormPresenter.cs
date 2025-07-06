using OpenCvSharp;
using StereoCalibration.Interfaces;
using StereoCalibration.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StereoCalibration.Presenters
{
    /// <summary>
    /// Презентер для главной формы приложения
    /// Координирует взаимодействие между всеми сервисами и UI
    /// </summary>
    public class MainFormPresenter
    {
        // Сервисы
        private readonly ICameraService _cameraService;
        private readonly ICalibrationService _calibrationService;
        private readonly IArUcoDetectionService _arUcoService;
        private readonly ITriangulationService _triangulationService;
        private readonly IFileService _fileService;
        
        // UI
        private readonly Form _view;
        
        // Состояние приложения
        private bool _isCapturing;
        private CalibrationResult _currentCalibration;
        private List<Mat> _objectPointsList;
        private List<Mat> _imagePointsList1;
        private List<Mat> _imagePointsList2;
        
        // Конфигурация
        private readonly OpenCvSharp.Size _patternSize = new OpenCvSharp.Size(9, 6);
        private const float _squareSize = 8.5f;
        private readonly string _currentFolder = "0204_1a";

        // События для UI
        public event EventHandler<FrameProcessedEventArgs> FrameProcessed;
        public event EventHandler<string> ErrorOccurred;
        public event EventHandler<CalibrationCompletedEventArgs> CalibrationCompleted;
        public event EventHandler<string> StatusChanged;

        /// <summary>
        /// Конструктор презентера
        /// </summary>
        public MainFormPresenter(
            ICameraService cameraService,
            ICalibrationService calibrationService,
            IArUcoDetectionService arUcoService,
            ITriangulationService triangulationService,
            IFileService fileService,
            Form view)
        {
            _cameraService = cameraService ?? throw new ArgumentNullException(nameof(cameraService));
            _calibrationService = calibrationService ?? throw new ArgumentNullException(nameof(calibrationService));
            _arUcoService = arUcoService ?? throw new ArgumentNullException(nameof(arUcoService));
            _triangulationService = triangulationService ?? throw new ArgumentNullException(nameof(triangulationService));
            _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
            _view = view ?? throw new ArgumentNullException(nameof(view));

            InitializeDataStructures();
            SubscribeToEvents();
        }

        /// <summary>
        /// Инициализация структур данных
        /// </summary>
        private void InitializeDataStructures()
        {
            _objectPointsList = new List<Mat>();
            _imagePointsList1 = new List<Mat>();
            _imagePointsList2 = new List<Mat>();
            
            // Создаем папки для сохранения изображений
            _fileService.CreateDirectoryIfNotExists($"cam1\\{_currentFolder}");
            _fileService.CreateDirectoryIfNotExists($"cam2\\{_currentFolder}");
        }

        /// <summary>
        /// Подписка на события сервисов
        /// </summary>
        private void SubscribeToEvents()
        {
            _cameraService.FrameReceived += OnFrameReceived;
            _cameraService.CameraStatusChanged += OnCameraStatusChanged;
        }

        /// <summary>
        /// Инициализация камер
        /// </summary>
        public async Task InitializeCamerasAsync(int camera1Id, int camera2Id)
        {
            try
            {
                OnStatusChanged("Инициализация камер...");

                var task1 = _cameraService.ConnectCameraAsync(camera1Id);
                var task2 = _cameraService.ConnectCameraAsync(camera2Id);

                var results = await Task.WhenAll(task1, task2);

                if (!results[0])
                    throw new Exception($"Не удалось подключить камеру 1 (ID: {camera1Id})");

                if (!results[1])
                    throw new Exception($"Не удалось подключить камеру 2 (ID: {camera2Id})");

                // Устанавливаем разрешение
                _cameraService.SetResolution(camera1Id, 640, 480);
                _cameraService.SetResolution(camera2Id, 640, 480);

                OnStatusChanged("Камеры успешно инициализированы");
            }
            catch (Exception ex)
            {
                OnError($"Ошибка инициализации камер: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Начать/остановить захват кадров
        /// </summary>
        public void ToggleCapture(int camera1Id, int camera2Id)
        {
            try
            {
                if (!_isCapturing)
                {
                    _cameraService.StartCapture(camera1Id);
                    _cameraService.StartCapture(camera2Id);
                    _isCapturing = true;
                    OnStatusChanged("Захват кадров запущен");
                }
                else
                {
                    _cameraService.StopCapture(camera1Id);
                    _cameraService.StopCapture(camera2Id);
                    _isCapturing = false;
                    OnStatusChanged("Захват кадров остановлен");
                }
            }
            catch (Exception ex)
            {
                OnError($"Ошибка управления захватом: {ex.Message}");
            }
        }

        /// <summary>
        /// Захват пары изображений для калибровки
        /// </summary>
        public async Task CaptureImagePairAsync(int camera1Id, int camera2Id)
        {
            try
            {
                OnStatusChanged("Захват пары изображений...");

                // Получаем кадры с обеих камер
                var frame1 = _cameraService.GetCurrentFrame(camera1Id);
                var frame2 = _cameraService.GetCurrentFrame(camera2Id);

                if (frame1 == null || frame2 == null || frame1.Empty() || frame2.Empty())
                {
                    OnError("Не удалось получить кадры с камер");
                    return;
                }

                // Поиск углов шахматной доски
                Point2f[] corners1, corners2;
                bool found1 = _calibrationService.FindChessboardCorners(frame1, _patternSize, out corners1);
                bool found2 = _calibrationService.FindChessboardCorners(frame2, _patternSize, out corners2);

                if (!found1 || !found2)
                {
                    OnError("Шахматная доска не найдена на одном или обоих изображениях");
                    frame1.Dispose();
                    frame2.Dispose();
                    return;
                }

                // Сохраняем изображения
                var imageIndex = _objectPointsList.Count;
                var path1 = $"cam1\\{_currentFolder}\\{imageIndex}.png";
                var path2 = $"cam2\\{_currentFolder}\\{imageIndex}.png";

                await Task.WhenAll(
                    _fileService.SaveImageAsync(frame1, path1),
                    _fileService.SaveImageAsync(frame2, path2)
                );

                // Добавляем точки для калибровки
                var objectPoints = _calibrationService.GenerateObjectPoints(_patternSize, _squareSize);
                var objectPointsMat = new Mat(objectPoints.Count, 1, MatType.CV_32FC3);
                objectPointsMat.SetArray(0, 0, objectPoints.ToArray());

                var imagePoints1Mat = new Mat(corners1.Length, 1, MatType.CV_32FC2);
                imagePoints1Mat.SetArray(0, 0, corners1);

                var imagePoints2Mat = new Mat(corners2.Length, 1, MatType.CV_32FC2);
                imagePoints2Mat.SetArray(0, 0, corners2);

                _objectPointsList.Add(objectPointsMat);
                _imagePointsList1.Add(imagePoints1Mat);
                _imagePointsList2.Add(imagePoints2Mat);

                frame1.Dispose();
                frame2.Dispose();

                OnStatusChanged($"Сохранена пара изображений {imageIndex + 1}. Всего пар: {_objectPointsList.Count}");
            }
            catch (Exception ex)
            {
                OnError($"Ошибка захвата пары изображений: {ex.Message}");
            }
        }

        /// <summary>
        /// Выполнение стерео калибровки
        /// </summary>
        public async Task PerformCalibrationAsync()
        {
            try
            {
                if (_objectPointsList.Count < 5)
                {
                    OnError($"Недостаточно изображений для калибровки. Требуется минимум 5, имеется {_objectPointsList.Count}");
                    return;
                }

                OnStatusChanged("Выполнение стерео калибровки...");

                var imageSize = new OpenCvSharp.Size(640, 480);
                _currentCalibration = await _calibrationService.PerformStereoCalibrationAsync(
                    _objectPointsList,
                    _imagePointsList1,
                    _imagePointsList2,
                    imageSize);

                // Сохраняем результаты
                await _calibrationService.SaveCalibrationResultAsync(_currentCalibration, "calibration_result.json");

                OnCalibrationCompleted(_currentCalibration);
                OnStatusChanged($"Калибровка завершена. Ошибка: {_currentCalibration.Error:F3}. Качество: {_currentCalibration.GetQualityDescription()}");
            }
            catch (Exception ex)
            {
                OnError($"Ошибка калибровки: {ex.Message}");
            }
        }

        /// <summary>
        /// Открытие папок с изображениями
        /// </summary>
        public void OpenImageFolders()
        {
            try
            {
                _fileService.OpenDirectoryInExplorer($"cam1\\{_currentFolder}");
                _fileService.OpenDirectoryInExplorer($"cam2\\{_currentFolder}");
            }
            catch (Exception ex)
            {
                OnError($"Ошибка открытия папок: {ex.Message}");
            }
        }

        /// <summary>
        /// Загрузка сохранённых результатов калибровки
        /// </summary>
        public async Task LoadCalibrationAsync()
        {
            try
            {
                _currentCalibration = await _calibrationService.LoadCalibrationResultAsync("calibration_result.json");
                if (_currentCalibration != null)
                {
                    OnStatusChanged("Результаты калибровки загружены");
                    OnCalibrationCompleted(_currentCalibration);
                }
                else
                {
                    OnStatusChanged("Файл калибровки не найден");
                }
            }
            catch (Exception ex)
            {
                OnError($"Ошибка загрузки калибровки: {ex.Message}");
            }
        }

        /// <summary>
        /// Обработка полученного кадра
        /// </summary>
        private void OnFrameReceived(object sender, FrameReceivedEventArgs e)
        {
            try
            {
                if (e.Frame == null || e.Frame.Empty())
                    return;

                var processedFrame = e.Frame.Clone();

                // Детекция ArUco маркеров
                var detectionResult = _arUcoService.DetectMarkers(processedFrame);
                if (detectionResult.HasMarkers)
                {
                    _arUcoService.DrawDetectedMarkers(processedFrame, detectionResult);
                }

                // Уведомляем UI о обработанном кадре
                _view?.BeginInvoke(new Action(() =>
                {
                    FrameProcessed?.Invoke(this, new FrameProcessedEventArgs
                    {
                        CameraId = e.CameraId,
                        ProcessedFrame = processedFrame,
                        DetectionResult = detectionResult,
                        Timestamp = e.Timestamp
                    });
                }));

                // Триангуляция если есть калибровка и маркеры обнаружены на обеих камерах
                // Эта логика может быть расширена в зависимости от требований
            }
            catch (Exception ex)
            {
                OnError($"Ошибка обработки кадра: {ex.Message}");
            }
        }

        /// <summary>
        /// Обработка изменения статуса камеры
        /// </summary>
        private void OnCameraStatusChanged(object sender, CameraStatusEventArgs e)
        {
            _view?.BeginInvoke(new Action(() =>
            {
                var status = e.IsConnected ? "подключена" : "отключена";
                OnStatusChanged($"Камера {e.CameraId} {status}");
                
                if (!e.IsConnected && !string.IsNullOrEmpty(e.ErrorMessage))
                {
                    OnError($"Камера {e.CameraId}: {e.ErrorMessage}");
                }
            }));
        }

        /// <summary>
        /// Генерация события обработки кадра
        /// </summary>
        protected virtual void OnFrameProcessed(FrameProcessedEventArgs args)
        {
            FrameProcessed?.Invoke(this, args);
        }

        /// <summary>
        /// Генерация события ошибки
        /// </summary>
        protected virtual void OnError(string message)
        {
            ErrorOccurred?.Invoke(this, message);
        }

        /// <summary>
        /// Генерация события завершения калибровки
        /// </summary>
        protected virtual void OnCalibrationCompleted(CalibrationResult result)
        {
            CalibrationCompleted?.Invoke(this, new CalibrationCompletedEventArgs { Result = result });
        }

        /// <summary>
        /// Генерация события изменения статуса
        /// </summary>
        protected virtual void OnStatusChanged(string status)
        {
            StatusChanged?.Invoke(this, status);
        }

        /// <summary>
        /// Освобождение ресурсов
        /// </summary>
        public void Dispose()
        {
            _cameraService.FrameReceived -= OnFrameReceived;
            _cameraService.CameraStatusChanged -= OnCameraStatusChanged;

            // Освобождаем Mat объекты
            foreach (var mat in _objectPointsList)
                mat?.Dispose();
            foreach (var mat in _imagePointsList1)
                mat?.Dispose();
            foreach (var mat in _imagePointsList2)
                mat?.Dispose();

            _objectPointsList.Clear();
            _imagePointsList1.Clear();
            _imagePointsList2.Clear();
        }
    }

    /// <summary>
    /// Аргументы события обработки кадра
    /// </summary>
    public class FrameProcessedEventArgs : EventArgs
    {
        public int CameraId { get; set; }
        public Mat ProcessedFrame { get; set; }
        public MarkerDetectionResult DetectionResult { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Аргументы события завершения калибровки
    /// </summary>
    public class CalibrationCompletedEventArgs : EventArgs
    {
        public CalibrationResult Result { get; set; }
    }
} 
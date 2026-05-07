using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using StereoCalibration.Services;
using OpenCvSharp.Aruco;
using OpenCvSharp.Extensions;

namespace StereoCalibration.Controllers
{
    /// <summary>
    /// Координатор всей прикладной логики главного окна.
    /// 
    /// `MainForm` отвечает только за WinForms-элементы и обработчики кнопок,
    /// а этот класс связывает между собой камеры, калибровку, ArUco-триангуляцию,
    /// таблицу маркеров и 3D-сцену. Связь с UI выполняется через события,
    /// чтобы контроллер не зависел от конкретных элементов формы.
    /// </summary>
    public class MainFormController
    {
        #region События для обновления UI
        /// <summary>Готовые Bitmap-кадры для двух PictureBox на главной форме.</summary>
        public event Action<Bitmap, Bitmap>? OnFramesUpdated;
        /// <summary>Событие изменения состояния запуска/остановки</summary>
        public event Action<bool>? OnRunningStateChanged;
        /// <summary>Событие завершения калибровки</summary>
        public event Action<string>? OnCalibrationCompleted;
        /// <summary>Событие возникновения ошибки</summary>
        public event Action<string>? OnError;
        /// <summary>Событие информационных сообщений</summary>
        public event Action<string>? OnInfoMessage;
        /// <summary>Событие обновления 3D сцены</summary>
        public event Action? OnScene3DUpdated;
        /// <summary>
        /// Сырые 3D-координаты маркеров в системе камеры 1 для таблицы справа
        /// на вкладке камер. 3D-сцена получает те же данные через Scene3DController.
        /// </summary>
        public event Action<IReadOnlyDictionary<int, (double X, double Y, double Z)>>? OnMarkerPositionsUpdated;
        #endregion

        #region Сервисы
        private readonly StereoCameraService _stereoCameraService;
        private readonly StereoCalibrationService _calibrationService;
        private readonly ImageProcessingService _imageProcessingService;
        private readonly Scene3DController _scene3DController;
        #endregion

        #region Состояние приложения
        private bool _isRunning = false;
        private Mat _frame1 = new Mat();
        private Mat _frame2 = new Mat();
        private Services.CalibrationResult? _calibrationResult;
        private List<Point3f> _ps3dAllOut = new List<Point3f>();
        #endregion

        #region Параметры калибровки
        private readonly OpenCvSharp.Size _patternSize = new OpenCvSharp.Size(9, 6);
        private readonly float _squareSize = 8.5f;
        private readonly string _currentFolder = "0204_1a";
        #endregion

        #region ArUco детектор
        private readonly Dictionary _dictionary;
        private readonly DetectorParameters _detectorParameters;
        #endregion

        /// <summary>
        /// Создаёт все сервисы приложения и связывает их события в единый поток.
        /// 
        /// Также загружает сохранённую калибровку, чтобы после запуска сразу можно
        /// было триангулировать ArUco без повторной калибровки, если файл есть.
        /// </summary>
        public MainFormController()
        {
            // Инициализация сервисов
            _stereoCameraService = new StereoCameraService();
            _calibrationService = new StereoCalibrationService(_patternSize, _squareSize);
            _imageProcessingService = new ImageProcessingService();
            _scene3DController = new Scene3DController();

            // Инициализация ArUco детектора
            _dictionary = ArucoDetectionProfile.CreateDictionary();
            _detectorParameters = ArucoDetectionProfile.CreateParameters();

            // Подписка на события сервиса
            _stereoCameraService.OnFramesUpdate += (f1, f2) =>
            {
                _frame1 = f1;
                _frame2 = f2;
            };

            // Подписка на пакетные обновления 3D позиций маркеров
            _imageProcessingService.OnMarkerPositions3DUpdated += (markerPositions) =>
            {
                _scene3DController.UpdateMarkers(markerPositions);
                OnMarkerPositionsUpdated?.Invoke(markerPositions);
            };

            // Подписка на обновления 3D сцены
            _scene3DController.OnSceneUpdated += () => OnScene3DUpdated?.Invoke();

            LoadExistingCalibration();
        }

        /// <summary>
        /// Обнаружение доступных камер в системе
        /// </summary>
        public List<int> DetectCameras()
        {
            return CameraManager.DetectAvailableCameras(10);
        }

        /// <summary>
        /// Инициализация камер с выбранными индексами
        /// </summary>
        public bool InitializeCameras(int cam1Index, int cam2Index)
        {
            try
            {
                return _stereoCameraService.InitializeCameras(cam1Index, cam2Index);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Ошибка инициализации камер: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Запуск/остановка захвата видео
        /// </summary>
        public void ToggleCapture()
        {
            try
            {
                if (!_isRunning)
                {
                    _isRunning = true;
                    _stereoCameraService.StartCapture();
                }
                else
                {
                    _isRunning = false;
                    _stereoCameraService.StopCapture();
                }
                OnRunningStateChanged?.Invoke(_isRunning);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Ошибка управления захватом: {ex.Message}");
            }
        }

        /// <summary>
        /// Обрабатывает один полный цикл видеопотока.
        /// 
        /// Последовательность:
        /// 1. Считать свежую пару кадров из камер.
        /// 2. Склонировать кадры для безопасной отрисовки overlay.
        /// 3. Найти ArUco и нарисовать рамки.
        /// 4. Если есть калибровка, триангулировать маркеры.
        /// 5. Найти шахматную доску для визуальной помощи при калибровке.
        /// 6. Преобразовать результат в Bitmap и отправить форме.
        /// </summary>
        public void ProcessFrame()
        {
            try
            {
                if (!_stereoCameraService.IsRunning)
                    return;

                _stereoCameraService.ProcessFrames();

                var (currentFrame1, currentFrame2) = _stereoCameraService.GetCurrentFrames();
                if (currentFrame1 == null || currentFrame1.Empty() || 
                    currentFrame2 == null || currentFrame2.Empty())
                    return;

                _frame1 = currentFrame1;
                _frame2 = currentFrame2;

                var fr1 = _frame1.Clone();
                var fr2 = _frame2.Clone();

                // ArUco-детект выполняется на кадрах сервиса, а рисование — на
                // клонах fr1/fr2, чтобы не портить исходные Mat, используемые
                // для сохранения калибровочных пар.
                var (corners1, corners2, ids1, ids2, staleIds1, staleIds2) = _stereoCameraService.DetectArucoMarkers();
                _stereoCameraService.DrawArucoMarkers(fr1, fr2, corners1, corners2, ids1, ids2);

                // Триангуляция ArUco маркеров
                if (_calibrationResult != null)
                {
                    _imageProcessingService.TriangulateArucoMarkers(fr1, fr2, corners1, corners2, 
                        ids1, ids2, _calibrationResult, staleIds1, staleIds2);
                }

                // Обнаружение шахматной доски
                ProcessChessboardDetection(fr1, fr2);

                // Конвертация в Bitmap для отображения в UI
                var bitmap1 = fr1.ToBitmap();
                var bitmap2 = fr2.ToBitmap();
                OnFramesUpdated?.Invoke(bitmap1, bitmap2);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Ошибка обработки кадра: {ex.Message}");
            }
        }

        /// <summary>
        /// Ищет шахматную доску на текущем кадре и рисует найденные углы.
        /// 
        /// Это не основная калибровка, а визуальная подсказка пользователю:
        /// по overlay видно, достаточно ли хорошо камеры видят калибровочную доску.
        /// </summary>
        private void ProcessChessboardDetection(Mat frame1, Mat frame2)
        {
            Point2f[] chessCorners1, chessCorners2;
            bool found1 = Cv2.FindChessboardCorners(_frame1, _patternSize, out chessCorners1, 
                ChessboardFlags.FastCheck);
            bool found2 = Cv2.FindChessboardCorners(_frame2, _patternSize, out chessCorners2, 
                ChessboardFlags.FastCheck);

            if (found1)
            {
                Cv2.DrawChessboardCorners(frame1, _patternSize, chessCorners1, true);
            }

            if (found2)
            {
                Cv2.DrawChessboardCorners(frame2, _patternSize, chessCorners2, true);
            }
        }

        #region Данные калибровки (перенесено из формы)
        private List<Mat>? _pairImagePointsList1;
        private List<Mat>? _pairImagePointsList2;
        private List<Mat>? _pairObjectPointsList;
        private int _capturedPairsCount = 0;
        #endregion

        /// <summary>
        /// Генерация объектных точек для шахматной доски
        /// </summary>
        private List<Point3f> GenerateObjectPoints()
        {
            var objectPoints = new List<Point3f>();
            for (int i = 0; i < _patternSize.Height; i++)
            {
                for (int j = 0; j < _patternSize.Width; j++)
                {
                    objectPoints.Add(new Point3f(j * _squareSize, i * _squareSize, 0));
                }
            }
            return objectPoints;
        }

        /// <summary>
        /// Сохраняет текущую пару изображений и проверяет, видна ли на ней шахматная доска.
        /// 
        /// Изображения сохраняются на диск через StereoCameraService. Дополнительные
        /// списки точек в контроллере оставлены как диагностический/legacy-след:
        /// финальная калибровка всё равно перечитывает файлы из папок.
        /// </summary>
        public void CapturePair()
        {
            try
            {
                // Сохраняем изображения на диск
                bool success = _stereoCameraService.CapturePair(_currentFolder, _capturedPairsCount);
                if (!success)
                {
                    OnError?.Invoke("Ошибка сохранения пары изображений.");
                    return;
                }

                // Анализируем текущие кадры на наличие шахматной доски
                Mat snapshot1 = _frame1.Clone();
                Mat snapshot2 = _frame2.Clone();

                Point2f[] corners1, corners2;
                bool found1 = Cv2.FindChessboardCorners(snapshot1, _patternSize, out corners1, ChessboardFlags.FastCheck);
                bool found2 = Cv2.FindChessboardCorners(snapshot2, _patternSize, out corners2, ChessboardFlags.FastCheck);

                if (found1 && found2)
                {
                    using (Mat gray1 = new Mat())
                    using (Mat gray2 = new Mat())
                    {
                        Cv2.CvtColor(snapshot1, gray1, ColorConversionCodes.BGR2GRAY);
                        Cv2.CvtColor(snapshot2, gray2, ColorConversionCodes.BGR2GRAY);

                        Cv2.CornerSubPix(gray1, corners1, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));
                        Cv2.CornerSubPix(gray2, corners2, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));

                        // Создаем Mat объекты для точек
                        Mat imagePoints1 = new Mat(corners1.Length, 1, MatType.CV_32FC2);
                        Mat imagePoints2 = new Mat(corners2.Length, 1, MatType.CV_32FC2);
                        Point3f[] objPoints = GenerateObjectPoints().ToArray();
                        Mat objectPoints = new Mat(objPoints.Length, 1, MatType.CV_32FC3);

                        for (int i = 0; i < corners1.Length; i++)
                        {
                            imagePoints1.Set(i, 0, corners1[i]);
                            imagePoints2.Set(i, 0, corners2[i]);
                        }
                        for (int i = 0; i < objPoints.Length; i++)
                        {
                            objectPoints.Set(i, 0, objPoints[i]);
                        }

                        // Инициализируем списки если нужно
                        if (_pairImagePointsList1 == null) _pairImagePointsList1 = new List<Mat>();
                        if (_pairImagePointsList2 == null) _pairImagePointsList2 = new List<Mat>();
                        if (_pairObjectPointsList == null) _pairObjectPointsList = new List<Mat>();

                        _pairImagePointsList1.Add(imagePoints1);
                        _pairImagePointsList2.Add(imagePoints2);
                        _pairObjectPointsList.Add(objectPoints);

                        _capturedPairsCount++;

                        // Уведомляем UI об успешном захвате
                        OnInfoMessage?.Invoke($"Пара изображений захвачена! Всего пар: {_capturedPairsCount}");
                    }
                }
                else
                {
                    OnError?.Invoke("Шахматная доска не найдена на одном или обоих изображениях.");
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Ошибка захвата пары: {ex.Message}");
            }
        }

        /// <summary>
        /// Запускает калибровку по сохранённым изображениям, сохраняет JSON и
        /// передаёт результат в 3D-сцену.
        /// </summary>
        public void StartCalibration()
        {
            try
            {
                _calibrationResult = _calibrationService.CalibrateFromImages(_currentFolder, out _ps3dAllOut);
                _calibrationService.SaveCalibrationResult(_calibrationResult, "calibration_result.json");

                // Обновляем 3D сцену с результатами калибровки
                _scene3DController.UpdateCalibration(_calibrationResult);

                // Расчет расстояния между камерами
                double distance = Math.Sqrt(_calibrationResult.T[0] * _calibrationResult.T[0] +
                                          _calibrationResult.T[1] * _calibrationResult.T[1] +
                                          _calibrationResult.T[2] * _calibrationResult.T[2]);

                // Формирование сообщения о результате
                string resultMessage = FormatCalibrationResult(_calibrationResult.Error, distance);
                OnCalibrationCompleted?.Invoke(resultMessage);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Ошибка калибровки: {ex.Message}");
            }
        }

        /// <summary>
        /// Загружает `calibration_result.json` при старте приложения.
        /// Если файла нет или он повреждён, приложение продолжает работать,
        /// но ArUco-триангуляция не выполняется до новой калибровки.
        /// </summary>
        private void LoadExistingCalibration()
        {
            try
            {
                _calibrationResult = _calibrationService.LoadCalibrationResult("calibration_result.json");
                if (_calibrationResult == null)
                {
                    System.Diagnostics.Debug.WriteLine("MainFormController: сохраненная калибровка не найдена");
                    return;
                }

                _scene3DController.UpdateCalibration(_calibrationResult);
                System.Diagnostics.Debug.WriteLine("MainFormController: сохраненная калибровка загружена");
            }
            catch (Exception ex)
            {
                _calibrationResult = null;
                System.Diagnostics.Debug.WriteLine($"MainFormController: ошибка загрузки сохраненной калибровки: {ex.Message}");
            }
        }

        /// <summary>
        /// Форматирование результатов калибровки для отображения
        /// </summary>
        private string FormatCalibrationResult(double error, double distance)
        {
            string qualityMessage = "";
            string qualityColor = "";
            
            if (error < 0.5)
            {
                qualityMessage = "ОТЛИЧНОЕ КАЧЕСТВО ✓";
                qualityColor = "🟢";
            }
            else if (error < 1.0)
            {
                qualityMessage = "ХОРОШЕЕ КАЧЕСТВО ✓";
                qualityColor = "🟡";
            }
            else if (error < 2.0)
            {
                qualityMessage = "УДОВЛЕТВОРИТЕЛЬНОЕ КАЧЕСТВО ⚠";
                qualityColor = "🟠";
            }
            else
            {
                qualityMessage = "ПЛОХОЕ КАЧЕСТВО ❌ (рекомендуется перекалибровка)";
                qualityColor = "🔴";
            }

            string resultMessage = $"{qualityColor} РЕЗУЛЬТАТЫ СТЕРЕОКАЛИБРОВКИ\n\n" +
                                 $"📊 Ошибка калибровки: {error:F4} пикселей\n" +
                                 $"🎯 Оценка качества: {qualityMessage}\n\n" +
                                 $"📏 Расстояние между камерами: {distance:F1} мм\n" +
                                 $"📝 Файл результатов: calibration_result.json\n\n" +
                                 $"💡 Рекомендации:\n";

            if (error < 1.0)
            {
                resultMessage += "• Калибровка готова к использованию\n";
                resultMessage += "• Можно приступать к измерениям";
            }
            else if (error < 2.0)
            {
                resultMessage += "• Рекомендуется добавить больше изображений\n";
                resultMessage += "• Проверьте качество фокусировки камер";
            }
            else
            {
                resultMessage += "• Необходима перекалибровка\n";
                resultMessage += "• Убедитесь в неподвижности камер\n";
                resultMessage += "• Используйте различные позиции доски";
            }

            return resultMessage;
        }

        /// <summary>
        /// Освобождение ресурсов
        /// </summary>
        public void Dispose()
        {
            _stereoCameraService?.Dispose();
            _frame1?.Dispose();
            _frame2?.Dispose();
        }

        #region Свойства для доступа к состоянию
        public bool IsRunning => _isRunning;
        public string CurrentFolder => _currentFolder;
        #endregion

        #region Доступ к 3D сцене
        /// <summary>
        /// Получение контроллера 3D сцены
        /// </summary>
        public Scene3DController GetScene3DController()
        {
            return _scene3DController;
        }
        #endregion
    }
}
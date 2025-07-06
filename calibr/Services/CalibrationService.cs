using OpenCvSharp;
using StereoCalibration.Interfaces;
using StereoCalibration.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Реализация сервиса стерео калибровки
    /// Инкапсулирует алгоритмы калибровки стерео системы
    /// </summary>
    public class CalibrationService : ICalibrationService
    {
        /// <summary>
        /// Выполнение стерео калибровки
        /// </summary>
        public async Task<CalibrationResult> PerformStereoCalibrationAsync(
            List<Mat> objectPoints,
            List<Mat> imagePoints1,
            List<Mat> imagePoints2,
            OpenCvSharp.Size imageSize)
        {
            if (objectPoints == null || imagePoints1 == null || imagePoints2 == null ||
                objectPoints.Count == 0 || imagePoints1.Count == 0 || imagePoints2.Count == 0 ||
                objectPoints.Count != imagePoints1.Count || imagePoints1.Count != imagePoints2.Count)
            {
                throw new ArgumentException("Некорректные данные для калибровки");
            }

            return await Task.Run(() =>
            {
                try
                {
                    Debug.WriteLine($"Начало стерео калибровки с {objectPoints.Count} парами изображений");

                    // Матрицы камер и коэффициенты искажения
                    var cameraMatrix1 = new Mat();
                    var cameraMatrix2 = new Mat();
                    var distCoeffs1 = new Mat();
                    var distCoeffs2 = new Mat();

                    // Стерео параметры
                    var R = new Mat(); // Матрица вращения между камерами
                    var T = new Mat(); // Вектор трансляции между камерами
                    var E = new Mat(); // Существенная матрица
                    var F = new Mat(); // Фундаментальная матрица

                                         // Выполнение стерео калибровки
                     double error = Cv2.StereoCalibrate(
                         objectPoints.ToArray(),
                         imagePoints1.ToArray(),
                         imagePoints2.ToArray(),
                         cameraMatrix1,
                         distCoeffs1,
                         cameraMatrix2,
                         distCoeffs2,
                         imageSize,
                         R,
                         T,
                         E,
                         F,
                         CalibrationFlags.FixIntrinsic
                     );

                    Debug.WriteLine($"Калибровка завершена. Ошибка: {error}");

                    // Создание результата
                    var result = new CalibrationResult
                    {
                        CameraMatrix1 = MatToArray(cameraMatrix1),
                        DistCoeffs1 = MatToVector(distCoeffs1),
                        CameraMatrix2 = MatToArray(cameraMatrix2),
                        DistCoeffs2 = MatToVector(distCoeffs2),
                        R = MatToArray(R),
                        T = MatToVector(T),
                        E = MatToArray(E),
                        F = MatToArray(F),
                        Error = error,
                                                 // Можно добавить дополнительные поля при необходимости
                    };

                    // Освобождение ресурсов
                    cameraMatrix1.Dispose();
                    cameraMatrix2.Dispose();
                    distCoeffs1.Dispose();
                    distCoeffs2.Dispose();
                    R.Dispose();
                    T.Dispose();
                    E.Dispose();
                    F.Dispose();

                    return result;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Ошибка стерео калибровки: {ex.Message}");
                    throw new Exception($"Не удалось выполнить стерео калибровку: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Генерация эталонных точек для шахматной доски
        /// </summary>
        public List<Point3f> GenerateObjectPoints(OpenCvSharp.Size patternSize, float squareSize)
        {
            var points = new List<Point3f>();
            
            for (int y = 0; y < patternSize.Height; y++)
            {
                for (int x = 0; x < patternSize.Width; x++)
                {
                    points.Add(new Point3f(x * squareSize, y * squareSize, 0f));
                }
            }
            
            return points;
        }

        /// <summary>
        /// Поиск углов шахматной доски на изображении
        /// </summary>
        public bool FindChessboardCorners(Mat image, OpenCvSharp.Size patternSize, out Point2f[] corners)
        {
            corners = null;
            
            if (image == null || image.Empty())
                return false;

            try
            {
                return Cv2.FindChessboardCorners(image, patternSize, out corners,
                    ChessboardFlags.AdaptiveThresh | ChessboardFlags.NormalizeImage);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка поиска углов шахматной доски: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Сохранение результатов калибровки в JSON файл
        /// </summary>
        public async Task SaveCalibrationResultAsync(CalibrationResult result, string filePath)
        {
            if (result == null || string.IsNullOrWhiteSpace(filePath))
                return;

            try
            {
                await Task.Run(() =>
                {
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    var json = JsonConvert.SerializeObject(result, Formatting.Indented);
                    File.WriteAllText(filePath, json);
                    
                    Debug.WriteLine($"Результаты калибровки сохранены в: {filePath}");
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка сохранения результатов калибровки: {ex.Message}");
                throw new Exception($"Не удалось сохранить результаты калибровки: {ex.Message}");
            }
        }

        /// <summary>
        /// Загрузка результатов калибровки из JSON файла
        /// </summary>
        public async Task<CalibrationResult> LoadCalibrationResultAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                return await Task.Run(() =>
                {
                    var json = File.ReadAllText(filePath);
                    var result = JsonConvert.DeserializeObject<CalibrationResult>(json);
                    
                    Debug.WriteLine($"Результаты калибровки загружены из: {filePath}");
                    return result;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка загрузки результатов калибровки: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Преобразование Mat в двумерный массив
        /// </summary>
        private double[,] MatToArray(Mat mat)
        {
            if (mat == null || mat.Empty())
                return null;

            try
            {
                var rows = mat.Rows;
                var cols = mat.Cols;
                var array = new double[rows, cols];

                for (int i = 0; i < rows; i++)
                {
                    for (int j = 0; j < cols; j++)
                    {
                        array[i, j] = mat.At<double>(i, j);
                    }
                }

                return array;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка преобразования Mat в массив: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Преобразование Mat в одномерный массив
        /// </summary>
        private double[] MatToVector(Mat mat)
        {
            if (mat == null || mat.Empty())
                return null;

            try
            {
                var total = mat.Total();
                var array = new double[total];

                for (int i = 0; i < total; i++)
                {
                    array[i] = mat.At<double>(i);
                }

                return array;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка преобразования Mat в вектор: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Валидация данных калибровки перед выполнением
        /// </summary>
        public bool ValidateCalibrationData(
            List<Mat> objectPoints,
            List<Mat> imagePoints1,
            List<Mat> imagePoints2)
        {
            if (objectPoints == null || imagePoints1 == null || imagePoints2 == null)
                return false;

            if (objectPoints.Count == 0 || imagePoints1.Count == 0 || imagePoints2.Count == 0)
                return false;

            if (objectPoints.Count != imagePoints1.Count || imagePoints1.Count != imagePoints2.Count)
                return false;

            // Минимальное количество изображений для калибровки
            if (objectPoints.Count < 5)
                return false;

            return true;
        }

        /// <summary>
        /// Получение рекомендуемого количества изображений для качественной калибровки
        /// </summary>
        public int GetRecommendedImageCount()
        {
            return 15; // Рекомендуется не менее 15 пар изображений
        }
    }
} 
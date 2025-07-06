using OpenCvSharp;
using StereoCalibration.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace StereoCalibration.Interfaces
{
    /// <summary>
    /// Интерфейс для сервиса стерео калибровки
    /// Инкапсулирует алгоритмы калибровки стерео системы
    /// </summary>
    public interface ICalibrationService
    {
        /// <summary>
        /// Выполнение стерео калибровки на основе собранных точек
        /// </summary>
        /// <param name="objectPoints">Трёхмерные точки объектов</param>
        /// <param name="imagePoints1">Точки изображения первой камеры</param>
        /// <param name="imagePoints2">Точки изображения второй камеры</param>
        /// <param name="imageSize">Размер изображения</param>
        /// <returns>Результат калибровки</returns>
        Task<CalibrationResult> PerformStereoCalibrationAsync(
            List<Mat> objectPoints,
            List<Mat> imagePoints1, 
            List<Mat> imagePoints2,
                         OpenCvSharp.Size imageSize);

        /// <summary>
        /// Генерация эталонных точек для шахматной доски
        /// </summary>
        /// <param name="patternSize">Размер паттерна (внутренние углы)</param>
        /// <param name="squareSize">Размер квадрата в мм</param>
        /// <returns>Список 3D точек</returns>
                 List<Point3f> GenerateObjectPoints(OpenCvSharp.Size patternSize, float squareSize);

        /// <summary>
        /// Поиск углов шахматной доски на изображении
        /// </summary>
        /// <param name="image">Входное изображение</param>
        /// <param name="patternSize">Размер паттерна</param>
        /// <param name="corners">Найденные углы</param>
        /// <returns>True если углы найдены</returns>
                 bool FindChessboardCorners(Mat image, OpenCvSharp.Size patternSize, out Point2f[] corners);

        /// <summary>
        /// Сохранение результатов калибровки в файл
        /// </summary>
        /// <param name="result">Результат калибровки</param>
        /// <param name="filePath">Путь к файлу</param>
        Task SaveCalibrationResultAsync(CalibrationResult result, string filePath);

        /// <summary>
        /// Загрузка результатов калибровки из файла
        /// </summary>
        /// <param name="filePath">Путь к файлу</param>
        /// <returns>Результат калибровки или null</returns>
        Task<CalibrationResult> LoadCalibrationResultAsync(string filePath);
    }
} 
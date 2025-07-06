using OpenCvSharp;
using OpenCvSharp.Aruco;
using StereoCalibration.Models;
using System.Collections.Generic;

namespace StereoCalibration.Interfaces
{
    /// <summary>
    /// Интерфейс для сервиса обнаружения ArUco маркеров
    /// Отделяет логику детекции маркеров от остального кода
    /// </summary>
    public interface IArUcoDetectionService
    {
        /// <summary>
        /// Обнаружение ArUco маркеров на изображении
        /// </summary>
        /// <param name="image">Входное изображение</param>
        /// <returns>Результат детекции маркеров</returns>
        MarkerDetectionResult DetectMarkers(Mat image);

        /// <summary>
        /// Отрисовка обнаруженных маркеров на изображении
        /// </summary>
        /// <param name="image">Изображение для отрисовки</param>
        /// <param name="detectionResult">Результат детекции</param>
        void DrawDetectedMarkers(Mat image, MarkerDetectionResult detectionResult);

        /// <summary>
        /// Сопоставление маркеров между двумя изображениями по ID
        /// </summary>
        /// <param name="result1">Результат детекции с первого изображения</param>
        /// <param name="result2">Результат детекции со второго изображения</param>
        /// <returns>Словарь сопоставленных маркеров</returns>
        Dictionary<int, (Point2f[] left, Point2f[] right)> MatchMarkers(
            MarkerDetectionResult result1, 
            MarkerDetectionResult result2);

        /// <summary>
        /// Вычисление центра маркера из его углов
        /// </summary>
        /// <param name="corners">Углы маркера</param>
        /// <returns>Центральная точка</returns>
        Point2f CalculateMarkerCenter(Point2f[] corners);
    }
} 
using OpenCvSharp;
using StereoCalibration.Models;
using System.Collections.Generic;

namespace StereoCalibration.Interfaces
{
    /// <summary>
    /// Интерфейс для сервиса 3D триангуляции
    /// Вычисляет 3D координаты точек из стерео изображений
    /// </summary>
    public interface ITriangulationService
    {
        /// <summary>
        /// Триангуляция точек из стерео изображений
        /// </summary>
        /// <param name="leftPoints">Точки на левом изображении</param>
        /// <param name="rightPoints">Точки на правом изображении</param>
        /// <param name="calibrationResult">Данные калибровки</param>
        /// <returns>Список 3D точек</returns>
        List<Point3f> TriangulatePoints(
            Point2f[] leftPoints, 
            Point2f[] rightPoints, 
            CalibrationResult calibrationResult);

        /// <summary>
        /// Вычисление расстояния между двумя 3D точками
        /// </summary>
        /// <param name="point1">Первая точка</param>
        /// <param name="point2">Вторая точка</param>
        /// <returns>Расстояние в миллиметрах</returns>
        double CalculateDistance(Point3f point1, Point3f point2);

        /// <summary>
        /// Фильтрация 3D точек по глубине
        /// </summary>
        /// <param name="points">Исходные точки</param>
        /// <param name="minDepth">Минимальная глубина</param>
        /// <param name="maxDepth">Максимальная глубина</param>
        /// <returns>Отфильтрованные точки</returns>
        List<Point3f> FilterPointsByDepth(List<Point3f> points, double minDepth, double maxDepth);
    }
} 
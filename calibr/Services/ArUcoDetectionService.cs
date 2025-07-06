using OpenCvSharp;
using OpenCvSharp.Aruco;
using StereoCalibration.Interfaces;
using StereoCalibration.Models;
using System.Collections.Generic;
using System.Linq;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Реализация сервиса обнаружения ArUco маркеров
    /// Инкапсулирует всю логику детекции и обработки маркеров
    /// </summary>
    public class ArUcoDetectionService : IArUcoDetectionService
    {
        private readonly Dictionary _dictionary;
        private DetectorParameters _detectorParameters;

        /// <summary>
        /// Конструктор сервиса детекции ArUco
        /// </summary>
        /// <param name="dictionaryName">Тип словаря маркеров</param>
        public ArUcoDetectionService(PredefinedDictionaryName dictionaryName = PredefinedDictionaryName.Dict6X6_250)
        {
            _dictionary = CvAruco.GetPredefinedDictionary(dictionaryName);
            _detectorParameters = new DetectorParameters
            {
                CornerRefinementMethod = CornerRefineMethod.Subpix,
                CornerRefinementWinSize = 5,
                CornerRefinementMaxIterations = 30,
                CornerRefinementMinAccuracy = 0.1
            };
        }

        /// <summary>
        /// Обнаружение ArUco маркеров на изображении
        /// </summary>
        public MarkerDetectionResult DetectMarkers(Mat image)
        {
            if (image == null || image.Empty())
                return new MarkerDetectionResult();

            try
            {
                Point2f[][] corners, rejectedCandidates;
                int[] ids;

                CvAruco.DetectMarkers(image, _dictionary, out corners, out ids, _detectorParameters, out rejectedCandidates);

                return new MarkerDetectionResult(corners, ids, rejectedCandidates);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка детекции маркеров: {ex.Message}");
                return new MarkerDetectionResult();
            }
        }

        /// <summary>
        /// Отрисовка обнаруженных маркеров на изображении
        /// </summary>
        public void DrawDetectedMarkers(Mat image, MarkerDetectionResult detectionResult)
        {
            if (image == null || image.Empty() || detectionResult == null || !detectionResult.HasMarkers)
                return;

            try
            {
                CvAruco.DrawDetectedMarkers(image, detectionResult.Corners, detectionResult.Ids);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка отрисовки маркеров: {ex.Message}");
            }
        }

        /// <summary>
        /// Сопоставление маркеров между двумя изображениями по ID
        /// </summary>
        public Dictionary<int, (Point2f[] left, Point2f[] right)> MatchMarkers(
            MarkerDetectionResult result1, 
            MarkerDetectionResult result2)
        {
            var matchedMarkers = new Dictionary<int, (Point2f[], Point2f[])>();

            if (result1 == null || result2 == null || 
                !result1.HasMarkers || !result2.HasMarkers)
                return matchedMarkers;

            try
            {
                // Поиск совпадающих ID маркеров
                for (int i = 0; i < result1.Ids.Length; i++)
                {
                    int markerId = result1.Ids[i];
                    
                    // Ищем этот же маркер во втором результате
                    for (int j = 0; j < result2.Ids.Length; j++)
                    {
                        if (result2.Ids[j] == markerId)
                        {
                            matchedMarkers[markerId] = (result1.Corners[i], result2.Corners[j]);
                            break;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка сопоставления маркеров: {ex.Message}");
            }

            return matchedMarkers;
        }

        /// <summary>
        /// Вычисление центра маркера из его углов
        /// </summary>
        public Point2f CalculateMarkerCenter(Point2f[] corners)
        {
            if (corners == null || corners.Length != 4)
                return new Point2f(0, 0);

            try
            {
                // Вычисляем геометрический центр из 4 углов
                float centerX = corners.Sum(c => c.X) / 4.0f;
                float centerY = corners.Sum(c => c.Y) / 4.0f;

                return new Point2f(centerX, centerY);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка вычисления центра маркера: {ex.Message}");
                return new Point2f(0, 0);
            }
        }

        /// <summary>
        /// Получение параметров детектора (для настройки)
        /// </summary>
        public DetectorParameters GetDetectorParameters()
        {
            return _detectorParameters;
        }

        /// <summary>
        /// Установка параметров детектора
        /// </summary>
        public void SetCornerRefinementParameters(int winSize, int maxIterations, double minAccuracy)
        {
            _detectorParameters.CornerRefinementWinSize = winSize;
            _detectorParameters.CornerRefinementMaxIterations = maxIterations;
            _detectorParameters.CornerRefinementMinAccuracy = minAccuracy;
        }
    }
} 
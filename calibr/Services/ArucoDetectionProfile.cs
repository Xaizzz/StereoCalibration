using OpenCvSharp.Aruco;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Единый профиль ArUco-детектора для всех частей приложения.
    /// 
    /// Раньше параметры создавались отдельно в нескольких классах, что могло
    /// приводить к разному поведению детекта. Теперь все сервисы используют один
    /// словарь и один набор DetectorParameters.
    /// </summary>
    public static class ArucoDetectionProfile
    {
        /// <summary>
        /// Возвращает предопределённый словарь ArUco 6x6 на 250 ID.
        /// Все физические маркеры должны быть сгенерированы из этого же словаря.
        /// </summary>
        public static Dictionary CreateDictionary()
        {
            return CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict6X6_250);
        }

        /// <summary>
        /// Создаёт усиленные параметры детектора.
        /// 
        /// Настройки расширяют адаптивные пороги и включают subpixel refinement,
        /// чтобы детект был устойчивее к шуму, бликам и небольшому размытию.
        /// </summary>
        public static DetectorParameters CreateParameters()
        {
            return new DetectorParameters
            {
                AdaptiveThreshWinSizeMin = 3,
                AdaptiveThreshWinSizeMax = 35,
                AdaptiveThreshWinSizeStep = 8,
                AdaptiveThreshConstant = 7,
                MinMarkerPerimeterRate = 0.02,
                MaxMarkerPerimeterRate = 4.0,
                PolygonalApproxAccuracyRate = 0.03,
                MinCornerDistanceRate = 0.03,
                MinDistanceToBorder = 3,
                MinMarkerDistanceRate = 0.05,
                CornerRefinementMethod = CornerRefineMethod.Subpix,
                CornerRefinementWinSize = 5,
                CornerRefinementMaxIterations = 30,
                CornerRefinementMinAccuracy = 0.05,
                MarkerBorderBits = 1,
                PerspectiveRemovePixelPerCell = 8,
                PerspectiveRemoveIgnoredMarginPerCell = 0.13,
                MaxErroneousBitsInBorderRate = 0.35,
                MinOtsuStdDev = 5.0,
                ErrorCorrectionRate = 0.6
            };
        }
    }
}

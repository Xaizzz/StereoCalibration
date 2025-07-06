using OpenCvSharp;

namespace StereoCalibration.Models
{
    /// <summary>
    /// Результат обнаружения ArUco маркеров на изображении
    /// Инкапсулирует данные детекции для дальнейшей обработки
    /// </summary>
    public class MarkerDetectionResult
    {
        /// <summary>
        /// Углы обнаруженных маркеров
        /// Каждый маркер представлен массивом из 4 углов
        /// </summary>
        public Point2f[][] Corners { get; set; }

        /// <summary>
        /// Идентификаторы обнаруженных маркеров
        /// Соответствуют массиву углов по индексу
        /// </summary>
        public int[] Ids { get; set; }

        /// <summary>
        /// Отклонённые кандидаты на маркеры
        /// Используется для отладки и анализа качества детекции
        /// </summary>
        public Point2f[][] RejectedCandidates { get; set; }

        /// <summary>
        /// Количество обнаруженных маркеров
        /// </summary>
        public int Count => Ids?.Length ?? 0;

        /// <summary>
        /// Проверка наличия обнаруженных маркеров
        /// </summary>
        public bool HasMarkers => Count > 0;

        /// <summary>
        /// Конструктор по умолчанию
        /// </summary>
        public MarkerDetectionResult()
        {
            Corners = new Point2f[0][];
            Ids = new int[0];
            RejectedCandidates = new Point2f[0][];
        }

        /// <summary>
        /// Конструктор с параметрами
        /// </summary>
        /// <param name="corners">Углы маркеров</param>
        /// <param name="ids">Идентификаторы маркеров</param>
        /// <param name="rejectedCandidates">Отклонённые кандидаты</param>
        public MarkerDetectionResult(Point2f[][] corners, int[] ids, Point2f[][] rejectedCandidates = null)
        {
            Corners = corners ?? new Point2f[0][];
            Ids = ids ?? new int[0];
            RejectedCandidates = rejectedCandidates ?? new Point2f[0][];
        }

        /// <summary>
        /// Получение углов маркера по его ID
        /// </summary>
        /// <param name="markerId">Идентификатор маркера</param>
        /// <returns>Углы маркера или null если не найден</returns>
        public Point2f[] GetMarkerCorners(int markerId)
        {
            for (int i = 0; i < Ids.Length; i++)
            {
                if (Ids[i] == markerId)
                {
                    return Corners[i];
                }
            }
            return null;
        }

        /// <summary>
        /// Проверка наличия маркера с определённым ID
        /// </summary>
        /// <param name="markerId">Идентификатор маркера</param>
        /// <returns>True если маркер найден</returns>
        public bool ContainsMarker(int markerId)
        {
            for (int i = 0; i < Ids.Length; i++)
            {
                if (Ids[i] == markerId)
                {
                    return true;
                }
            }
            return false;
        }
    }
} 
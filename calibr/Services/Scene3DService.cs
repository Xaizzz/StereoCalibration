using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Центральная модель данных для вкладки 3D сцены.
    /// 
    /// Сервис не рисует WPF/Helix-объекты напрямую. Он хранит только вычисленное
    /// состояние сцены: положение камер, центр стереопары, позиции маркеров,
    /// стабильные номера маркеров для UI и признаки готовности калибровки.
    /// Отрисовка выполняется в <see cref="StereoCalibration.UI.Scene3DUserControl"/>,
    /// который подписан на <see cref="OnSceneUpdated"/>.
    /// 
    /// Важная особенность: входные координаты маркеров приходят из
    /// <see cref="ImageProcessingService"/> в системе координат первой камеры.
    /// Перед показом они переводятся в удобный визуальный базис стереопары:
    /// X — линия между камерами, Y — общее направление взгляда, Z — вверх.
    /// </summary>
    public class Scene3DService
    {
        #region События
        /// <summary>
        /// Сигнал для UI о том, что изменились камеры, маркеры или статус калибровки.
        /// Событие намеренно не передаёт параметры: визуальный контрол сам читает
        /// актуальные свойства сервиса, чтобы не копировать большие структуры сцены.
        /// </summary>
        public event Action? OnSceneUpdated;
        #endregion

        #region Данные сцены
        /// <summary>Позиция первой камеры (смещена от центра стереосистемы)</summary>
        public Point3D Camera1Position { get; private set; } = new Point3D(0, 0, 0);
        
        /// <summary>Позиция второй камеры</summary>
        public Point3D Camera2Position { get; private set; } = new Point3D(0, 0, 0);
        
        /// <summary>Центр стереосистемы</summary>
        public Point3D StereoCenter { get; private set; } = new Point3D(0, 0, 0);
        
        /// <summary>Текущие позиции ArUco маркеров</summary>
        public Dictionary<int, Point3D> MarkerPositions { get; private set; } = new Dictionary<int, Point3D>();

        /// <summary>Стабильные отображаемые номера маркеров: ArUco ID -> номер в UI</summary>
        public Dictionary<int, int> MarkerDisplayIndices { get; private set; } = new Dictionary<int, int>();

        /// <summary>
        /// Номер последнего кадра, где конкретный ArUco ID был реально триангулирован.
        /// Нужен для удержания маркера при кратковременной потере детекта.
        /// </summary>
        private readonly Dictionary<int, int> _markerLastSeenFrame = new Dictionary<int, int>();

        /// <summary>
        /// Последние сглаженные позиции. Отдельный словарь нужен, чтобы не смешивать
        /// сырые измерения из триангуляции и отображаемое положение в сцене.
        /// </summary>
        private readonly Dictionary<int, Point3D> _smoothedMarkerPositions = new Dictionary<int, Point3D>();
        private int _currentFrameIndex = 0;
        /// <summary>
        /// Сколько кадров маркер может отсутствовать в новых измерениях, оставаясь
        /// в сцене на последней сглаженной позиции. Это уменьшает мерцание.
        /// </summary>
        private const int MissingFramesBeforeRemoval = 10;
        private const double MarkerSmoothingAlpha = 0.25;
        private const double MarkerFastSmoothingAlpha = 0.65;
        private const double FastMovementThresholdMm = 80.0;
        private Point3D _stereoCenterInCamera1 = new Point3D(0, 0, 0);
        private Vector3D _sceneXAxisInCamera1 = new Vector3D(1, 0, 0);
        private Vector3D _sceneYAxisInCamera1 = new Vector3D(0, 0, 1);
        private Vector3D _sceneZAxisInCamera1 = new Vector3D(0, -1, 0);
        
        /// <summary>Флаг готовности калибровки</summary>
        public bool IsCalibrated { get; private set; } = false;
        #endregion

        /// <summary>
        /// Пересчитывает виртуальную 3D сцену после загрузки или выполнения калибровки.
        /// 
        /// OpenCV хранит стереорезультат как переход из камеры 1 в камеру 2:
        /// X2 = R * X1 + T. Для отображения камер удобнее знать реальный центр
        /// второй камеры в системе первой камеры, поэтому ниже вычисляется
        /// C2 = -R^T * T, строится базис сцены и уже затем камеры ставятся
        /// симметрично относительно визуального центра.
        /// </summary>
        /// <param name="calibrationResult">Результаты стерео калибровки</param>
        public void UpdateCameraPositions(CalibrationResult calibrationResult)
        {
            if (calibrationResult == null)
            {
                IsCalibrated = false;
                return;
            }

            try
            {
                // Центр стереосистемы теперь в начале координат (0, 0, 0)
                StereoCenter = new Point3D(0, 0, 0);
                
                // OpenCV возвращает R,T как X2 = R * X1 + T.
                // Реальный центр второй камеры в системе первой камеры: C2 = -R^T * T.
                var camera2InCamera1 = CalculateSecondCameraCenter(calibrationResult);
                var visualBaseline = DistanceFromOrigin(camera2InCamera1);
                BuildSceneBasis(camera2InCamera1, calibrationResult);
                _stereoCenterInCamera1 = new Point3D(
                    camera2InCamera1.X / 2.0,
                    camera2InCamera1.Y / 2.0,
                    camera2InCamera1.Z / 2.0
                );

                // Визуальная сцена строится в базисе реальной стереопары:
                // X - линия между камерами, Y - вперед, Z - вверх.
                Camera1Position = new Point3D(-visualBaseline / 2.0, 0, 0);
                
                Camera2Position = new Point3D(visualBaseline / 2.0, 0, 0);
                
                // При изменении калибровки очищаем существующие маркеры
                // чтобы они заново рассчитались с правильными координатами в новой системе
                if (MarkerPositions.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"Scene3D: Очищаем {MarkerPositions.Count} существующих маркеров для пересчета");
                    MarkerPositions.Clear();
                }
                _markerLastSeenFrame.Clear();
                _smoothedMarkerPositions.Clear();
                _currentFrameIndex = 0;
                
                IsCalibrated = true;
                OnSceneUpdated?.Invoke();
                
                System.Diagnostics.Debug.WriteLine($"Scene3D: Обновлены позиции камер относительно центра:");
                System.Diagnostics.Debug.WriteLine($"  Центр: (0, 0, 0)");
                System.Diagnostics.Debug.WriteLine($"  Камера 1: ({Camera1Position.X:F1}, {Camera1Position.Y:F1}, {Camera1Position.Z:F1})");
                System.Diagnostics.Debug.WriteLine($"  Камера 2: ({Camera2Position.X:F1}, {Camera2Position.Y:F1}, {Camera2Position.Z:F1})");
                System.Diagnostics.Debug.WriteLine($"  Центр камеры 2 в СК камеры 1: ({camera2InCamera1.X:F1}, {camera2InCamera1.Y:F1}, {camera2InCamera1.Z:F1})");
                System.Diagnostics.Debug.WriteLine($"  Визуальная базовая линия: {visualBaseline:F1} мм");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления позиций камер: {ex.Message}");
                IsCalibrated = false;
            }
        }

        /// <summary>
        /// Обновление позиции ArUco маркера
        /// </summary>
        /// <param name="markerId">ID маркера</param>
        /// <param name="x">X координата в миллиметрах (в системе координат первой камеры)</param>
        /// <param name="y">Y координата в миллиметрах (в системе координат первой камеры)</param>
        /// <param name="z">Z координата в миллиметрах (в системе координат первой камеры)</param>
        public void UpdateMarkerPosition(int markerId, double x, double y, double z)
        {
            try
            {
                RegisterDisplayIndices(new[] { markerId });
                var position = GetSmoothedMarkerPosition(markerId, ConvertFromCamera1ToScene(x, y, z));
                MarkerPositions[markerId] = position;
                OnSceneUpdated?.Invoke();
                
                // Дополнительная отладочная информация
                double distanceFromCenter = Math.Sqrt(position.X * position.X + position.Y * position.Y + position.Z * position.Z);
                double distanceFromCamera1 = Math.Sqrt(x * x + y * y + z * z);
                
                System.Diagnostics.Debug.WriteLine($"Scene3D: Маркер {GetMarkerDisplayName(markerId)} (ArUco ID {markerId}):");
                System.Diagnostics.Debug.WriteLine($"  От камеры 1: ({x:F1}, {y:F1}, {z:F1}) мм, расстояние: {distanceFromCamera1:F1} мм");
                System.Diagnostics.Debug.WriteLine($"  От центра: ({position.X:F1}, {position.Y:F1}, {position.Z:F1}) мм, расстояние: {distanceFromCenter:F1} мм");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления позиции маркера {markerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Пакетно обновляет все маркеры, найденные и триангулированные на текущем кадре.
        /// 
        /// Этот путь используется основным видеопотоком. Он важнее одиночного
        /// <see cref="UpdateMarkerPosition"/>, потому что увеличивает индекс кадра,
        /// обновляет TTL маркеров и удаляет только те ID, которые отсутствуют
        /// дольше <see cref="MissingFramesBeforeRemoval"/> кадров.
        /// </summary>
        public void UpdateMarkerPositions(IReadOnlyDictionary<int, (double X, double Y, double Z)> markerPositions)
        {
            try
            {
                _currentFrameIndex++;

                if (markerPositions.Count > 0)
                {
                    RegisterDisplayIndices(markerPositions.Keys);
                }

                foreach (var marker in markerPositions)
                {
                    // Триангуляция возвращает координаты в СК камеры 1. Перед записью
                    // в MarkerPositions переводим их в визуальный базис стереопары
                    // и сглаживаем, чтобы 3D-сфера не дрожала от шума детекции.
                    var measuredPosition = ConvertFromCamera1ToScene(marker.Value.X, marker.Value.Y, marker.Value.Z);
                    MarkerPositions[marker.Key] = GetSmoothedMarkerPosition(marker.Key, measuredPosition);
                    _markerLastSeenFrame[marker.Key] = _currentFrameIndex;
                }

                // Если в текущем кадре маркера нет, он всё равно остаётся в
                // MarkerPositions до истечения TTL. Поэтому UI видит устойчивую
                // последнюю позицию, а не удаление/создание сферы каждый раз.
                var staleMarkerIds = MarkerPositions.Keys
                    .Where(id => !_markerLastSeenFrame.TryGetValue(id, out var lastSeenFrame) ||
                                 _currentFrameIndex - lastSeenFrame > MissingFramesBeforeRemoval)
                    .ToList();

                foreach (var markerId in staleMarkerIds)
                {
                    MarkerPositions.Remove(markerId);
                    _markerLastSeenFrame.Remove(markerId);
                    _smoothedMarkerPositions.Remove(markerId);
                }

                OnSceneUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка пакетного обновления маркеров: {ex.Message}");
            }
        }

        /// <summary>
        /// Удаление маркера из сцены
        /// </summary>
        /// <param name="markerId">ID маркера</param>
        public void RemoveMarker(int markerId)
        {
            if (MarkerPositions.ContainsKey(markerId))
            {
                MarkerPositions.Remove(markerId);
                _markerLastSeenFrame.Remove(markerId);
                _smoothedMarkerPositions.Remove(markerId);
                OnSceneUpdated?.Invoke();
            }
        }

        /// <summary>
        /// Очистка всех маркеров
        /// </summary>
        public void ClearMarkers()
        {
            if (MarkerPositions.Count > 0)
            {
                MarkerPositions.Clear();
                _markerLastSeenFrame.Clear();
                _smoothedMarkerPositions.Clear();
                OnSceneUpdated?.Invoke();
            }
        }

        public string GetMarkerDisplayName(int markerId)
        {
            return MarkerDisplayIndices.TryGetValue(markerId, out var displayIndex)
                ? $"Маркер {displayIndex}"
                : $"Маркер ?";
        }

        /// <summary>
        /// Получение информации о сцене для отладки
        /// </summary>
        public string GetSceneInfo()
        {
            if (!IsCalibrated)
                return "Калибровка не выполнена";

            var info = $"Камера 1: ({Camera1Position.X:F1}, {Camera1Position.Y:F1}, {Camera1Position.Z:F1}) мм\n";
            info += $"Камера 2: ({Camera2Position.X:F1}, {Camera2Position.Y:F1}, {Camera2Position.Z:F1}) мм\n";
            info += $"Центр: ({StereoCenter.X:F1}, {StereoCenter.Y:F1}, {StereoCenter.Z:F1}) мм\n";
            info += $"Маркеров: {MarkerPositions.Count}";
            
            return info;
        }

        /// <summary>
        /// Сброс состояния сцены
        /// </summary>
        public void Reset()
        {
            // При сбросе все элементы возвращаются в начало координат
            Camera1Position = new Point3D(0, 0, 0);
            Camera2Position = new Point3D(0, 0, 0);
            StereoCenter = new Point3D(0, 0, 0);
            MarkerPositions.Clear();
            MarkerDisplayIndices.Clear();
            _markerLastSeenFrame.Clear();
            _smoothedMarkerPositions.Clear();
            _currentFrameIndex = 0;
            IsCalibrated = false;
            OnSceneUpdated?.Invoke();
            
            System.Diagnostics.Debug.WriteLine("Scene3D: Сцена сброшена");
        }

        /// <summary>
        /// Вычисляет центр второй камеры в системе координат первой камеры.
        /// 
        /// В OpenCV вектор T описывает перенос точек из СК камеры 1 в СК камеры 2:
        /// X2 = R * X1 + T. Центр камеры 2 — это точка, которая в собственной
        /// системе камеры 2 имеет координаты (0,0,0). Решая 0 = R*C2 + T,
        /// получаем C2 = -R^T*T.
        /// </summary>
        private Point3D CalculateSecondCameraCenter(CalibrationResult calibrationResult)
        {
            var r = calibrationResult.R;
            var t = calibrationResult.T;

            return new Point3D(
                -(r[0, 0] * t[0] + r[1, 0] * t[1] + r[2, 0] * t[2]),
                -(r[0, 1] * t[0] + r[1, 1] * t[1] + r[2, 1] * t[2]),
                -(r[0, 2] * t[0] + r[1, 2] * t[1] + r[2, 2] * t[2])
            );
        }

        /// <summary>
        /// Строит ортонормированный базис визуальной сцены внутри системы координат камеры 1.
        /// 
        /// Задача метода — сделать 3D-сцену интуитивной: камеры расположены слева
        /// и справа по оси X, центральная линия стереопары идёт вперёд по общей
        /// области обзора, а ось Z направлена вверх. Без такого базиса маркеры
        /// были бы математически корректны, но визуально могли выглядеть смещёнными
        /// относительно двух камер.
        /// </summary>
        private void BuildSceneBasis(Point3D camera2InCamera1, CalibrationResult calibrationResult)
        {
            var baseline = new Vector3D(camera2InCamera1.X, camera2InCamera1.Y, camera2InCamera1.Z);
            if (baseline.Length < 1e-6)
            {
                _sceneXAxisInCamera1 = new Vector3D(1, 0, 0);
                _sceneYAxisInCamera1 = new Vector3D(0, 0, 1);
                _sceneZAxisInCamera1 = new Vector3D(0, -1, 0);
                return;
            }

            baseline.Normalize();
            _sceneXAxisInCamera1 = baseline;

            // Центральная линия стереопары должна идти по общей зоне обзора,
            // поэтому берем биссектрису оптических осей двух камер, а не только
            // направление первой камеры.
            var camera1Forward = new Vector3D(0, 0, 1);
            var camera2Forward = TransformCamera2DirectionToCamera1(new Vector3D(0, 0, 1), calibrationResult);
            if (Vector3D.DotProduct(camera1Forward, camera2Forward) < 0)
            {
                camera2Forward = -camera2Forward;
            }

            var commonForward = camera1Forward + camera2Forward;
            if (commonForward.Length < 1e-6)
            {
                commonForward = camera1Forward;
            }
            commonForward.Normalize();

            var sceneY = commonForward - Vector3D.DotProduct(commonForward, _sceneXAxisInCamera1) * _sceneXAxisInCamera1;
            if (sceneY.Length < 1e-6)
            {
                sceneY = camera1Forward - Vector3D.DotProduct(camera1Forward, _sceneXAxisInCamera1) * _sceneXAxisInCamera1;
            }
            sceneY.Normalize();
            _sceneYAxisInCamera1 = sceneY;

            var camera1Up = new Vector3D(0, -1, 0);
            var camera2Up = TransformCamera2DirectionToCamera1(new Vector3D(0, -1, 0), calibrationResult);
            if (Vector3D.DotProduct(camera1Up, camera2Up) < 0)
            {
                camera2Up = -camera2Up;
            }

            var cameraUp = camera1Up + camera2Up;
            if (cameraUp.Length < 1e-6)
            {
                cameraUp = camera1Up;
            }
            cameraUp.Normalize();

            var sceneZ = cameraUp
                - Vector3D.DotProduct(cameraUp, _sceneXAxisInCamera1) * _sceneXAxisInCamera1
                - Vector3D.DotProduct(cameraUp, _sceneYAxisInCamera1) * _sceneYAxisInCamera1;

            if (sceneZ.Length < 1e-6)
            {
                sceneZ = Vector3D.CrossProduct(_sceneXAxisInCamera1, _sceneYAxisInCamera1);
                if (Vector3D.DotProduct(sceneZ, cameraUp) < 0)
                {
                    sceneZ = -sceneZ;
                }
            }

            sceneZ.Normalize();
            _sceneZAxisInCamera1 = sceneZ;
        }

        private Vector3D TransformCamera2DirectionToCamera1(Vector3D directionInCamera2, CalibrationResult calibrationResult)
        {
            var r = calibrationResult.R;

            // Для направлений перенос из СК камеры 2 в СК камеры 1 выполняется через R^T.
            return new Vector3D(
                r[0, 0] * directionInCamera2.X + r[1, 0] * directionInCamera2.Y + r[2, 0] * directionInCamera2.Z,
                r[0, 1] * directionInCamera2.X + r[1, 1] * directionInCamera2.Y + r[2, 1] * directionInCamera2.Z,
                r[0, 2] * directionInCamera2.X + r[1, 2] * directionInCamera2.Y + r[2, 2] * directionInCamera2.Z
            );
        }

        /// <summary>
        /// Переводит 3D-точку из координат камеры 1 в координаты визуальной сцены.
        /// 
        /// Сначала точка смещается относительно середины базовой линии камер,
        /// затем проецируется на оси сцены через скалярные произведения.
        /// </summary>
        private Point3D ConvertFromCamera1ToScene(double x, double y, double z)
        {
            // Координаты триангуляции приходят в системе камеры 1.
            // Для отображения переводим их в базис стереопары, чтобы камеры,
            // маркеры и центр находились в одной визуальной системе координат.
            var relativeToStereoCenter = new Vector3D(
                x - _stereoCenterInCamera1.X,
                y - _stereoCenterInCamera1.Y,
                z - _stereoCenterInCamera1.Z
            );

            return new Point3D(
                Vector3D.DotProduct(relativeToStereoCenter, _sceneXAxisInCamera1),
                Vector3D.DotProduct(relativeToStereoCenter, _sceneYAxisInCamera1),
                Vector3D.DotProduct(relativeToStereoCenter, _sceneZAxisInCamera1)
            );
        }

        private static double DistanceFromOrigin(Point3D point)
        {
            return Math.Sqrt(point.X * point.X + point.Y * point.Y + point.Z * point.Z);
        }

        /// <summary>
        /// Сглаживает позицию маркера линейной интерполяцией.
        /// 
        /// При обычном шуме используется малый alpha, чтобы подавить дрожание.
        /// При большом скачке alpha увеличивается, иначе реальное быстрое движение
        /// таблички будет заметно запаздывать.
        /// </summary>
        private Point3D GetSmoothedMarkerPosition(int markerId, Point3D measuredPosition)
        {
            if (!_smoothedMarkerPositions.TryGetValue(markerId, out var previousPosition))
            {
                _smoothedMarkerPositions[markerId] = measuredPosition;
                return measuredPosition;
            }

            var movement = Distance(previousPosition, measuredPosition);
            var alpha = movement > FastMovementThresholdMm ? MarkerFastSmoothingAlpha : MarkerSmoothingAlpha;
            var smoothedPosition = Lerp(previousPosition, measuredPosition, alpha);
            _smoothedMarkerPositions[markerId] = smoothedPosition;
            return smoothedPosition;
        }

        private static Point3D Lerp(Point3D from, Point3D to, double alpha)
        {
            return new Point3D(
                from.X + (to.X - from.X) * alpha,
                from.Y + (to.Y - from.Y) * alpha,
                from.Z + (to.Z - from.Z) * alpha
            );
        }

        private static double Distance(Point3D a, Point3D b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// Назначает стабильные отображаемые номера "Маркер 1", "Маркер 2"...
        /// на основе ArUco ID. Номера нужны UI и таблицам, чтобы человеку было
        /// проще читать сцену, не запоминая исходные ID словаря.
        /// </summary>
        private void RegisterDisplayIndices(IEnumerable<int> markerIds)
        {
            var knownMarkerIds = MarkerDisplayIndices.Keys
                .Concat(markerIds)
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            MarkerDisplayIndices.Clear();

            for (int i = 0; i < knownMarkerIds.Count; i++)
            {
                MarkerDisplayIndices[knownMarkerIds[i]] = i + 1;
            }
        }
    }
}
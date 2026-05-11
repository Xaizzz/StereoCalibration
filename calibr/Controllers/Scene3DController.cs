using System;
using System.Collections.Generic;
using StereoCalibration.Models;
using StereoCalibration.Services;

namespace StereoCalibration.Controllers
{
    /// <summary>
    /// Контроллер для управления 3D сценой.
    /// Связывает данные калибровки и маркеров с 3D визуализацией.
    /// </summary>
    public class Scene3DController
    {
        #region События
        /// <summary>Событие обновления 3D сцены для UI</summary>
        public event Action? OnSceneUpdated;
        #endregion

        #region Сервисы
        private readonly Scene3DService _scene3DService;
        #endregion

        #region Состояние
        private CalibrationResult? _currentCalibration;
        #endregion

        /// <summary>
        /// Конструктор контроллера
        /// </summary>
        public Scene3DController()
        {
            _scene3DService = new Scene3DService();
            
            // Подписываемся на обновления сцены
            _scene3DService.OnSceneUpdated += () => OnSceneUpdated?.Invoke();
        }

        /// <summary>
        /// Получение сервиса 3D сцены
        /// </summary>
        public Scene3DService GetScene3DService()
        {
            return _scene3DService;
        }

        /// <summary>
        /// Обновление калибровки
        /// </summary>
        /// <param name="calibrationResult">Результаты калибровки</param>
        public void UpdateCalibration(CalibrationResult calibrationResult)
        {
            try
            {
                _currentCalibration = calibrationResult;
                _scene3DService.UpdateCameraPositions(calibrationResult);
                
                System.Diagnostics.Debug.WriteLine("Scene3DController: Калибровка обновлена");
                System.Diagnostics.Debug.WriteLine(_scene3DService.GetSceneInfo());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления калибровки в 3D сцене: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновление позиции маркера
        /// </summary>
        /// <param name="markerId">ID маркера</param>
        /// <param name="x">X координата в миллиметрах</param>
        /// <param name="y">Y координата в миллиметрах</param>
        /// <param name="z">Z координата в миллиметрах</param>
        public void UpdateMarker(int markerId, double x, double y, double z)
        {
            try
            {
                _scene3DService.UpdateMarkerPosition(markerId, x, y, z);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления маркера {markerId} в 3D сцене: {ex.Message}");
            }
        }

        /// <summary>
        /// Пакетное обновление позиций маркеров за один кадр.
        /// </summary>
        public void UpdateMarkers(IReadOnlyDictionary<int, (double X, double Y, double Z)> markerPositions)
        {
            try
            {
                _scene3DService.UpdateMarkerPositions(markerPositions);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка пакетного обновления маркеров в 3D сцене: {ex.Message}");
            }
        }

        /// <summary>
        /// Удаление маркера
        /// </summary>
        /// <param name="markerId">ID маркера</param>
        public void RemoveMarker(int markerId)
        {
            _scene3DService.RemoveMarker(markerId);
        }

        /// <summary>
        /// Очистка всех маркеров
        /// </summary>
        public void ClearAllMarkers()
        {
            _scene3DService.ClearMarkers();
        }

        /// <summary>
        /// Проверка готовности 3D сцены
        /// </summary>
        public bool IsSceneReady()
        {
            return _scene3DService.IsCalibrated;
        }

        /// <summary>
        /// Получение информации о сцене
        /// </summary>
        public string GetSceneInfo()
        {
            return _scene3DService.GetSceneInfo();
        }

        /// <summary>
        /// Сброс сцены
        /// </summary>
        public void ResetScene()
        {
            _scene3DService.Reset();
            _currentCalibration = null;
        }

        /// <summary>
        /// Получение текущих позиций для отладки
        /// </summary>
        public void LogCurrentPositions()
        {
            if (!_scene3DService.IsCalibrated)
            {
                System.Diagnostics.Debug.WriteLine("Scene3D: Калибровка не выполнена");
                return;
            }

            var service = _scene3DService;
            System.Diagnostics.Debug.WriteLine("=== ПОЗИЦИИ 3D СЦЕНЫ ===");
            System.Diagnostics.Debug.WriteLine($"Камера 1: ({service.Camera1Position.X:F2}, {service.Camera1Position.Y:F2}, {service.Camera1Position.Z:F2})");
            System.Diagnostics.Debug.WriteLine($"Камера 2: ({service.Camera2Position.X:F2}, {service.Camera2Position.Y:F2}, {service.Camera2Position.Z:F2})");
            System.Diagnostics.Debug.WriteLine($"Центр: ({service.StereoCenter.X:F2}, {service.StereoCenter.Y:F2}, {service.StereoCenter.Z:F2})");
            
            foreach (var marker in service.MarkerPositions)
            {
                System.Diagnostics.Debug.WriteLine($"Маркер {marker.Key}: ({marker.Value.X:F2}, {marker.Value.Y:F2}, {marker.Value.Z:F2})");
            }
            System.Diagnostics.Debug.WriteLine("========================");
        }
    }
}
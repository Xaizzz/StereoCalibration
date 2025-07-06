using OpenCvSharp;
using StereoCalibration.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StereoCalibration.Interfaces
{
    /// <summary>
    /// Интерфейс для управления камерами в стерео системе
    /// Абстрагирует работу с камерами от конкретной реализации OpenCV
    /// </summary>
    public interface ICameraService : IDisposable
    {
        /// <summary>
        /// Событие получения нового кадра от камеры
        /// </summary>
        event EventHandler<FrameReceivedEventArgs> FrameReceived;

        /// <summary>
        /// Событие изменения статуса подключения камеры
        /// </summary>
        event EventHandler<CameraStatusEventArgs> CameraStatusChanged;

        /// <summary>
        /// Обнаружение доступных камер в системе
        /// </summary>
        /// <returns>Список доступных камер</returns>
        Task<List<CameraInfo>> DetectAvailableCamerasAsync();

        /// <summary>
        /// Подключение к камере
        /// </summary>
        /// <param name="cameraId">Идентификатор камеры</param>
        /// <param name="cancellationToken">Токен отмены операции</param>
        /// <returns>True если подключение успешно</returns>
        Task<bool> ConnectCameraAsync(int cameraId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Отключение камеры
        /// </summary>
        /// <param name="cameraId">Идентификатор камеры</param>
        void DisconnectCamera(int cameraId);

        /// <summary>
        /// Проверка статуса подключения камеры
        /// </summary>
        /// <param name="cameraId">Идентификатор камеры</param>
        /// <returns>True если камера подключена</returns>
        bool IsCameraConnected(int cameraId);

        /// <summary>
        /// Начать захват кадров с камеры
        /// </summary>
        /// <param name="cameraId">Идентификатор камеры</param>
        void StartCapture(int cameraId);

        /// <summary>
        /// Остановить захват кадров с камеры
        /// </summary>
        /// <param name="cameraId">Идентификатор камеры</param>
        void StopCapture(int cameraId);

        /// <summary>
        /// Получить текущий кадр с камеры
        /// </summary>
        /// <param name="cameraId">Идентификатор камеры</param>
        /// <returns>Кадр или null если камера недоступна</returns>
        Mat GetCurrentFrame(int cameraId);

        /// <summary>
        /// Настройка разрешения камеры
        /// </summary>
        /// <param name="cameraId">Идентификатор камеры</param>
        /// <param name="width">Ширина</param>
        /// <param name="height">Высота</param>
        void SetResolution(int cameraId, int width, int height);
    }

    /// <summary>
    /// Аргументы события получения кадра
    /// </summary>
    public class FrameReceivedEventArgs : EventArgs
    {
        public int CameraId { get; set; }
        public Mat Frame { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Аргументы события изменения статуса камеры
    /// </summary>
    public class CameraStatusEventArgs : EventArgs
    {
        public int CameraId { get; set; }
        public bool IsConnected { get; set; }
        public string ErrorMessage { get; set; }
    }
} 
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Обёртка над одной OpenCV-камерой.
    /// 
    /// Класс используется для поиска камер и может использоваться для предпросмотра.
    /// Основной стереопоток в приложении работает через <see cref="StereoCameraService"/>,
    /// но `CameraManager` остаётся полезным инфраструктурным компонентом для работы
    /// с одной камерой.
    /// </summary>
    public class CameraManager : IDisposable
    {
        #region Приватные поля
        /// <summary>Объект захвата OpenCV для работы с камерой</summary>
        private VideoCapture? _capture;
        /// <summary>Индекс камеры в системе</summary>
        private readonly int _cameraIndex;
        /// <summary>Флаг состояния подключения к камере</summary>
        private bool _isConnected = false;
        #endregion

        #region Публичные свойства
        /// <summary>Индекс камеры в системе</summary>
        public int CameraIndex => _cameraIndex;
        /// <summary>Статус подключения к камере</summary>
        public bool IsConnected => _isConnected;
        #endregion

        public CameraManager(int cameraIndex)
        {
            _cameraIndex = cameraIndex;
        }

        /// <summary>
        /// Перебирает индексы камер от 0 до maxCameras-1 и возвращает те,
        /// которые удалось открыть через OpenCV.
        /// </summary>
        public static List<int> DetectAvailableCameras(int maxCameras = 10)
        {
            var availableCameras = new List<int>();
            for (int i = 0; i < maxCameras; i++)
            {
                using (var cap = new VideoCapture(i))
                {
                    if (cap.IsOpened())
                    {
                        availableCameras.Add(i);
                        cap.Release();
                    }
                }
            }
            return availableCameras;
        }

        /// <summary>
        /// Асинхронно открывает камеру и проверяет, что она действительно отдаёт кадр.
        /// 
        /// Открытие выполняется в Task.Run, чтобы потенциально долгие операции
        /// драйвера камеры не блокировали UI-поток.
        /// </summary>
        public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
        {
            if (_isConnected)
            {
                await DisconnectAsync();
            }

            // Задержка для освобождения ресурсов
            await Task.Delay(200, cancellationToken);

            return await Task.Run(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    _capture = new VideoCapture(_cameraIndex);

                    // Проверяем подключение с несколькими попытками
                    for (int i = 0; i < 5 && !cancellationToken.IsCancellationRequested; i++)
                    {
                        if (_capture.IsOpened())
                            break;
                        Thread.Sleep(200);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (!_capture.IsOpened())
                    {
                        throw new Exception($"Не удалось открыть камеру {_cameraIndex}");
                    }

                    // Проверяем, можем ли мы прочитать кадр
                    using (var testFrame = new Mat())
                    {
                        if (!_capture.Read(testFrame) || testFrame.Empty())
                        {
                            throw new Exception("Камера не передает данные");
                        }
                    }

                    _isConnected = true;
                    return true;
                }
                catch (Exception)
                {
                    if (_capture != null)
                    {
                        _capture.Dispose();
                        _capture = null;
                    }
                    _isConnected = false;
                    throw;
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Освобождает VideoCapture и переводит менеджер в состояние disconnected.
        /// </summary>
        public async Task DisconnectAsync()
        {
            await Task.Run(() =>
            {
                _isConnected = false;
                
                if (_capture != null)
                {
                    try
                    {
                        _capture.Release();
                        _capture.Dispose();
                        _capture = null;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка при освобождении камеры {_cameraIndex}: {ex.Message}");
                    }
                }
            });
        }

        /// <summary>
        /// Возвращает один кадр с камеры.
        /// 
        /// При ошибке возвращается пустой Mat, а не null. Вызывающий код должен
        /// проверять `Mat.Empty()`.
        /// </summary>
        public Mat GetFrame()
        {
            if (!_isConnected || _capture == null || !_capture.IsOpened())
                return new Mat(); // Возвращаем пустой Mat вместо null

            var frame = new Mat();
            if (_capture.Read(frame) && !frame.Empty())
            {
                return frame;
            }
            
            frame?.Dispose();
            return new Mat(); // Возвращаем пустой Mat вместо null
        }

        /// <summary>
        /// Установка параметров камеры
        /// </summary>
        public void SetResolution(int width, int height)
        {
            if (_capture != null && _capture.IsOpened())
            {
                _capture.Set(VideoCaptureProperties.FrameWidth, width);
                _capture.Set(VideoCaptureProperties.FrameHeight, height);
            }
        }

        /// <summary>
        /// Синхронно освобождает ресурсы камеры.
        /// 
        /// Внутри используется Wait() для совместимости с IDisposable; это стоит
        /// учитывать, если вызывать Dispose из UI-потока во время долгого disconnect.
        /// </summary>
        public void Dispose()
        {
            DisconnectAsync().Wait();
        }
    }
}
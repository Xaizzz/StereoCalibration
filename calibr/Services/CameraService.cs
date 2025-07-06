using OpenCvSharp;
using StereoCalibration.Interfaces;
using StereoCalibration.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Реализация сервиса управления камерами
    /// Инкапсулирует всю логику работы с OpenCV VideoCapture
    /// </summary>
    public class CameraService : ICameraService
    {
        // Словарь активных камер: ID -> VideoCapture
        private readonly ConcurrentDictionary<int, VideoCapture> _cameras;
        
        // Словарь информации о камерах: ID -> CameraInfo
        private readonly ConcurrentDictionary<int, CameraInfo> _cameraInfos;
        
        // Словарь статуса захвата: ID -> bool
        private readonly ConcurrentDictionary<int, bool> _captureStatus;
        
        // Токены отмены для операций
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _cancellationTokens;

        // События
        public event EventHandler<FrameReceivedEventArgs> FrameReceived;
        public event EventHandler<CameraStatusEventArgs> CameraStatusChanged;

        /// <summary>
        /// Конструктор сервиса камер
        /// </summary>
        public CameraService()
        {
            _cameras = new ConcurrentDictionary<int, VideoCapture>();
            _cameraInfos = new ConcurrentDictionary<int, CameraInfo>();
            _captureStatus = new ConcurrentDictionary<int, bool>();
            _cancellationTokens = new ConcurrentDictionary<int, CancellationTokenSource>();
        }

        /// <summary>
        /// Обнаружение доступных камер в системе
        /// </summary>
        public async Task<List<CameraInfo>> DetectAvailableCamerasAsync()
        {
            return await Task.Run(() =>
            {
                var availableCameras = new List<CameraInfo>();
                
                // Проверяем первые 10 индексов камер
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        using (var testCapture = new VideoCapture(i))
                        {
                            if (testCapture.IsOpened())
                            {
                                var cameraInfo = new CameraInfo(i)
                                {
                                    IsAvailable = true,
                                    MaxWidth = (int)testCapture.Get(VideoCaptureProperties.FrameWidth),
                                    MaxHeight = (int)testCapture.Get(VideoCaptureProperties.FrameHeight)
                                };
                                
                                availableCameras.Add(cameraInfo);
                                _cameraInfos.TryAdd(i, cameraInfo);
                                
                                testCapture.Release();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Ошибка при проверке камеры {i}: {ex.Message}");
                    }
                }
                
                return availableCameras;
            });
        }

        /// <summary>
        /// Подключение к камере
        /// </summary>
        public async Task<bool> ConnectCameraAsync(int cameraId, CancellationToken cancellationToken = default)
        {
            try
            {
                // Отменяем предыдущую операцию если она есть
                if (_cancellationTokens.TryGetValue(cameraId, out var existingToken))
                {
                    existingToken.Cancel();
                    existingToken.Dispose();
                }

                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _cancellationTokens.AddOrUpdate(cameraId, cts, (key, old) => { old?.Dispose(); return cts; });

                // Устанавливаем таймаут подключения
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                return await Task.Run(() =>
                {
                    try
                    {
                        // Освобождаем предыдущую камеру если есть
                        DisconnectCamera(cameraId);

                        // Задержка для освобождения ресурсов
                        Thread.Sleep(200);
                        cts.Token.ThrowIfCancellationRequested();

                        var capture = new VideoCapture(cameraId);
                        
                        // Проверяем подключение с несколькими попытками
                        for (int i = 0; i < 5 && !cts.Token.IsCancellationRequested; i++)
                        {
                            if (capture.IsOpened())
                                break;

                            Thread.Sleep(200);
                            cts.Token.ThrowIfCancellationRequested();
                        }

                        if (!capture.IsOpened())
                        {
                            capture.Dispose();
                            throw new Exception($"Не удалось открыть камеру {cameraId}");
                        }

                        // Тест чтения кадра
                        using (var testFrame = new Mat())
                        {
                            if (!capture.Read(testFrame) || testFrame.Empty())
                            {
                                capture.Dispose();
                                throw new Exception("Камера не передает данные");
                            }
                        }

                        // Устанавливаем разрешение по умолчанию
                        capture.Set(VideoCaptureProperties.FrameWidth, 640);
                        capture.Set(VideoCaptureProperties.FrameHeight, 480);

                        _cameras.AddOrUpdate(cameraId, capture, (key, old) => { old?.Dispose(); return capture; });

                        // Обновляем информацию о камере
                        if (_cameraInfos.TryGetValue(cameraId, out var cameraInfo))
                        {
                            cameraInfo.IsConnected = true;
                        }

                        OnCameraStatusChanged(cameraId, true, null);
                        return true;
                    }
                    catch (OperationCanceledException)
                    {
                        OnCameraStatusChanged(cameraId, false, "Таймаут подключения к камере");
                        return false;
                    }
                    catch (Exception ex)
                    {
                        OnCameraStatusChanged(cameraId, false, ex.Message);
                        return false;
                    }
                }, cts.Token);
            }
            catch (Exception ex)
            {
                OnCameraStatusChanged(cameraId, false, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Отключение камеры
        /// </summary>
        public void DisconnectCamera(int cameraId)
        {
            try
            {
                // Останавливаем захват
                StopCapture(cameraId);

                // Отменяем операции
                if (_cancellationTokens.TryRemove(cameraId, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                }

                // Освобождаем камеру
                if (_cameras.TryRemove(cameraId, out var capture))
                {
                    capture.Release();
                    capture.Dispose();
                }

                // Обновляем информацию
                if (_cameraInfos.TryGetValue(cameraId, out var cameraInfo))
                {
                    cameraInfo.IsConnected = false;
                }

                OnCameraStatusChanged(cameraId, false, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка отключения камеры {cameraId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Проверка статуса подключения камеры
        /// </summary>
        public bool IsCameraConnected(int cameraId)
        {
            return _cameras.TryGetValue(cameraId, out var capture) && 
                   capture != null && capture.IsOpened();
        }

        /// <summary>
        /// Начать захват кадров
        /// </summary>
        public void StartCapture(int cameraId)
        {
            if (!IsCameraConnected(cameraId))
                return;

            _captureStatus.AddOrUpdate(cameraId, true, (key, old) => true);
        }

        /// <summary>
        /// Остановить захват кадров
        /// </summary>
        public void StopCapture(int cameraId)
        {
            _captureStatus.AddOrUpdate(cameraId, false, (key, old) => false);
        }

        /// <summary>
        /// Получить текущий кадр с камеры
        /// </summary>
        public Mat GetCurrentFrame(int cameraId)
        {
            if (!_cameras.TryGetValue(cameraId, out var capture) || 
                !capture.IsOpened())
                return null;

            try
            {
                var frame = new Mat();
                if (capture.Read(frame) && !frame.Empty())
                {
                    // Генерируем событие получения кадра если захват активен
                    if (_captureStatus.TryGetValue(cameraId, out var isCapturing) && isCapturing)
                    {
                        OnFrameReceived(cameraId, frame.Clone());
                    }
                    
                    return frame;
                }
                else
                {
                    frame.Dispose();
                    return null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка получения кадра с камеры {cameraId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Настройка разрешения камеры
        /// </summary>
        public void SetResolution(int cameraId, int width, int height)
        {
            if (!_cameras.TryGetValue(cameraId, out var capture))
                return;

            try
            {
                capture.Set(VideoCaptureProperties.FrameWidth, width);
                capture.Set(VideoCaptureProperties.FrameHeight, height);

                // Обновляем информацию о камере
                if (_cameraInfos.TryGetValue(cameraId, out var cameraInfo))
                {
                    cameraInfo.CurrentWidth = width;
                    cameraInfo.CurrentHeight = height;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка установки разрешения камеры {cameraId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Генерация события получения кадра
        /// </summary>
        protected virtual void OnFrameReceived(int cameraId, Mat frame)
        {
            FrameReceived?.Invoke(this, new FrameReceivedEventArgs
            {
                CameraId = cameraId,
                Frame = frame,
                Timestamp = DateTime.Now
            });
        }

        /// <summary>
        /// Генерация события изменения статуса камеры
        /// </summary>
        protected virtual void OnCameraStatusChanged(int cameraId, bool isConnected, string errorMessage)
        {
            CameraStatusChanged?.Invoke(this, new CameraStatusEventArgs
            {
                CameraId = cameraId,
                IsConnected = isConnected,
                ErrorMessage = errorMessage
            });
        }

        /// <summary>
        /// Освобождение ресурсов
        /// </summary>
        public void Dispose()
        {
            foreach (var cameraId in _cameras.Keys.ToList())
            {
                DisconnectCamera(cameraId);
            }

            _cameras.Clear();
            _cameraInfos.Clear();
            _captureStatus.Clear();
            _cancellationTokens.Clear();
        }
    }
} 
using StereoCalibration.Interfaces;
using StereoCalibration.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StereoCalibration.Presenters
{
    /// <summary>
    /// Презентер для формы выбора камер
    /// Отделяет бизнес-логику от UI согласно MVP паттерну
    /// </summary>
    public class CameraSelectionPresenter
    {
        private readonly ICameraService _cameraService;
        private readonly Form _view;
        
        // События для уведомления UI
        public event EventHandler<CameraConnectionEventArgs> CameraConnectionChanged;
        public event EventHandler<string> ErrorOccurred;
        public event EventHandler<List<CameraInfo>> CamerasDetected;

        // Выбранные камеры
        public int SelectedCamera1Id { get; private set; } = -1;
        public int SelectedCamera2Id { get; private set; } = -1;
        public bool IsValidSelection => SelectedCamera1Id >= 0 && SelectedCamera2Id >= 0 && 
                                       SelectedCamera1Id != SelectedCamera2Id;

        /// <summary>
        /// Конструктор презентера
        /// </summary>
        /// <param name="cameraService">Сервис для работы с камерами</param>
        /// <param name="view">Представление (форма)</param>
        public CameraSelectionPresenter(ICameraService cameraService, Form view)
        {
            _cameraService = cameraService ?? throw new ArgumentNullException(nameof(cameraService));
            _view = view ?? throw new ArgumentNullException(nameof(view));

            // Подписываемся на события сервиса камер
            _cameraService.CameraStatusChanged += OnCameraStatusChanged;
        }

        /// <summary>
        /// Инициализация презентера - загрузка доступных камер
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                var availableCameras = await _cameraService.DetectAvailableCamerasAsync();
                
                if (availableCameras.Count < 2)
                {
                    OnError("Недостаточно камер для работы. Требуется как минимум 2 камеры.");
                    return;
                }

                // Уведомляем UI о доступных камерах
                OnCamerasDetected(availableCameras);
            }
            catch (Exception ex)
            {
                OnError($"Ошибка инициализации: {ex.Message}");
            }
        }

        /// <summary>
        /// Начать предпросмотр камеры
        /// </summary>
        public async Task StartPreviewAsync(int cameraId)
        {
            try
            {
                // Проверяем, что не выбраны одинаковые камеры
                if (IsConflictingCamera(cameraId))
                {
                    OnError("Пожалуйста, выберите разные камеры для камеры 1 и камеры 2");
                    return;
                }

                var success = await _cameraService.ConnectCameraAsync(cameraId);
                if (success)
                {
                    _cameraService.StartCapture(cameraId);
                }
            }
            catch (Exception ex)
            {
                OnError($"Ошибка подключения камеры {cameraId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Остановить предпросмотр камеры
        /// </summary>
        public void StopPreview(int cameraId)
        {
            try
            {
                _cameraService.StopCapture(cameraId);
                _cameraService.DisconnectCamera(cameraId);
            }
            catch (Exception ex)
            {
                OnError($"Ошибка отключения камеры {cameraId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Установить выбор первой камеры
        /// </summary>
        public void SetCamera1Selection(int cameraId)
        {
            if (SelectedCamera1Id != cameraId)
            {
                SelectedCamera1Id = cameraId;
                OnSelectionChanged();
            }
        }

        /// <summary>
        /// Установить выбор второй камеры
        /// </summary>
        public void SetCamera2Selection(int cameraId)
        {
            if (SelectedCamera2Id != cameraId)
            {
                SelectedCamera2Id = cameraId;
                OnSelectionChanged();
            }
        }

        /// <summary>
        /// Проверка конфликтующих камер
        /// </summary>
        private bool IsConflictingCamera(int cameraId)
        {
            return (SelectedCamera1Id == cameraId && SelectedCamera2Id >= 0) ||
                   (SelectedCamera2Id == cameraId && SelectedCamera1Id >= 0);
        }

        /// <summary>
        /// Обработка события изменения статуса камеры
        /// </summary>
        private void OnCameraStatusChanged(object sender, CameraStatusEventArgs e)
        {
            _view?.BeginInvoke(new Action(() =>
            {
                CameraConnectionChanged?.Invoke(this, new CameraConnectionEventArgs
                {
                    CameraId = e.CameraId,
                    IsConnected = e.IsConnected,
                    ErrorMessage = e.ErrorMessage
                });
            }));
        }

        /// <summary>
        /// Уведомление об ошибке
        /// </summary>
        private void OnError(string message)
        {
            _view?.BeginInvoke(new Action(() =>
            {
                ErrorOccurred?.Invoke(this, message);
            }));
        }

        /// <summary>
        /// Уведомление об обнаружении камер
        /// </summary>
        private void OnCamerasDetected(List<CameraInfo> cameras)
        {
            _view?.BeginInvoke(new Action(() =>
            {
                CamerasDetected?.Invoke(this, cameras);
            }));
        }

        /// <summary>
        /// Уведомление об изменении выбора
        /// </summary>
        private void OnSelectionChanged()
        {
            // Логика уведомления UI может быть расширена
        }

        /// <summary>
        /// Получение текущего кадра с камеры для отображения
        /// </summary>
        public OpenCvSharp.Mat GetCurrentFrame(int cameraId)
        {
            return _cameraService.GetCurrentFrame(cameraId);
        }

        /// <summary>
        /// Завершение работы презентера
        /// </summary>
        public void Dispose()
        {
            _cameraService.CameraStatusChanged -= OnCameraStatusChanged;
            
            // Отключаем все камеры
            if (SelectedCamera1Id >= 0)
                StopPreview(SelectedCamera1Id);
            
            if (SelectedCamera2Id >= 0)
                StopPreview(SelectedCamera2Id);
        }
    }

    /// <summary>
    /// Аргументы события изменения подключения камеры
    /// </summary>
    public class CameraConnectionEventArgs : EventArgs
    {
        public int CameraId { get; set; }
        public bool IsConnected { get; set; }
        public string ErrorMessage { get; set; }
    }
} 
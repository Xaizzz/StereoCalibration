using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace StereoCalibration.Services
{
    /// <summary>
    /// Сервис предпросмотра двух камер через два CameraManager.
    /// 
    /// На текущий момент основной CameraSelectionForm реализует предпросмотр
    /// самостоятельно через VideoCapture, поэтому этот сервис является
    /// вспомогательным/legacy-компонентом и заделом для возможного рефакторинга.
    /// </summary>
    public class CameraPreviewService : IDisposable
    {
        private readonly CameraManager _camera1Manager;
        private readonly CameraManager _camera2Manager;
        private readonly System.Windows.Forms.Timer _timer;
        
        private bool _isPreviewingCamera1 = false;
        private bool _isPreviewingCamera2 = false;
        
        public bool IsPreviewingCamera1 => _isPreviewingCamera1;
        public bool IsPreviewingCamera2 => _isPreviewingCamera2;
        
        /// <summary>
        /// Событие отдаёт готовый Bitmap для отображения. Подписчик должен следить
        /// за жизненным циклом Bitmap и освобождать старые изображения.
        /// </summary>
        public delegate void FrameUpdateHandler(int cameraIndex, Bitmap frame);
        public event FrameUpdateHandler OnFrameUpdate;
        
        public CameraPreviewService(int camera1Index, int camera2Index)
        {
            _camera1Manager = new CameraManager(camera1Index);
            _camera2Manager = new CameraManager(camera2Index);
            
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 100; // 10 FPS
            _timer.Tick += Timer_Tick;
        }
        
        /// <summary>
        /// Подключает первую камеру и запускает общий таймер обновления превью.
        /// </summary>
        public async System.Threading.Tasks.Task StartCamera1PreviewAsync(CancellationToken cancellationToken)
        {
            if (_isPreviewingCamera1) return;
            
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                
                await _camera1Manager.ConnectAsync(cts.Token);
                _isPreviewingCamera1 = true;
                
                if (!_timer.Enabled)
                {
                    _timer.Start();
                }
            }
        }
        
        /// <summary>
        /// Подключает вторую камеру и запускает общий таймер обновления превью.
        /// </summary>
        public async System.Threading.Tasks.Task StartCamera2PreviewAsync(CancellationToken cancellationToken)
        {
            if (_isPreviewingCamera2) return;
            
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                
                await _camera2Manager.ConnectAsync(cts.Token);
                _isPreviewingCamera2 = true;
                
                if (!_timer.Enabled)
                {
                    _timer.Start();
                }
            }
        }
        
        /// <summary>
        /// Остановка предпросмотра камеры 1
        /// </summary>
        public async System.Threading.Tasks.Task StopCamera1PreviewAsync()
        {
            _isPreviewingCamera1 = false;
            await _camera1Manager.DisconnectAsync();
            
            CheckAndStopTimer();
        }
        
        /// <summary>
        /// Остановка предпросмотра камеры 2
        /// </summary>
        public async System.Threading.Tasks.Task StopCamera2PreviewAsync()
        {
            _isPreviewingCamera2 = false;
            await _camera2Manager.DisconnectAsync();
            
            CheckAndStopTimer();
        }
        
        /// <summary>
        /// Каждые 100 мс запрашивает кадры у активных камер. Частота около 10 FPS
        /// выбрана как компромисс между плавностью предпросмотра и нагрузкой.
        /// </summary>
        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_isPreviewingCamera1)
            {
                UpdatePreview(_camera1Manager, 1);
            }
            if (_isPreviewingCamera2)
            {
                UpdatePreview(_camera2Manager, 2);
            }
        }
        
        /// <summary>
        /// Берёт Mat у CameraManager, конвертирует его в Bitmap и передаёт наружу.
        /// Сам Mat освобождается здесь, Bitmap остаётся у подписчика события.
        /// </summary>
        private void UpdatePreview(CameraManager cameraManager, int cameraNumber)
        {
            var frame = cameraManager.GetFrame();
            if (frame != null && !frame.Empty())
            {
                using (frame)
                {
                    var bitmap = BitmapConverter.ToBitmap(frame);
                    OnFrameUpdate?.Invoke(cameraNumber, bitmap);
                }
            }
        }
        
        private void CheckAndStopTimer()
        {
            if (!_isPreviewingCamera1 && !_isPreviewingCamera2 && _timer.Enabled)
            {
                _timer.Stop();
            }
        }
        
        public void Dispose()
        {
            _timer?.Stop();
            _timer?.Dispose();
            
            _camera1Manager?.Dispose();
            _camera2Manager?.Dispose();
        }
    }
}
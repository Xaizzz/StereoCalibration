using OpenCvSharp;
using System;

namespace StereoCalibration.Models
{
    /// <summary>
    /// Модель стерео кадра из двух синхронизированных камер
    /// Инкапсулирует данные одновременного захвата кадров
    /// </summary>
    public class StereoFrame : IDisposable
    {
        /// <summary>
        /// Кадр с левой камеры
        /// </summary>
        public Mat LeftFrame { get; private set; }

        /// <summary>
        /// Кадр с правой камеры  
        /// </summary>
        public Mat RightFrame { get; private set; }

        /// <summary>
        /// Временная метка захвата кадра
        /// </summary>
        public DateTime Timestamp { get; private set; }

        /// <summary>
        /// ID левой камеры
        /// </summary>
        public int LeftCameraId { get; private set; }

        /// <summary>
        /// ID правой камеры
        /// </summary>
        public int RightCameraId { get; private set; }

        /// <summary>
        /// Проверка валидности кадров
        /// </summary>
        public bool IsValid => LeftFrame != null && !LeftFrame.Empty() && 
                              RightFrame != null && !RightFrame.Empty();

        /// <summary>
        /// Конструктор стерео кадра
        /// </summary>
        /// <param name="leftFrame">Кадр левой камеры</param>
        /// <param name="rightFrame">Кадр правой камеры</param>
        /// <param name="leftCameraId">ID левой камеры</param>
        /// <param name="rightCameraId">ID правой камеры</param>
        public StereoFrame(Mat leftFrame, Mat rightFrame, int leftCameraId, int rightCameraId)
        {
            LeftFrame = leftFrame?.Clone();
            RightFrame = rightFrame?.Clone();
            LeftCameraId = leftCameraId;
            RightCameraId = rightCameraId;
            Timestamp = DateTime.Now;
        }

        /// <summary>
        /// Клонирование стерео кадра
        /// </summary>
        /// <returns>Копия текущего кадра</returns>
        public StereoFrame Clone()
        {
            return new StereoFrame(LeftFrame, RightFrame, LeftCameraId, RightCameraId);
        }

        /// <summary>
        /// Освобождение ресурсов
        /// </summary>
        public void Dispose()
        {
            LeftFrame?.Dispose();
            RightFrame?.Dispose();
            LeftFrame = null;
            RightFrame = null;
        }
    }
} 
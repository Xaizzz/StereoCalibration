using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace StereoCalibration
{
    /// <summary>
    /// Загрузочный экран приложения с фоновым изображением `loading.png`,
    /// индикатором прогресса и текстовым статусом.
    /// 
    /// Форма используется только во время старта: пока ищутся камеры, создаётся
    /// контроллер и инициализируется главное окно. Файл `loading.png` должен быть
    /// скопирован в выходную директорию рядом с exe.
    /// </summary>
    public sealed class LoadingForm : Form
    {
        private readonly ProgressBar _progressBar;
        private readonly Label _statusLabel;

        public LoadingForm()
        {
            Text = "Загрузка";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(720, 480);
            BackColor = Color.White;
            ShowInTaskbar = false;
            TopMost = true;

            var imagePath = Path.Combine(AppContext.BaseDirectory, "loading.png");
            if (File.Exists(imagePath))
            {
                using (var image = Image.FromFile(imagePath))
                {
                    BackgroundImage = new Bitmap(image);
                }

                BackgroundImageLayout = ImageLayout.Stretch;
            }

            _progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Style = ProgressBarStyle.Continuous,
                Size = new Size(330, 16),
                Location = new Point(320, 300)
            };

            _statusLabel = new Label
            {
                AutoSize = false,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(70, 70, 70),
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 0),
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(330, 24),
                Location = new Point(320, 320),
                Text = "Загрузка..."
            };

            Controls.Add(_progressBar);
            Controls.Add(_statusLabel);
        }

        /// <summary>
        /// Обновляет прогресс и подпись загрузки.
        /// 
        /// Значение ограничивается диапазоном ProgressBar. `Application.DoEvents`
        /// используется осознанно: тяжёлая инициализация идёт в UI-потоке, поэтому
        /// без обработки очереди сообщений splash screen не успевал бы перерисоваться.
        /// </summary>
        public void SetProgress(int value, string status)
        {
            if (IsDisposed)
                return;

            var boundedValue = Math.Max(_progressBar.Minimum, Math.Min(_progressBar.Maximum, value));
            _progressBar.Value = boundedValue;
            _statusLabel.Text = status;
            Refresh();
            Application.DoEvents();
        }
    }
}

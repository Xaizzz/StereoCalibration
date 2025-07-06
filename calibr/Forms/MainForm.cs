using OpenCvSharp;
using StereoCalibration.Models;
using StereoCalibration.Presenters;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace StereoCalibration.Forms
{
    /// <summary>
    /// Новая главная форма с использованием MVP паттерна
    /// Содержит только UI логику, бизнес-логика вынесена в презентер
    /// </summary>
    public partial class MainForm : Form
    {
        private MainFormPresenter _presenter;
        private int _camera1Id;
        private int _camera2Id;

        // UI элементы
        private Button btnStart;
        private Button btnCapture;
        private Button btnCalibrate;
        private Button btnOpenFolders;
        private PictureBox pictureBoxCamera1;
        private PictureBox pictureBoxCamera2;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel statusLabel;

        /// <summary>
        /// Конструктор по умолчанию
        /// </summary>
        public MainForm()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Конструктор формы
        /// </summary>
        public MainForm(MainFormPresenter presenter, int camera1Id, int camera2Id) : this()
        {
            SetPresenter(presenter, camera1Id, camera2Id);
        }

        /// <summary>
        /// Установка презентера и инициализация
        /// </summary>
        public void SetPresenter(MainFormPresenter presenter, int camera1Id, int camera2Id)
        {
            _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
            _camera1Id = camera1Id;
            _camera2Id = camera2Id;
            
            InitializePresenter();
        }

        /// <summary>
        /// Инициализация UI компонентов
        /// </summary>
        private void InitializeComponent()
        {
            this.SuspendLayout();

            // Настройки формы
            this.Text = "Stereo Calibration - Новая архитектура";
            this.Size = new System.Drawing.Size(1400, 800);
            this.StartPosition = FormStartPosition.CenterScreen;

            // Кнопки управления
            btnStart = new Button
            {
                Text = "Начать",
                Location = new System.Drawing.Point(20, 20),
                Size = new System.Drawing.Size(100, 30),
                Font = new Font("Segoe UI", 9F),
                UseVisualStyleBackColor = true
            };

            btnCapture = new Button
            {
                Text = "Снимок",
                Location = new System.Drawing.Point(130, 20),
                Size = new System.Drawing.Size(100, 30),
                Font = new Font("Segoe UI", 9F),
                UseVisualStyleBackColor = true
            };

            btnCalibrate = new Button
            {
                Text = "Калибровка",
                Location = new System.Drawing.Point(240, 20),
                Size = new System.Drawing.Size(100, 30),
                Font = new Font("Segoe UI", 9F),
                UseVisualStyleBackColor = true
            };

            btnOpenFolders = new Button
            {
                Text = "Открыть папки",
                Location = new System.Drawing.Point(350, 20),
                Size = new System.Drawing.Size(120, 30),
                Font = new Font("Segoe UI", 9F),
                UseVisualStyleBackColor = true
            };

            // PictureBox для отображения кадров с камер
            pictureBoxCamera1 = new PictureBox
            {
                Location = new System.Drawing.Point(20, 70),
                Size = new System.Drawing.Size(640, 480),
                BackColor = Color.Black,
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom
            };

            pictureBoxCamera2 = new PictureBox
            {
                Location = new System.Drawing.Point(680, 70),
                Size = new System.Drawing.Size(640, 480),
                BackColor = Color.Black,
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom
            };

            // Подписи для камер
            var labelCamera1 = new Label
            {
                Text = "Камера 1",
                Location = new System.Drawing.Point(20, 555),
                Size = new System.Drawing.Size(100, 20),
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(64, 64, 64)
            };

            var labelCamera2 = new Label
            {
                Text = "Камера 2",
                Location = new System.Drawing.Point(680, 555),
                Size = new System.Drawing.Size(100, 20),
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(64, 64, 64)
            };

            // Статус бар
            statusStrip = new StatusStrip();
            statusLabel = new ToolStripStatusLabel
            {
                Text = "Готов к работе",
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            statusStrip.Items.Add(statusLabel);

            // Добавляем элементы на форму
            this.Controls.AddRange(new Control[]
            {
                btnStart, btnCapture, btnCalibrate, btnOpenFolders,
                pictureBoxCamera1, pictureBoxCamera2,
                labelCamera1, labelCamera2, statusStrip
            });

            // Привязываем события
            btnStart.Click += BtnStart_Click;
            btnCapture.Click += BtnCapture_Click;
            btnCalibrate.Click += BtnCalibrate_Click;
            btnOpenFolders.Click += BtnOpenFolders_Click;

            this.ResumeLayout(false);
        }

        /// <summary>
        /// Инициализация презентера и подписка на события
        /// </summary>
        private void InitializePresenter()
        {
            _presenter.FrameProcessed += OnFrameProcessed;
            _presenter.ErrorOccurred += OnErrorOccurred;
            _presenter.CalibrationCompleted += OnCalibrationCompleted;
            _presenter.StatusChanged += OnStatusChanged;

            // Инициализируем камеры асинхронно
            BeginInvoke(new Action(async () =>
            {
                try
                {
                    await _presenter.InitializeCamerasAsync(_camera1Id, _camera2Id);
                    await _presenter.LoadCalibrationAsync(); // Загружаем сохраненные результаты если есть
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка инициализации камер: {ex.Message}", "Ошибка", 
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }));
        }

        #region События презентера

        private void OnFrameProcessed(object sender, FrameProcessedEventArgs e)
        {
            try
            {
                if (e.ProcessedFrame == null || e.ProcessedFrame.Empty())
                    return;

                var pictureBox = e.CameraId == _camera1Id ? pictureBoxCamera1 : pictureBoxCamera2;
                var bitmap = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(e.ProcessedFrame);
                
                var oldImage = pictureBox.Image;
                pictureBox.Image = bitmap;
                oldImage?.Dispose();
                
                e.ProcessedFrame.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка отображения кадра: {ex.Message}");
            }
        }

        private void OnErrorOccurred(object sender, string message)
        {
            MessageBox.Show(message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void OnCalibrationCompleted(object sender, CalibrationCompletedEventArgs e)
        {
            var quality = GetQualityDescription(e.Result.Error);
            var message = $"Калибровка завершена!\n\n" +
                         $"Ошибка: {e.Result.Error:F3}\n" +
                         $"Качество: {quality}";
            
            MessageBox.Show(message, "Калибровка завершена", 
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnStatusChanged(object sender, string status)
        {
            statusLabel.Text = status;
        }

        #endregion

        #region События UI

        private void BtnStart_Click(object sender, EventArgs e)
        {
            _presenter.ToggleCapture(_camera1Id, _camera2Id);
            btnStart.Text = btnStart.Text == "Начать" ? "Остановить" : "Начать";
        }

        private async void BtnCapture_Click(object sender, EventArgs e)
        {
            btnCapture.Enabled = false;
            try
            {
                await _presenter.CaptureImagePairAsync(_camera1Id, _camera2Id);
            }
            finally
            {
                btnCapture.Enabled = true;
            }
        }

        private async void BtnCalibrate_Click(object sender, EventArgs e)
        {
            btnCalibrate.Enabled = false;
            try
            {
                await _presenter.PerformCalibrationAsync();
            }
            finally
            {
                btnCalibrate.Enabled = true;
            }
        }

        private void BtnOpenFolders_Click(object sender, EventArgs e)
        {
            _presenter.OpenImageFolders();
        }

        #endregion

        /// <summary>
        /// Получение описания качества калибровки
        /// </summary>
        private string GetQualityDescription(double error)
        {
            if (error < 0.5)
                return "Отличное";
            else if (error < 1.0)
                return "Хорошее";
            else if (error < 2.0)
                return "Удовлетворительное";
            else
                return "Плохое";
        }

        /// <summary>
        /// Освобождение ресурсов
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _presenter?.Dispose();
            
            pictureBoxCamera1?.Image?.Dispose();
            pictureBoxCamera2?.Image?.Dispose();
            
            base.OnFormClosing(e);
        }
    }
} 
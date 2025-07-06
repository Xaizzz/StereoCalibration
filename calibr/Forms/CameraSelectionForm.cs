using StereoCalibration.Models;
using StereoCalibration.Presenters;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StereoCalibration.Forms
{
    /// <summary>
    /// Новая форма выбора камер с использованием MVP паттерна
    /// Содержит только UI логику, бизнес-логика вынесена в презентер
    /// </summary>
    public partial class CameraSelectionForm : Form
    {
        private CameraSelectionPresenter _presenter;
        private System.Windows.Forms.Timer _previewTimer;

        // UI элементы
        private ComboBox comboBoxCamera1;
        private ComboBox comboBoxCamera2;
        private Button btnPreviewCamera1;
        private Button btnPreviewCamera2;
        private Button btnApply;
        private PictureBox pictureBoxCamera1;
        private PictureBox pictureBoxCamera2;
        private Label labelInstruction;

        public int Camera1Index { get; private set; }
        public int Camera2Index { get; private set; }

        /// <summary>
        /// Конструктор формы
        /// </summary>
        public CameraSelectionForm(CameraSelectionPresenter presenter)
        {
            _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
            InitializeComponent();
            InitializePresenter();
            InitializeTimer();
        }

        /// <summary>
        /// Инициализация UI компонентов
        /// </summary>
        private void InitializeComponent()
        {
            this.SuspendLayout();

            // Настройки формы
            this.Text = "Выбор камер для стерео калибровки";
            this.Size = new System.Drawing.Size(800, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;

            // Инструкция
            labelInstruction = new Label
            {
                Text = "Выберите камеры и нажмите 'Предпросмотр' для проверки подключения",
                Location = new System.Drawing.Point(20, 20),
                Size = new System.Drawing.Size(760, 40),
                Font = new Font("Segoe UI", 10F),
                ForeColor = Color.FromArgb(64, 64, 64)
            };

            // ComboBox для первой камеры
            var label1 = new Label
            {
                Text = "Камера 1:",
                Location = new System.Drawing.Point(20, 80),
                Size = new System.Drawing.Size(80, 23),
                Font = new Font("Segoe UI", 9F)
            };

            comboBoxCamera1 = new ComboBox
            {
                Location = new System.Drawing.Point(110, 80),
                Size = new System.Drawing.Size(150, 23),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9F)
            };

            btnPreviewCamera1 = new Button
            {
                Text = "Предпросмотр",
                Location = new System.Drawing.Point(270, 80),
                Size = new System.Drawing.Size(100, 25),
                Font = new Font("Segoe UI", 9F),
                UseVisualStyleBackColor = true
            };

            // ComboBox для второй камеры
            var label2 = new Label
            {
                Text = "Камера 2:",
                Location = new System.Drawing.Point(400, 80),
                Size = new System.Drawing.Size(80, 23),
                Font = new Font("Segoe UI", 9F)
            };

            comboBoxCamera2 = new ComboBox
            {
                Location = new System.Drawing.Point(490, 80),
                Size = new System.Drawing.Size(150, 23),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9F)
            };

            btnPreviewCamera2 = new Button
            {
                Text = "Предпросмотр",
                Location = new System.Drawing.Point(650, 80),
                Size = new System.Drawing.Size(100, 25),
                Font = new Font("Segoe UI", 9F),
                UseVisualStyleBackColor = true
            };

            // PictureBox для предпросмотра камер
            pictureBoxCamera1 = new PictureBox
            {
                Location = new System.Drawing.Point(20, 120),
                Size = new System.Drawing.Size(350, 280),
                BackColor = Color.Black,
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom
            };

            pictureBoxCamera2 = new PictureBox
            {
                Location = new System.Drawing.Point(400, 120),
                Size = new System.Drawing.Size(350, 280),
                BackColor = Color.Black,
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom
            };

            // Кнопка применения
            btnApply = new Button
            {
                Text = "Применить",
                Location = new System.Drawing.Point(350, 420),
                Size = new System.Drawing.Size(100, 35),
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Enabled = false
            };

            // Добавляем элементы на форму
            this.Controls.AddRange(new Control[]
            {
                labelInstruction, label1, comboBoxCamera1, btnPreviewCamera1,
                label2, comboBoxCamera2, btnPreviewCamera2,
                pictureBoxCamera1, pictureBoxCamera2, btnApply
            });

            // Привязываем события
            comboBoxCamera1.SelectedIndexChanged += ComboBoxCamera1_SelectedIndexChanged;
            comboBoxCamera2.SelectedIndexChanged += ComboBoxCamera2_SelectedIndexChanged;
            btnPreviewCamera1.Click += BtnPreviewCamera1_Click;
            btnPreviewCamera2.Click += BtnPreviewCamera2_Click;
            btnApply.Click += BtnApply_Click;

            this.ResumeLayout(false);
        }

        /// <summary>
        /// Инициализация презентера и подписка на события
        /// </summary>
        private void InitializePresenter()
        {
            _presenter.CamerasDetected += OnCamerasDetected;
            _presenter.ErrorOccurred += OnErrorOccurred;
            _presenter.CameraConnectionChanged += OnCameraConnectionChanged;

            // Инициализируем презентер асинхронно
            BeginInvoke(new Action(async () =>
            {
                try
                {
                    await _presenter.InitializeAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ошибка инициализации: {ex.Message}", "Ошибка", 
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    this.Close();
                }
            }));
        }

        /// <summary>
        /// Инициализация таймера для обновления предпросмотра
        /// </summary>
        private void InitializeTimer()
        {
            _previewTimer = new System.Windows.Forms.Timer
            {
                Interval = 100 // 10 FPS
            };
            _previewTimer.Tick += PreviewTimer_Tick;
        }

        #region События презентера

        private void OnCamerasDetected(object sender, List<CameraInfo> cameras)
        {
            comboBoxCamera1.DataSource = cameras.Select(c => c.Name).ToList();
            comboBoxCamera2.DataSource = cameras.Select(c => c.Name).ToList();

            if (cameras.Count >= 2)
            {
                comboBoxCamera1.SelectedIndex = 0;
                comboBoxCamera2.SelectedIndex = 1;
            }
        }

        private void OnErrorOccurred(object sender, string message)
        {
            MessageBox.Show(message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void OnCameraConnectionChanged(object sender, CameraConnectionEventArgs e)
        {
            var button = e.CameraId == _presenter.SelectedCamera1Id ? btnPreviewCamera1 : btnPreviewCamera2;
            button.Text = e.IsConnected ? "Остановить" : "Предпросмотр";
            
            if (e.IsConnected)
            {
                _previewTimer.Start();
            }

            UpdateApplyButton();
        }

        #endregion

        #region События UI

        private void ComboBoxCamera1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboBoxCamera1.SelectedIndex >= 0)
            {
                _presenter.SetCamera1Selection(comboBoxCamera1.SelectedIndex);
                Camera1Index = comboBoxCamera1.SelectedIndex;
            }
        }

        private void ComboBoxCamera2_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboBoxCamera2.SelectedIndex >= 0)
            {
                _presenter.SetCamera2Selection(comboBoxCamera2.SelectedIndex);
                Camera2Index = comboBoxCamera2.SelectedIndex;
            }
        }

        private async void BtnPreviewCamera1_Click(object sender, EventArgs e)
        {
            if (btnPreviewCamera1.Text == "Предпросмотр")
            {
                await _presenter.StartPreviewAsync(_presenter.SelectedCamera1Id);
            }
            else
            {
                _presenter.StopPreview(_presenter.SelectedCamera1Id);
                pictureBoxCamera1.Image = null;
            }
        }

        private async void BtnPreviewCamera2_Click(object sender, EventArgs e)
        {
            if (btnPreviewCamera2.Text == "Предпросмотр")
            {
                await _presenter.StartPreviewAsync(_presenter.SelectedCamera2Id);
            }
            else
            {
                _presenter.StopPreview(_presenter.SelectedCamera2Id);
                pictureBoxCamera2.Image = null;
            }
        }

        private void BtnApply_Click(object sender, EventArgs e)
        {
            if (_presenter.IsValidSelection)
            {
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
        }

        private void PreviewTimer_Tick(object sender, EventArgs e)
        {
            // Обновляем предпросмотр камер
            UpdateCameraPreview(pictureBoxCamera1, _presenter.SelectedCamera1Id);
            UpdateCameraPreview(pictureBoxCamera2, _presenter.SelectedCamera2Id);
        }

        #endregion

        /// <summary>
        /// Обновление предпросмотра камеры
        /// </summary>
        private void UpdateCameraPreview(PictureBox pictureBox, int cameraId)
        {
            if (cameraId < 0) return;

            try
            {
                var frame = _presenter.GetCurrentFrame(cameraId);
                if (frame != null && !frame.Empty())
                {
                    var bitmap = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(frame);
                    
                    var oldImage = pictureBox.Image;
                    pictureBox.Image = bitmap;
                    oldImage?.Dispose();
                    
                    frame.Dispose();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления предпросмотра: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновление состояния кнопки применения
        /// </summary>
        private void UpdateApplyButton()
        {
            btnApply.Enabled = _presenter.IsValidSelection;
        }

        /// <summary>
        /// Освобождение ресурсов
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _previewTimer?.Stop();
            _previewTimer?.Dispose();
            
            _presenter?.Dispose();
            
            pictureBoxCamera1?.Image?.Dispose();
            pictureBoxCamera2?.Image?.Dispose();
            
            base.OnFormClosing(e);
        }
    }
} 
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace StereoCalibration
{
    public partial class CameraSelectionForm : Form
    {
        public int Camera1Index { get; private set; }
        public int Camera2Index { get; private set; }

        private VideoCapture capture1;
        private VideoCapture capture2;
        private System.Windows.Forms.Timer timer;

        public CameraSelectionForm(List<int> availableCameras)
        {
            InitializeComponent();

            // Заполняем ComboBox доступными камерами
            comboBoxCamera1.DataSource = availableCameras.Select(i => $"Camera {i}").ToList();
            comboBoxCamera2.DataSource = availableCameras.Select(i => $"Camera {i}").ToList();

            // Устанавливаем начальные значения
            if (availableCameras.Count >= 2)
            {
                comboBoxCamera1.SelectedIndex = 0;
                comboBoxCamera2.SelectedIndex = 1;
            }

            // Подключаем обработчики событий для ComboBox
            comboBoxCamera1.SelectedIndexChanged += ComboBoxCamera1_SelectedIndexChanged;
            comboBoxCamera2.SelectedIndexChanged += ComboBoxCamera2_SelectedIndexChanged;

            // Инициализируем таймер для обновления предпросмотра
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 100; // 10 FPS
            timer.Tick += Timer_Tick;
            timer.Start();
            Console.WriteLine("Таймер запущен");
        }

        private void ComboBoxCamera1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (capture1 != null)
            {
                capture1.Dispose();
            }
            int selectedIndex = comboBoxCamera1.SelectedIndex;
            if (selectedIndex >= 0)
            {
                capture1 = new VideoCapture(selectedIndex);
                if (!capture1.IsOpened())
                {
                    MessageBox.Show($"Не удалось открыть камеру {selectedIndex}");
                    capture1 = null;
                }
                else
                {
                    Console.WriteLine($"Камера 1 открыта: {selectedIndex}");
                }
            }
        }

        private void ComboBoxCamera2_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (capture2 != null)
            {
                capture2.Dispose();
            }
            int selectedIndex = comboBoxCamera2.SelectedIndex;
            if (selectedIndex >= 0)
            {
                capture2 = new VideoCapture(selectedIndex);
                if (!capture2.IsOpened())
                {
                    MessageBox.Show($"Не удалось открыть камеру {selectedIndex}");
                    capture2 = null;
                }
                else
                {
                    Console.WriteLine($"Камера 2 открыта: {selectedIndex}");
                }
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            UpdatePreview(pictureBoxCamera1, capture1);
            UpdatePreview(pictureBoxCamera2, capture2);
        }

        private void UpdatePreview(PictureBox pictureBox, VideoCapture capture)
        {
            if (capture != null && capture.IsOpened())
            {
                using (Mat frame = new Mat())
                {
                    if (capture.Read(frame) && !frame.Empty())
                    {
                        Console.WriteLine("Кадр прочитан");
                        if (pictureBox.InvokeRequired)
                        {
                            pictureBox.Invoke(new Action(() => pictureBox.Image = BitmapConverter.ToBitmap(frame)));
                        }
                        else
                        {
                            pictureBox.Image = BitmapConverter.ToBitmap(frame);
                        }
                    }
                    else
                    {
                        Console.WriteLine("Не удалось прочитать кадр");
                    }
                }
            }
            else
            {
                Console.WriteLine("Камера не открыта");
            }
        }

        private void btnOK_Click(object sender, EventArgs e)
        {
            Camera1Index = comboBoxCamera1.SelectedIndex;
            Camera2Index = comboBoxCamera2.SelectedIndex;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (capture1 != null)
            {
                capture1.Dispose();
            }
            if (capture2 != null)
            {
                capture2.Dispose();
            }
            timer.Stop();
            timer.Dispose();
        }

        private System.Windows.Forms.ComboBox comboBoxCamera1;
        private System.Windows.Forms.ComboBox comboBoxCamera2;
        private System.Windows.Forms.Button btnOK;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.PictureBox pictureBoxCamera1;
        private System.Windows.Forms.PictureBox pictureBoxCamera2;
        private System.Windows.Forms.Label labelCamera1;
        private System.Windows.Forms.Label labelCamera2;

        private void InitializeComponent()
        {
            comboBoxCamera1 = new ComboBox();
            comboBoxCamera2 = new ComboBox();
            btnOK = new Button();
            label1 = new Label();
            label2 = new Label();
            pictureBoxCamera1 = new PictureBox();
            pictureBoxCamera2 = new PictureBox();
            labelCamera1 = new Label();
            labelCamera2 = new Label();
            ((System.ComponentModel.ISupportInitialize)pictureBoxCamera1).BeginInit();
            ((System.ComponentModel.ISupportInitialize)pictureBoxCamera2).BeginInit();
            SuspendLayout();
            // 
            // comboBoxCamera1
            // 
            comboBoxCamera1.FormattingEnabled = true;
            comboBoxCamera1.Location = new System.Drawing.Point(175, 23);
            comboBoxCamera1.Margin = new Padding(4, 3, 4, 3);
            comboBoxCamera1.Name = "comboBoxCamera1";
            comboBoxCamera1.Size = new System.Drawing.Size(233, 23);
            comboBoxCamera1.TabIndex = 0;
            // 
            // comboBoxCamera2
            // 
            comboBoxCamera2.FormattingEnabled = true;
            comboBoxCamera2.Location = new System.Drawing.Point(175, 69);
            comboBoxCamera2.Margin = new Padding(4, 3, 4, 3);
            comboBoxCamera2.Name = "comboBoxCamera2";
            comboBoxCamera2.Size = new System.Drawing.Size(233, 23);
            comboBoxCamera2.TabIndex = 1;
            // 
            // btnOK
            // 
            btnOK.Location = new System.Drawing.Point(175, 115);
            btnOK.Margin = new Padding(4, 3, 4, 3);
            btnOK.Name = "btnOK";
            btnOK.Size = new System.Drawing.Size(88, 27);
            btnOK.TabIndex = 2;
            btnOK.Text = "OK";
            btnOK.UseVisualStyleBackColor = true;
            btnOK.Click += btnOK_Click;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new System.Drawing.Point(23, 23);
            label1.Margin = new Padding(4, 0, 4, 0);
            label1.Name = "label1";
            label1.Size = new System.Drawing.Size(116, 15);
            label1.TabIndex = 3;
            label1.Text = "Выберите камеру 1:";
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new System.Drawing.Point(23, 69);
            label2.Margin = new Padding(4, 0, 4, 0);
            label2.Name = "label2";
            label2.Size = new System.Drawing.Size(116, 15);
            label2.TabIndex = 4;
            label2.Text = "Выберите камеру 2:";
            // 
            // pictureBoxCamera1
            // 
            pictureBoxCamera1.Location = new System.Drawing.Point(467, 23);
            pictureBoxCamera1.Margin = new Padding(4, 3, 4, 3);
            pictureBoxCamera1.Name = "pictureBoxCamera1";
            pictureBoxCamera1.Size = new System.Drawing.Size(373, 277);
            pictureBoxCamera1.TabIndex = 5;
            pictureBoxCamera1.TabStop = false;
            // 
            // pictureBoxCamera2
            // 
            pictureBoxCamera2.Location = new System.Drawing.Point(467, 323);
            pictureBoxCamera2.Margin = new Padding(4, 3, 4, 3);
            pictureBoxCamera2.Name = "pictureBoxCamera2";
            pictureBoxCamera2.Size = new System.Drawing.Size(373, 277);
            pictureBoxCamera2.TabIndex = 6;
            pictureBoxCamera2.TabStop = false;
            // 
            // labelCamera1
            // 
            labelCamera1.AutoSize = true;
            labelCamera1.Location = new System.Drawing.Point(467, 0);
            labelCamera1.Margin = new Padding(4, 0, 4, 0);
            labelCamera1.Name = "labelCamera1";
            labelCamera1.Size = new System.Drawing.Size(57, 15);
            labelCamera1.TabIndex = 7;
            labelCamera1.Text = "Камера 1";
            // 
            // labelCamera2
            // 
            labelCamera2.AutoSize = true;
            labelCamera2.Location = new System.Drawing.Point(467, 300);
            labelCamera2.Margin = new Padding(4, 0, 4, 0);
            labelCamera2.Name = "labelCamera2";
            labelCamera2.Size = new System.Drawing.Size(57, 15);
            labelCamera2.TabIndex = 8;
            labelCamera2.Text = "Камера 2";
            // 
            // CameraSelectionForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new System.Drawing.Size(926, 635);
            Controls.Add(labelCamera2);
            Controls.Add(labelCamera1);
            Controls.Add(pictureBoxCamera2);
            Controls.Add(pictureBoxCamera1);
            Controls.Add(label2);
            Controls.Add(label1);
            Controls.Add(btnOK);
            Controls.Add(comboBoxCamera2);
            Controls.Add(comboBoxCamera1);
            Margin = new Padding(4, 3, 4, 3);
            Name = "CameraSelectionForm";
            Text = "Выбор камер";
            ((System.ComponentModel.ISupportInitialize)pictureBoxCamera1).EndInit();
            ((System.ComponentModel.ISupportInitialize)pictureBoxCamera2).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }
    }
}
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Linq;
using System.Threading.Tasks;
using System.Drawing;

namespace StereoCalibration
{
    public partial class CameraSelectionForm : Form
    {
        public int Camera1Index { get; private set; }
        public int Camera2Index { get; private set; }

        private VideoCapture capture1;
        private VideoCapture capture2;
        private System.Windows.Forms.Timer timer;

        // Флаги для контроля предпросмотра каждой камеры
        private bool isPreviewingCamera1 = false;
        private bool isPreviewingCamera2 = false;

        // Токены отмены для операций подключения
        private System.Threading.CancellationTokenSource camera1CancelToken;
        private System.Threading.CancellationTokenSource camera2CancelToken;

        // Флаг для последовательного выбора камер
        private bool sequentialMode = true;

        // Метод для безопасного отображения сообщений об ошибках
        private void ShowErrorMessage(string message)
        {
            this.BeginInvoke(new Action(() =>
            {
                MessageBox.Show(this, message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }));
        }

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

            // В последовательном режиме блокируем вторую камеру до подключения первой
            if (sequentialMode)
            {
                comboBoxCamera2.Enabled = false;
                btnPreviewCamera2.Enabled = false;
                label2.ForeColor = System.Drawing.Color.Gray;
                labelCamera2.ForeColor = System.Drawing.Color.Gray;
            }

            // Подключаем обработчики событий для ComboBox
            comboBoxCamera1.SelectedIndexChanged += ComboBoxCamera1_SelectedIndexChanged;
            comboBoxCamera2.SelectedIndexChanged += ComboBoxCamera2_SelectedIndexChanged;

            // Инициализируем таймер для обновления предпросмотра, но НЕ запускаем его автоматически
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 100; // 10 FPS
            timer.Tick += Timer_Tick;
            Console.WriteLine("Таймер инициализирован, но не запущен");
        }

        private void ComboBoxCamera1_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Останавливаем предпросмотр если он был активен
            if (isPreviewingCamera1)
            {
                StopPreviewCamera1();
            }

            if (capture1 != null)
            {
                capture1.Dispose();
                capture1 = null;
            }

            // Очищаем PictureBox
            pictureBoxCamera1.Image = null;
            Console.WriteLine($"Камера 1 изменена на индекс: {comboBoxCamera1.SelectedIndex}");
        }

        private void ComboBoxCamera2_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Останавливаем предпросмотр если он был активен
            if (isPreviewingCamera2)
            {
                StopPreviewCamera2();
            }

            if (capture2 != null)
            {
                capture2.Dispose();
                capture2 = null;
            }

            // Очищаем PictureBox
            pictureBoxCamera2.Image = null;
            Console.WriteLine($"Камера 2 изменена на индекс: {comboBoxCamera2.SelectedIndex}");
        }

        private async void btnPreviewCamera1_Click(object sender, EventArgs e)
        {
            if (!isPreviewingCamera1)
            {
                // Проверяем, что выбраны разные камеры
                if (comboBoxCamera1.SelectedIndex == comboBoxCamera2.SelectedIndex && comboBoxCamera2.SelectedIndex >= 0)
                {
                    ShowErrorMessage("Пожалуйста, выберите разные камеры для камеры 1 и камеры 2");
                    return;
                }

                // Отменяем предыдущую операцию если она была
                if (camera1CancelToken != null)
                {
                    camera1CancelToken.Cancel();
                    camera1CancelToken.Dispose();
                }

                camera1CancelToken = new System.Threading.CancellationTokenSource();
                btnPreviewCamera1.Enabled = false;
                btnPreviewCamera1.Text = "Подключение...";

                try
                {
                    await StartPreviewCamera1Async();
                }
                catch (Exception ex)
                {
                    ShowErrorMessage($"Ошибка подключения камеры 1: {ex.Message}");
                }
                finally
                {
                    btnPreviewCamera1.Enabled = true;
                    if (camera1CancelToken != null)
                    {
                        camera1CancelToken.Dispose();
                        camera1CancelToken = null;
                    }
                }
            }
            else
            {
                // Если идет подключение - отменяем его
                if (camera1CancelToken != null)
                {
                    camera1CancelToken.Cancel();
                    return;
                }

                StopPreviewCamera1();
            }
        }

        private async void btnPreviewCamera2_Click(object sender, EventArgs e)
        {
            if (!isPreviewingCamera2)
            {
                // Проверяем, что выбраны разные камеры
                if (comboBoxCamera1.SelectedIndex == comboBoxCamera2.SelectedIndex && comboBoxCamera1.SelectedIndex >= 0)
                {
                    ShowErrorMessage("Пожалуйста, выберите разные камеры для камеры 1 и камеры 2");
                    return;
                }

                // Отменяем предыдущую операцию если она была
                if (camera2CancelToken != null)
                {
                    camera2CancelToken.Cancel();
                    camera2CancelToken.Dispose();
                }

                camera2CancelToken = new System.Threading.CancellationTokenSource();
                btnPreviewCamera2.Enabled = false;
                btnPreviewCamera2.Text = "Подключение...";

                try
                {
                    await StartPreviewCamera2Async();
                }
                catch (Exception ex)
                {
                    ShowErrorMessage($"Ошибка подключения камеры 2: {ex.Message}");
                }
                finally
                {
                    btnPreviewCamera2.Enabled = true;
                    if (camera2CancelToken != null)
                    {
                        camera2CancelToken.Dispose();
                        camera2CancelToken = null;
                    }
                }
            }
            else
            {
                // Если идет подключение - отменяем его
                if (camera2CancelToken != null)
                {
                    camera2CancelToken.Cancel();
                    return;
                }

                StopPreviewCamera2();
            }
        }

        private async Task StartPreviewCamera1Async()
        {
            int selectedIndex = comboBoxCamera1.SelectedIndex;
            if (selectedIndex >= 0)
            {
                // Освобождаем ресурсы если есть
                if (capture1 != null)
                {
                    capture1.Dispose();
                    capture1 = null;
                }

                // Добавляем задержку для освобождения ресурсов
                await Task.Delay(200);

                // Асинхронное подключение к камере с таймаутом
                camera1CancelToken.CancelAfter(TimeSpan.FromSeconds(10));

                try
                {
                    await Task.Run(() =>
                    {
                        camera1CancelToken.Token.ThrowIfCancellationRequested();

                        capture1 = new VideoCapture(selectedIndex);

                        // Проверяем подключение с несколькими попытками
                        for (int i = 0; i < 5 && !camera1CancelToken.Token.IsCancellationRequested; i++)
                        {
                            if (capture1.IsOpened())
                                break;

                            System.Threading.Thread.Sleep(200);
                            camera1CancelToken.Token.ThrowIfCancellationRequested();
                        }

                        // Проверяем, можем ли мы прочитать кадр
                        using (var testFrame = new Mat())
                        {
                            if (!capture1.Read(testFrame) || testFrame.Empty())
                            {
                                throw new Exception("Камера не передает данные");
                            }
                        }

                    }, camera1CancelToken.Token);

                    if (capture1 == null || !capture1.IsOpened())
                    {
                        throw new Exception($"Не удалось инициализировать камеру {selectedIndex}");
                    }

                    isPreviewingCamera1 = true;
                    btnPreviewCamera1.Text = "Остановить";

                    // В последовательном режиме разблокируем вторую камеру
                    if (sequentialMode)
                    {
                        comboBoxCamera2.Enabled = true;
                        btnPreviewCamera2.Enabled = true;
                        label2.ForeColor = System.Drawing.Color.FromArgb(29, 29, 31);
                        labelCamera2.ForeColor = System.Drawing.Color.FromArgb(29, 29, 31);
                    }

                    // Запускаем таймер если он еще не запущен
                    if (!timer.Enabled)
                    {
                        timer.Start();
                    }

                    Console.WriteLine($"Предпросмотр камеры 1 запущен: {selectedIndex}");
                }
                catch (OperationCanceledException)
                {
                    if (capture1 != null)
                    {
                        capture1.Dispose();
                        capture1 = null;
                    }
                    btnPreviewCamera1.Text = "Предпросмотр";
                    throw new Exception("Таймаут подключения к камере 1 (10 секунд)");
                }
                catch (Exception ex)
                {
                    if (capture1 != null)
                    {
                        capture1.Dispose();
                        capture1 = null;
                    }
                    btnPreviewCamera1.Text = "Предпросмотр";
                    throw new Exception($"Ошибка инициализации камеры 1: {ex.Message}");
                }
            }
        }

        private void StopPreviewCamera1()
        {
            isPreviewingCamera1 = false;
            btnPreviewCamera1.Text = "Предпросмотр";

            // В последовательном режиме блокируем вторую камеру если первая отключается
            if (sequentialMode)
            {
                // Сначала останавливаем вторую камеру если она работает
                if (isPreviewingCamera2)
                {
                    StopPreviewCamera2();
                }

                comboBoxCamera2.Enabled = false;
                btnPreviewCamera2.Enabled = false;
                label2.ForeColor = System.Drawing.Color.Gray;
                labelCamera2.ForeColor = System.Drawing.Color.Gray;
            }

            if (capture1 != null)
            {
                try
                {
                    capture1.Release();
                    capture1.Dispose();
                    capture1 = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка при освобождении камеры 1: {ex.Message}");
                }
            }

            try
            {
                if (pictureBoxCamera1.Image != null)
                {
                    pictureBoxCamera1.Image.Dispose();
                }
                pictureBoxCamera1.Image = null;
            }
            catch { }

            // Останавливаем таймер если обе камеры не используются
            if (!isPreviewingCamera1 && !isPreviewingCamera2 && timer.Enabled)
            {
                timer.Stop();
            }

            Console.WriteLine("Предпросмотр камеры 1 остановлен");
        }

        private async Task StartPreviewCamera2Async()
        {
            int selectedIndex = comboBoxCamera2.SelectedIndex;
            if (selectedIndex >= 0)
            {
                // Освобождаем ресурсы если есть
                if (capture2 != null)
                {
                    capture2.Dispose();
                    capture2 = null;
                }

                // Добавляем задержку для освобождения ресурсов
                await Task.Delay(200);

                // Асинхронное подключение к камере с таймаутом
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    try
                    {
                        await Task.Run(() =>
                        {
                            cts.Token.ThrowIfCancellationRequested();

                            capture2 = new VideoCapture(selectedIndex);

                            // Проверяем подключение с несколькими попытками
                            for (int i = 0; i < 5 && !cts.Token.IsCancellationRequested; i++)
                            {
                                if (capture2.IsOpened())
                                    break;

                                System.Threading.Thread.Sleep(200);
                                cts.Token.ThrowIfCancellationRequested();
                            }

                            // Проверяем, можем ли мы прочитать кадр
                            using (var testFrame = new Mat())
                            {
                                if (!capture2.Read(testFrame) || testFrame.Empty())
                                {
                                    throw new Exception("Камера не передает данные");
                                }
                            }

                        }, cts.Token);

                        if (capture2 == null || !capture2.IsOpened())
                        {
                            throw new Exception($"Не удалось инициализировать камеру {selectedIndex}");
                        }

                        isPreviewingCamera2 = true;
                        btnPreviewCamera2.Text = "Остановить";

                        // Запускаем таймер если он еще не запущен
                        if (!timer.Enabled)
                        {
                            timer.Start();
                        }

                        Console.WriteLine($"Предпросмотр камеры 2 запущен: {selectedIndex}");
                    }
                    catch (OperationCanceledException)
                    {
                        if (capture2 != null)
                        {
                            capture2.Dispose();
                            capture2 = null;
                        }
                        btnPreviewCamera2.Text = "Предпросмотр";
                        throw new Exception("Таймаут подключения к камере 2 (10 секунд)");
                    }
                    catch (Exception ex)
                    {
                        if (capture2 != null)
                        {
                            capture2.Dispose();
                            capture2 = null;
                        }
                        btnPreviewCamera2.Text = "Предпросмотр";
                        throw new Exception($"Ошибка инициализации камеры 2: {ex.Message}");
                    }
                }
            }
        }

        private void StopPreviewCamera2()
        {
            isPreviewingCamera2 = false;
            btnPreviewCamera2.Text = "Предпросмотр";

            if (capture2 != null)
            {
                try
                {
                    capture2.Release();
                    capture2.Dispose();
                    capture2 = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка при освобождении камеры 2: {ex.Message}");
                }
            }

            try
            {
                if (pictureBoxCamera2.Image != null)
                {
                    pictureBoxCamera2.Image.Dispose();
                }
                pictureBoxCamera2.Image = null;
            }
            catch { }

            // Останавливаем таймер если обе камеры не используются
            if (!isPreviewingCamera1 && !isPreviewingCamera2 && timer.Enabled)
            {
                timer.Stop();
            }

            Console.WriteLine("Предпросмотр камеры 2 остановлен");
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            // Обновляем только те камеры, для которых включен предпросмотр
            if (isPreviewingCamera1)
            {
                UpdatePreview(pictureBoxCamera1, capture1);
            }
            if (isPreviewingCamera2)
            {
                UpdatePreview(pictureBoxCamera2, capture2);
            }
        }

        private void UpdatePreview(PictureBox pictureBox, VideoCapture capture)
        {
            if (capture != null && capture.IsOpened())
            {
                using (Mat frame = new Mat())
                {
                    if (capture.Read(frame) && !frame.Empty())
                    {
                        if (pictureBox.InvokeRequired)
                        {
                            pictureBox.Invoke(new Action(() =>
                            {
                                var oldImage = pictureBox.Image;
                                pictureBox.Image = BitmapConverter.ToBitmap(frame);
                                oldImage?.Dispose(); // Освобождаем память от старого изображения
                            }));
                        }
                        else
                        {
                            var oldImage = pictureBox.Image;
                            pictureBox.Image = BitmapConverter.ToBitmap(frame);
                            oldImage?.Dispose(); // Освобождаем память от старого изображения
                        }
                    }
                }
            }
        }

        private void btnApply_Click(object sender, EventArgs e)
        {
            // Проверяем, что обе камеры выбраны
            if (comboBoxCamera1.SelectedIndex < 0)
            {
                ShowErrorMessage("Пожалуйста, выберите камеру 1");
                return;
            }

            if (comboBoxCamera2.SelectedIndex < 0)
            {
                ShowErrorMessage("Пожалуйста, выберите камеру 2");
                return;
            }

            // Проверяем, что выбраны разные камеры
            if (comboBoxCamera1.SelectedIndex == comboBoxCamera2.SelectedIndex)
            {
                ShowErrorMessage("Пожалуйста, выберите разные камеры для камеры 1 и камеры 2");
                return;
            }

            // В последовательном режиме проверяем, что камера 1 подключена
            if (sequentialMode && !isPreviewingCamera1)
            {
                ShowErrorMessage("Пожалуйста, сначала подключитесь к камере 1");
                return;
            }

            // Рекомендуем подключить камеру 2 для проверки
            if (sequentialMode && !isPreviewingCamera2)
            {
                var result = MessageBox.Show("Рекомендуется проверить камеру 2 перед применением. Продолжить?",
                    "Предупреждение", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result == DialogResult.No)
                    return;
            }

            // Останавливаем предпросмотр перед закрытием
            if (isPreviewingCamera1)
            {
                StopPreviewCamera1();
            }
            if (isPreviewingCamera2)
            {
                StopPreviewCamera2();
            }

            // Дополнительная задержка для освобождения ресурсов
            System.Threading.Thread.Sleep(300);

            Camera1Index = comboBoxCamera1.SelectedIndex;
            Camera2Index = comboBoxCamera2.SelectedIndex;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            try
            {
                // Отменяем все активные операции подключения
                if (camera1CancelToken != null)
                {
                    camera1CancelToken.Cancel();
                    camera1CancelToken.Dispose();
                    camera1CancelToken = null;
                }
                if (camera2CancelToken != null)
                {
                    camera2CancelToken.Cancel();
                    camera2CancelToken.Dispose();
                    camera2CancelToken = null;
                }

                // Останавливаем предпросмотр
                if (isPreviewingCamera1)
                {
                    StopPreviewCamera1();
                }
                if (isPreviewingCamera2)
                {
                    StopPreviewCamera2();
                }

                // Принудительно освобождаем ресурсы камер
                if (capture1 != null)
                {
                    try
                    {
                        capture1.Release();
                        capture1.Dispose();
                        capture1 = null;
                    }
                    catch { }
                }
                if (capture2 != null)
                {
                    try
                    {
                        capture2.Release();
                        capture2.Dispose();
                        capture2 = null;
                    }
                    catch { }
                }

                if (timer != null)
                {
                    timer.Stop();
                    timer.Dispose();
                    timer = null;
                }

                // Очищаем изображения
                try
                {
                    if (pictureBoxCamera1.Image != null)
                    {
                        pictureBoxCamera1.Image.Dispose();
                        pictureBoxCamera1.Image = null;
                    }
                    if (pictureBoxCamera2.Image != null)
                    {
                        pictureBoxCamera2.Image.Dispose();
                        pictureBoxCamera2.Image = null;
                    }
                }
                catch { }

                // Принудительная сборка мусора для освобождения ресурсов камер
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                Console.WriteLine("Ресурсы камер полностью освобождены");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при освобождении ресурсов: {ex.Message}");
            }
        }

        private System.Windows.Forms.ComboBox comboBoxCamera1;
        private System.Windows.Forms.ComboBox comboBoxCamera2;
        private System.Windows.Forms.Button btnApply;
        private System.Windows.Forms.Button btnPreviewCamera1;
        private System.Windows.Forms.Button btnPreviewCamera2;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.PictureBox pictureBoxCamera1;
        private System.Windows.Forms.PictureBox pictureBoxCamera2;
        private System.Windows.Forms.Label labelCamera1;
        private System.Windows.Forms.Label labelCamera2;
        private System.Windows.Forms.Label labelInstruction;

        private void InitializeComponent()
        {
            comboBoxCamera1 = new ComboBox();
            comboBoxCamera2 = new ComboBox();
            btnApply = new Button();
            btnPreviewCamera1 = new Button();
            btnPreviewCamera2 = new Button();
            label1 = new Label();
            label2 = new Label();
            pictureBoxCamera1 = new PictureBox();
            pictureBoxCamera2 = new PictureBox();
            labelCamera1 = new Label();
            labelCamera2 = new Label();
            labelInstruction = new Label();
            ((System.ComponentModel.ISupportInitialize)pictureBoxCamera1).BeginInit();
            ((System.ComponentModel.ISupportInitialize)pictureBoxCamera2).BeginInit();
            SuspendLayout();

            // Apple-style design colors
            Color primaryBackground = Color.FromArgb(248, 249, 250);
            Color secondaryBackground = Color.White;
            Color accentColor = Color.FromArgb(0, 122, 255);
            Color textColor = Color.FromArgb(29, 29, 31);
            Color borderColor = Color.FromArgb(209, 213, 219);

            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Font = new Font("Segoe UI", 14F, FontStyle.Regular, GraphicsUnit.Point, 0);
            label1.ForeColor = textColor;
            label1.Location = new System.Drawing.Point(30, 30);
            label1.Name = "label1";
            label1.Size = new System.Drawing.Size(90, 25);
            label1.TabIndex = 5;
            label1.Text = "Камера 1";
            // 
            // comboBoxCamera1
            // 
            comboBoxCamera1.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBoxCamera1.FlatStyle = FlatStyle.Flat;
            comboBoxCamera1.Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, 0);
            comboBoxCamera1.FormattingEnabled = true;
            comboBoxCamera1.Location = new System.Drawing.Point(30, 65);
            comboBoxCamera1.Name = "comboBoxCamera1";
            comboBoxCamera1.Size = new System.Drawing.Size(280, 28);
            comboBoxCamera1.TabIndex = 0;
            // 
            // btnPreviewCamera1
            // 
            btnPreviewCamera1.BackColor = accentColor;
            btnPreviewCamera1.FlatAppearance.BorderSize = 0;
            btnPreviewCamera1.FlatStyle = FlatStyle.Flat;
            btnPreviewCamera1.Font = new Font("Segoe UI", 11F, FontStyle.Bold, GraphicsUnit.Point, 0);
            btnPreviewCamera1.ForeColor = Color.White;
            btnPreviewCamera1.Location = new System.Drawing.Point(330, 64);
            btnPreviewCamera1.Name = "btnPreviewCamera1";
            btnPreviewCamera1.Size = new System.Drawing.Size(120, 30);
            btnPreviewCamera1.TabIndex = 3;
            btnPreviewCamera1.Text = "Предпросмотр";
            btnPreviewCamera1.UseVisualStyleBackColor = false;
            btnPreviewCamera1.Click += btnPreviewCamera1_Click;
            // Add rounded corners
            btnPreviewCamera1.Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, btnPreviewCamera1.Width, btnPreviewCamera1.Height, 8, 8));
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Font = new Font("Segoe UI", 14F, FontStyle.Regular, GraphicsUnit.Point, 0);
            label2.ForeColor = textColor;
            label2.Location = new System.Drawing.Point(30, 120);
            label2.Name = "label2";
            label2.Size = new System.Drawing.Size(90, 25);
            label2.TabIndex = 6;
            label2.Text = "Камера 2";
            // 
            // comboBoxCamera2
            // 
            comboBoxCamera2.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBoxCamera2.FlatStyle = FlatStyle.Flat;
            comboBoxCamera2.Font = new Font("Segoe UI", 12F, FontStyle.Regular, GraphicsUnit.Point, 0);
            comboBoxCamera2.FormattingEnabled = true;
            comboBoxCamera2.Location = new System.Drawing.Point(30, 155);
            comboBoxCamera2.Name = "comboBoxCamera2";
            comboBoxCamera2.Size = new System.Drawing.Size(280, 28);
            comboBoxCamera2.TabIndex = 1;
            // 
            // btnPreviewCamera2
            // 
            btnPreviewCamera2.BackColor = accentColor;
            btnPreviewCamera2.FlatAppearance.BorderSize = 0;
            btnPreviewCamera2.FlatStyle = FlatStyle.Flat;
            btnPreviewCamera2.Font = new Font("Segoe UI", 11F, FontStyle.Bold, GraphicsUnit.Point, 0);
            btnPreviewCamera2.ForeColor = Color.White;
            btnPreviewCamera2.Location = new System.Drawing.Point(330, 154);
            btnPreviewCamera2.Name = "btnPreviewCamera2";
            btnPreviewCamera2.Size = new System.Drawing.Size(120, 30);
            btnPreviewCamera2.TabIndex = 4;
            btnPreviewCamera2.Text = "Предпросмотр";
            btnPreviewCamera2.UseVisualStyleBackColor = false;
            btnPreviewCamera2.Click += btnPreviewCamera2_Click;
            // Add rounded corners
            btnPreviewCamera2.Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, btnPreviewCamera2.Width, btnPreviewCamera2.Height, 8, 8));
            // 
            // btnApply
            // 
            btnApply.BackColor = accentColor;
            btnApply.FlatAppearance.BorderSize = 0;
            btnApply.FlatStyle = FlatStyle.Flat;
            btnApply.Font = new Font("Segoe UI", 13F, FontStyle.Bold, GraphicsUnit.Point, 0);
            btnApply.ForeColor = Color.White;
            btnApply.Location = new System.Drawing.Point(180, 210);
            btnApply.Name = "btnApply";
            btnApply.Size = new System.Drawing.Size(140, 40);
            btnApply.TabIndex = 2;
            btnApply.Text = "Применить";
            btnApply.UseVisualStyleBackColor = false;
            btnApply.Click += btnApply_Click;
            // Add rounded corners
            btnApply.Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, btnApply.Width, btnApply.Height, 10, 10));
            // 
            // labelCamera1
            // 
            labelCamera1.AutoSize = true;
            labelCamera1.Font = new Font("Segoe UI", 16F, FontStyle.Bold, GraphicsUnit.Point, 0);
            labelCamera1.ForeColor = textColor;
            labelCamera1.Location = new System.Drawing.Point(500, 20);
            labelCamera1.Name = "labelCamera1";
            labelCamera1.Size = new System.Drawing.Size(100, 28);
            labelCamera1.TabIndex = 9;
            labelCamera1.Text = "Камера 1";
            // 
            // pictureBoxCamera1
            // 
            pictureBoxCamera1.BackColor = Color.FromArgb(17, 17, 19);
            pictureBoxCamera1.Location = new System.Drawing.Point(500, 55);
            pictureBoxCamera1.Name = "pictureBoxCamera1";
            pictureBoxCamera1.Size = new System.Drawing.Size(400, 225);
            pictureBoxCamera1.SizeMode = PictureBoxSizeMode.Zoom;
            pictureBoxCamera1.TabIndex = 7;
            pictureBoxCamera1.TabStop = false;
            // Add rounded corners
            pictureBoxCamera1.Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, pictureBoxCamera1.Width, pictureBoxCamera1.Height, 12, 12));
            // 
            // labelCamera2
            // 
            labelCamera2.AutoSize = true;
            labelCamera2.Font = new Font("Segoe UI", 16F, FontStyle.Bold, GraphicsUnit.Point, 0);
            labelCamera2.ForeColor = textColor;
            labelCamera2.Location = new System.Drawing.Point(500, 300);
            labelCamera2.Name = "labelCamera2";
            labelCamera2.Size = new System.Drawing.Size(100, 28);
            labelCamera2.TabIndex = 10;
            labelCamera2.Text = "Камера 2";
            // 
            // pictureBoxCamera2
            // 
            pictureBoxCamera2.BackColor = Color.FromArgb(17, 17, 19);
            pictureBoxCamera2.Location = new System.Drawing.Point(500, 335);
            pictureBoxCamera2.Name = "pictureBoxCamera2";
            pictureBoxCamera2.Size = new System.Drawing.Size(400, 225);
            pictureBoxCamera2.SizeMode = PictureBoxSizeMode.Zoom;
            pictureBoxCamera2.TabIndex = 8;
            pictureBoxCamera2.TabStop = false;
            // Add rounded corners
            pictureBoxCamera2.Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, pictureBoxCamera2.Width, pictureBoxCamera2.Height, 12, 12));
            // 
            // labelInstruction
            // 
            labelInstruction.AutoSize = false;
            labelInstruction.Font = new Font("Segoe UI", 10F, FontStyle.Italic, GraphicsUnit.Point, 0);
            labelInstruction.ForeColor = System.Drawing.Color.FromArgb(128, 128, 128);
            labelInstruction.Location = new System.Drawing.Point(30, 270);
            labelInstruction.Name = "labelInstruction";
            labelInstruction.Size = new System.Drawing.Size(420, 60);
            labelInstruction.TabIndex = 11;
            labelInstruction.Text = "Инструкция:\n1. Выберите и подключите камеру 1\n2. После успешного подключения станет доступна камера 2\n3. Проверьте обе камеры и нажмите 'Применить'";
            // 
            // CameraSelectionForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = primaryBackground;
            ClientSize = new System.Drawing.Size(950, 600);
            Controls.Add(labelInstruction);
            Controls.Add(labelCamera2);
            Controls.Add(labelCamera1);
            Controls.Add(pictureBoxCamera2);
            Controls.Add(pictureBoxCamera1);
            Controls.Add(btnApply);
            Controls.Add(btnPreviewCamera2);
            Controls.Add(btnPreviewCamera1);
            Controls.Add(comboBoxCamera2);
            Controls.Add(comboBoxCamera1);
            Controls.Add(label2);
            Controls.Add(label1);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Name = "CameraSelectionForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Настройка камер (последовательный режим)";
            ((System.ComponentModel.ISupportInitialize)pictureBoxCamera1).EndInit();
            ((System.ComponentModel.ISupportInitialize)pictureBoxCamera2).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        // Импорт функции Windows API для создания скругленных углов
        [System.Runtime.InteropServices.DllImport("Gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
        private static extern System.IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);
    }
}
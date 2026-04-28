using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;
using OpenCvSharp.Aruco;
using StereoCalibration.Services;
using StereoCalibration.Controllers;
using StereoCalibration.UI;
using System.Windows.Forms.Integration;

namespace StereoCalibration
{
    /// <summary>
    /// Главное WinForms-окно приложения.
    /// 
    /// Форма отвечает за визуальные элементы: вкладку камер, таблицу 3D-координат,
    /// вкладку 3D-сцены и кнопки управления. Бизнес-логика вынесена в
    /// <see cref="MainFormController"/>; форма только подписывается на его события
    /// и безопасно обновляет UI из основного потока.
    /// </summary>
    public partial class MainForm : Form
    {
        #region Контроллер (новая архитектура)
        /// <summary>Контроллер для управления бизнес-логикой</summary>
        private MainFormController _controller;
        /// <summary>Загрузочный экран, отображаемый во время долгих этапов запуска</summary>
        private readonly LoadingForm? _loadingForm;
        #endregion



        #region Элементы пользовательского интерфейса
        /// <summary>Кнопка запуска/остановки видеопотока</summary>
        private System.Windows.Forms.Button StartButton = new Button();
        /// <summary>Кнопка открытия папок с изображениями</summary>
        private System.Windows.Forms.Button OpenImagesButton = new Button();
        /// <summary>Кнопка захвата пары изображений для калибровки</summary>
        private System.Windows.Forms.Button CapturePairButton = new Button();
        /// <summary>Кнопка запуска процедуры стереокалибровки</summary>
        private System.Windows.Forms.Button StereoCalibrateButton = new Button();
        /// <summary>Кнопка перезапуска приложения</summary>
        private System.Windows.Forms.Button RestartButton = new Button();

        /// <summary>Основные вкладки интерфейса</summary>
        private TabControl _mainTabs = new TabControl();
        private TabPage _camerasTab = new TabPage();
        private TabPage _sceneTab = new TabPage();
        private Panel _scenePreviewPanel = new Panel();
        private Panel _sceneHostPanel = new Panel();
        
        /// <summary>Основное окно отображения первой камеры</summary>
        private System.Windows.Forms.PictureBox pictureBox1 = new PictureBox();
        /// <summary>Основное окно отображения второй камеры</summary>
        private System.Windows.Forms.PictureBox pictureBox2 = new PictureBox();
        
        /// <summary>Подпись для первой камеры</summary>
        private System.Windows.Forms.Label labelCamera1 = new Label();
        /// <summary>Подпись для второй камеры</summary>
        private System.Windows.Forms.Label labelCamera2 = new Label();
        /// <summary>Правая панель с 3D координатами найденных маркеров</summary>
        private Panel _markerInfoPanel = new Panel();
        private Label _markerInfoTitle = new Label();
        private DataGridView _markerInfoGrid = new DataGridView();

        /// <summary>Миниатюры камер на вкладке 3D сцены</summary>
        private System.Windows.Forms.PictureBox scenePreviewBox1 = new PictureBox();
        private System.Windows.Forms.PictureBox scenePreviewBox2 = new PictureBox();
        private System.Windows.Forms.Label scenePreviewLabel1 = new Label();
        private System.Windows.Forms.Label scenePreviewLabel2 = new Label();

        /// <summary>3D сцена для отображения камер и маркеров</summary>
        private ElementHost _scene3DHost;
        private Scene3DUserControl _scene3DControl;
        #endregion

        private const int LayoutMargin = 10;
        private const int LayoutGap = 10;
        private const int TopControlsHeight = 35;
        private const int CameraLabelHeight = 22;
        private const int ScenePreviewHeight = 90;
        private const int MarkerInfoPanelWidth = 300;

        public bool StartupCancelled { get; private set; }



        /// <summary>
        /// Основной конструктор с опциональным загрузочным экраном.
        /// 
        /// Startup выполняется последовательно: создать UI, создать контроллер,
        /// встроить WPF 3D-сцену через ElementHost, найти камеры и показать окно
        /// выбора. Если пользователь отменяет выбор или камер меньше двух,
        /// выставляется <see cref="StartupCancelled"/>.
        /// </summary>
        public MainForm() : this(null)
        {
        }

        public MainForm(LoadingForm? loadingForm)
        {
            _loadingForm = loadingForm;
            UpdateLoadingProgress(20, "Инициализация интерфейса...");

            InitializeComponent();

            // Инициализация нового контроллера
            UpdateLoadingProgress(35, "Подготовка контроллера...");
            _controller = new MainFormController();
            SetupControllerEvents();

            // Инициализация 3D сцены
            UpdateLoadingProgress(50, "Подготовка 3D сцены...");
            Initialize3DScene();

            // Инициализация камер через контроллер
            UpdateLoadingProgress(65, "Поиск доступных камер...");
            InitializeCamerasWithController();
        }

        private void UpdateLoadingProgress(int value, string status)
        {
            _loadingForm?.SetProgress(value, status);
        }

        private void CancelStartup()
        {
            StartupCancelled = true;
            _loadingForm?.Hide();
            Close();
        }

        /// <summary>
        /// Подписывает форму на события контроллера.
        /// 
        /// Контроллер может вызывать события не из UI-потока, поэтому каждое
        /// обновление формы проверяет <see cref="Control.InvokeRequired"/>.
        /// Это защищает WinForms от cross-thread access.
        /// </summary>
        private void SetupControllerEvents()
        {
            _controller.OnFramesUpdated += (bitmap1, bitmap2) =>
            {
                if (InvokeRequired)
                {
                    Invoke(new Action(() => UpdatePictureBoxes(bitmap1, bitmap2)));
                }
                else
                {
                    UpdatePictureBoxes(bitmap1, bitmap2);
                }
            };

            _controller.OnRunningStateChanged += (isRunning) =>
            {
                if (InvokeRequired)
                {
                    Invoke(new Action(() => UpdateStartButtonState(isRunning)));
                }
                else
                {
                    UpdateStartButtonState(isRunning);
                }
            };

            _controller.OnCalibrationCompleted += (message) =>
            {
                if (InvokeRequired)
                {
                    Invoke(new Action(() => ShowCalibrationResult(message)));
                }
                else
                {
                    ShowCalibrationResult(message);
                }
            };

            _controller.OnError += (errorMessage) =>
            {
                if (InvokeRequired)
                {
                    Invoke(new Action(() => MessageBox.Show(errorMessage, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error)));
                }
                else
                {
                    MessageBox.Show(errorMessage, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            _controller.OnInfoMessage += (infoMessage) =>
            {
                if (InvokeRequired)
                {
                    Invoke(new Action(() => MessageBox.Show(infoMessage, "Информация", MessageBoxButtons.OK, MessageBoxIcon.Information)));
                }
                else
                {
                    MessageBox.Show(infoMessage, "Информация", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };

            _controller.OnScene3DUpdated += () =>
            {
                // 3D сцена обновляется автоматически через привязку к сервису
                // Дополнительные действия при необходимости можно добавить здесь
            };

            _controller.OnMarkerPositionsUpdated += (markerPositions) =>
            {
                if (InvokeRequired)
                {
                    Invoke(new Action(() => UpdateMarkerInfoTable(markerPositions)));
                }
                else
                {
                    UpdateMarkerInfoTable(markerPositions);
                }
            };
        }

        /// <summary>
        /// Создаёт WPF-контрол 3D-сцены и размещает его внутри WinForms через ElementHost.
        /// 
        /// Это гибридный UI: основное приложение написано на WinForms, но 3D-сцена
        /// использует WPF/HelixToolkit. Поэтому сцена живёт в отдельном UserControl,
        /// а форма только передаёт ей ссылку на Scene3DService.
        /// </summary>
        private void Initialize3DScene()
        {
            try
            {
                // Создаем WPF UserControl для 3D сцены
                _scene3DControl = new Scene3DUserControl();
                
                // Создаем ElementHost для размещения WPF контрола в WinForms
                _scene3DHost = new ElementHost
                {
                    Dock = DockStyle.Fill
                };
                
                // Привязываем WPF контрол к ElementHost
                _scene3DHost.Child = _scene3DControl;
                
                // Добавляем 3D сцену на отдельную вкладку
                _sceneHostPanel.Controls.Add(_scene3DHost);

                PerformResponsiveLayout();
                
                // Привязываем к контроллеру 3D сцены
                var scene3DController = _controller.GetScene3DController();
                _scene3DControl.BindToService(scene3DController.GetScene3DService());
                
                Debug.WriteLine("3D сцена успешно инициализирована");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка инициализации 3D сцены: {ex.Message}");
                MessageBox.Show($"Ошибка инициализации 3D сцены: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Находит доступные камеры, показывает диалог выбора и инициализирует
        /// выбранную стереопару.
        /// 
        /// После предпросмотра камеры явно освобождаются и делается пауза: это
        /// уменьшает вероятность конфликта, когда устройство ещё занято старым
        /// VideoCapture из формы выбора.
        /// </summary>
        private void InitializeCamerasWithController()
        {
            var availableCameras = _controller.DetectCameras();
            UpdateLoadingProgress(75, "Камеры найдены. Ожидание выбора...");

            if (availableCameras.Count < 2)
            {
                _loadingForm?.Hide();
                MessageBox.Show("Недостаточно камер для работы. Требуется как минимум 2 камеры.");
                CancelStartup();
                return;
            }

            using (var selectionForm = new CameraSelectionForm(availableCameras))
            {
                _loadingForm?.Hide();

                if (selectionForm.ShowDialog() == DialogResult.OK)
                {
                    int cam1Index = availableCameras[selectionForm.Camera1Index];
                    int cam2Index = availableCameras[selectionForm.Camera2Index];

                    _loadingForm?.Show();
                    _loadingForm?.BringToFront();
                    UpdateLoadingProgress(82, "Освобождение камер после предпросмотра...");
                    
                    System.Threading.Thread.Sleep(1000);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    
                    Console.WriteLine($"Инициализация камер: {cam1Index}, {cam2Index}");
                    UpdateLoadingProgress(90, "Инициализация выбранных камер...");
                    
                    bool success = _controller.InitializeCameras(cam1Index, cam2Index);
                    if (!success)
                    {
                        _loadingForm?.Hide();
                        MessageBox.Show($"Не удалось инициализировать камеры с индексами {cam1Index} и {cam2Index}");
                        CancelStartup();
                        return;
                    }

                    UpdateLoadingProgress(96, "Запуск основного окна...");
                }
                else
                {
                    CancelStartup();
                }
            }
        }

        /// <summary>
        /// Обновление изображений в PictureBox
        /// </summary>
        private void UpdatePictureBoxes(Bitmap bitmap1, Bitmap bitmap2)
        {
            try
            {
                SetPictureBoxImage(pictureBox1, bitmap1);
                SetPictureBoxImage(pictureBox2, bitmap2);
                SetPictureBoxImage(scenePreviewBox1, bitmap1);
                SetPictureBoxImage(scenePreviewBox2, bitmap2);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка обновления PictureBox: {ex.Message}");
            }
        }

        private void SetPictureBoxImage(PictureBox pictureBox, Bitmap bitmap)
        {
            pictureBox.Image?.Dispose();
            pictureBox.Image = new Bitmap(bitmap);
        }

        /// <summary>
        /// Обновление состояния кнопки "Начать/Остановить"
        /// </summary>
        private void UpdateStartButtonState(bool isRunning)
        {
            StartButton.Text = isRunning ? "Остановить" : "Начать";
        }

        /// <summary>
        /// Отображение результатов калибровки
        /// </summary>
        private void ShowCalibrationResult(string message)
        {
            MessageBox.Show(message, "Калибровка завершена", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// Обновляет таблицу справа от видеопотоков.
        /// 
        /// В таблице показываются координаты, полученные сразу после триангуляции
        /// в системе камеры 1. Это быстрый справочный вид для основного экрана;
        /// преобразование в визуальную 3D-систему выполняется отдельно в Scene3DService.
        /// </summary>
        private void UpdateMarkerInfoTable(IReadOnlyDictionary<int, (double X, double Y, double Z)> markerPositions)
        {
            _markerInfoGrid.Rows.Clear();

            foreach (var marker in markerPositions.OrderBy(marker => marker.Key))
            {
                var distance = Math.Sqrt(
                    marker.Value.X * marker.Value.X +
                    marker.Value.Y * marker.Value.Y +
                    marker.Value.Z * marker.Value.Z);

                _markerInfoGrid.Rows.Add(
                    marker.Key,
                    distance.ToString("F0"),
                    marker.Value.X.ToString("F0"),
                    marker.Value.Y.ToString("F0"),
                    marker.Value.Z.ToString("F0"));
            }

            _markerInfoTitle.Text = markerPositions.Count == 0
                ? "3D маркеры не найдены"
                : $"3D маркеры: {markerPositions.Count}";
        }



        /// <summary>
        /// Ручная сборка WinForms-интерфейса.
        /// 
        /// Файл не использует Designer: все элементы создаются кодом, чтобы проще
        /// контролировать адаптивную раскладку, вкладки и правую таблицу маркеров.
        /// </summary>
        private void InitializeComponent()
        {
            _mainTabs = new TabControl();
            _camerasTab = new TabPage();
            _sceneTab = new TabPage();
            _scenePreviewPanel = new Panel();
            _sceneHostPanel = new Panel();
            StartButton = new Button();
            OpenImagesButton = new Button();
            CapturePairButton = new Button();
            StereoCalibrateButton = new Button();
            RestartButton = new Button();
            pictureBox1 = new PictureBox();
            pictureBox2 = new PictureBox();
            _markerInfoPanel = new Panel();
            _markerInfoTitle = new Label();
            _markerInfoGrid = new DataGridView();
            scenePreviewBox1 = new PictureBox();
            scenePreviewBox2 = new PictureBox();
            scenePreviewLabel1 = new Label();
            scenePreviewLabel2 = new Label();

            _mainTabs.Dock = DockStyle.Fill;
            _mainTabs.TabPages.Add(_camerasTab);
            _mainTabs.TabPages.Add(_sceneTab);
            _mainTabs.SelectedIndexChanged += MainTabs_SelectedIndexChanged;
            _mainTabs.Resize += MainTabs_Resize;

            _camerasTab.Text = "Камеры";
            _camerasTab.Padding = new Padding(LayoutMargin);
            _camerasTab.BackColor = SystemColors.Control;

            _sceneTab.Text = "3D сцена";
            _sceneTab.Padding = new Padding(LayoutMargin);
            _sceneTab.BackColor = SystemColors.Control;

            _scenePreviewPanel.Dock = DockStyle.Top;
            _scenePreviewPanel.Height = ScenePreviewHeight + 28;
            _scenePreviewPanel.BackColor = SystemColors.Control;

            _sceneHostPanel.Dock = DockStyle.Fill;
            _sceneHostPanel.BackColor = SystemColors.Control;

            // Настройка кнопок
            StartButton.Text = "Начать";
            OpenImagesButton.Text = "Открыть папки";
            CapturePairButton.Text = "Захватить пару";
            StereoCalibrateButton.Text = "Калибровать";
            RestartButton.Text = "Перезапуск";
            
            // Увеличенные размеры кнопок для лучшего вида
            StartButton.Size = new System.Drawing.Size(160, 35);
            OpenImagesButton.Size = new System.Drawing.Size(160, 35);
            CapturePairButton.Size = new System.Drawing.Size(160, 35);
            StereoCalibrateButton.Size = new System.Drawing.Size(160, 35);
            RestartButton.Size = new System.Drawing.Size(160, 35);
            
            // Размещение кнопок в одну линию с отступами
            StartButton.Location = new System.Drawing.Point(LayoutMargin, LayoutMargin);
            OpenImagesButton.Location = new System.Drawing.Point(LayoutMargin + 170, LayoutMargin);
            CapturePairButton.Location = new System.Drawing.Point(LayoutMargin + 340, LayoutMargin);
            StereoCalibrateButton.Location = new System.Drawing.Point(LayoutMargin + 510, LayoutMargin);
            RestartButton.Location = new System.Drawing.Point(LayoutMargin + 680, LayoutMargin);

            this.Controls.Add(_mainTabs);

            _camerasTab.Controls.Add(StartButton);
            _camerasTab.Controls.Add(OpenImagesButton);
            _camerasTab.Controls.Add(CapturePairButton);
            _camerasTab.Controls.Add(StereoCalibrateButton);
            _camerasTab.Controls.Add(RestartButton);
            _camerasTab.Controls.Add(pictureBox1);
            _camerasTab.Controls.Add(pictureBox2);
            _camerasTab.Controls.Add(labelCamera1);
            _camerasTab.Controls.Add(labelCamera2);
            _camerasTab.Controls.Add(_markerInfoPanel);

            StartButton.Click += StartButton_Click;
            CapturePairButton.Click += CapturePairButton_Click;
            StereoCalibrateButton.Click += StereoCalibrateButton_Click;
            OpenImagesButton.Click += OpenImagesButton_Click;
            RestartButton.Click += RestartButton_Click;

            // Настройка формы
            this.Text = "Стереокалибровка";
            this.ClientSize = new System.Drawing.Size(1080, 720);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimumSize = new System.Drawing.Size(900, 650);
            this.Resize += MainForm_Resize;
            this.Shown += MainForm_Shown;
            
            // Добавляем рамки для PictureBox для лучшего визуального восприятия
            pictureBox1.BorderStyle = BorderStyle.FixedSingle;
            pictureBox2.BorderStyle = BorderStyle.FixedSingle;
            _markerInfoPanel.BorderStyle = BorderStyle.FixedSingle;
            _markerInfoPanel.BackColor = Color.White;
            
            // Устанавливаем режим масштабирования изображений
            pictureBox1.SizeMode = PictureBoxSizeMode.Zoom;
            pictureBox2.SizeMode = PictureBoxSizeMode.Zoom;
            scenePreviewBox1.SizeMode = PictureBoxSizeMode.Zoom;
            scenePreviewBox2.SizeMode = PictureBoxSizeMode.Zoom;

            scenePreviewBox1.BorderStyle = BorderStyle.FixedSingle;
            scenePreviewBox2.BorderStyle = BorderStyle.FixedSingle;
            
            // Настройка подписей камер
            labelCamera1.Text = "📹 Камера 1";
            labelCamera1.Font = new Font("Segoe UI", 12, FontStyle.Bold);
            labelCamera1.ForeColor = Color.FromArgb(64, 64, 64);
            labelCamera1.Size = new System.Drawing.Size(200, CameraLabelHeight);
            labelCamera1.TextAlign = ContentAlignment.MiddleLeft;
            
            labelCamera2.Text = "📹 Камера 2";
            labelCamera2.Font = new Font("Segoe UI", 12, FontStyle.Bold);
            labelCamera2.ForeColor = Color.FromArgb(64, 64, 64);
            labelCamera2.Size = new System.Drawing.Size(200, CameraLabelHeight);
            labelCamera2.TextAlign = ContentAlignment.MiddleLeft;

            _markerInfoTitle.Text = "3D маркеры не найдены";
            _markerInfoTitle.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            _markerInfoTitle.ForeColor = Color.FromArgb(64, 64, 64);
            _markerInfoTitle.TextAlign = ContentAlignment.MiddleLeft;

            _markerInfoGrid.AllowUserToAddRows = false;
            _markerInfoGrid.AllowUserToDeleteRows = false;
            _markerInfoGrid.AllowUserToResizeRows = false;
            _markerInfoGrid.ReadOnly = true;
            _markerInfoGrid.RowHeadersVisible = false;
            _markerInfoGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _markerInfoGrid.MultiSelect = false;
            _markerInfoGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _markerInfoGrid.BackgroundColor = Color.White;
            _markerInfoGrid.BorderStyle = BorderStyle.None;
            _markerInfoGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _markerInfoGrid.Columns.Add("MarkerId", "ID");
            _markerInfoGrid.Columns.Add("Distance", "D, мм");
            _markerInfoGrid.Columns.Add("X", "X");
            _markerInfoGrid.Columns.Add("Y", "Y");
            _markerInfoGrid.Columns.Add("Z", "Z");

            _markerInfoPanel.Controls.Add(_markerInfoTitle);
            _markerInfoPanel.Controls.Add(_markerInfoGrid);

            scenePreviewLabel1.Text = "Камера 1";
            scenePreviewLabel1.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            scenePreviewLabel1.ForeColor = Color.FromArgb(64, 64, 64);
            scenePreviewLabel1.TextAlign = ContentAlignment.MiddleLeft;

            scenePreviewLabel2.Text = "Камера 2";
            scenePreviewLabel2.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            scenePreviewLabel2.ForeColor = Color.FromArgb(64, 64, 64);
            scenePreviewLabel2.TextAlign = ContentAlignment.MiddleLeft;

            _scenePreviewPanel.Controls.Add(scenePreviewLabel1);
            _scenePreviewPanel.Controls.Add(scenePreviewLabel2);
            _scenePreviewPanel.Controls.Add(scenePreviewBox1);
            _scenePreviewPanel.Controls.Add(scenePreviewBox2);
            _sceneTab.Controls.Add(_sceneHostPanel);
            _sceneTab.Controls.Add(_scenePreviewPanel);

            PerformResponsiveLayout();
        }

        private void MainForm_Resize(object? sender, EventArgs e)
        {
            PerformResponsiveLayout();
        }

        private void MainForm_Shown(object? sender, EventArgs e)
        {
            BeginInvoke(new Action(PerformResponsiveLayout));
        }

        private void MainTabs_SelectedIndexChanged(object? sender, EventArgs e)
        {
            PerformResponsiveLayout();
        }

        private void MainTabs_Resize(object? sender, EventArgs e)
        {
            PerformResponsiveLayout();
        }

        private void PerformResponsiveLayout()
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
                return;

            LayoutCamerasTab();
            LayoutSceneTab();
        }

        /// <summary>
        /// Адаптивно размещает две камеры и правую таблицу маркеров.
        /// 
        /// Правую таблицу нельзя рисовать поверх изображения, поэтому под неё
        /// резервируется фиксированная область справа, а две камеры занимают
        /// оставшуюся ширину.
        /// </summary>
        private void LayoutCamerasTab()
        {
            if (_camerasTab.ClientSize.Width <= 0 || _camerasTab.ClientSize.Height <= 0)
                return;

            int contentWidth = Math.Max(1, _camerasTab.ClientSize.Width - LayoutMargin * 2);
            int cameraTop = LayoutMargin + TopControlsHeight + LayoutGap;
            int markerPanelWidth = contentWidth >= 960
                ? MarkerInfoPanelWidth
                : Math.Max(220, Math.Min(MarkerInfoPanelWidth, contentWidth / 4));
            int camerasAreaWidth = Math.Max(1, contentWidth - markerPanelWidth - LayoutGap);
            int cameraWidth = Math.Max(1, (camerasAreaWidth - LayoutGap) / 2);

            int availableHeight = _camerasTab.ClientSize.Height - cameraTop - CameraLabelHeight - LayoutMargin - 2;
            int cameraHeightByAspect = (int)(cameraWidth * 0.75);
            int cameraHeight = Math.Max(160, Math.Min(cameraHeightByAspect, availableHeight));

            if (availableHeight < 160)
            {
                cameraHeight = Math.Max(120, _camerasTab.ClientSize.Height / 3);
            }

            pictureBox1.Location = new System.Drawing.Point(LayoutMargin, cameraTop);
            pictureBox1.Size = new System.Drawing.Size(cameraWidth, cameraHeight);

            pictureBox2.Location = new System.Drawing.Point(LayoutMargin + cameraWidth + LayoutGap, cameraTop);
            pictureBox2.Size = new System.Drawing.Size(cameraWidth, cameraHeight);

            int labelTop = cameraTop + cameraHeight + 2;
            labelCamera1.Location = new System.Drawing.Point(pictureBox1.Left, labelTop);
            labelCamera1.Size = new System.Drawing.Size(cameraWidth, CameraLabelHeight);
            labelCamera2.Location = new System.Drawing.Point(pictureBox2.Left, labelTop);
            labelCamera2.Size = new System.Drawing.Size(cameraWidth, CameraLabelHeight);

            int markerPanelLeft = LayoutMargin + camerasAreaWidth + LayoutGap;
            int markerPanelHeight = Math.Max(160, cameraHeight + CameraLabelHeight + 2);
            _markerInfoPanel.Location = new System.Drawing.Point(markerPanelLeft, cameraTop);
            _markerInfoPanel.Size = new System.Drawing.Size(markerPanelWidth, markerPanelHeight);

            _markerInfoTitle.Location = new System.Drawing.Point(8, 8);
            _markerInfoTitle.Size = new System.Drawing.Size(markerPanelWidth - 16, 24);
            _markerInfoGrid.Location = new System.Drawing.Point(8, 38);
            _markerInfoGrid.Size = new System.Drawing.Size(markerPanelWidth - 16, markerPanelHeight - 46);

            StartButton.Location = new System.Drawing.Point(LayoutMargin, LayoutMargin);
            OpenImagesButton.Location = new System.Drawing.Point(LayoutMargin + 170, LayoutMargin);
            CapturePairButton.Location = new System.Drawing.Point(LayoutMargin + 340, LayoutMargin);
            StereoCalibrateButton.Location = new System.Drawing.Point(LayoutMargin + 510, LayoutMargin);
            RestartButton.Location = new System.Drawing.Point(LayoutMargin + 680, LayoutMargin);
        }

        private void LayoutSceneTab()
        {
            if (_scenePreviewPanel.ClientSize.Width <= 0)
                return;

            int previewLabelTop = 4;
            int previewTop = previewLabelTop + 18;
            int previewWidth = Math.Max(120, (int)(ScenePreviewHeight * 4.0 / 3.0));
            int secondPreviewLeft = LayoutMargin + previewWidth + LayoutGap;

            scenePreviewLabel1.Location = new System.Drawing.Point(LayoutMargin, previewLabelTop);
            scenePreviewLabel1.Size = new System.Drawing.Size(previewWidth, 18);
            scenePreviewBox1.Location = new System.Drawing.Point(LayoutMargin, previewTop);
            scenePreviewBox1.Size = new System.Drawing.Size(previewWidth, ScenePreviewHeight);

            scenePreviewLabel2.Location = new System.Drawing.Point(secondPreviewLeft, previewLabelTop);
            scenePreviewLabel2.Size = new System.Drawing.Size(previewWidth, 18);
            scenePreviewBox2.Location = new System.Drawing.Point(secondPreviewLeft, previewTop);
            scenePreviewBox2.Size = new System.Drawing.Size(previewWidth, ScenePreviewHeight);
        }



        private void OpenImagesButton_Click(object? sender, EventArgs e)
        {
            // Открытие папки cam1
            string cam1Path = Path.GetFullPath("cam1\\" + _controller.CurrentFolder);
            if (Directory.Exists(cam1Path))
            {
                Process.Start("explorer.exe", cam1Path);
            }
            else
            {
                MessageBox.Show("Папка с изображениями для камеры 1 не найдена.");
            }

            // Открытие папки cam2
            string cam2Path = Path.GetFullPath("cam2\\" + _controller.CurrentFolder);
            if (Directory.Exists(cam2Path))
            {
                Process.Start("explorer.exe", cam2Path);
            }
            else
            {
                MessageBox.Show("Папка с изображениями для камеры 2 не найдена.");
            }
        }
        
        /// <summary>
        /// Запускает или останавливает поток обработки кадров.
        /// 
        /// Сам контроллер включает захват, а форма добавляет/удаляет обработчик
        /// Application.Idle. Пока приложение свободно, Idle вызывает ProcessFrame
        /// максимально часто для живого видео.
        /// </summary>
        private void StartButton_Click(object? sender, EventArgs e)
        {
            // Используем контроллер для управления захватом
            _controller.ToggleCapture();
            
            // Управляем обработкой кадров
            if (_controller.IsRunning)
            {
                Application.Idle += ProcessFrame;
            }
            else
            {
                Application.Idle -= ProcessFrame;
            }
        }

        private void ProcessFrame(object? sender, EventArgs e)
        {
            // Делегируем всю обработку кадров контроллеру
            // Контроллер сам вызовет событие OnFramesUpdated для обновления UI
            _controller.ProcessFrame();
        }
        
        private void CapturePairButton_Click(object? sender, EventArgs e)
        {
            if (!_controller.IsRunning)
            {
                MessageBox.Show("Сначала начните видеопоток.");
                return;
            }

            // Используем контроллер для захвата пары
            _controller.CapturePair();
        }

        private void StereoCalibrateButton_Click(object? sender, EventArgs e)
        {
            // Используем контроллер для калибровки
            // Результат будет отображен через событие OnCalibrationCompleted
            _controller.StartCalibration();
        }

        /// <summary>
        /// Перезапускает приложение через `dotnet build && dotnet run`.
        /// 
        /// Перед запуском команда ищет директорию проекта вверх от папки exe,
        /// чтобы перезапуск работал как из bin, так и из временной папки сборки.
        /// </summary>
        private void RestartButton_Click(object? sender, EventArgs e)
        {
            try
            {
                var projectDirectory = AppContext.BaseDirectory;
                while (!File.Exists(Path.Combine(projectDirectory, "StereoCalibration.csproj")))
                {
                    var parentDirectory = Directory.GetParent(projectDirectory);
                    if (parentDirectory == null)
                    {
                        throw new DirectoryNotFoundException("Не удалось найти директорию проекта.");
                    }

                    projectDirectory = parentDirectory.FullName;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c \"timeout /t 1 /nobreak > nul && dotnet build && dotnet run\"",
                    WorkingDirectory = projectDirectory,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Normal
                };

                Process.Start(startInfo);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось перезапустить приложение: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Освобождение ресурсов при закрытии формы
        /// </summary>
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try
            {
                // Остановка обработки кадров
                Application.Idle -= ProcessFrame;
                
                // Очистка 3D сцены
                _scene3DControl?.Cleanup();
                
                // Освобождение ресурсов контроллера
                _controller?.Dispose();
                
                // Освобождение изображений
                pictureBox1.Image?.Dispose();
                pictureBox2.Image?.Dispose();
                scenePreviewBox1.Image?.Dispose();
                scenePreviewBox2.Image?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка при закрытии формы: {ex.Message}");
            }
            finally
            {
                base.OnFormClosed(e);
            }
        }
    }


}
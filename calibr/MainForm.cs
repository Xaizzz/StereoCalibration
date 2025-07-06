using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using OpenCvSharp.Internal.Vectors;
using System.Data;
using Newtonsoft.Json;
using OpenCvSharp.Aruco;

namespace StereoCalibration
{
    public partial class MainForm : Form
    {
        private VideoCapture capture1, capture2;
        private Mat frame1, frame2;
        private bool isRunning;
        private List<Mat> pairImagePointsList1, pairImagePointsList2;
        private List<Mat> pairObjectPointsList;
        private OpenCvSharp.Size patternSize = new OpenCvSharp.Size(9, 6); // Размер шахматной доски (10x7)
        private const float squareSize = 8.5f; // Размер квадрата на доске в мм

        private System.Windows.Forms.Button StartButton;
        private System.Windows.Forms.Button OpenImagesButton;
        private System.Windows.Forms.Button CapturePairButton;
        private System.Windows.Forms.Button StereoCalibrateButton;
        private System.Windows.Forms.PictureBox pictureBox1;
        private System.Windows.Forms.PictureBox pictureBox2;

        private System.Windows.Forms.PictureBox pictureBox1a;
        private System.Windows.Forms.PictureBox pictureBox2a;
        string cur_folder = "0204_1a";

        CalibrationResult calibrationResult;
        List<Point3f> ps_3d_all_out = new List<Point3f>();
        private List<int> DetectCameras()
        {
            List<int> availableCameras = new List<int>();
            for (int i = 0; i < 10; i++) // Проверка первых 10 индексов
            {
                using (var cap = new VideoCapture(i))
                {
                    if (cap.IsOpened())
                    {
                        availableCameras.Add(i);
                        cap.Release();
                    }
                }
            }
            return availableCameras;
        }

        private Dictionary dictionary;
        private DetectorParameters detectorParameters;

        public MainForm()
        {
            InitializeComponent();

            // Инициализация ArUco детектора
            dictionary = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict6X6_250);
            detectorParameters = new DetectorParameters
            {
                CornerRefinementMethod = CornerRefineMethod.Subpix // Исправлено SubPix на Subpix
                // При необходимости можно настроить и другие параметры уточнения:
                // CornerRefinementWinSize = 5,
                // CornerRefinementMaxIterations = 30,
                // CornerRefinementMinAccuracy = 0.1
            };
            // Обнаружение доступных камер
            var availableCameras = DetectCameras();
            if (availableCameras.Count < 2)
            {
                MessageBox.Show("Недостаточно камер для работы. Требуется как минимум 2 камеры.");
                this.Close();
                return;
            }

            // Открываем окно для выбора камер
            using (var selectionForm = new CameraSelectionForm(availableCameras))
            {
                if (selectionForm.ShowDialog() == DialogResult.OK)
                {
                    int cam1Index = availableCameras[selectionForm.Camera1Index];
                    int cam2Index = availableCameras[selectionForm.Camera2Index];
                    InitializeCamera(cam1Index, cam2Index); // Инициализация с выбранными индексами
                }
                else
                {
                    MessageBox.Show("Не выбраны камеры. Приложение будет закрыто.");
                    this.Close();
                    return;
                }
            }

            // Инициализация списков и флагов
            pairImagePointsList1 = new List<Mat>();
            pairImagePointsList2 = new List<Mat>();
            pairObjectPointsList = new List<Mat>();
            isRunning = false;
        }

        private void InitializeComponent()
        {
            StartButton = new Button();
            CapturePairButton = new Button();
            StereoCalibrateButton = new Button();
            OpenImagesButton = new Button();


            pictureBox1 = new PictureBox();
            pictureBox2 = new PictureBox();

            pictureBox1a = new PictureBox();
            pictureBox2a = new PictureBox();

            StartButton.Text = "Начать";
            CapturePairButton.Text = "Снимок";
            StereoCalibrateButton.Text = "Калибр";
            OpenImagesButton.Text = "Хранилище";

            StartButton.Location = new System.Drawing.Point(10, 10);
            StartButton.Size = new System.Drawing.Size(100, 30);
            OpenImagesButton.Location = new System.Drawing.Point(340, 10);
            OpenImagesButton.Size = new System.Drawing.Size(150, 30);
            CapturePairButton.Location = new System.Drawing.Point(120, 10);
            CapturePairButton.Size = new System.Drawing.Size(100, 30);
            StereoCalibrateButton.Location = new System.Drawing.Point(230, 10);
            StereoCalibrateButton.Size = new System.Drawing.Size(100, 30);

            pictureBox1.Location = new System.Drawing.Point(10, 50);
            pictureBox1.Size = new System.Drawing.Size(640, 480);
            pictureBox2.Location = new System.Drawing.Point(660, 50);
            pictureBox2.Size = new System.Drawing.Size(640, 480);

            pictureBox1a.Location = new System.Drawing.Point(10, 540);
            pictureBox1a.Size = new System.Drawing.Size(640, 480);
            pictureBox2a.Location = new System.Drawing.Point(660, 540);
            pictureBox2a.Size = new System.Drawing.Size(640, 480);

            Controls.Add(StartButton);
            Controls.Add(CapturePairButton);
            Controls.Add(StereoCalibrateButton);
            Controls.Add(pictureBox1);
            Controls.Add(pictureBox2);
            Controls.Add(OpenImagesButton);

            StartButton.Click += StartButton_Click;
            CapturePairButton.Click += CapturePairButton_Click;
            StereoCalibrateButton.Click += StereoCalibrateButton_Click;
            OpenImagesButton.Click += OpenImagesButton_Click;

            this.ClientSize = new System.Drawing.Size(1310, 1040);
            this.Text = "Stereo Calibration";
        }

        private List<Point3f> GenerateObjectPoints()
        {
            List<Point3f> points = new List<Point3f>();
            for (int y = 0; y < patternSize.Height; y++)
            {
                for (int x = 0; x < patternSize.Width; x++)
                {
                    points.Add(new Point3f(x * squareSize, y * squareSize, 0f));
                }
            }
            return points;
        }

        private void InitializeCamera(int cam1Index, int cam2Index)
        {
            Debug.WriteLine("init start");
            capture1 = new VideoCapture(cam1Index);
            capture2 = new VideoCapture(cam2Index);
            if (!capture1.IsOpened())
            {
                MessageBox.Show($"Не удалось открыть камеру 1 (индекс {cam1Index}).");
                return;
            }
            if (!capture2.IsOpened())
            {
                MessageBox.Show($"Не удалось открыть камеру 2 (индекс {cam2Index}).");
                return;
            }
            Debug.WriteLine("init end");
            capture1.Set(VideoCaptureProperties.FrameWidth, 640);
            capture1.Set(VideoCaptureProperties.FrameHeight, 480);
            capture2.Set(VideoCaptureProperties.FrameWidth, 640);
            capture2.Set(VideoCaptureProperties.FrameHeight, 480);
            frame1 = new Mat();
            frame2 = new Mat();
            Directory.CreateDirectory("cam1\\" + cur_folder + "\\");
            Directory.CreateDirectory("cam2\\" + cur_folder + "\\");
        }

        private void OpenImagesButton_Click(object sender, EventArgs e)
        {
            // Открытие папки cam1
            string cam1Path = Path.GetFullPath("cam1\\" + cur_folder);
            if (Directory.Exists(cam1Path))
            {
                System.Diagnostics.Process.Start("explorer.exe", cam1Path);
            }
            else
            {
                MessageBox.Show("Папка с изображениями для камеры 1 не найдена.");
            }

            // Открытие папки cam2
            string cam2Path = Path.GetFullPath("cam2\\" + cur_folder);
            if (Directory.Exists(cam2Path))
            {
                System.Diagnostics.Process.Start("explorer.exe", cam2Path);
            }
            else
            {
                MessageBox.Show("Папка с изображениями для камеры 2 не найдена.");
            }
        }
        private void StartButton_Click(object sender, EventArgs e)
        {
            if (!isRunning)
            {
                isRunning = true;
                StartButton.Text = "Остановить";
                Application.Idle += ProcessFrame;
            }
            else
            {
                isRunning = false;
                StartButton.Text = "Начать";
                Application.Idle -= ProcessFrame;
            }
        }

        private int distancePrintCount = 0;

        private void ProcessFrame(object sender, EventArgs e)
        {
            try
            {
                // Проверка, открыты ли камеры
                if (!capture1.IsOpened() || !capture2.IsOpened())
                {
                    MessageBox.Show("Одна из камер не открыта! Проверьте подключение.");
                    return;
                }

            // Получение кадров с камер
            bool frame1Captured = capture1.Read(frame1);
            bool frame2Captured = capture2.Read(frame2);

            // Проверка, были ли кадры успешно захвачены и не пусты
            if (frame1Captured && !frame1.Empty() && frame2Captured && !frame2.Empty())
            {
                var fr1 = frame1.Clone();
                var fr2 = frame2.Clone();

                // Обнаружение и отрисовка ArUco маркеров на кадре 1
                Point2f[][] cornersAruco1, rejectedAruco1;
                int[] idsAruco1;
                CvAruco.DetectMarkers(fr1, dictionary, out cornersAruco1, out idsAruco1, detectorParameters, out rejectedAruco1);
                if (idsAruco1.Length > 0)
                {
                    CvAruco.DrawDetectedMarkers(fr1, cornersAruco1, idsAruco1);
                }

                // Обнаружение и отрисовка ArUco маркеров на кадре 2
                Point2f[][] cornersAruco2, rejectedAruco2;
                int[] idsAruco2;
                CvAruco.DetectMarkers(fr2, dictionary, out cornersAruco2, out idsAruco2, detectorParameters, out rejectedAruco2);
                if (idsAruco2.Length > 0)
                {
                    CvAruco.DrawDetectedMarkers(fr2, cornersAruco2, idsAruco2);
                }

                // ═══════════════════════════════════════════════════════════════════════════════════════
                // ТРИАНГУЛЯЦИЯ ArUco МАРКЕРОВ - ОПРЕДЕЛЕНИЕ 3D КООРДИНАТ ИЗ СТЕРЕО ИЗОБРАЖЕНИЙ
                // ═══════════════════════════════════════════════════════════════════════════════════════
                // Триангуляция работает только при наличии калибровочных данных и обнаруженных маркеров
                if (calibrationResult != null && idsAruco1.Length > 0 && idsAruco2.Length > 0)
                {
                    try
                    {
                        Debug.WriteLine($"=== НАЧАЛО ТРИАНГУЛЯЦИИ ===");
                        Debug.WriteLine($"Найдено маркеров: камера 1 - {idsAruco1.Length}, камера 2 - {idsAruco2.Length}");
                    
                        // ═══════════════════════════════════════════════════════════════════════════════════
                        // ЭТАП 1: СОПОСТАВЛЕНИЕ МАРКЕРОВ ПО ID МЕЖДУ ДВУМЯ КАМЕРАМИ
                        // ═══════════════════════════════════════════════════════════════════════════════════
                        // Для триангуляции нужны одинаковые маркеры, видимые обеими камерами
                        Dictionary<int, (Point2f[] left, Point2f[] right)> matchedMarkers = new Dictionary<int, (Point2f[], Point2f[])>();
                        for (int i = 0; i < idsAruco1.Length; i++)
                        {
                            for (int j = 0; j < idsAruco2.Length; j++)
                            {
                                if (idsAruco1[i] == idsAruco2[j])
                                {
                                    // Сохраняем пары: ID маркера -> (углы на левом кадре, углы на правом кадре)
                                    matchedMarkers[idsAruco1[i]] = (cornersAruco1[i], cornersAruco2[j]);
                                    Debug.WriteLine($"Найдено совпадение маркера ID {idsAruco1[i]}");
                                    break;
                                }
                            }
                        }

                        Debug.WriteLine($"Сопоставленных маркеров: {matchedMarkers.Count}");

                        // ═══════════════════════════════════════════════════════════════════════════════════
                        // ЭТАП 2: ПОДГОТОВКА КАЛИБРОВОЧНЫХ МАТРИЦ
                        // ═══════════════════════════════════════════════════════════════════════════════════
                        // Создаем OpenCV матрицы из калибровочных данных для каждой камеры
                        Mat cameraMatrix1Mat = new Mat(3, 3, MatType.CV_64FC1);  // Внутренние параметры камеры 1 (K1)
                        Mat cameraMatrix2Mat = new Mat(3, 3, MatType.CV_64FC1);  // Внутренние параметры камеры 2 (K2)
                        Mat distCoeffs1Mat = new Mat(1, 5, MatType.CV_64FC1);    // Коэффициенты искажения камеры 1
                        Mat distCoeffs2Mat = new Mat(1, 5, MatType.CV_64FC1);    // Коэффициенты искажения камеры 2

                        // Заполняем матрицы внутренних параметров K1 и K2
                        // K = [fx  0  cx]  где fx,fy - фокусные расстояния, cx,cy - центр изображения
                        //     [ 0 fy  cy]
                        //     [ 0  0   1]
                        for (int i = 0; i < 3; i++)
                        {
                            for (int j = 0; j < 3; j++)
                            {
                                cameraMatrix1Mat.Set(i, j, calibrationResult.CameraMatrix1[i, j]);
                                cameraMatrix2Mat.Set(i, j, calibrationResult.CameraMatrix2[i, j]);
                            }
                        }
                        
                        // Заполняем коэффициенты радиальных и тангенциальных искажений
                        // [k1, k2, p1, p2, k3] - стандартная модель искажений OpenCV
                        for (int i = 0; i < 5; i++)
                        {
                            distCoeffs1Mat.Set(0, i, calibrationResult.DistCoeffs1[i]);
                            distCoeffs2Mat.Set(0, i, calibrationResult.DistCoeffs2[i]);
                        }

                        // ═══════════════════════════════════════════════════════════════════════════════════
                        // ЭТАП 3: ПОДГОТОВКА СТЕРЕО ПАРАМЕТРОВ
                        // ═══════════════════════════════════════════════════════════════════════════════════
                        // R и T описывают взаимное расположение камер в стерео системе
                        Mat R_stereo = new Mat(3, 3, MatType.CV_64FC1);  // Матрица поворота между камерами
                        Mat T_stereo = new Mat(3, 1, MatType.CV_64FC1);  // Вектор трансляции между камерами
                        
                        for (int i = 0; i < 3; i++)
                        {
                            for (int j = 0; j < 3; j++)
                            {
                                R_stereo.Set(i, j, calibrationResult.R[i, j]);
                            }
                            T_stereo.Set(i, 0, calibrationResult.T[i]);  // T в миллиметрах
                        }

                        Debug.WriteLine("Матрицы камер и стерео параметры созданы");

                        // ═══════════════════════════════════════════════════════════════════════════════════
                        // ЭТАП 4: ТРИАНГУЛЯЦИЯ КАЖДОГО СОПОСТАВЛЕННОГО МАРКЕРА
                        // ═══════════════════════════════════════════════════════════════════════════════════
                        foreach (var marker in matchedMarkers)
                        {
                            int id = marker.Key;
                            Point2f[] leftCorners = marker.Value.left;   // 4 угла маркера на левом изображении
                            Point2f[] rightCorners = marker.Value.right; // 4 угла маркера на правом изображении

                            Debug.WriteLine($"--- Обработка маркера ID {id} ---");

                            // ═══════════════════════════════════════════════════════════════════════════════
                            // ЭТАП 4.1: ВЫЧИСЛЕНИЕ ЦЕНТРА МАРКЕРА
                            // ═══════════════════════════════════════════════════════════════════════════════
                            // ArUco маркер имеет 4 угла, вычисляем геометрический центр
                            // Это более точно, чем использование одного угла
                            Point2f leftCenter = new Point2f(
                                (leftCorners[0].X + leftCorners[1].X + leftCorners[2].X + leftCorners[3].X) / 4.0f,
                                (leftCorners[0].Y + leftCorners[1].Y + leftCorners[2].Y + leftCorners[3].Y) / 4.0f
                            );
                            Point2f rightCenter = new Point2f(
                                (rightCorners[0].X + rightCorners[1].X + rightCorners[2].X + rightCorners[3].X) / 4.0f,
                                (rightCorners[0].Y + rightCorners[1].Y + rightCorners[2].Y + rightCorners[3].Y) / 4.0f
                            );

                            Debug.WriteLine($"Центры маркера: левый ({leftCenter.X:F4}, {leftCenter.Y:F4}), правый ({rightCenter.X:F4}, {rightCenter.Y:F4})");

                            // ═══════════════════════════════════════════════════════════════════════════════
                            // ЭТАП 4.2: ИСПРАВЛЕНИЕ ИСКАЖЕНИЙ ЛИНЗ
                            // ═══════════════════════════════════════════════════════════════════════════════
                            // Реальные линзы имеют радиальные и тангенциальные искажения
                            // UndistortPoints преобразует искаженные координаты в идеальные (нормализованные)
                            Point2f[] leftCenterArray = new Point2f[] { leftCenter };
                            Point2f[] rightCenterArray = new Point2f[] { rightCenter };
                            
                            Mat leftPointsMat = InputArray.Create(leftCenterArray).GetMat();
                            Mat rightPointsMat = InputArray.Create(rightCenterArray).GetMat();
                            Mat undistortedLeft = new Mat();
                            Mat undistortedRight = new Mat();

                            // UndistortPoints без третьего параметра возвращает нормализованные координаты:
                            // - Исправляет искажения линз
                            // - Переводит в систему координат камеры (метрические координаты)
                            // - Результат: x' = (x - cx)/fx, y' = (y - cy)/fy после исправления искажений
                            Cv2.UndistortPoints(leftPointsMat, undistortedLeft, cameraMatrix1Mat, distCoeffs1Mat);
                            Cv2.UndistortPoints(rightPointsMat, undistortedRight, cameraMatrix2Mat, distCoeffs2Mat);

                            // Извлекаем нормализованные координаты
                            Point2f leftNormalized = undistortedLeft.At<Point2f>(0, 0);
                            Point2f rightNormalized = undistortedRight.At<Point2f>(0, 0);

                            Debug.WriteLine($"Нормализованные координаты: левый ({leftNormalized.X:F6}, {leftNormalized.Y:F6}), правый ({rightNormalized.X:F6}, {rightNormalized.Y:F6})");

                            // ═══════════════════════════════════════════════════════════════════════════════
                            // ЭТАП 4.3: СОЗДАНИЕ ПРОЕКЦИОННЫХ МАТРИЦ ДЛЯ СТЕРЕО СИСТЕМЫ
                            // ═══════════════════════════════════════════════════════════════════════════════
                            // Проекционная матрица P связывает 3D точки мира с 2D точками изображения: x = P*X
                            // Для нормализованных координат используем упрощенные матрицы
                            
                            // P1 = [I|0] - камера 1 в начале системы координат (эталонная)
                            // I - единичная матрица 3x3, 0 - нулевой вектор трансляции
                            Mat P1 = new Mat(3, 4, MatType.CV_64FC1);
                            for (int i = 0; i < 3; i++)
                            {
                                for (int j = 0; j < 3; j++)
                                {
                                    P1.Set(i, j, i == j ? 1.0 : 0.0); // Единичная матрица для нормализованных координат
                                }
                                P1.Set(i, 3, 0.0); // Нулевая трансляция
                            }

                            // P2 = [R|T] - камера 2 относительно камеры 1
                            // R - поворот камеры 2 относительно камеры 1
                            // T - позиция камеры 2 относительно камеры 1 (в мм)
                            Mat P2 = new Mat(3, 4, MatType.CV_64FC1);
                            for (int i = 0; i < 3; i++)
                            {
                                for (int j = 0; j < 3; j++)
                                {
                                    P2.Set(i, j, calibrationResult.R[i, j]);
                                }
                                P2.Set(i, 3, calibrationResult.T[i]); // Трансляция в мм
                            }

                            Debug.WriteLine("Проекционные матрицы для нормализованных координат созданы");

                            // ═══════════════════════════════════════════════════════════════════════════════
                            // ЭТАП 4.4: ТРИАНГУЛЯЦИЯ - ВОССТАНОВЛЕНИЕ 3D КООРДИНАТ
                            // ═══════════════════════════════════════════════════════════════════════════════
                            // TriangulatePoints решает систему линейных уравнений для нахождения 3D точки,
                            // которая проецируется в заданные 2D точки на обоих изображениях
                            Mat points4D = new Mat();
                            Point2d[] leftPoints = new Point2d[] { new Point2d(leftNormalized.X, leftNormalized.Y) };
                            Point2d[] rightPoints = new Point2d[] { new Point2d(rightNormalized.X, rightNormalized.Y) };
                            
                            // Алгоритм DLT (Direct Linear Transform):
                            // Для каждой камеры: x_i = P_i * X, где X - искомая 3D точка
                            // Решается система: A * X = 0 методом SVD
                            Cv2.TriangulatePoints(P1, P2, 
                                InputArray.Create(leftPoints), 
                                InputArray.Create(rightPoints), 
                                points4D);

                            Debug.WriteLine($"Результат триангуляции размер: {points4D.Rows}x{points4D.Cols}");

                            // ═══════════════════════════════════════════════════════════════════════════════
                            // ЭТАП 4.5: ПРЕОБРАЗОВАНИЕ ИЗ ГОМОГЕННЫХ КООРДИНАТ В ДЕКАРТОВЫ
                            // ═══════════════════════════════════════════════════════════════════════════════
                            // Результат триангуляции - гомогенные координаты [X, Y, Z, W]
                            // Декартовы координаты: x = X/W, y = Y/W, z = Z/W
                            double x = points4D.At<double>(0, 0);
                            double y = points4D.At<double>(1, 0);
                            double z = points4D.At<double>(2, 0);
                            double w = points4D.At<double>(3, 0);

                            Debug.WriteLine($"Гомогенные координаты: ({x:F8}, {y:F8}, {z:F8}, {w:F8})");

                            // Проверяем корректность гомогенной координаты W (не должна быть близка к нулю)
                            if (Math.Abs(w) > 1e-10)
                            {
                                // Нормализация: переход к декартовым координатам
                                x /= w;
                                y /= w;
                                z /= w;

                                // ═══════════════════════════════════════════════════════════════════════════
                                // ЭТАП 4.6: ВЫЧИСЛЕНИЕ РАССТОЯНИЯ И ВЫВОД РЕЗУЛЬТАТОВ
                                // ═══════════════════════════════════════════════════════════════════════════
                                // Координаты уже в миллиметрах благодаря калибровке с squareSize в мм
                                Debug.WriteLine($"3D координаты в мм: ({x:F4}, {y:F4}, {z:F4})");

                                // Евклидово расстояние от камеры 1 до маркера
                                double distance = Math.Sqrt(x * x + y * y + z * z);

                                Debug.WriteLine($"Точное расстояние: {distance:F4} мм");

                                // Фильтрация нереалистичных результатов (от 1 см до 10 метров)
                                if (distance > 10 && distance < 10000)
                                {
                                    // Отображение результатов на изображениях
                                    Cv2.PutText(fr1,
                                        $"ID {id}: {distance:F1}mm",
                                        new OpenCvSharp.Point((int)leftCenter.X, (int)leftCenter.Y - 10),
                                        HersheyFonts.HersheySimplex,
                                        0.7,
                                        Scalar.Red,
                                        2);

                                    Cv2.PutText(fr2,
                                        $"ID {id}: {distance:F1}mm",
                                        new OpenCvSharp.Point((int)rightCenter.X, (int)rightCenter.Y - 10),
                                        HersheyFonts.HersheySimplex,
                                        0.7,
                                        Scalar.Red,
                                        2);

                                    // Дополнительная информация о 3D координатах
                                    Cv2.PutText(fr1,
                                        $"X:{x:F1} Y:{y:F1} Z:{z:F1}",
                                        new OpenCvSharp.Point((int)leftCenter.X, (int)leftCenter.Y + 20),
                                        HersheyFonts.HersheySimplex,
                                        0.5,
                                        Scalar.Blue,
                                        1);

                                    Debug.WriteLine($"*** ТОЧНЫЙ РЕЗУЛЬТАТ: Маркер {id} на расстоянии {distance:F4} мм ***");
                                    Debug.WriteLine($"*** Координаты: X={x:F4}мм, Y={y:F4}мм, Z={z:F4}мм ***");
                                }
                                else
                                {
                                    Debug.WriteLine($"ПРЕДУПРЕЖДЕНИЕ: Нереалистичное расстояние {distance:F4} мм для маркера {id}");
                                }
                            }
                            else
                            {
                                Debug.WriteLine($"ОШИБКА: Деление на ноль в гомогенных координатах для маркера {id} (w={w:F10})");
                            }

                            // Освобождение ресурсов для текущего маркера
                            leftPointsMat.Dispose();
                            rightPointsMat.Dispose();
                            undistortedLeft.Dispose();
                            undistortedRight.Dispose();
                        }
                        
                        Debug.WriteLine($"=== КОНЕЦ ТРИАНГУЛЯЦИИ ===");
                        
                        // Освобождение общих ресурсов
                        cameraMatrix1Mat.Dispose();
                        cameraMatrix2Mat.Dispose();
                        distCoeffs1Mat.Dispose();
                        distCoeffs2Mat.Dispose();
                        R_stereo.Dispose();
                        T_stereo.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ОШИБКА В ТРИАНГУЛЯЦИИ: {ex.Message}");
                        Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                    }
                }

                Point2f[] corners1, corners2;
                bool found1 = Cv2.FindChessboardCorners(frame1, patternSize, out corners1, ChessboardFlags.FastCheck);
                bool found2 = Cv2.FindChessboardCorners(frame2, patternSize, out corners2, ChessboardFlags.FastCheck);

                if (found1)
                {
                    using (Mat gray1 = new Mat())
                    {
                        Cv2.CvtColor(frame1, gray1, ColorConversionCodes.BGR2GRAY);
                        Cv2.CornerSubPix(gray1, corners1, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));
                        Cv2.DrawChessboardCorners(fr1, patternSize, corners1, found1);
                    }
                }
                if (found2)
                {
                    using (Mat gray2 = new Mat())
                    {
                        Cv2.CvtColor(frame2, gray2, ColorConversionCodes.BGR2GRAY);
                        Cv2.CornerSubPix(gray2, corners2, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));
                        Cv2.DrawChessboardCorners(fr2, patternSize, corners2, found2);
                    }
                }

                // Обновление pictureBox1
                if (pictureBox1.InvokeRequired)
                {
                    pictureBox1.Invoke(new Action(() => pictureBox1.Image = BitmapConverter.ToBitmap(fr1)));
                }
                else
                {
                    pictureBox1.Image = BitmapConverter.ToBitmap(fr1);
                }

                // Обновление pictureBox2
                if (pictureBox2.InvokeRequired)
                {
                    pictureBox2.Invoke(new Action(() => pictureBox2.Image = BitmapConverter.ToBitmap(fr2)));
                }
                else
                {
                    pictureBox2.Image = BitmapConverter.ToBitmap(fr2);
                }

                if (calibrationResult != null && found1 && found2 && ps_3d_all_out.Count > 0)
                {
                    // Проверяем, что количество точек соответствует количеству углов
                    if (ps_3d_all_out.Count != corners1.Length || ps_3d_all_out.Count != corners2.Length)
                    {
                        Debug.WriteLine($"ПРЕДУПРЕЖДЕНИЕ: Несоответствие количества точек. ps_3d_all_out: {ps_3d_all_out.Count}, corners1: {corners1.Length}, corners2: {corners2.Length}");
                        return;
                    }
                    
                    // Преобразование данных в Mat для SolvePnP
                    Mat objectPointsMat = new Mat(ps_3d_all_out.Count, 1, MatType.CV_32FC3);
                    for (int i = 0; i < ps_3d_all_out.Count; i++)
                    {
                        objectPointsMat.Set(i, 0, ps_3d_all_out[i]);
                    }

                    Mat imagePoints1Mat = new Mat(corners1.Length, 1, MatType.CV_32FC2);
                    for (int i = 0; i < corners1.Length; i++)
                    {
                        imagePoints1Mat.Set(i, 0, corners1[i]);
                    }

                    Mat imagePoints2Mat = new Mat(corners2.Length, 1, MatType.CV_32FC2);
                    for (int i = 0; i < corners2.Length; i++)
                    {
                        imagePoints2Mat.Set(i, 0, corners2[i]);
                    }

                    Mat cameraMatrix1Mat = new Mat(3, 3, MatType.CV_64FC1);
                    for (int i = 0; i < 3; i++)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            cameraMatrix1Mat.Set(i, j, calibrationResult.CameraMatrix1[i, j]);
                        }
                    }

                    Mat cameraMatrix2Mat = new Mat(3, 3, MatType.CV_64FC1);
                    for (int i = 0; i < 3; i++)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            cameraMatrix2Mat.Set(i, j, calibrationResult.CameraMatrix2[i, j]);
                        }
                    }

                    Mat distCoeffs1Mat = new Mat(1, calibrationResult.DistCoeffs1.Length, MatType.CV_64FC1);
                    for (int i = 0; i < calibrationResult.DistCoeffs1.Length; i++)
                    {
                        distCoeffs1Mat.Set(0, i, calibrationResult.DistCoeffs1[i]);
                    }

                    Mat distCoeffs2Mat = new Mat(1, calibrationResult.DistCoeffs2.Length, MatType.CV_64FC1);
                    for (int i = 0; i < calibrationResult.DistCoeffs2.Length; i++)
                    {
                        distCoeffs2Mat.Set(0, i, calibrationResult.DistCoeffs2[i]);
                    }

                    try
                    {
                        // SolvePnP для обеих камер
                        double[] rvec1 = new double[3];
                        double[] tvec1 = new double[3];
                        double[] rvec2 = new double[3];
                        double[] tvec2 = new double[3];

                        Mat rvec1Mat = new Mat(3, 1, MatType.CV_64FC1);
                        Mat tvec1Mat = new Mat(3, 1, MatType.CV_64FC1);
                        Mat rvec2Mat = new Mat(3, 1, MatType.CV_64FC1);
                        Mat tvec2Mat = new Mat(3, 1, MatType.CV_64FC1);

                        Cv2.SolvePnP(objectPointsMat, imagePoints1Mat, cameraMatrix1Mat, distCoeffs1Mat, rvec1Mat, tvec1Mat);
                        Cv2.SolvePnP(objectPointsMat, imagePoints2Mat, cameraMatrix2Mat, distCoeffs2Mat, rvec2Mat, tvec2Mat);

                    // Извлечение данных из Mat в double[]
                    for (int i = 0; i < 3; i++)
                    {
                        rvec1[i] = rvec1Mat.At<double>(i, 0);
                        tvec1[i] = tvec1Mat.At<double>(i, 0);
                        rvec2[i] = rvec2Mat.At<double>(i, 0);
                        tvec2[i] = tvec2Mat.At<double>(i, 0);
                    }

                    // Преобразование rvec в матрицы поворота
                    Mat R1 = new Mat(3, 3, MatType.CV_64FC1);
                    Cv2.Rodrigues(rvec1Mat, R1);
                    Mat R2 = new Mat(3, 3, MatType.CV_64FC1);
                    Cv2.Rodrigues(rvec2Mat, R2);

                    // Вычисление относительного поворота
                    Mat R_rel = R2 * R1.T();
                    

                    // Преобразование tvec1 и tvec2 в Mat для вычислений
                    tvec1Mat = new Mat(3, 1, MatType.CV_64FC1);
                    for (int i = 0; i < 3; i++)
                    {
                        tvec1Mat.Set(i, 0, tvec1[i]);
                    }

                    tvec2Mat = new Mat(3, 1, MatType.CV_64FC1);
                    for (int i = 0; i < 3; i++)
                    {
                        tvec2Mat.Set(i, 0, tvec2[i]);
                    }

                    // Вычисление относительной трансляции
                    Mat t_rel = tvec2Mat - R_rel * tvec1Mat;

                    // Извлечение динамических значений положения камеры 2 относительно камеры 1
                    double dynamic_cam_dx = t_rel.At<double>(0, 0);
                    double dynamic_cam_dy = t_rel.At<double>(1, 0);
                    double dynamic_cam_dz = t_rel.At<double>(2, 0);
                    double dynamic_cam_distance = Math.Sqrt(dynamic_cam_dx * dynamic_cam_dx + dynamic_cam_dy * dynamic_cam_dy + dynamic_cam_dz * dynamic_cam_dz);

                    // Вычисление положения камеры 1 относительно камеры 2
                    Mat R_rel_inv = R_rel.T();
                    Mat t_rel_inv = -R_rel_inv * t_rel;
                    double dynamic_cam1_x = t_rel_inv.At<double>(0, 0);
                    double dynamic_cam1_y = t_rel_inv.At<double>(1, 0);
                    double dynamic_cam1_z = t_rel_inv.At<double>(2, 0);

                    // Вывод динамических данных
                    Debug.WriteLine($"Динамическое положение камеры 2 относительно камеры 1 (мм): X {dynamic_cam_dx:F2}, Y {dynamic_cam_dy:F2}, Z {dynamic_cam_dz:F2}");
                    Debug.WriteLine($"Динамическое положение камеры 1 относительно камеры 2 (мм): X {dynamic_cam1_x:F2}, Y {dynamic_cam1_y:F2}, Z {dynamic_cam1_z:F2}");
                    Debug.WriteLine($"Динамическое расстояние между камерами: {dynamic_cam_distance:F2} мм");

                    // Опционально: сравнение с статическим T для оценки стабильности
                    Mat T_static = new Mat(3, 1, MatType.CV_64FC1);
                    for (int i = 0; i < 3; i++)
                    {
                        T_static.Set(i, 0, calibrationResult.T[i]);
                    }
                    Mat diff = t_rel - T_static;
                    double diff_x = diff.At<double>(0, 0);
                    double diff_y = diff.At<double>(1, 0);
                    double diff_z = diff.At<double>(2, 0);
                    double diff_norm = Math.Sqrt(diff_x * diff_x + diff_y * diff_y + diff_z * diff_z);
                    Debug.WriteLine($"Разница между динамическим и статическим T: X {diff_x:F2}, Y {diff_y:F2}, Z {diff_z:F2}, Общая: {diff_norm:F2} мм");

                    // Преобразование R в Mat (предполагается, что calibrationResult.R - это double[,])
                    Mat R = new Mat(3, 3, MatType.CV_64FC1);
                    for (int i = 0; i < 3; i++)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            R.Set(i, j, calibrationResult.R[i, j]);
                        }
                    }

                    // Преобразование T в Mat (предполагается, что calibrationResult.T - это double[])
                    Mat T = new Mat(3, 1, MatType.CV_64FC1);
                    for (int i = 0; i < 3; i++)
                    {
                        T.Set(i, 0, calibrationResult.T[i]);
                    }

                    // Преобразование tvec2 в систему координат камеры 1: R * tvec2 + T
                    Mat tvec2_transformed = R.T() * (tvec2Mat - T);

                    // Вычисление разницы для проверки калибровки
                    Mat difference = tvec1Mat - tvec2_transformed;
                    double dx = difference.At<double>(0, 0);
                    double dy = difference.At<double>(1, 0);
                    double dz = difference.At<double>(2, 0);
                    double error = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                    // Вывод положения камеры 2 относительно камеры 1 из T
                    double cam_dx = calibrationResult.T[0];
                    double cam_dy = calibrationResult.T[1];
                    double cam_dz = calibrationResult.T[2];
                    double cam_distance = Math.Sqrt(cam_dx * cam_dx + cam_dy * cam_dy + cam_dz * cam_dz);

                    // Вывод положения камеры 1 относительно камеры 2 (в системе камеры 2)
                    double cam1_x = -calibrationResult.T[0];
                    double cam1_y = -calibrationResult.T[1];
                    double cam1_z = -calibrationResult.T[2];

                    Debug.WriteLine($"Положение камеры 2 относительно камеры 1 (мм): X {cam_dx:F2}, Y {cam_dy:F2}, Z {cam_dz:F2}");
                    Debug.WriteLine($"Положение камеры 1 относительно камеры 2 (мм): X {cam1_x:F2}, Y {cam1_y:F2}, Z {cam1_z:F2}");
                    Debug.WriteLine($"Расстояние между камерами: {cam_distance:F2} мм");

                    Debug.WriteLine("Кадр с камеры 1 обновлен: " + frame1.GetHashCode());
                    Debug.WriteLine("Кадр с камеры 2 обновлен: " + frame2.GetHashCode());
                    Debug.WriteLine($"tvec1: X {tvec1[0]:F2}, Y {tvec1[1]:F2}, Z {tvec1[2]:F2}");
                    Debug.WriteLine($"tvec2: X {tvec2[0]:F2}, Y {tvec2[1]:F2}, Z {tvec2[2]:F2}");

                        Debug.WriteLine($"Ошибка калибровки (мм): X {dx:F2}, Y {dy:F2}, Z {dz:F2}, Общая: {error:F2}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ОШИБКА В SOLVEPNP: {ex.Message}");
                    }
                }
            }
            else
            {
                MessageBox.Show("Не удалось прочитать кадры с камер. Проверьте подключение.");
            }

            if (calibrationResult != null && distancePrintCount < 10)
            {
                Debug.WriteLine($"T: {calibrationResult.T[0]} {calibrationResult.T[1]} {calibrationResult.T[2]}");
                double distance = Math.Sqrt(calibrationResult.T[0] * calibrationResult.T[0] +
                                            calibrationResult.T[1] * calibrationResult.T[1] +
                                            calibrationResult.T[2] * calibrationResult.T[2]);
                Debug.WriteLine($"Расстояние между камерами: {distance} mm");
                distancePrintCount++;
            }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"КРИТИЧЕСКАЯ ОШИБКА В PROCESSFRAME: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }
        private void CapturePairButton_Click(object sender, EventArgs e)
        {
            if (!isRunning)
            {
                MessageBox.Show("Сначала начните видеопоток.");
                return;
            }

            Mat snapshot1 = frame1.Clone();
            Mat snapshot2 = frame2.Clone();




            Point2f[] corners1, corners2;
            bool found1 = Cv2.FindChessboardCorners(snapshot1, patternSize, out corners1, ChessboardFlags.FastCheck);
            bool found2 = Cv2.FindChessboardCorners(snapshot2, patternSize, out corners2, ChessboardFlags.FastCheck);

            frame1.SaveImage("cam1\\" + cur_folder + "\\" + pairImagePointsList1.Count + ".png");
            frame2.SaveImage("cam2\\" + cur_folder + "\\" + pairImagePointsList1.Count + ".png");
            if (found1 && found2)
            {
                using (Mat gray1 = new Mat())
                using (Mat gray2 = new Mat())
                {
                    Cv2.CvtColor(snapshot1, gray1, ColorConversionCodes.BGR2GRAY);
                    Cv2.CvtColor(snapshot2, gray2, ColorConversionCodes.BGR2GRAY);

                    Cv2.CornerSubPix(gray1, corners1, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                        new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));
                    Cv2.CornerSubPix(gray2, corners2, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                        new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));

                    // Создание матриц с явным указанием типа и размера
                    Mat imagePoints1 = new Mat(corners1.Length, 1, MatType.CV_32FC2);
                    Mat imagePoints2 = new Mat(corners2.Length, 1, MatType.CV_32FC2);
                    Point3f[] objPoints = GenerateObjectPoints().ToArray();
                    Mat objectPoints = new Mat(objPoints.Length, 1, MatType.CV_32FC3);

                    // Заполнение матриц данными
                    for (int i = 0; i < corners1.Length; i++)
                    {
                        imagePoints1.Set(i, 0, corners1[i]);
                        imagePoints2.Set(i, 0, corners2[i]);
                    }
                    for (int i = 0; i < objPoints.Length; i++)
                    {
                        objectPoints.Set(i, 0, objPoints[i]);
                    }

                    pairImagePointsList1.Add(imagePoints1);
                    pairImagePointsList2.Add(imagePoints2);
                    pairObjectPointsList.Add(objectPoints);


                    MessageBox.Show($"Парный снимок {pairImagePointsList1.Count} сохранен.");
                }
            }
            else
            {
                MessageBox.Show("Шахматная доска не обнаружена на одном из снимков.");
            }
        }

        private double[,] MatToArray(Mat mat)
        {
            if (mat.Rows != 3 || mat.Cols != 3)
                throw new ArgumentException("Mat должен быть размером 3x3");

            double[,] array = new double[3, 3];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    array[i, j] = mat.At<double>(i, j);
                }
            }
            return array;
        }

        private double[] MatToVector(Mat mat)
        {
            if (mat.Rows == 3 && mat.Cols == 1)
            {
                return new double[] { mat.At<double>(0, 0), mat.At<double>(1, 0), mat.At<double>(2, 0) };
            }
            else if (mat.Rows == 1 && mat.Cols == 3)
            {
                return new double[] { mat.At<double>(0, 0), mat.At<double>(0, 1), mat.At<double>(0, 2) };
            }
            else
            {
                throw new ArgumentException("Mat должен быть размером 3x1 или 1x3");
            }
        }

        private void StereoCalibrateButton_Click(object sender, EventArgs e)
        {
            pairImagePointsList1 = new List<Mat>();
            pairImagePointsList2 = new List<Mat>();
            pairObjectPointsList = new List<Mat>();

            var mats1 = new List<Mat>();
            var mats2 = new List<Mat>();

            // Получение списка файлов из директорий cam1 и cam2
            var names1 = Directory.GetFiles("cam1\\" + cur_folder);
            var names2 = Directory.GetFiles("cam2\\" + cur_folder);

            // Проверка наличия достаточного количества файлов
            if (names1.Length < 10 || names2.Length < 10 || names1.Length != names2.Length)
            {
                Debug.WriteLine("В каждой папке должно быть не менее 10 изображений, и их количество должно совпадать!");
                return;
            }

            // Загрузка изображений
            for (int i = 0; i < names1.Length; i++)
            {
                mats1.Add(new Mat(names1[i]));
                mats2.Add(new Mat(names2[i]));
            }

            // Обработка изображений
            for (int j = 0; j < mats1.Count; j++)
            {
                Point2f[] corners1, corners2;
                bool found1 = Cv2.FindChessboardCorners(mats1[j], patternSize, out corners1, ChessboardFlags.FastCheck);
                bool found2 = Cv2.FindChessboardCorners(mats2[j], patternSize, out corners2, ChessboardFlags.FastCheck);

                if (found1 && found2)
                {
                    using (Mat gray1 = new Mat())
                    using (Mat gray2 = new Mat())
                    {
                        Cv2.CvtColor(mats1[j], gray1, ColorConversionCodes.BGR2GRAY);
                        Cv2.CvtColor(mats2[j], gray2, ColorConversionCodes.BGR2GRAY);

                        Cv2.CornerSubPix(gray1, corners1, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));
                        Cv2.CornerSubPix(gray2, corners2, new OpenCvSharp.Size(11, 11), new OpenCvSharp.Size(-1, -1),
                            new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.1));

                        Mat imagePoints1 = new Mat(corners1.Length, 1, MatType.CV_32FC2);
                        Mat imagePoints2 = new Mat(corners2.Length, 1, MatType.CV_32FC2);
                        Point3f[] objPoints = GenerateObjectPoints().ToArray();
                        Mat objectPoints = new Mat(objPoints.Length, 1, MatType.CV_32FC3);

                        for (int i = 0; i < corners1.Length; i++)
                        {
                            imagePoints1.Set(i, 0, corners1[i]);
                            imagePoints2.Set(i, 0, corners2[i]);
                        }
                        for (int i = 0; i < objPoints.Length; i++)
                        {
                            objectPoints.Set(i, 0, objPoints[i]);
                        }

                        pairImagePointsList1.Add(imagePoints1);
                        pairImagePointsList2.Add(imagePoints2);
                        pairObjectPointsList.Add(objectPoints);
                        /*  Cv2.CvtColor(gray1, gray1, ColorConversionCodes.GRAY2RGB);
                          Cv2.CvtColor(gray2, gray2, ColorConversionCodes.GRAY2RGB);
                          Cv2.DrawChessboardCorners(gray1, patternSize, corners1, found1);
                          Cv2.DrawChessboardCorners(gray2, patternSize, corners2, found2);
                          Cv2.ImShow("asfd1", gray1);
                          Cv2.ImShow("asfd2", gray2);
                          Cv2.WaitKey();*/

                    }


                }
            }

            // Проверка количества пар
            if (pairObjectPointsList.Count < 10 || pairImagePointsList1.Count < 10 || pairImagePointsList2.Count < 10 ||
                pairObjectPointsList.Count != pairImagePointsList1.Count || pairObjectPointsList.Count != pairImagePointsList2.Count)
            {
                MessageBox.Show("Нужно не менее 10 пар изображений с обнаруженной шахматной доской, и количество элементов в списках должно совпадать!");
                return;
            }

            var ps_3d_all = new List<List<Point3f>>();
            var ps_2d_all_1 = new List<List<Point2f>>();
            var ps_2d_all_2 = new List<List<Point2f>>();

            for (int i = 0; i < pairObjectPointsList.Count; i++)
            {
                var objMat = pairObjectPointsList[i];
                var imgMat1 = pairImagePointsList1[i];
                var imgMat2 = pairImagePointsList2[i];
                var ps_3d = new List<Point3f>();
                var ps_2d_1 = new List<Point2f>();
                var ps_2d_2 = new List<Point2f>();

                for (int j = 0; j < objMat.Rows; j++)
                {
                    var objPt = objMat.Get<Vec3f>(j, 0);
                    var imgPt1 = imgMat1.Get<Vec2f>(j, 0);
                    var imgPt2 = imgMat2.Get<Vec2f>(j, 0);

                    ps_3d.Add(new Point3f(objPt.Item0, objPt.Item1, objPt.Item2));
                    ps_2d_1.Add(new Point2f(imgPt1.Item0, imgPt1.Item1));
                    ps_2d_2.Add(new Point2f(imgPt2.Item0, imgPt2.Item1));
                }
                ps_3d_all.Add(ps_3d);
                ps_2d_all_1.Add(ps_2d_1);
                ps_2d_all_2.Add(ps_2d_2);
            }
            ps_3d_all_out = ps_3d_all[0];
            // Калибровка
            var cameraMatrix1 = new double[3, 3];
            var distCoeffs1 = new double[5];
            var cameraMatrix2 = new double[3, 3];
            var distCoeffs2 = new double[5];
            Mat R = new Mat();
            Mat T = new Mat();
            Mat E = new Mat();
            Mat F = new Mat();

            var tvecs1 = new Vec3d[ps_3d_all.Count];
            var tvecs2 = new Vec3d[ps_3d_all.Count];
            var rvecs1 = new Vec3d[ps_3d_all.Count];
            var rvecs2 = new Vec3d[ps_3d_all.Count];
            var image_size = mats1[0].Size();
            var err1 = Cv2.CalibrateCamera(ps_3d_all, ps_2d_all_1, image_size, cameraMatrix1, distCoeffs1, out rvecs1, out tvecs1, CalibrationFlags.None, new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 100, 1e-6));
            var err2 = Cv2.CalibrateCamera(ps_3d_all, ps_2d_all_2, image_size, cameraMatrix2, distCoeffs2, out rvecs2, out tvecs2, CalibrationFlags.None, new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 100, 1e-6));
            
            Debug.WriteLine("Ошибки индивидуальной калибровки: " + err1 + " " + err2);
            
            // Проверяем качество индивидуальной калибровки
            if (err1 > 1.0 || err2 > 1.0)
            {
                MessageBox.Show($"Предупреждение: Высокая ошибка индивидуальной калибровки. Камера 1: {err1:F3}, Камера 2: {err2:F3}");
            }
            
            print_double(cameraMatrix1);
            print_double(distCoeffs1);
            print_double(cameraMatrix2);
            print_double(distCoeffs2);
            Debug.WriteLine("errors" + err1 + " " + err2);
            var image_size_opt = mats1[0].Size();
            var rect_roi1 = new Rect();
            var rect_roi2 = new Rect();
            Cv2.GetOptimalNewCameraMatrix(cameraMatrix1, distCoeffs1, image_size, 1, image_size_opt, out rect_roi1);
            Cv2.GetOptimalNewCameraMatrix(cameraMatrix2, distCoeffs2, image_size, 1, image_size_opt, out rect_roi2);
            Debug.WriteLine("rect_roi1" + rect_roi1.Width + " " + rect_roi1.Height);
            Debug.WriteLine("rect_roi2" + rect_roi2.Width + " " + rect_roi2.Height);
            try
            {
                double error = Cv2.StereoCalibrate(
                    ps_3d_all,
                    ps_2d_all_1,
                    ps_2d_all_2,
                    cameraMatrix1, 
                    distCoeffs1, 
                    cameraMatrix2, 
                    distCoeffs2,
                    image_size,
                     R, 
                     T,
                      E, 
                      F,
                    CalibrationFlags.FixIntrinsic
                );

                // Преобразование Mat в массивы
                var result = new CalibrationResult
                {
                    CameraMatrix1 = cameraMatrix1,
                    DistCoeffs1 = distCoeffs1,
                    CameraMatrix2 = cameraMatrix2,
                    DistCoeffs2 = distCoeffs2,
                    R = MatToArray(R),
                    T = MatToVector(T),
                    E = MatToArray(E),
                    F = MatToArray(F),
                    Error = error
                };
                calibrationResult = result;
                // Сериализация в JSON и сохранение в файл
                string json = JsonConvert.SerializeObject(result, Formatting.Indented);
                File.WriteAllText("calibration_result.json", json);

                // Дополнительная диагностика
                Debug.WriteLine($"Стереокалибровка завершена. Ошибка: {error:F6}");
                Debug.WriteLine($"Расстояние между камерами: {Math.Sqrt(result.T[0]*result.T[1] + result.T[2]*result.T[2]):F2} мм");
                
                // Проверка качества результатов
                string qualityMessage = "";
                if (error < 0.5)
                    qualityMessage = " (Отличное качество)";
                else if (error < 1.0)
                    qualityMessage = " (Хорошее качество)";
                else if (error < 2.0)
                    qualityMessage = " (Удовлетворительное качество)";
                else
                    qualityMessage = " (Плохое качество - рекомендуется перекалибровка)";

                MessageBox.Show($"Калибровка успешна! Ошибка: {error:F3}{qualityMessage}\nРезультаты сохранены в calibration_result.json");
            }
            catch (OpenCvSharp.OpenCVException ex)
            {
                MessageBox.Show($"Ошибка стереокалибровки: {ex.Message}");
            }
        }

        void print_mat(Mat mat)
        {
            Console.WriteLine(mat.Rows + " " + mat.Cols);
            for (var rowIndex = 0; rowIndex < mat.Rows; rowIndex++)
            {
                for (var colIndex = 0; colIndex < mat.Cols; colIndex++)
                {
                    Console.Write($"{mat.At<double>(rowIndex, colIndex)} ");
                }
                Console.WriteLine("");
            }
        }
        void print_double(double[,] mat)
        {
            Console.WriteLine(mat.GetLength(0) + " " + mat.GetLength(1));
            for (var rowIndex = 0; rowIndex < mat.GetLength(0); rowIndex++)
            {
                for (var colIndex = 0; colIndex < mat.GetLength(1); colIndex++)
                {
                    Console.Write(Math.Round(mat[rowIndex, colIndex], 4) + " ");
                }
                Console.WriteLine("");
            }
        }

        void print_double(double[] mat)
        {
            Console.WriteLine(mat.GetLength(0));
            for (var rowIndex = 0; rowIndex < mat.GetLength(0); rowIndex++)
            {
                Console.Write(Math.Round(mat[rowIndex], 4) + " ");
            }
            Console.WriteLine("");
        }
        object to_double(Mat mat)
        {

            if (mat.Rows == 1)
            {
                Console.WriteLine(mat.Rows + " " + mat.Cols);
                var data = new double[mat.Cols];
                for (var colIndex = 0; colIndex < mat.Cols; colIndex++)
                {
                    data[colIndex] = mat.At<double>(0, colIndex);
                }
                return (object)data;
            }
            else
            {
                Console.WriteLine(mat.Rows + " " + mat.Cols);
                var data = new double[mat.Rows, mat.Cols];

                for (var rowIndex = 0; rowIndex < mat.Rows; rowIndex++)
                {
                    for (var colIndex = 0; colIndex < mat.Cols; colIndex++)
                    {
                        data[rowIndex, colIndex] = mat.At<double>(0, colIndex);
                    }
                }
                return (object)data;
            }


        }


    }

    public class CalibrationResult
    {
        public double[,] CameraMatrix1 { get; set; } // Матрица камеры 1 (3x3)
        public double[] DistCoeffs1 { get; set; }   // Коэффициенты искажения 1 (5)
        public double[,] CameraMatrix2 { get; set; } // Матрица камеры 2 (3x3)
        public double[] DistCoeffs2 { get; set; }   // Коэффициенты искажения 2 (5)
        public double[,] R { get; set; }            // Матрица вращения (3x3)
        public double[] T { get; set; }             // Вектор трансляции (3)
        public double[,] E { get; set; }            // Существенная матрица (3x3)
        public double[,] F { get; set; }            // Фундаментальная матрица (3x3)
        public double Error { get; set; }           // Ошибка калибровки
    }
}
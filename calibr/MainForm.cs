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
        private const float squareSize = 9f; // Размер квадрата на доске в мм

        // Размер маркера ArUco в миллиметрах
        private float arucoMarkerSize = 45f; // Предполагаемый размер маркера - 5 см

        // Порог репроекционной ошибки (можно регулировать для точности)
        private float reprojectionErrorThreshold = 3.0f; // Увеличиваем порог для отображения результатов

        // Словарь для хранения историй расстояний для каждого маркера
        private Dictionary<int, List<double>> markerDistanceHistory = new Dictionary<int, List<double>>();
        private const int MAX_HISTORY_SIZE = 5; // Максимальный размер истории для сглаживания

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

                // ТРИАНГУЛЯЦИЯ ArUco МАРКЕРОВ
                if (calibrationResult != null && idsAruco1.Length > 0 && idsAruco2.Length > 0)
                {
                    // Переменные для хранения расстояния до маркера для отображения на главном экране
                    string distanceDisplay = "";
                    double? globalDistance = null;

                    Mat camMatrix1Mat = new Mat(3, 3, MatType.CV_64FC1);
                    for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) camMatrix1Mat.Set(r, c, calibrationResult.CameraMatrix1[r, c]);

                    Mat distCoeffs1Mat = new Mat(1, calibrationResult.DistCoeffs1.Length, MatType.CV_64FC1);
                    for (int i = 0; i < calibrationResult.DistCoeffs1.Length; i++) distCoeffs1Mat.Set(0, i, calibrationResult.DistCoeffs1[i]);

                    Mat camMatrix2Mat = new Mat(3, 3, MatType.CV_64FC1);
                    for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) camMatrix2Mat.Set(r, c, calibrationResult.CameraMatrix2[r, c]);

                    Mat distCoeffs2Mat = new Mat(1, calibrationResult.DistCoeffs2.Length, MatType.CV_64FC1);
                    for (int i = 0; i < calibrationResult.DistCoeffs2.Length; i++) distCoeffs2Mat.Set(0, i, calibrationResult.DistCoeffs2[i]);

                    Mat R_stereoMat = new Mat(3, 3, MatType.CV_64FC1);
                    for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) R_stereoMat.Set(r, c, calibrationResult.R[r, c]);

                    Mat T_stereoMat = new Mat(3, 1, MatType.CV_64FC1);
                    for (int i = 0; i < 3; i++) T_stereoMat.Set(i, 0, calibrationResult.T[i]);

                    // Создаем правильные проекционные матрицы для триангуляции
                    // !! Полностью переделываем проекционные матрицы, возможно некорректная калибровка
                    
                    // Для первой камеры: [I | 0]
                    Mat P1 = new Mat(3, 4, MatType.CV_64FC1);
                    
                    // Установим внутренние параметры для P1
                    P1.Set(0, 0, calibrationResult.CameraMatrix1[0, 0]);
                    P1.Set(0, 1, calibrationResult.CameraMatrix1[0, 1]);
                    P1.Set(0, 2, calibrationResult.CameraMatrix1[0, 2]);
                    P1.Set(0, 3, 0);
                    
                    P1.Set(1, 0, calibrationResult.CameraMatrix1[1, 0]);
                    P1.Set(1, 1, calibrationResult.CameraMatrix1[1, 1]);
                    P1.Set(1, 2, calibrationResult.CameraMatrix1[1, 2]);
                    P1.Set(1, 3, 0);
                    
                    P1.Set(2, 0, calibrationResult.CameraMatrix1[2, 0]);
                    P1.Set(2, 1, calibrationResult.CameraMatrix1[2, 1]);
                    P1.Set(2, 2, calibrationResult.CameraMatrix1[2, 2]);
                    P1.Set(2, 3, 0);
                    
                    // Для второй камеры: [R | T]
                    Mat P2 = new Mat(3, 4, MatType.CV_64FC1);
                    Mat RT2 = new Mat(3, 4, MatType.CV_64FC1);
                    
                    // Заполняем RT2 [R|T]
                    for (int i = 0; i < 3; i++)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            RT2.Set(i, j, calibrationResult.R[i, j]);
                        }
                        RT2.Set(i, 3, calibrationResult.T[i]);
                    }
                    
                    // Установим внутренние параметры для P2
                    for (int i = 0; i < 3; i++)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            double sum = 0;
                            for (int k = 0; k < 3; k++)
                            {
                                sum += calibrationResult.CameraMatrix2[i, k] * RT2.At<double>(k, j);
                            }
                            P2.Set(i, j, sum);
                        }
                        
                        // Последний столбец
                        double sumLastCol = 0;
                        for (int k = 0; k < 3; k++)
                        {
                            sumLastCol += calibrationResult.CameraMatrix2[i, k] * RT2.At<double>(k, 3);
                        }
                        P2.Set(i, 3, sumLastCol);
                    }

                    // Отладочная информация о проекционных матрицах
                    Debug.WriteLine("Проекционная матрица P1:");
                    print_mat(P1);
                    Debug.WriteLine("Проекционная матрица P2:");
                    print_mat(P2);
                    
                    // Отображаем расстояние между камерами
                    double camDistance = Math.Sqrt(
                        calibrationResult.T[0] * calibrationResult.T[0] +
                        calibrationResult.T[1] * calibrationResult.T[1] + 
                        calibrationResult.T[2] * calibrationResult.T[2]);
                    
                    Cv2.PutText(fr1, $"The distance between the cameras: {camDistance:F1} mm", 
                        new OpenCvSharp.Point(10, 30), HersheyFonts.HersheySimplex, 0.6, new Scalar(255, 255, 0), 2);
                    Cv2.PutText(fr2, $"The distance between the cameras: {camDistance:F1} mm", 
                        new OpenCvSharp.Point(10, 30), HersheyFonts.HersheySimplex, 0.6, new Scalar(255, 255, 0), 2);

                    var arucoIds1List = idsAruco1.ToList();
                    var arucoIds2List = idsAruco2.ToList();

                    for (int idx1 = 0; idx1 < arucoIds1List.Count; idx1++)
                    {
                        int markerId = arucoIds1List[idx1];
                        int idx2 = arucoIds2List.IndexOf(markerId);

                        if (idx2 != -1) // Маркер найден на обоих изображениях
                        {
                            Point2f[] markerCornersCam1 = cornersAruco1[idx1];
                            Point2f[] markerCornersCam2 = cornersAruco2[idx2];

                            Point2f centerCam1 = new Point2f(markerCornersCam1.Average(p => p.X), markerCornersCam1.Average(p => p.Y));
                            Point2f centerCam2 = new Point2f(markerCornersCam2.Average(p => p.X), markerCornersCam2.Average(p => p.Y));

                            // Устранение искажений для точек центра маркера и перевод в нормализованные координаты
                            Mat distortedPoints1 = Mat.FromArray(new[] { centerCam1 });
                            Mat undistortedPoints1Mat = new Mat();
                            Cv2.UndistortPoints(distortedPoints1, undistortedPoints1Mat, camMatrix1Mat, distCoeffs1Mat);

                            Mat distortedPoints2 = Mat.FromArray(new[] { centerCam2 });
                            Mat undistortedPoints2Mat = new Mat();
                            Cv2.UndistortPoints(distortedPoints2, undistortedPoints2Mat, camMatrix2Mat, distCoeffs2Mat);

                            // Извлечение нормализованных координат
                            Point2f undistortedCenter1 = undistortedPoints1Mat.Get<Point2f>(0, 0);
                            Point2f undistortedCenter2 = undistortedPoints2Mat.Get<Point2f>(0, 0);

                            // Отладочная информация о координатах
                            Debug.WriteLine($"Normalized coordinates 1: ({undistortedCenter1.X:F4}, {undistortedCenter1.Y:F4})");
                            Debug.WriteLine($"Normalized coordinates 2: ({undistortedCenter2.X:F4}, {undistortedCenter2.Y:F4})");

                            // Подготовка точек для триангуляции
                            Mat pt1Mat = new Mat(2, 1, MatType.CV_64FC1);
                            pt1Mat.Set(0, 0, undistortedCenter1.X);
                            pt1Mat.Set(1, 0, undistortedCenter1.Y);

                            Mat pt2Mat = new Mat(2, 1, MatType.CV_64FC1);
                            pt2Mat.Set(0, 0, undistortedCenter2.X);
                            pt2Mat.Set(1, 0, undistortedCenter2.Y);

                            // Триангуляция точек
                            Mat points4D = new Mat();
                            Cv2.TriangulatePoints(P1, P2, pt1Mat, pt2Mat, points4D);

                            if (!points4D.Empty() && points4D.Rows == 4 && points4D.Cols == 1)
                            {
                                double hx = points4D.At<double>(0, 0);
                                double hy = points4D.At<double>(1, 0);
                                double hz = points4D.At<double>(2, 0);
                                double hw = points4D.At<double>(3, 0);

                                Debug.WriteLine($"Homogeneous coordinates: X={hx:F6}, Y={hy:F6}, Z={hz:F6}, W={hw:F6}");
                                
                                double hw_orig = hw; // Сохраняем оригинальное значение для отладки
                                
                                // Отображение результатов триангуляции на изображениях
                                string hwInfo = $"hw={hw_orig:F4}";
                                Cv2.PutText(fr1, hwInfo, new OpenCvSharp.Point(10, fr1.Rows - 10), 
                                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 255, 255), 1);
                                Cv2.PutText(fr2, hwInfo, new OpenCvSharp.Point(10, fr2.Rows - 10), 
                                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 255, 255), 1);
                                
                                // Инвертируем все координаты, если hw отрицательный
                                // Это может исправить проблему с ориентацией камер
                                if (hw_orig < 0)
                                {
                                    hx = -hx;
                                    hy = -hy;
                                    hz = -hz;
                                    hw = -hw_orig; // Используем положительное значение hw
                                }
                                else
                                {
                                    hw = hw_orig; // Используем исходное положительное значение
                                }

                                if (!double.IsNaN(hw) && !double.IsInfinity(hw) && hw > 1e-9)
                                {
                                    // Преобразование из гомогенных координат в Евклидовы
                                    double X = hx / hw;
                                    double Y = hy / hw;
                                    double Z = hz / hw;
                                    
                                    // Больше не нужно инвертировать координаты, мы уже сделали это выше
                                    
                                    // Отладочная информация
                                    Debug.WriteLine($"Евклидовы координаты после нормализации: X={X:F2}, Y={Y:F2}, Z={Z:F2}");

                                    // Вычисление репроекционной ошибки для проверки точности триангуляции
                                    Mat point3D = new Mat(4, 1, MatType.CV_64FC1);
                                    point3D.Set(0, 0, X);
                                    point3D.Set(1, 0, Y);
                                    point3D.Set(2, 0, Z);
                                    point3D.Set(3, 0, 1.0);

                                    // Репроекция 3D точки обратно на изображение камеры 1
                                    Mat reprojection1 = P1 * point3D;
                                    double rx1 = reprojection1.At<double>(0, 0) / reprojection1.At<double>(2, 0);
                                    double ry1 = reprojection1.At<double>(1, 0) / reprojection1.At<double>(2, 0);

                                    // Репроекция 3D точки обратно на изображение камеры 2
                                    Mat reprojection2 = P2 * point3D;
                                    double rx2 = reprojection2.At<double>(0, 0) / reprojection2.At<double>(2, 0);
                                    double ry2 = reprojection2.At<double>(1, 0) / reprojection2.At<double>(2, 0);

                                    // Вычисление репроекционной ошибки
                                    double reproj_error1 = Math.Sqrt(Math.Pow(undistortedCenter1.X - rx1, 2) + Math.Pow(undistortedCenter1.Y - ry1, 2));
                                    double reproj_error2 = Math.Sqrt(Math.Pow(undistortedCenter2.X - rx2, 2) + Math.Pow(undistortedCenter2.Y - ry2, 2));
                                    double avg_reproj_error = (reproj_error1 + reproj_error2) / 2;

                                    if (Z > 0) // Проверяем, что точка перед камерой 1
                                    {
                                        // Проверка для камеры 2
                                        // Точка (X,Y,Z) в системе координат камеры 1
                                        // Преобразуем ее в систему координат камеры 2: X_c2 = R * X_c1 + T_stereo
                                        double X_c2 = R_stereoMat.At<double>(0, 0) * X + R_stereoMat.At<double>(0, 1) * Y + R_stereoMat.At<double>(0, 2) * Z + T_stereoMat.At<double>(0, 0);
                                        double Y_c2 = R_stereoMat.At<double>(1, 0) * X + R_stereoMat.At<double>(1, 1) * Y + R_stereoMat.At<double>(1, 2) * Z + T_stereoMat.At<double>(1, 0);
                                        double Z_c2 = R_stereoMat.At<double>(2, 0) * X + R_stereoMat.At<double>(2, 1) * Y + R_stereoMat.At<double>(2, 2) * Z + T_stereoMat.At<double>(2, 0);

                                        if (Z_c2 > 0) // Проверяем, что точка перед камерой 2
                                        {
                                            // Расстояние от первой камеры
                                            double distanceFromCam1 = Math.Sqrt(X * X + Y * Y + Z * Z);
                                            
                                            // Расстояние от второй камеры
                                            double distanceFromCam2 = Math.Sqrt(X_c2 * X_c2 + Y_c2 * Y_c2 + Z_c2 * Z_c2);
                                            
                                            // Усредненное расстояние
                                            double avgDistance = (distanceFromCam1 + distanceFromCam2) / 2.0;
                                            
                                            // Применяем фильтрацию для устранения скачков в измерениях расстояния
                                            double filteredDistance = GetFilteredDistance(markerId, avgDistance);
                                            
                                            // Для корректности триангуляции, репроекционная ошибка должна быть малой
                                            if (avg_reproj_error < reprojectionErrorThreshold) // Используем настраиваемый порог ошибки
                                            {
                                                Debug.WriteLine($"Маркер ID {markerId}: 3D ({X:F2}, {Y:F2}, {Z:F2}) мм");
                                                Debug.WriteLine($"Расстояние от камеры 1: {distanceFromCam1:F2} мм, от камеры 2: {distanceFromCam2:F2} мм");
                                                Debug.WriteLine($"Исходное расстояние: {avgDistance:F2} мм, Фильтрованное: {filteredDistance:F2} мм");
                                                Debug.WriteLine($"Репрoекц. ошибка: {avg_reproj_error:F4}");
                                                
                                                // Отображение текста расстояния на изображении
                                                Cv2.PutText(fr1, $"ID {markerId}: {filteredDistance:F1} mm", 
                                                    new OpenCvSharp.Point(centerCam1.X + 10, centerCam1.Y), 
                                                    HersheyFonts.HersheySimplex, 0.5, Scalar.Red, 2);
                                                
                                                Cv2.PutText(fr2, $"ID {markerId}: {filteredDistance:F1} mm", 
                                                    new OpenCvSharp.Point(centerCam2.X + 10, centerCam2.Y), 
                                                    HersheyFonts.HersheySimplex, 0.5, Scalar.Red, 2);
                                                
                                                // Отображаем 3D координаты маркера
                                                Cv2.PutText(fr1, $"3D: ({X:F1},{Y:F1},{Z:F1})mm", 
                                                    new OpenCvSharp.Point(centerCam1.X + 10, centerCam1.Y + 25), 
                                                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 255, 255), 2);
                                                
                                                Cv2.PutText(fr2, $"3D: ({X:F1},{Y:F1},{Z:F1})mm", 
                                                    new OpenCvSharp.Point(centerCam2.X + 10, centerCam2.Y + 25), 
                                                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 255, 255), 2);
                                                
                                                // Преобразование нормализованных координат обратно в пиксельные для отображения
                                                Point2f pixelOriginal1 = new Point2f(
                                                    (float)(camMatrix1Mat.At<double>(0, 0) * undistortedCenter1.X + camMatrix1Mat.At<double>(0, 2)),
                                                    (float)(camMatrix1Mat.At<double>(1, 1) * undistortedCenter1.Y + camMatrix1Mat.At<double>(1, 2))
                                                );
                                                
                                                Point2f pixelReprojected1 = new Point2f(
                                                    (float)(camMatrix1Mat.At<double>(0, 0) * rx1 + camMatrix1Mat.At<double>(0, 2)),
                                                    (float)(camMatrix1Mat.At<double>(1, 1) * ry1 + camMatrix1Mat.At<double>(1, 2))
                                                );
                                                
                                                Point2f pixelOriginal2 = new Point2f(
                                                    (float)(camMatrix2Mat.At<double>(0, 0) * undistortedCenter2.X + camMatrix2Mat.At<double>(0, 2)),
                                                    (float)(camMatrix2Mat.At<double>(1, 1) * undistortedCenter2.Y + camMatrix2Mat.At<double>(1, 2))
                                                );
                                                
                                                Point2f pixelReprojected2 = new Point2f(
                                                    (float)(camMatrix2Mat.At<double>(0, 0) * rx2 + camMatrix2Mat.At<double>(0, 2)),
                                                    (float)(camMatrix2Mat.At<double>(1, 1) * ry2 + camMatrix2Mat.At<double>(1, 2))
                                                );
                                                
                                                // Визуализация репроекционной ошибки
                                                DrawReprojectionError(fr1, centerCam1, pixelReprojected1, $"Err: {reproj_error1:F2}");
                                                DrawReprojectionError(fr2, centerCam2, pixelReprojected2, $"Err: {reproj_error2:F2}");

                                                // Устанавливаем глобальное расстояние для отображения в верхней части экрана
                                                globalDistance = filteredDistance;
                                                distanceDisplay = $"Distance: {filteredDistance:F1} mm";
                                            }
                                            else
                                            {
                                                // Показываем расстояние даже при высокой репроекционной ошибке
                                                Debug.WriteLine($"Маркер ID {markerId}: Большая репроекционная ошибка ({avg_reproj_error:F4})");
                                                
                                                // Вычисляем расстояние напрямую из 3D координат
                                                double rawDistance = Math.Sqrt(X * X + Y * Y + Z * Z);
                                                
                                                // Применяем фильтрацию
                                                double filteredRawDistance = GetFilteredDistance(markerId, rawDistance);
                                                
                                                // Отображаем с предупреждением о высокой ошибке
                                                globalDistance = filteredRawDistance;
                                                distanceDisplay = $"Distance: {filteredRawDistance:F1} mm (not sure)";
                                                
                                                // Отображаем расстояние и предупреждение на изображениях
                                                Cv2.PutText(fr1, $"ID {markerId}: {filteredRawDistance:F1} mm", 
                                                    new OpenCvSharp.Point(centerCam1.X + 10, centerCam1.Y), 
                                                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 0, 255), 2);
                                                
                                                Cv2.PutText(fr2, $"ID {markerId}: {filteredRawDistance:F1} mm", 
                                                    new OpenCvSharp.Point(centerCam2.X + 10, centerCam2.Y), 
                                                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 0, 255), 2);
                                                
                                                // Отображаем 3D координаты и ошибку
                                                Cv2.PutText(fr1, $"3D: ({X:F1},{Y:F1},{Z:F1})mm Err:{avg_reproj_error:F1}", 
                                                    new OpenCvSharp.Point(centerCam1.X + 10, centerCam1.Y + 25), 
                                                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 0, 255), 2);
                                                
                                                Cv2.PutText(fr2, $"3D: ({X:F1},{Y:F1},{Z:F1})mm Err:{avg_reproj_error:F1}", 
                                                    new OpenCvSharp.Point(centerCam2.X + 10, centerCam2.Y + 25), 
                                                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 0, 255), 2);
                                            }
                                        }
                                        else
                                        {
                                            Debug.WriteLine($"Маркер ID {markerId}: Триангулированная точка за камерой 2 (Z_c2={Z_c2:F2})");
                                        }
                                    }
                                    else
                                    {
                                        Debug.WriteLine($"Маркер ID {markerId}: Триангулированная точка за камерой 1 (Z_c1={Z:F2})");
                                    }
                                    
                                    // Освобождение ресурсов
                                    point3D.Dispose();
                                    reprojection1.Dispose();
                                    reprojection2.Dispose();
                                }
                                else
                                {
                                    Debug.WriteLine($"Маркер ID {markerId}: Проблема с гомогенными координатами (hw={hw:E2})");
                                }
                            }
                            else
                            {
                                Debug.WriteLine($"Маркер ID {markerId}: points4D пуст или имеет неверный размер после триангуляции.");
                            }
                            // Освобождение ресурсов
                            pt1Mat.Dispose();
                            pt2Mat.Dispose();
                            points4D.Dispose();
                            distortedPoints1.Dispose();
                            undistortedPoints1Mat.Dispose();
                            distortedPoints2.Dispose();
                            undistortedPoints2Mat.Dispose();
                        }
                    }
                    
                    // Освобождение ресурсов
                    camMatrix1Mat.Dispose();
                    distCoeffs1Mat.Dispose();
                    camMatrix2Mat.Dispose();
                    distCoeffs2Mat.Dispose();
                    R_stereoMat.Dispose();
                    T_stereoMat.Dispose();
                    P1.Dispose();
                    P2.Dispose();
                    RT2.Dispose();

                    // Отображаем крупное расстояние до маркера в верхней части экрана
                    if (globalDistance.HasValue)
                    {
                        // Создаем тень для лучшей видимости
                        Cv2.PutText(fr1, distanceDisplay, 
                            new OpenCvSharp.Point(fr1.Cols / 2 - 150, 70), 
                            HersheyFonts.HersheySimplex, 1.2, Scalar.Black, 4);
                        Cv2.PutText(fr1, distanceDisplay, 
                            new OpenCvSharp.Point(fr1.Cols / 2 - 150, 70), 
                            HersheyFonts.HersheySimplex, 1.2, Scalar.Yellow, 2);
                        
                        Cv2.PutText(fr2, distanceDisplay, 
                            new OpenCvSharp.Point(fr2.Cols / 2 - 150, 70), 
                            HersheyFonts.HersheySimplex, 1.2, Scalar.Black, 4);
                        Cv2.PutText(fr2, distanceDisplay, 
                            new OpenCvSharp.Point(fr2.Cols / 2 - 150, 70), 
                            HersheyFonts.HersheySimplex, 1.2, Scalar.Yellow, 2);
                    }

                    // Отображаем информацию о настройках на экране
                    Cv2.PutText(fr1, $"Error threshold: {reprojectionErrorThreshold:F1}", 
                        new OpenCvSharp.Point(10, fr1.Rows - 40), 
                        HersheyFonts.HersheySimplex, 0.5, new Scalar(255, 255, 255), 1);
                    Cv2.PutText(fr2, $"Error threshold: {reprojectionErrorThreshold:F1}", 
                        new OpenCvSharp.Point(10, fr2.Rows - 40), 
                        HersheyFonts.HersheySimplex, 0.5, new Scalar(255, 255, 255), 1);

                    Cv2.PutText(fr1, $"Marker Size: {arucoMarkerSize} mm", 
                        new OpenCvSharp.Point(10, fr1.Rows - 70), 
                        HersheyFonts.HersheySimplex, 0.5, new Scalar(255, 255, 255), 1);
                    Cv2.PutText(fr2, $"Marker Size: {arucoMarkerSize} mm", 
                        new OpenCvSharp.Point(10, fr2.Rows - 70), 
                        HersheyFonts.HersheySimplex, 0.5, new Scalar(255, 255, 255), 1);
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

                if (calibrationResult != null && found1 && found2)
                {
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

            var tvecs1 = new Vec3d[10];
            var tvecs2 = new Vec3d[10];
            var rvecs1 = new Vec3d[10];
            var rvecs2 = new Vec3d[10];
            //print_mat(pairObjectPointsList[0]);
            //print_mat(pairImagePointsList1[0]);
            /* var cameraMatrix1_double =(double[,]) to_double(cameraMatrix1);
             var cameraMatrix2_double = (double[,])to_double(cameraMatrix2);


             var distCoeffs1_double = (double[])to_double(distCoeffs1);
             var distCoeffs2_double = (double[])to_double(distCoeffs2);*/
            var image_size = mats1[0].Size();
            var err1 = Cv2.CalibrateCamera(ps_3d_all, ps_2d_all_1, image_size, cameraMatrix1, distCoeffs1, out rvecs1, out tvecs1, CalibrationFlags.None, new TermCriteria(CriteriaTypes.Count, 100, 0.1));
            var err2 = Cv2.CalibrateCamera(ps_3d_all, ps_2d_all_2, image_size, cameraMatrix2, distCoeffs2, out rvecs2, out tvecs2, CalibrationFlags.None, new TermCriteria(CriteriaTypes.Count, 100, 0.1));
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
                    cameraMatrix1, distCoeffs1, cameraMatrix2, distCoeffs2,
                    new OpenCvSharp.Size(640, 480), R, T, E, F,
                    CalibrationFlags.RationalModel
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

                MessageBox.Show($"Калибровка успешна! Ошибка: {error}\nРезультаты сохранены в calibration_result.json");
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
                    try
                    {
                        if (mat.Type() == MatType.CV_64FC1)
                        {
                            Console.Write($"{mat.At<double>(rowIndex, colIndex):F4} ");
                        }
                        else if (mat.Type() == MatType.CV_32FC1)
                        {
                            Console.Write($"{mat.At<float>(rowIndex, colIndex):F4} ");
                        }
                        else if (mat.Type() == MatType.CV_32FC2)
                        {
                            var vec = mat.At<Vec2f>(rowIndex, colIndex);
                            Console.Write($"({vec.Item0:F2}, {vec.Item1:F2}) ");
                        }
                        else if (mat.Type() == MatType.CV_32FC3)
                        {
                            var vec = mat.At<Vec3f>(rowIndex, colIndex);
                            Console.Write($"({vec.Item0:F2}, {vec.Item1:F2}, {vec.Item2:F2}) ");
                        }
                        else
                {
                    Console.Write($"{mat.At<double>(rowIndex, colIndex)} ");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Write("ERROR ");
                    }
                }
                Console.WriteLine("");
            }
            Console.WriteLine("----------------------------------");
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

        private void DrawReprojectionError(Mat image, Point2f original, Point2f reprojected, string text)
        {
            // Рисуем оригинальную и репроецированную точки
            Cv2.Circle(image, new OpenCvSharp.Point(original.X, original.Y), 3, new Scalar(0, 255, 0), -1); // Оригинальная точка (зеленым)
            Cv2.Circle(image, new OpenCvSharp.Point(reprojected.X, reprojected.Y), 3, new Scalar(0, 0, 255), -1); // Репроецированная точка (красным)
            
            // Рисуем линию между ними
            Cv2.Line(image, new OpenCvSharp.Point(original.X, original.Y), new OpenCvSharp.Point(reprojected.X, reprojected.Y), 
                new Scalar(255, 0, 0), 1, LineTypes.AntiAlias);
            
            // Отображаем текст о репроекционной ошибке
            if (!string.IsNullOrEmpty(text))
            {
                Cv2.PutText(image, text, new OpenCvSharp.Point(original.X + 5, original.Y - 5), 
                    HersheyFonts.HersheySimplex, 0.4, new Scalar(255, 255, 0), 1);
            }
        }

        // Метод для фильтрации расстояния с использованием скользящего среднего
        private double GetFilteredDistance(int markerId, double newDistance)
        {
            // Инициализация истории для нового маркера
            if (!markerDistanceHistory.ContainsKey(markerId))
            {
                markerDistanceHistory[markerId] = new List<double>();
            }
            
            var history = markerDistanceHistory[markerId];
            
            // Добавляем новое значение в историю
            history.Add(newDistance);
            
            // Ограничиваем размер истории
            if (history.Count > MAX_HISTORY_SIZE)
            {
                history.RemoveAt(0);
            }
            
            // Проверка на выбросы (значения, сильно отличающиеся от предыдущих)
            if (history.Count > 1)
            {
                double prevAvg = history.Take(history.Count - 1).Average();
                double diff = Math.Abs(newDistance - prevAvg);
                
                // Если разница более 20%, считаем это выбросом и игнорируем
                if (diff > prevAvg * 0.2)
                {
                    history.RemoveAt(history.Count - 1); // Удаляем выброс
                    return prevAvg; // Возвращаем предыдущее среднее
                }
            }
            
            // Вычисляем среднее значение из истории
            return history.Average();
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
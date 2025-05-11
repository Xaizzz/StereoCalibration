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
        List<Point3f>  ps_3d_all_out = new List<Point3f>();
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

        public MainForm()
        {
            InitializeComponent();

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
                    double[] rvec1 = new double[3];
                    double[] tvec1 = new double[3];
                    double[] rvec2 = new double[3];
                    double[] tvec2 = new double[3];
                    Cv2.SolvePnP(ps_3d_all_out, corners1, calibrationResult.CameraMatrix1, calibrationResult.DistCoeffs1, ref rvec1, ref tvec1);
                    Cv2.SolvePnP(ps_3d_all_out, corners2, calibrationResult.CameraMatrix2, calibrationResult.DistCoeffs2, ref rvec2, ref tvec2);
                    double dt_x = tvec1[0] - tvec2[0];
                    double dt_y = tvec1[1] - tvec2[1];
                    double dt_z = tvec1[2] - tvec2[2];
                    Debug.WriteLine($"X {dt_x} Y {dt_y} Z {dt_z}");
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
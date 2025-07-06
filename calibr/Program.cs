using StereoCalibration.Forms;
using StereoCalibration.Interfaces;
using StereoCalibration.Presenters;
using StereoCalibration.Services;
using System;
using System.Windows.Forms;

namespace StereoCalibration
{
    /// <summary>
    /// Точка входа в приложение с новой архитектурой
    /// Демонстрирует Dependency Injection и MVP паттерн
    /// </summary>
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                // Создаем сервисы (в реальном приложении лучше использовать DI контейнер)
                var cameraService = new CameraService();
                var calibrationService = new CalibrationService();
                var arUcoService = new ArUcoDetectionService();
                var triangulationService = new TriangulationService();
                var fileService = new FileService();

                // Создаем презентер для выбора камер
                var cameraSelectionPresenter = new CameraSelectionPresenter(cameraService, null);
                
                // Показываем форму выбора камер
                var cameraSelectionForm = new CameraSelectionForm(cameraSelectionPresenter);

                if (cameraSelectionForm.ShowDialog() == DialogResult.OK)
                {
                    // Получаем выбранные камеры
                    int camera1Id = cameraSelectionForm.Camera1Index;
                    int camera2Id = cameraSelectionForm.Camera2Index;

                    // Создаем главную форму
                    var mainForm = new MainForm();

                    // Создаем презентер для главной формы
                    var mainFormPresenter = new MainFormPresenter(
                        cameraService,
                        calibrationService,
                        arUcoService,
                        triangulationService,
                        fileService,
                        mainForm);
                    
                    // Устанавливаем презентер в форму
                    mainForm.SetPresenter(mainFormPresenter, camera1Id, camera2Id);

                    // Запускаем главную форму
                    Application.Run(mainForm);
                }

                // Освобождаем ресурсы сервисов
                cameraService?.Dispose();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Критическая ошибка приложения: {ex.Message}", 
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
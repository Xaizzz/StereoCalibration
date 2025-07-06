using OpenCvSharp;
using System.Threading.Tasks;

namespace StereoCalibration.Interfaces
{
    /// <summary>
    /// Интерфейс для файловых операций
    /// Абстрагирует работу с файловой системой
    /// </summary>
    public interface IFileService
    {
        /// <summary>
        /// Сохранение изображения в файл
        /// </summary>
        /// <param name="image">Изображение для сохранения</param>
        /// <param name="filePath">Путь к файлу</param>
        /// <returns>True если сохранение успешно</returns>
        Task<bool> SaveImageAsync(Mat image, string filePath);

        /// <summary>
        /// Загрузка изображения из файла
        /// </summary>
        /// <param name="filePath">Путь к файлу</param>
        /// <returns>Загруженное изображение или null</returns>
        Task<Mat> LoadImageAsync(string filePath);

        /// <summary>
        /// Создание директории для сохранения изображений
        /// </summary>
        /// <param name="path">Путь к директории</param>
        void CreateDirectoryIfNotExists(string path);

        /// <summary>
        /// Открытие папки в проводнике
        /// </summary>
        /// <param name="path">Путь к папке</param>
        void OpenDirectoryInExplorer(string path);

        /// <summary>
        /// Проверка существования файла
        /// </summary>
        /// <param name="filePath">Путь к файлу</param>
        /// <returns>True если файл существует</returns>
        bool FileExists(string filePath);

        /// <summary>
        /// Проверка существования директории
        /// </summary>
        /// <param name="directoryPath">Путь к директории</param>
        /// <returns>True если директория существует</returns>
        bool DirectoryExists(string directoryPath);
    }
} 
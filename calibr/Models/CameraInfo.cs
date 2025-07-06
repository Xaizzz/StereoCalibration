namespace StereoCalibration.Models
{
    /// <summary>
    /// Модель информации о камере
    /// Инкапсулирует данные о камере в системе
    /// </summary>
    public class CameraInfo
    {
        /// <summary>
        /// Уникальный идентификатор камеры в системе
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Отображаемое имя камеры
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Доступность камеры для подключения
        /// </summary>
        public bool IsAvailable { get; set; }

        /// <summary>
        /// Статус подключения камеры
        /// </summary>
        public bool IsConnected { get; set; }

        /// <summary>
        /// Максимальная ширина разрешения
        /// </summary>
        public int MaxWidth { get; set; }

        /// <summary>
        /// Максимальная высота разрешения
        /// </summary>
        public int MaxHeight { get; set; }

        /// <summary>
        /// Текущая ширина разрешения
        /// </summary>
        public int CurrentWidth { get; set; }

        /// <summary>
        /// Текущая высота разрешения
        /// </summary>
        public int CurrentHeight { get; set; }

        /// <summary>
        /// Конструктор по умолчанию
        /// </summary>
        public CameraInfo()
        {
            IsAvailable = false;
            IsConnected = false;
            CurrentWidth = 640;
            CurrentHeight = 480;
        }

        /// <summary>
        /// Конструктор с параметрами
        /// </summary>
        /// <param name="id">Идентификатор камеры</param>
        /// <param name="name">Имя камеры</param>
        public CameraInfo(int id, string name = null) : this()
        {
            Id = id;
            Name = name ?? $"Camera {id}";
        }

        /// <summary>
        /// Строковое представление объекта
        /// </summary>
        public override string ToString()
        {
            return Name;
        }
    }
} 
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using System.Windows.Media;
using System.Threading.Tasks;
using HelixToolkit.Wpf;
using StereoCalibration.Services;

namespace StereoCalibration.UI
{
    /// <summary>
    /// Класс для данных таблицы координат маркеров с поддержкой обновления UI
    /// </summary>
    public class MarkerCoordinate : INotifyPropertyChanged
    {
        private string _x = "";
        private string _y = "";
        private string _z = "";
        private string _distance = "";
        private string _name = "";
        private int _displayIndex;

        public int ID { get; set; }

        public int DisplayIndex
        {
            get => _displayIndex;
            set
            {
                if (_displayIndex != value)
                {
                    _displayIndex = value;
                    OnPropertyChanged(nameof(DisplayIndex));
                }
            }
        }

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged(nameof(Name));
                }
            }
        }

        public string X
        {
            get => _x;
            set
            {
                if (_x != value)
                {
                    _x = value;
                    OnPropertyChanged(nameof(X));
                }
            }
        }

        public string Y
        {
            get => _y;
            set
            {
                if (_y != value)
                {
                    _y = value;
                    OnPropertyChanged(nameof(Y));
                }
            }
        }

        public string Z
        {
            get => _z;
            set
            {
                if (_z != value)
                {
                    _z = value;
                    OnPropertyChanged(nameof(Z));
                }
            }
        }

        public string Distance
        {
            get => _distance;
            set
            {
                if (_distance != value)
                {
                    _distance = value;
                    OnPropertyChanged(nameof(Distance));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// WPF UserControl для отображения 3D-сцены стереокалибровки.
    /// 
    /// Несмотря на имя файла `.xaml.cs`, визуальная часть создаётся полностью
    /// программно: внутри строится Grid, HelixViewport3D, таблица координат и
    /// вспомогательные панели. Контрол используется из WinForms через ElementHost.
    /// 
    /// Основные визуальные объекты: две камеры, центр стереопары, базовая линия,
    /// сферы-маркеры, подписи, линии от объективов к маркерам и полупрозрачная
    /// деформируемая поверхность по маркерам.
    /// </summary>
    public class Scene3DUserControl : UserControl
    {
        #region Поля
        private Scene3DService? _scene3DService;
        private readonly Dictionary<int, SphereVisual3D> _markerVisuals = new Dictionary<int, SphereVisual3D>();
        private readonly Dictionary<int, TextVisual3D> _markerTexts = new Dictionary<int, TextVisual3D>();
        
        // 3D элементы интерфейса
        private HelixViewport3D _viewport3D;
        private BoxVisual3D _camera1Visual;
        private BoxVisual3D _camera2Visual;
        private SphereVisual3D _camera1LensVisual;
        private SphereVisual3D _camera2LensVisual;
        private TruncatedConeVisual3D _stereoCenterVisual;
        private LinesVisual3D _cameraBaselineVisual;
        private LinesVisual3D _stereoAxisVisual;
        private TextVisual3D _baselineText;
        private TextVisual3D _camera1Text;
        private TextVisual3D _camera2Text;
        private TextVisual3D _centerText;
        private TextBlock _infoText;
        private readonly Dictionary<int, LinesVisual3D> _markerGuideLines = new Dictionary<int, LinesVisual3D>();
        /// <summary>
        /// Единый визуальный объект поверхности по маркерам. Поверхность обновляется
        /// заменой геометрии MeshGeometry3D, а не созданием множества новых объектов,
        /// чтобы снизить нагрузку на WPF/Helix.
        /// </summary>
        private ModelVisual3D _markerSurfaceVisual;
        private GeometryModel3D _markerSurfaceModel;
        private MeshGeometry3D _markerSurfaceMesh;
        private readonly Dictionary<int, Point3D> _lastSurfaceMarkerSnapshot = new Dictionary<int, Point3D>();
        private readonly Dictionary<int, string> _markerTextCache = new Dictionary<int, string>();
        private DateTime _lastSurfaceUpdateTime = DateTime.MinValue;
        private DateTime _lastMarkerTableUpdateTime = DateTime.MinValue;
        private DateTime _lastInfoPanelUpdateTime = DateTime.MinValue;
        
        // UI элементы для таблицы координат
        private DataGrid _coordinatesTable;
        private System.Collections.ObjectModel.ObservableCollection<MarkerCoordinate> _markersData;
        #endregion

        private const double CameraBodyHalfWidth = 7.5;
        private const double CameraLensOffset = CameraBodyHalfWidth + 2.0;
        private const int SurfaceUpdateIntervalMs = 300;
        private const int MarkerTableUpdateIntervalMs = 200;
        private const int InfoPanelUpdateIntervalMs = 500;
        private const int MaxSurfaceMarkers = 24;
        private const double SurfaceUpdateThresholdMm = 8.0;
        private const double MinTriangleArea = 1e-3;

        /// <summary>
        /// Создаёт WPF-разметку и начальную 3D-сцену.
        /// </summary>
        public Scene3DUserControl()
        {
            InitializeComponent();
            InitializeScene();
        }

        /// <summary>
        /// Создаёт интерфейс UserControl без XAML-файла.
        /// 
        /// Левая колонка содержит HelixViewport3D, правая — таблицу координат.
        /// Отдельная информационная панель накладывается поверх viewport для
        /// краткого состояния калибровки и управления.
        /// </summary>
        private void InitializeComponent()
        {
            // Создаем Grid как основной контейнер с двумя колонками
            var mainGrid = new Grid();
            
            // Определяем колонки: 3D сцена (75%) и таблица (25%)
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            
            // Инициализируем данные для таблицы маркеров
            _markersData = new System.Collections.ObjectModel.ObservableCollection<MarkerCoordinate>();
            
            // Создаем HelixViewport3D с максимально совместимыми настройками рендеринга
            _viewport3D = new HelixViewport3D
            {
                Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)), // Светло-серый
                CameraRotationMode = CameraRotationMode.Trackball,
                ShowCoordinateSystem = true,
                ShowViewCube = false,
                ShowFrameRate = false,
                IsHitTestVisible = true,
                ZoomExtentsWhenLoaded = false
            };
            
            // ЭКСПЕРИМЕНТАЛЬНО: Настройки рендеринга (без SetProcessRenderMode для совместимости)
            // Используем доступные настройки рендеринга
            try
            {
                var processRenderModeProperty = typeof(System.Windows.Media.RenderOptions).GetProperty("ProcessRenderMode");
                if (processRenderModeProperty != null)
                {
                    processRenderModeProperty.SetValue(null, 0); // Default
                    System.Diagnostics.Debug.WriteLine("Установлен режим рендеринга: Default");
                }
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine("ProcessRenderMode недоступен в данной версии .NET");
            }
            
            // Применяем настройки рендеринга для исправления полос
            ApplyAntiInterlaceSettings();
            
            // Дополнительные настройки для устранения проблем рендеринга
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(_viewport3D, BitmapScalingMode.HighQuality);
            System.Windows.Media.RenderOptions.SetEdgeMode(_viewport3D, EdgeMode.Aliased);
            
            // Принудительно устанавливаем настройки качества
            _viewport3D.SetValue(System.Windows.Media.RenderOptions.CachingHintProperty, CachingHint.Cache);
            _viewport3D.SetValue(System.Windows.Media.RenderOptions.CacheInvalidationThresholdMinimumProperty, 0.5);
            _viewport3D.SetValue(System.Windows.Media.RenderOptions.CacheInvalidationThresholdMaximumProperty, 2.0);

            // Настройка камеры
            _viewport3D.Camera = new PerspectiveCamera
            {
                Position = new Point3D(200, 200, 200),
                LookDirection = new Vector3D(-1, -1, -1),
                UpDirection = new Vector3D(0, 0, 1),
                FieldOfView = 45
            };

            // Добавляем простое и четкое освещение
            _viewport3D.Children.Add(new ModelVisual3D
            {
                Content = new AmbientLight(Color.FromRgb(180, 180, 180))
            });
            
            _viewport3D.Children.Add(new ModelVisual3D
            {
                Content = new DirectionalLight(Colors.White, new Vector3D(-1, -1, -1))
            });

            // Создаем координатные оси
            _viewport3D.Children.Add(new CoordinateSystemVisual3D
            {
                ArrowLengths = 50
            });

            // Создаем визуальные элементы камер и центра (исправлен рендеринг)
            _camera1Visual = new BoxVisual3D
            {
                Center = new Point3D(0, 0, 0),
                Length = 20,
                Width = 15,
                Height = 10,
                Fill = new SolidColorBrush(Color.FromRgb(70, 130, 180)), // Steel Blue
                Visible = false
            };
            _viewport3D.Children.Add(_camera1Visual);

            _camera2Visual = new BoxVisual3D
            {
                Center = new Point3D(0, 0, 0),
                Length = 20,
                Width = 15,
                Height = 10,
                Fill = new SolidColorBrush(Color.FromRgb(220, 20, 60)), // Crimson
                Visible = false
            };
            _viewport3D.Children.Add(_camera2Visual);

            _camera1LensVisual = new SphereVisual3D
            {
                Center = new Point3D(0, 0, 0),
                Radius = 3,
                Fill = Brushes.Black,
                Visible = false
            };
            _viewport3D.Children.Add(_camera1LensVisual);

            _camera2LensVisual = new SphereVisual3D
            {
                Center = new Point3D(0, 0, 0),
                Radius = 3,
                Fill = Brushes.Black,
                Visible = false
            };
            _viewport3D.Children.Add(_camera2LensVisual);

            _stereoCenterVisual = new TruncatedConeVisual3D
            {
                Origin = new Point3D(0, 0, 0),
                Height = 15,
                BaseRadius = 8,
                TopRadius = 0,
                Fill = new SolidColorBrush(Color.FromRgb(255, 215, 0)), // Gold
                Visible = false
            };
            _viewport3D.Children.Add(_stereoCenterVisual);

            _cameraBaselineVisual = new LinesVisual3D
            {
                Color = Colors.DarkSlateBlue,
                Thickness = 4
            };

            _stereoAxisVisual = new LinesVisual3D
            {
                Color = Colors.Goldenrod,
                Thickness = 3
            };

            _baselineText = new TextVisual3D
            {
                Position = new Point3D(0, 0, 0),
                Text = "Базовая линия камер",
                Foreground = Brushes.DarkSlateBlue,
                FontSize = 12
            };

            // Создаем текстовые элементы
            _camera1Text = new TextVisual3D
            {
                Position = new Point3D(10, 10, 10),
                Text = "Камера 1",
                Foreground = Brushes.Black,
                FontSize = 12
            };

            _camera2Text = new TextVisual3D
            {
                Position = new Point3D(10, 10, 10),
                Text = "Камера 2",
                Foreground = Brushes.Black,
                FontSize = 12
            };

            _centerText = new TextVisual3D
            {
                Position = new Point3D(10, 10, 10),
                Text = "Центр",
                Foreground = Brushes.Black,
                FontSize = 12
            };

            // Поверхность маркеров создаётся один раз и дальше только меняет
            // коллекции Positions/TriangleIndices. Это дешевле, чем постоянно
            // добавлять и удалять ModelVisual3D из viewport.
            _markerSurfaceMesh = new MeshGeometry3D();
            var surfaceBrush = new SolidColorBrush(Color.FromArgb(70, 30, 144, 255));
            var backSurfaceBrush = new SolidColorBrush(Color.FromArgb(45, 30, 144, 255));
            surfaceBrush.Freeze();
            backSurfaceBrush.Freeze();
            _markerSurfaceModel = new GeometryModel3D
            {
                Geometry = _markerSurfaceMesh,
                Material = new DiffuseMaterial(surfaceBrush),
                BackMaterial = new DiffuseMaterial(backSurfaceBrush)
            };
            _markerSurfaceVisual = new ModelVisual3D
            {
                Content = _markerSurfaceModel
            };
            _viewport3D.Children.Add(_markerSurfaceVisual);

            // Добавляем viewport в первую колонку
            Grid.SetColumn(_viewport3D, 0);
            mainGrid.Children.Add(_viewport3D);

            // Создаем информационную панель
            var infoBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(10),
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(5)
            };

            _infoText = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Text = "3D Сцена стереокалибровки\nКалибровка не выполнена",
                FontWeight = FontWeights.Bold
            };

            infoBorder.Child = _infoText;
            mainGrid.Children.Add(infoBorder);

            // Создаем правую панель с таблицей координат
            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(10),
                Background = new SolidColorBrush(Color.FromArgb(240, 248, 248, 248))
            };
            Grid.SetColumn(rightPanel, 1);

            // Заголовок таблицы
            var tableHeader = new TextBlock
            {
                Text = "КООРДИНАТЫ ОБЪЕКТОВ",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = Brushes.DarkBlue,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            rightPanel.Children.Add(tableHeader);

            // Создаем таблицу координат
            _coordinatesTable = new DataGrid
            {
                ItemsSource = _markersData,
                AutoGenerateColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                IsReadOnly = true,
                Background = Brushes.White,
                FontSize = 10,
                RowHeight = 25,
                ColumnHeaderHeight = 30
            };

            // Настраиваем колонки таблицы
            _coordinatesTable.Columns.Add(new DataGridTextColumn
            {
                Header = "Объект",
                Binding = new System.Windows.Data.Binding("Name"),
                Width = 100
            });
            _coordinatesTable.Columns.Add(new DataGridTextColumn
            {
                Header = "X (мм)",
                Binding = new System.Windows.Data.Binding("X"),
                Width = 50
            });
            _coordinatesTable.Columns.Add(new DataGridTextColumn
            {
                Header = "Y (мм)",
                Binding = new System.Windows.Data.Binding("Y"),
                Width = 50
            });
            _coordinatesTable.Columns.Add(new DataGridTextColumn
            {
                Header = "Z (мм)",
                Binding = new System.Windows.Data.Binding("Z"),
                Width = 50
            });
            _coordinatesTable.Columns.Add(new DataGridTextColumn
            {
                Header = "Расст.",
                Binding = new System.Windows.Data.Binding("Distance"),
                Width = 50
            });

            rightPanel.Children.Add(_coordinatesTable);
            mainGrid.Children.Add(rightPanel);

            // Устанавливаем grid как содержимое UserControl
            this.Content = mainGrid;
        }

        /// <summary>
        /// Инициализация 3D сцены
        /// </summary>
        private void InitializeScene()
        {
            try
            {
                // Настройка начального вида
                _infoText.Text = "3D Сцена стереокалибровки\nКалибровка не выполнена\n\nУправление:\n• ПКМ - поворот\n• Колесо - масштаб\n• Shift+ПКМ - панорама";
                
                // Дополнительная попытка отключить все возможные стерео эффекты после загрузки
                _viewport3D.Loaded += (sender, e) =>
                {
                    try
                    {
                        // Многоуровневая попытка исправить рендеринг через разные интервалы
                        Task.Delay(100).ContinueWith(_ => Dispatcher.Invoke(() => DisableStereoEffects()));
                        Task.Delay(500).ContinueWith(_ => Dispatcher.Invoke(() => TryAlternativeRenderMode()));
                        Task.Delay(1000).ContinueWith(_ => Dispatcher.Invoke(() => FinalRenderingFix()));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Ошибка финальной настройки рендеринга: {ex.Message}");
                    }
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка инициализации 3D сцены: {ex.Message}");
            }
        }

        /// <summary>
        /// Комплексное отключение всех возможных источников интерлейса
        /// </summary>
        private void ApplyAntiInterlaceSettings()
        {
            try
            {
                // 1. Базовые настройки viewport
                DisableViewportStereoSettings();
                
                // 2. Настройки через рефлексию на RenderHost
                ConfigureRenderHost();
                
                // 3. Настройки уровня WPF
                ConfigureWpfRenderingSettings();
                
                System.Diagnostics.Debug.WriteLine("Применены все настройки против интерлейса");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка при применении настроек против интерлейса: {ex.Message}");
            }
        }

        /// <summary>
        /// Отключение стерео настроек на уровне viewport
        /// </summary>
        private void DisableViewportStereoSettings()
        {
            try
            {
                var type = _viewport3D.GetType();
                var allProperties = type.GetProperties();
                foreach (var prop in allProperties)
                {
                    var name = prop.Name.ToLower();
                    if (name.Contains("stereo") || name.Contains("interlace") || name.Contains("interleave"))
                    {
                        try
                        {
                            if (prop.PropertyType == typeof(bool))
                            {
                                prop.SetValue(_viewport3D, false);
                                System.Diagnostics.Debug.WriteLine($"Viewport: Отключено {prop.Name}");
                            }
                            else if (prop.PropertyType.IsEnum)
                            {
                                var enumValues = Enum.GetValues(prop.PropertyType);
                                if (enumValues.Length > 0)
                                {
                                    prop.SetValue(_viewport3D, enumValues.GetValue(0));
                                    System.Diagnostics.Debug.WriteLine($"Viewport: {prop.Name} = {enumValues.GetValue(0)}");
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка в DisableViewportStereoSettings: {ex.Message}");
            }
        }

        /// <summary>
        /// Настройка RenderHost для устранения интерлейса
        /// </summary>
        private void ConfigureRenderHost()
        {
            try
            {
                var renderHostProperty = _viewport3D.GetType().GetProperty("RenderHost");
                if (renderHostProperty != null)
                {
                    var renderHost = renderHostProperty.GetValue(_viewport3D);
                    if (renderHost != null)
                    {
                        var hostType = renderHost.GetType();
                        
                        // Настраиваем RenderConfiguration
                        var configProperty = hostType.GetProperty("RenderConfiguration");
                        if (configProperty != null)
                        {
                            var config = configProperty.GetValue(renderHost);
                            if (config != null)
                            {
                                var configType = config.GetType();
                                
                                // Отключаем стерео режим
                                var stereoProperty = configType.GetProperty("StereoMode");
                                if (stereoProperty != null)
                                {
                                    stereoProperty.SetValue(config, 0); // None
                                    System.Diagnostics.Debug.WriteLine("RenderHost: StereoMode = None");
                                }
                                
                                // Отключаем MSAA (может вызывать проблемы)
                                var msaaProperty = configType.GetProperty("MSAALevel");
                                if (msaaProperty != null)
                                {
                                    msaaProperty.SetValue(config, 0); // Off
                                    System.Diagnostics.Debug.WriteLine("RenderHost: MSAA = Off");
                                }
                                
                                // Отключаем все возможные буферы
                                foreach (var prop in configType.GetProperties())
                                {
                                    var name = prop.Name.ToLower();
                                    if ((name.Contains("buffer") || name.Contains("depth") || name.Contains("stencil")) && prop.PropertyType == typeof(bool))
                                    {
                                        try
                                        {
                                            prop.SetValue(config, false);
                                            System.Diagnostics.Debug.WriteLine($"RenderHost: Отключено {prop.Name}");
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                        
                        // Отключаем интерлейс на уровне хоста
                        foreach (var prop in hostType.GetProperties())
                        {
                            var name = prop.Name.ToLower();
                            if ((name.Contains("interlace") || name.Contains("interleave") || name.Contains("stereo")) && prop.PropertyType == typeof(bool))
                            {
                                try
                                {
                                    prop.SetValue(renderHost, false);
                                    System.Diagnostics.Debug.WriteLine($"RenderHost: Отключено {prop.Name}");
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка в ConfigureRenderHost: {ex.Message}");
            }
        }

        /// <summary>
        /// Настройка рендеринга на уровне WPF
        /// </summary>
        private void ConfigureWpfRenderingSettings()
        {
            try
            {
                // Отключаем кэширование, которое может вызывать проблемы
                _viewport3D.CacheMode = null;
                
                // Устанавливаем режим рендеринга
                _viewport3D.SnapsToDevicePixels = true;
                _viewport3D.UseLayoutRounding = true;
                
                System.Diagnostics.Debug.WriteLine("WPF: Применены настройки рендеринга");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка в ConfigureWpfRenderingSettings: {ex.Message}");
            }
        }

        /// <summary>
        /// Финальное отключение стерео эффектов после загрузки
        /// </summary>
        private void DisableStereoEffects()
        {
            try
            {
                // Повторно применяем все настройки после полной загрузки
                ApplyAntiInterlaceSettings();
                
                // Принудительно обновляем рендеринг
                _viewport3D.InvalidateVisual();
                _viewport3D.UpdateLayout();
                
                System.Diagnostics.Debug.WriteLine("Финальное отключение стерео эффектов завершено");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка в финальном DisableStereoEffects: {ex.Message}");
            }
        }

        /// <summary>
        /// Попытка альтернативного режима рендеринга
        /// </summary>
        private void TryAlternativeRenderMode()
        {
            try
            {
                // Пробуем установить программный рендеринг через рефлексию
                try
                {
                    var processRenderModeProperty = typeof(System.Windows.Media.RenderOptions).GetProperty("ProcessRenderMode");
                    if (processRenderModeProperty != null)
                    {
                        processRenderModeProperty.SetValue(null, 1); // SoftwareOnly
                        System.Diagnostics.Debug.WriteLine("Установлен программный режим рендеринга");
                    }
                }
                catch
                {
                    System.Diagnostics.Debug.WriteLine("Не удалось установить программный режим рендеринга");
                }
                
                // Альтернативные настройки
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(_viewport3D, BitmapScalingMode.Linear);
                System.Windows.Media.RenderOptions.SetEdgeMode(_viewport3D, EdgeMode.Unspecified);
                
                // Принудительно пересоздаем рендер контекст
                _viewport3D.InvalidateVisual();
                
                System.Diagnostics.Debug.WriteLine("Применен альтернативный режим рендеринга");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка в TryAlternativeRenderMode: {ex.Message}");
            }
        }

        /// <summary>
        /// Финальные попытки исправить рендеринг
        /// </summary>
        private void FinalRenderingFix()
        {
            try
            {
                // Последняя попытка - полное отключение всех возможных эффектов
                _viewport3D.Effect = null;
                // BitmapEffect устарело в .NET, убираем
                
                // Принудительно убираем все трансформации которые могут влиять на рендеринг
                _viewport3D.RenderTransform = null;
                _viewport3D.LayoutTransform = null;
                
                // Финальная настройка viewport
                _viewport3D.ClipToBounds = true;
                
                // Принудительное обновление всей иерархии
                _viewport3D.InvalidateArrange();
                _viewport3D.InvalidateMeasure();
                _viewport3D.InvalidateVisual();
                _viewport3D.UpdateLayout();
                
                System.Diagnostics.Debug.WriteLine("Применены финальные исправления рендеринга");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка в FinalRenderingFix: {ex.Message}");
            }
        }

        /// <summary>
        /// Привязывает визуальный контрол к модели 3D-сцены.
        /// 
        /// При повторной привязке старый обработчик обязательно снимается, иначе
        /// один и тот же UI начал бы обновляться несколько раз на одно событие.
        /// </summary>
        /// <param name="scene3DService">Сервис 3D сцены</param>
        public void BindToService(Scene3DService scene3DService)
        {
            try
            {
                if (_scene3DService != null)
                {
                    _scene3DService.OnSceneUpdated -= UpdateScene;
                }

                _scene3DService = scene3DService;
                _scene3DService.OnSceneUpdated += UpdateScene;
                
                // Первоначальное обновление
                UpdateScene();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка привязки к сервису 3D сцены: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновляет все визуальные части сцены после события Scene3DService.
        /// 
        /// Dispatcher нужен потому, что событие может прийти не из WPF-потока.
        /// Внутри метод разделён на камеры, маркеры и инфопанель, чтобы проще
        /// ограничивать частоту тяжёлых обновлений.
        /// </summary>
        private void UpdateScene()
        {
            if (_scene3DService == null)
                return;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateCameras();
                    UpdateMarkers();
                    UpdateInfoPanel();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления 3D сцены: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновление позиций камер
        /// </summary>
        private void UpdateCameras()
        {
            if (_scene3DService == null)
                return;

            try
            {
                bool isCalibrated = _scene3DService.IsCalibrated;
                
                // Показать/скрыть камеры
                _camera1Visual.Visible = isCalibrated;
                _camera2Visual.Visible = isCalibrated;
                _camera1LensVisual.Visible = isCalibrated;
                _camera2LensVisual.Visible = isCalibrated;
                _stereoCenterVisual.Visible = isCalibrated;
                
                // Управление видимостью текстовых элементов через добавление/удаление
                if (isCalibrated)
                {
                    // Добавляем текстовые элементы если их нет
                    if (!_viewport3D.Children.Contains(_camera1Text))
                        _viewport3D.Children.Add(_camera1Text);
                    if (!_viewport3D.Children.Contains(_camera2Text))
                        _viewport3D.Children.Add(_camera2Text);
                    if (!_viewport3D.Children.Contains(_centerText))
                        _viewport3D.Children.Add(_centerText);
                    if (!_viewport3D.Children.Contains(_cameraBaselineVisual))
                        _viewport3D.Children.Add(_cameraBaselineVisual);
                    if (!_viewport3D.Children.Contains(_stereoAxisVisual))
                        _viewport3D.Children.Add(_stereoAxisVisual);
                    if (!_viewport3D.Children.Contains(_baselineText))
                        _viewport3D.Children.Add(_baselineText);

                    // Камера 1 (смещена от центра координат)
                    var cam1Pos = _scene3DService.Camera1Position;
                    _camera1Visual.Center = new Point3D(cam1Pos.X, cam1Pos.Y, cam1Pos.Z);
                    _camera1LensVisual.Center = GetCameraLensPoint(cam1Pos);
                    _camera1Text.Position = new Point3D(cam1Pos.X + 15, cam1Pos.Y + 15, cam1Pos.Z + 15);
                    _camera1Text.Text = $"Камера 1\n({cam1Pos.X:F0}, {cam1Pos.Y:F0}, {cam1Pos.Z:F0}) мм";

                    // Камера 2
                    var cam2Pos = _scene3DService.Camera2Position;
                    _camera2Visual.Center = new Point3D(cam2Pos.X, cam2Pos.Y, cam2Pos.Z);
                    _camera2LensVisual.Center = GetCameraLensPoint(cam2Pos);
                    _camera2Text.Position = new Point3D(cam2Pos.X + 15, cam2Pos.Y + 15, cam2Pos.Z + 15);
                    _camera2Text.Text = $"Камера 2\n({cam2Pos.X:F0}, {cam2Pos.Y:F0}, {cam2Pos.Z:F0}) мм";

                    // Центр стереосистемы
                    var centerPos = _scene3DService.StereoCenter;
                    _stereoCenterVisual.Origin = new Point3D(centerPos.X, centerPos.Y, centerPos.Z);
                    _centerText.Position = new Point3D(centerPos.X + 15, centerPos.Y + 15, centerPos.Z + 15);
                    _centerText.Text = $"Центр\n({centerPos.X:F0}, {centerPos.Y:F0}, {centerPos.Z:F0}) мм";

                    UpdateStereoGuides(cam1Pos, cam2Pos, centerPos);
                }
                else
                {
                    // Удаляем текстовые элементы если калибровка не выполнена
                    if (_viewport3D.Children.Contains(_camera1Text))
                        _viewport3D.Children.Remove(_camera1Text);
                    if (_viewport3D.Children.Contains(_camera2Text))
                        _viewport3D.Children.Remove(_camera2Text);
                    if (_viewport3D.Children.Contains(_centerText))
                        _viewport3D.Children.Remove(_centerText);
                    if (_viewport3D.Children.Contains(_cameraBaselineVisual))
                        _viewport3D.Children.Remove(_cameraBaselineVisual);
                    if (_viewport3D.Children.Contains(_stereoAxisVisual))
                        _viewport3D.Children.Remove(_stereoAxisVisual);
                    if (_viewport3D.Children.Contains(_baselineText))
                        _viewport3D.Children.Remove(_baselineText);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления камер: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновление направляющих между камерами и рабочей зоной маркеров
        /// </summary>
        private void UpdateStereoGuides(Point3D cam1Pos, Point3D cam2Pos, Point3D centerPos)
        {
            var farthestMarkerY = _scene3DService?.MarkerPositions.Values
                .Select(marker => marker.Y)
                .DefaultIfEmpty(350)
                .Max() ?? 350;

            var axisLength = Math.Max(350, farthestMarkerY + 80);
            var axisEnd = new Point3D(centerPos.X, axisLength, centerPos.Z);

            _cameraBaselineVisual.Points = new Point3DCollection
            {
                cam1Pos,
                cam2Pos
            };

            _stereoAxisVisual.Points = new Point3DCollection
            {
                centerPos,
                axisEnd
            };

            _baselineText.Position = new Point3D(centerPos.X, centerPos.Y - 35, centerPos.Z + 20);
            _baselineText.Text = $"Базовая линия камер\n{Distance(cam1Pos, cam2Pos):F0} мм";
        }

        /// <summary>
        /// Синхронизирует визуальные объекты маркеров с MarkerPositions сервиса.
        /// 
        /// Метод удаляет сферы для исчезнувших ID, создаёт новые сферы для новых ID,
        /// обновляет существующие, а затем при необходимости перестраивает поверхность.
        /// Таблица и сортировка обновляются не на каждый кадр, чтобы не вызывать лаги.
        /// </summary>
        private void UpdateMarkers()
        {
            if (_scene3DService == null)
                return;

            try
            {
                var currentMarkers = _scene3DService.MarkerPositions;
                var currentIds = new HashSet<int>(currentMarkers.Keys);
                var existingIds = new HashSet<int>(_markerVisuals.Keys);
                var markerSetChanged = currentIds.Count != existingIds.Count || currentIds.Any(id => !existingIds.Contains(id));
                var shouldUpdateMarkerTable = ShouldUpdateMarkerTable();

                // Удаляем маркеры, которых больше нет
                foreach (var id in existingIds)
                {
                    if (!currentIds.Contains(id))
                    {
                        RemoveMarkerVisual(id);
                    }
                }

                // Добавляем или обновляем маркеры в стабильном порядке отображения
                foreach (var marker in currentMarkers.OrderBy(m => GetMarkerDisplayIndex(m.Key)).ThenBy(m => m.Key))
                {
                    if (_markerVisuals.ContainsKey(marker.Key))
                    {
                        // Обновляем существующий маркер
                        UpdateMarkerVisual(marker.Key, marker.Value, shouldUpdateMarkerTable);
                    }
                    else
                    {
                        // Создаем новый маркер
                        CreateMarkerVisual(marker.Key, marker.Value);
                    }
                }

                UpdateMarkerSurface(currentMarkers);
                if (markerSetChanged || shouldUpdateMarkerTable)
                {
                    SortMarkersTable();
                }

                if (!_scene3DService.IsCalibrated)
                {
                    ClearMarkerGuideLines();
                    ClearMarkerSurface();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления маркеров: {ex.Message}");
            }
        }

        /// <summary>
        /// Создаёт сферу, подпись, направляющие линии и строку таблицы для нового маркера.
        /// 
        /// Сфера остаётся основным визуальным обозначением маркера даже при включённой
        /// поверхности. Поверхность является дополнительным слоем и не заменяет точки.
        /// </summary>
        private void CreateMarkerVisual(int markerId, Point3D position)
        {
            try
            {
                // Создаем сферу для маркера
                var markerSphere = new SphereVisual3D
                {
                    Center = position,
                    Radius = 5,
                    Fill = new SolidColorBrush(GetMarkerColor(markerId))
                };

                var displayIndex = GetMarkerDisplayIndex(markerId);
                var markerName = GetMarkerName(markerId);
                var markerLabel = GetMarkerLabel(markerId);

                // Создаем текст для маркера
                var markerText = new TextVisual3D
                {
                    Position = new Point3D(position.X + 8, position.Y + 8, position.Z + 8),
                    Text = $"{markerLabel}\n({position.X:F0}, {position.Y:F0}, {position.Z:F0}) мм",
                    Foreground = Brushes.Black,
                    FontSize = 10
                };

                // Добавляем в сцену
                _viewport3D.Children.Add(markerSphere);
                _viewport3D.Children.Add(markerText);
                CreateOrUpdateMarkerGuideLine(markerId, position);

                // Сохраняем ссылки
                _markerVisuals[markerId] = markerSphere;
                _markerTexts[markerId] = markerText;
                _markerTextCache[markerId] = markerText.Text;
                
                // Добавляем в таблицу координат
                var distance = Math.Sqrt(position.X * position.X + position.Y * position.Y + position.Z * position.Z);
                var newMarker = new MarkerCoordinate
                {
                    ID = markerId,
                    DisplayIndex = displayIndex,
                    Name = markerLabel,
                    X = position.X.ToString("F0"),
                    Y = position.Y.ToString("F0"),
                    Z = position.Z.ToString("F0"),
                    Distance = distance.ToString("F1")
                };
                AddMarkerDataSorted(newMarker);
                
                System.Diagnostics.Debug.WriteLine($"3D: Создан {markerName} (ArUco ID {markerId})");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка создания маркера {markerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновляет положение существующего маркера.
        /// 
        /// Координаты сферы обновляются каждый кадр, потому что это лёгкая операция.
        /// Текст и таблица обновляются только при изменении округлённых значений или
        /// по throttle-флагу, чтобы WPF не перерисовывал тяжёлые текстовые элементы
        /// слишком часто.
        /// </summary>
        private void UpdateMarkerVisual(int markerId, Point3D position, bool updateTable)
        {
            try
            {
                if (_markerVisuals.TryGetValue(markerId, out var visual) && 
                    _markerTexts.TryGetValue(markerId, out var text))
                {
                    var displayIndex = GetMarkerDisplayIndex(markerId);
                    var markerLabel = GetMarkerLabel(markerId);

                    visual.Center = position;
                    text.Position = new Point3D(position.X + 8, position.Y + 8, position.Z + 8);
                    var markerText = $"{markerLabel}\n({position.X:F0}, {position.Y:F0}, {position.Z:F0}) мм";
                    if (!_markerTextCache.TryGetValue(markerId, out var previousText) || previousText != markerText)
                    {
                        text.Text = markerText;
                        _markerTextCache[markerId] = markerText;
                    }

                    CreateOrUpdateMarkerGuideLine(markerId, position);
                    
                    // Обновляем данные в таблице
                    if (updateTable)
                    {
                        var markerData = _markersData.FirstOrDefault(m => m.ID == markerId);
                        if (markerData != null)
                        {
                            var distance = Math.Sqrt(position.X * position.X + position.Y * position.Y + position.Z * position.Z);
                            markerData.DisplayIndex = displayIndex;
                            markerData.Name = markerLabel;
                            markerData.X = position.X.ToString("F0");
                            markerData.Y = position.Y.ToString("F0");
                            markerData.Z = position.Z.ToString("F0");
                            markerData.Distance = distance.ToString("F1");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления маркера {markerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Удаление визуального представления маркера
        /// </summary>
        private void RemoveMarkerVisual(int markerId)
        {
            try
            {
                if (_markerVisuals.TryGetValue(markerId, out var visual))
                {
                    _viewport3D.Children.Remove(visual);
                    _markerVisuals.Remove(markerId);
                }

                if (_markerTexts.TryGetValue(markerId, out var text))
                {
                    _viewport3D.Children.Remove(text);
                    _markerTexts.Remove(markerId);
                    _markerTextCache.Remove(markerId);
                }

                RemoveMarkerGuideLine(markerId);

                // Удаляем из таблицы (только реальные маркеры, не системные объекты)
                var markerData = _markersData.FirstOrDefault(m => m.ID == markerId && m.ID >= 0);
                if (markerData != null)
                {
                    _markersData.Remove(markerData);
                    System.Diagnostics.Debug.WriteLine($"3D: Удален маркер {markerId} из таблицы");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка удаления маркера {markerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Создание или обновление тонких линий от камер к маркеру.
        /// Они показывают, что маркер находится в общей зоне обзора двух камер.
        /// </summary>
        private void CreateOrUpdateMarkerGuideLine(int markerId, Point3D markerPosition)
        {
            if (_scene3DService == null || !_scene3DService.IsCalibrated)
                return;

            if (!_markerGuideLines.TryGetValue(markerId, out var guideLine))
            {
                guideLine = new LinesVisual3D
                {
                    Color = Colors.Gray,
                    Thickness = 1
                };

                _markerGuideLines[markerId] = guideLine;
                _viewport3D.Children.Add(guideLine);
            }
            else if (!_viewport3D.Children.Contains(guideLine))
            {
                _viewport3D.Children.Add(guideLine);
            }

            var cam1Pos = _scene3DService.Camera1Position;
            var cam2Pos = _scene3DService.Camera2Position;
            var cam1GuideStart = GetCameraLensPoint(cam1Pos);
            var cam2GuideStart = GetCameraLensPoint(cam2Pos);

            guideLine.Points = new Point3DCollection
            {
                cam1GuideStart,
                markerPosition,
                cam2GuideStart,
                markerPosition
            };
        }

        private static Point3D GetCameraLensPoint(Point3D cameraCenter)
        {
            return new Point3D(cameraCenter.X, cameraCenter.Y + CameraLensOffset, cameraCenter.Z);
        }

        private void RemoveMarkerGuideLine(int markerId)
        {
            if (_markerGuideLines.TryGetValue(markerId, out var guideLine))
            {
                _viewport3D.Children.Remove(guideLine);
                _markerGuideLines.Remove(markerId);
            }
        }

        private void ClearMarkerGuideLines()
        {
            foreach (var guideLine in _markerGuideLines.Values)
            {
                _viewport3D.Children.Remove(guideLine);
            }

            _markerGuideLines.Clear();
        }

        /// <summary>
        /// Ограничивает частоту обновления WPF DataGrid.
        /// DataGrid заметно тяжелее сфер в HelixViewport3D, поэтому частые
        /// PropertyChanged на каждую координату могут давать лаги.
        /// </summary>
        private bool ShouldUpdateMarkerTable()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastMarkerTableUpdateTime).TotalMilliseconds < MarkerTableUpdateIntervalMs)
                return false;

            _lastMarkerTableUpdateTime = now;
            return true;
        }

        /// <summary>
        /// Обновляет полупрозрачную поверхность, проходящую через текущие маркеры.
        /// 
        /// Поверхность строится только при наличии минимум трёх точек и только
        /// когда позиции заметно изменились. Это компромисс: пользователь видит
        /// деформацию таблички, но алгоритм триангуляции сетки не запускается
        /// на каждый кадр видеопотока.
        /// </summary>
        private void UpdateMarkerSurface(IReadOnlyDictionary<int, Point3D> currentMarkers)
        {
            if (currentMarkers.Count < 3)
            {
                ClearMarkerSurface();
                return;
            }

            if (!ShouldUpdateMarkerSurface(currentMarkers))
                return;

            var markerPoints = currentMarkers
                .OrderBy(marker => GetMarkerDisplayIndex(marker.Key))
                .ThenBy(marker => marker.Key)
                .Take(MaxSurfaceMarkers)
                .Select(marker => marker.Value)
                .ToList();

            var mesh = BuildMarkerSurfaceMesh(markerPoints);
            _markerSurfaceMesh.Positions = mesh.Positions;
            _markerSurfaceMesh.TriangleIndices = mesh.TriangleIndices;

            _lastSurfaceMarkerSnapshot.Clear();
            foreach (var marker in currentMarkers)
            {
                _lastSurfaceMarkerSnapshot[marker.Key] = marker.Value;
            }

            _lastSurfaceUpdateTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Проверяет, нужно ли реально перестраивать mesh поверхности.
        /// 
        /// Обновление пропускается, если набор маркеров тот же и все точки
        /// сдвинулись меньше порога. Дополнительно действует временной интервал,
        /// чтобы поверхность не потребляла много ресурсов при небольшом шуме.
        /// </summary>
        private bool ShouldUpdateMarkerSurface(IReadOnlyDictionary<int, Point3D> currentMarkers)
        {
            if (_lastSurfaceMarkerSnapshot.Count != currentMarkers.Count)
                return true;

            bool hasSignificantChange = false;
            foreach (var marker in currentMarkers)
            {
                if (!_lastSurfaceMarkerSnapshot.TryGetValue(marker.Key, out var previousPosition))
                    return true;

                if (Distance(previousPosition, marker.Value) >= SurfaceUpdateThresholdMm)
                {
                    hasSignificantChange = true;
                    break;
                }
            }

            if (!hasSignificantChange)
                return false;

            return (DateTime.UtcNow - _lastSurfaceUpdateTime).TotalMilliseconds >= SurfaceUpdateIntervalMs;
        }

        private void ClearMarkerSurface()
        {
            if (_markerSurfaceMesh.Positions.Count == 0 && _markerSurfaceMesh.TriangleIndices.Count == 0)
                return;

            _markerSurfaceMesh.Positions = new Point3DCollection();
            _markerSurfaceMesh.TriangleIndices = new Int32Collection();
            _lastSurfaceMarkerSnapshot.Clear();
        }

        /// <summary>
        /// Строит MeshGeometry3D по набору 3D-точек маркеров.
        /// 
        /// Триангуляция выполняется не в 3D напрямую, а в 2D-проекции на две оси
        /// с наибольшим разбросом. Это позволяет корректнее работать и с почти
        /// вертикальной табличкой, где глубина меняется мало, а точки лежат в
        /// плоскости X/Z или X/Y.
        /// </summary>
        private static MeshGeometry3D BuildMarkerSurfaceMesh(IReadOnlyList<Point3D> points3D)
        {
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection(points3D),
                TriangleIndices = new Int32Collection()
            };

            if (points3D.Count < 3)
                return mesh;

            var projectionAxes = ChooseProjectionAxes(points3D);
            var projectedPoints = points3D
                .Select(point => new SurfacePoint(
                    GetAxisValue(point, projectionAxes.First),
                    GetAxisValue(point, projectionAxes.Second)))
                .ToList();

            var triangles = BuildDelaunayTriangles(projectedPoints);
            foreach (var triangle in triangles)
            {
                mesh.TriangleIndices.Add(triangle.A);
                mesh.TriangleIndices.Add(triangle.B);
                mesh.TriangleIndices.Add(triangle.C);
            }

            return mesh;
        }

        /// <summary>
        /// Строит 2D Delaunay-подобную триангуляцию методом Bowyer-Watson.
        /// 
        /// Алгоритм начинает с большого "super-triangle", добавляет точки по одной,
        /// удаляет треугольники, чьи описанные окружности содержат новую точку,
        /// и сшивает образовавшуюся границу новыми треугольниками. В конце
        /// треугольники с искусственными вершинами отбрасываются.
        /// </summary>
        private static List<SurfaceTriangle> BuildDelaunayTriangles(IReadOnlyList<SurfacePoint> sourcePoints)
        {
            var points = sourcePoints.ToList();
            var bounds = GetSurfaceBounds(points);
            var delta = Math.Max(bounds.MaxU - bounds.MinU, bounds.MaxV - bounds.MinV);
            if (delta <= 1e-6)
                return new List<SurfaceTriangle>();

            var midU = (bounds.MinU + bounds.MaxU) / 2.0;
            var midV = (bounds.MinV + bounds.MaxV) / 2.0;
            int firstSuperIndex = points.Count;
            points.Add(new SurfacePoint(midU - 20 * delta, midV - delta));
            points.Add(new SurfacePoint(midU, midV + 20 * delta));
            points.Add(new SurfacePoint(midU + 20 * delta, midV - delta));

            var triangles = new List<SurfaceTriangle>
            {
                new SurfaceTriangle(firstSuperIndex, firstSuperIndex + 1, firstSuperIndex + 2)
            };

            for (int pointIndex = 0; pointIndex < sourcePoints.Count; pointIndex++)
            {
                var point = points[pointIndex];
                var badTriangles = triangles
                    .Where(triangle => CircumcircleContains(points, triangle, point))
                    .ToList();

                var boundaryEdges = new List<SurfaceEdge>();
                foreach (var triangle in badTriangles)
                {
                    AddOrRemoveBoundaryEdge(boundaryEdges, new SurfaceEdge(triangle.A, triangle.B));
                    AddOrRemoveBoundaryEdge(boundaryEdges, new SurfaceEdge(triangle.B, triangle.C));
                    AddOrRemoveBoundaryEdge(boundaryEdges, new SurfaceEdge(triangle.C, triangle.A));
                }

                foreach (var triangle in badTriangles)
                {
                    triangles.Remove(triangle);
                }

                foreach (var edge in boundaryEdges)
                {
                    var triangle = new SurfaceTriangle(edge.A, edge.B, pointIndex);
                    if (Math.Abs(GetTriangleArea(points, triangle)) < MinTriangleArea)
                        continue;

                    if (GetTriangleArea(points, triangle) < 0)
                    {
                        triangle = new SurfaceTriangle(edge.B, edge.A, pointIndex);
                    }

                    triangles.Add(triangle);
                }
            }

            return triangles
                .Where(triangle => triangle.A < firstSuperIndex &&
                                   triangle.B < firstSuperIndex &&
                                   triangle.C < firstSuperIndex)
                .ToList();
        }

        private static void AddOrRemoveBoundaryEdge(List<SurfaceEdge> edges, SurfaceEdge edge)
        {
            var existingIndex = edges.FindIndex(existing => existing.Equals(edge));
            if (existingIndex >= 0)
            {
                edges.RemoveAt(existingIndex);
            }
            else
            {
                edges.Add(edge);
            }
        }

        private static bool CircumcircleContains(IReadOnlyList<SurfacePoint> points, SurfaceTriangle triangle, SurfacePoint point)
        {
            var a = points[triangle.A];
            var b = points[triangle.B];
            var c = points[triangle.C];

            var ax = a.U - point.U;
            var ay = a.V - point.V;
            var bx = b.U - point.U;
            var by = b.V - point.V;
            var cx = c.U - point.U;
            var cy = c.V - point.V;

            var determinant =
                (ax * ax + ay * ay) * (bx * cy - cx * by) -
                (bx * bx + by * by) * (ax * cy - cx * ay) +
                (cx * cx + cy * cy) * (ax * by - bx * ay);

            var orientation = GetTriangleArea(points, triangle);
            return orientation > 0
                ? determinant > 1e-6
                : determinant < -1e-6;
        }

        private static double GetTriangleArea(IReadOnlyList<SurfacePoint> points, SurfaceTriangle triangle)
        {
            var a = points[triangle.A];
            var b = points[triangle.B];
            var c = points[triangle.C];

            return (b.U - a.U) * (c.V - a.V) - (b.V - a.V) * (c.U - a.U);
        }

        private static SurfaceBounds GetSurfaceBounds(IReadOnlyList<SurfacePoint> points)
        {
            return new SurfaceBounds(
                points.Min(point => point.U),
                points.Max(point => point.U),
                points.Min(point => point.V),
                points.Max(point => point.V));
        }

        private static (SurfaceAxis First, SurfaceAxis Second) ChooseProjectionAxes(IReadOnlyList<Point3D> points)
        {
            var ranges = new[]
            {
                (Axis: SurfaceAxis.X, Range: points.Max(point => point.X) - points.Min(point => point.X)),
                (Axis: SurfaceAxis.Y, Range: points.Max(point => point.Y) - points.Min(point => point.Y)),
                (Axis: SurfaceAxis.Z, Range: points.Max(point => point.Z) - points.Min(point => point.Z))
            };

            var selectedAxes = ranges
                .OrderByDescending(range => range.Range)
                .Take(2)
                .Select(range => range.Axis)
                .ToArray();

            return (selectedAxes[0], selectedAxes[1]);
        }

        private static double GetAxisValue(Point3D point, SurfaceAxis axis)
        {
            return axis switch
            {
                SurfaceAxis.X => point.X,
                SurfaceAxis.Y => point.Y,
                SurfaceAxis.Z => point.Z,
                _ => point.X
            };
        }

        /// <summary>
        /// Получение цвета для маркера
        /// </summary>
        private Color GetMarkerColor(int markerId)
        {
            // Разные цвета для разных маркеров
            var colors = new Color[]
            {
                Colors.Green, Colors.Orange, Colors.Cyan, Colors.Magenta,
                Colors.Purple, Colors.Lime, Colors.Pink, Colors.Gold
            };
            
            return colors[markerId % colors.Length];
        }

        private int GetMarkerDisplayIndex(int markerId)
        {
            if (_scene3DService != null &&
                _scene3DService.MarkerDisplayIndices.TryGetValue(markerId, out var displayIndex))
            {
                return displayIndex;
            }

            return int.MaxValue;
        }

        private string GetMarkerName(int markerId)
        {
            return _scene3DService?.GetMarkerDisplayName(markerId) ?? "Маркер ?";
        }

        private string GetMarkerLabel(int markerId)
        {
            return $"{GetMarkerName(markerId)} (ID {markerId})";
        }

        private void AddMarkerDataSorted(MarkerCoordinate marker)
        {
            var insertIndex = 0;
            while (insertIndex < _markersData.Count &&
                   (_markersData[insertIndex].DisplayIndex < marker.DisplayIndex ||
                    (_markersData[insertIndex].DisplayIndex == marker.DisplayIndex &&
                     _markersData[insertIndex].ID < marker.ID)))
            {
                insertIndex++;
            }

            _markersData.Insert(insertIndex, marker);
        }

        private void SortMarkersTable()
        {
            for (int targetIndex = 0; targetIndex < _markersData.Count; targetIndex++)
            {
                var markerAtTarget = _markersData
                    .Skip(targetIndex)
                    .OrderBy(m => m.DisplayIndex)
                    .ThenBy(m => m.ID)
                    .FirstOrDefault();

                if (markerAtTarget != null)
                {
                    var currentIndex = _markersData.IndexOf(markerAtTarget);
                    if (currentIndex != targetIndex)
                    {
                        _markersData.Move(currentIndex, targetIndex);
                    }
                }
            }
        }

        private static double Distance(Point3D a, Point3D b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;

            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
        /// Обновление информационной панели
        /// </summary>
        private void UpdateInfoPanel()
        {
            if (_scene3DService == null)
                return;

            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastInfoPanelUpdateTime).TotalMilliseconds < InfoPanelUpdateIntervalMs)
                    return;

                _lastInfoPanelUpdateTime = now;

                string info = "3D Сцена стереокалибровки\n\n";
                
                if (_scene3DService.IsCalibrated)
                {
                    info += $"✓ Калибровка выполнена\n\n";
                    
                    var cam1 = _scene3DService.Camera1Position;
                    var cam2 = _scene3DService.Camera2Position;
                    var center = _scene3DService.StereoCenter;
                    
                    info += $"📹 Камера 1: ({cam1.X:F0}, {cam1.Y:F0}, {cam1.Z:F0}) мм\n";
                    info += $"📹 Камера 2: ({cam2.X:F0}, {cam2.Y:F0}, {cam2.Z:F0}) мм\n";
                    info += $"🎯 Центр: ({center.X:F0}, {center.Y:F0}, {center.Z:F0}) мм\n";
                    info += $"🎯 Маркеров: {_scene3DService.MarkerPositions.Count}\n\n";
                }
                else
                {
                    info += "❌ Калибровка не выполнена\n\n";
                }
                
                info += "Управление:\n";
                info += "• ПКМ - поворот\n";
                info += "• Колесо - масштаб\n";
                info += "• Shift+ПКМ - панорама";
                
                _infoText.Text = info;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обновления информационной панели: {ex.Message}");
            }
        }



        /// <summary>
        /// Очистка ресурсов
        /// </summary>
        public void Cleanup()
        {
            try
            {
                if (_scene3DService != null)
                {
                    _scene3DService.OnSceneUpdated -= UpdateScene;
                }

                // Очищаем маркеры
                foreach (var visual in _markerVisuals.Values)
                {
                    _viewport3D.Children.Remove(visual);
                }
                _markerVisuals.Clear();

                foreach (var text in _markerTexts.Values)
                {
                    _viewport3D.Children.Remove(text);
                }
                _markerTexts.Clear();
                _markerTextCache.Clear();

                ClearMarkerGuideLines();
                ClearMarkerSurface();

                // Очищаем таблицу координат
                _markersData?.Clear();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка очистки 3D сцены: {ex.Message}");
            }
        }

        private enum SurfaceAxis
        {
            X,
            Y,
            Z
        }

        private readonly struct SurfacePoint
        {
            public SurfacePoint(double u, double v)
            {
                U = u;
                V = v;
            }

            public double U { get; }
            public double V { get; }
        }

        private readonly struct SurfaceBounds
        {
            public SurfaceBounds(double minU, double maxU, double minV, double maxV)
            {
                MinU = minU;
                MaxU = maxU;
                MinV = minV;
                MaxV = maxV;
            }

            public double MinU { get; }
            public double MaxU { get; }
            public double MinV { get; }
            public double MaxV { get; }
        }

        private readonly struct SurfaceTriangle
        {
            public SurfaceTriangle(int a, int b, int c)
            {
                A = a;
                B = b;
                C = c;
            }

            public int A { get; }
            public int B { get; }
            public int C { get; }
        }

        private readonly struct SurfaceEdge : IEquatable<SurfaceEdge>
        {
            public SurfaceEdge(int a, int b)
            {
                A = a;
                B = b;
            }

            public int A { get; }
            public int B { get; }

            public bool Equals(SurfaceEdge other)
            {
                return (A == other.A && B == other.B) ||
                       (A == other.B && B == other.A);
            }

            public override bool Equals(object? obj)
            {
                return obj is SurfaceEdge other && Equals(other);
            }

            public override int GetHashCode()
            {
                var min = Math.Min(A, B);
                var max = Math.Max(A, B);
                return HashCode.Combine(min, max);
            }
        }
    }
}
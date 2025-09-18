using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MvCameraControl;
using ZXing;
using ZXing.Common;
using System.Drawing;// для Bitmap
using ZXing.Windows.Compatibility;
using Brushes = System.Windows.Media.Brushes;
using PixelFormat = System.Drawing.Imaging.PixelFormat; // Явное указание
using System.Collections.Generic;
using System.Linq.Expressions; // чтобы работал List<string>


namespace WpfApp1
{
    // В этом варианте, мы используем 
    public partial class MainWindow : Window
    {
        private readonly DeviceTLayerType enumTLayerType = DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvUsbDevice;
        private List<IDeviceInfo> deviceInfoList = new();
        private IDevice? device = null;
        private bool isGrabbing = false;
        private Thread? receiveThread = null;
        private readonly DispatcherTimer fpsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private int frameCounter = 0;
        private BarcodeReader<Bitmap> barcodeReader = null!;
        private DateTime lastAnalysisTime = DateTime.MinValue;
        private readonly TimeSpan analysisInterval = TimeSpan.FromMilliseconds(500);
        private YoloDetector? yoloDetector;
        private SimpleCnnPredictor? cnnClassifier;
        private string photoSavePath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        private DispatcherTimer? paramTimer;
        private bool isAutoMode = false;


        public MainWindow()
        {
            InitializeComponent();
            InitializeBarcodeReader();
            InitializeFpsTimer();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void InitializeBarcodeReader()
        {
            barcodeReader = new BarcodeReader<Bitmap>(
             (bitmap) => new ZXing.Windows.Compatibility.BitmapLuminanceSource(bitmap))
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    TryInverted = true,
                    TryHarder = true,
                    PossibleFormats = new List<BarcodeFormat>
                    {
                        BarcodeFormat.CODE_128,
                        BarcodeFormat.QR_CODE,
                        BarcodeFormat.EAN_13
                    }
                }
            };
        }

        private void InitializeFpsTimer()
        {
            fpsTimer.Tick += (s, e) =>
            {
                txtFps.Text = $"FPS: {frameCounter}";
                frameCounter = 0;
            };
            fpsTimer.Start();
        }

        //Определяем модели
        private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                SDKSystem.Initialize();
                RefreshDeviceList();

                // === YOLOv5 (детектор упаковок) ===
                // путь к твоему best.onnx
                yoloDetector = new YoloDetector("Assets/best.onnx");

                // === SimpleCNN (классификатор типа продукции) ===
                try
                {
                    var onnxPath = "Assets/simple_cnn.onnx";             // твоя CNN
                    var classesPath = "Assets/classes.json";      // список классов
                    cnnClassifier = new SimpleCnnPredictor(onnxPath, File.Exists(classesPath) ? classesPath : null, 224, 224);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Ошибка инициализации CNN {ex.Message}");
                    cnnClassifier = null;
                }

                ////Это не моя модель
                //yoloDetector = new YoloDetector("Assets/yolov5/yolov5s.onnx");
                //// А Это моя модель(если classes.join лежит рядом, он будет загружен автоматически)
                //try
                //{
                //    var onnxPath = "../../../product_classifier/final_model.onnx";
                //    var classesPath = "../../../product_classifier/classes.json";
                //    cnnClassifier = new SimpleCnnPredictor(onnxPath, File.Exists(classesPath) ? classesPath : null, 224, 224);
                //}
                //catch (Exception ex)
                //{
                //    Debug.WriteLine($" Ошибка инициализации CNN {ex.Message}");
                //    cnnClassifier = null;
                //}
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка инициализации: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #region Настройка камер
        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            StopGrabbing();
            StopParamTimer();
            device?.Close();
            device = null;
            SDKSystem.Finalize();
        }

        private void RefreshDeviceList()
        {
            cmbCameras.Items.Clear();
            int nRet = DeviceEnumerator.EnumDevices(enumTLayerType, out deviceInfoList);

            if (nRet != MvError.MV_OK || deviceInfoList.Count == 0)
            {
                txtStatus.Text = "Камеры не обнаружены";
                return;
            }

            foreach (var deviceInfo in deviceInfoList)
            {
                cmbCameras.Items.Add($"{deviceInfo.ModelName} (SN: {deviceInfo.SerialNumber})");
            }

            cmbCameras.SelectedIndex = 0;
            txtStatus.Text = $"Найдено {deviceInfoList.Count} камер";
        }

        private void RefreshExposureAndGainFromCamera()
        {
            if (device == null) return;

            try
            {
                // --- Выдержка ---
                if (device.Parameters.GetFloatValue("ExposureTime", out MvCameraControl.IFloatValue expInfo) == MvError.MV_OK)
                {
                    double exposureUs = expInfo.CurValue;
                    txtExposureValue.Text = $"Выдержка: {exposureUs:0} мкс ({exposureUs / 1000.0:F2} мс)";
                    txtExposureRange.Text = $"Диапазон: {expInfo.Min:0} – {expInfo.Max:0} мкс";
                }
                else
                {
                    txtExposureValue.Text = "Не удалось прочитать выдержку";
                    txtExposureRange.Text = string.Empty;
                }

                // --- Усиление ---
                if (device.Parameters.GetFloatValue("Gain", out MvCameraControl.IFloatValue gainInfo) == MvError.MV_OK)
                {
                    double gainDb = gainInfo.CurValue;
                    txtGainValue.Text = $"Усиление: {gainDb:0.0} dB";
                    txtGainRange.Text = $"Диапазон: {gainInfo.Min:0.0} – {gainInfo.Max:0.0} dB";
                }
                else
                {
                    txtGainValue.Text = "Не удалось прочитать усиление";
                    txtGainRange.Text = string.Empty;
                }
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Ошибка чтения параметров: {ex.Message}";
            }
        }


        private void StartParamTimer()
        {
            if (paramTimer == null)
            {
                paramTimer = new DispatcherTimer();
                paramTimer.Interval = TimeSpan.FromSeconds(1); // раз в секунду
                paramTimer.Tick += (s, e) => RefreshExposureAndGainFromCamera();
            }

            paramTimer.Start();
        }

        private void StopParamTimer()
        {
            paramTimer?.Stop();
        }



        // Подключение/отключение
        private void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (device != null)
            {
                // Отключение от камеры
                StopGrabbing();
                StopParamTimer();
                device.Close();
                device = null;

                btnStart.IsEnabled = false;
                btnStop.IsEnabled = false;
                txtStatus.Text = "Отключение";
                btnConnect.Content = "Подключиться";
                return;
            }

            //подключение 
            if (cmbCameras.SelectedIndex == -1)
            {
                MessageBox.Show("Выберите камеру из списка", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                device?.Close();
                device = null;

                txtStatus.Text = $"Подключение к камере {deviceInfoList[cmbCameras.SelectedIndex].ModelName}...";
                device = DeviceFactory.CreateDevice(deviceInfoList[cmbCameras.SelectedIndex]);

                if (device.Open() != MvError.MV_OK)
                {
                    txtStatus.Text = "Ошибка подключения";
                    return;
                }

                ConfigureCameraParameters();
                btnStart.IsEnabled = true;
                btnConnect.Content = "Отключиться";
                txtStatus.Text = $"Камера {deviceInfoList[cmbCameras.SelectedIndex].ModelName} подключена";
                RefreshExposureFromCamera();

                RefreshExposureAndGainFromCamera(); // сразу показать текущее значение
                StartParamTimer(); // запуск автообновления раз в секунду

            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка подключения: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        //Настройка изображения
        private void ConfigureCameraParameters()
        {
            SetParameter("AcquisitionMode", "Continuous");
            SetParameter("TriggerMode", "Off");
            SetParameter("ExposureTime", 50000.0f); //Время экспозиции
            SetParameter("Width", 1920);
            SetParameter("Height", 1480);
            SetParameter("Gain", 10.0f); //Яркость
            SetParameter("PixelFormat", "BayerRG8");
            RefreshExposureFromCamera();
            RefreshExposureAndGainFromCamera();
        }

        private void SetParameter(string parameterName, object value)
        {
            int result = value switch
            {
                string s => device!.Parameters.SetEnumValueByString(parameterName, s),
                float f => device!.Parameters.SetFloatValue(parameterName, f),
                int i => device!.Parameters.SetIntValue(parameterName, i),
                _ => throw new ArgumentException($"Неизвестный тип параметра {parameterName}")
            };

            if (result != MvError.MV_OK)
                throw new Exception($"Ошибка установки параметра {parameterName}: {result:X}");
        }

        // Чтение фактической выдержки (ExposureTime) из камеры и вывод в UI
        private void RefreshExposureFromCamera()
        {
            if (device == null) return;

            try
            {
                int ret = device.Parameters.GetFloatValue("ExposureTime", out MvCameraControl.IFloatValue expNode);
                if (ret == MvError.MV_OK)
                {
                    // В MvCameraControl экспозиция задаётся в микросекундах
                    double exposureUs = expNode.CurValue;
                    txtExposureValue.Text = $"{exposureUs:0} мкс ({exposureUs / 1000.0:F2} мс)";
                }
                else
                {
                    txtStatus.Text = $"Не удалось прочитать выдержку: 0x{ret:X}";
                }
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Ошибка чтения выдержки: {ex.Message}";
            }
        }


        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            StartGrabbing();
            StartParamTimer();
            btnStart.IsEnabled = false;
            btnStop.IsEnabled = true;
            txtStatus.Text = "Захват изображения запущен";
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            StopGrabbing();
            StopParamTimer();
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
            txtStatus.Text = "Захват изображения остановлен";
        }

        private void StartGrabbing()
        {
            if (isGrabbing) return;

            isGrabbing = true;
            receiveThread = new Thread(ReceiveThreadProcess) { IsBackground = true };
            receiveThread.Start();
        }

        private void StopGrabbing()
        {
            if (!isGrabbing) return;

            isGrabbing = false;
            device?.StreamGrabber.StopGrabbing();

            if (receiveThread != null && receiveThread.IsAlive)
            {
                if (!receiveThread.Join(1000)) receiveThread.Interrupt();
            }
        }

        private void ReceiveThreadProcess()
        {
            if (device == null) return;

            int result = device.StreamGrabber.StartGrabbing();
            if (result != MvError.MV_OK)
            {
                Dispatcher.Invoke(() => txtStatus.Text = $"Ошибка запуска захвата: {result:X}");
                return;
            }

            while (isGrabbing)
            {
                try
                {
                    int ret = device.StreamGrabber.GetImageBuffer(1000, out IFrameOut frameOut);
                    if (ret == MvError.MV_OK)
                    {
                        ProcessFrame(frameOut);
                        Interlocked.Increment(ref frameCounter);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Ошибка потока: {ex.Message}");
                }
            }

            device.StreamGrabber.StopGrabbing();
        }
        #endregion

        #region Работа с изобржением
        private void ProcessFrame(IFrameOut frameOut)
        {
            if (device == null) return;

            try
            {
                // Возвращаемся к временному файлу как временное решение
                string tempFile = Path.GetTempFileName() + ".bmp";
                try
                {
                    var formatInfo = new ImageFormatInfo { FormatType = ImageFormatType.Bmp };
                    int result = device.ImageSaver.SaveImageToFile(tempFile, frameOut.Image, formatInfo, CFAMethod.Equilibrated);

                    if (result == MvError.MV_OK)
                    {
                        var bitmap = LoadBitmapFromFile(tempFile);
                        if (bitmap != null)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                imgCameraView.Source = bitmap;
                                if (chkAnalyze.IsChecked == true &&
                                    DateTime.Now - lastAnalysisTime > analysisInterval)
                                {
                                    var analysis = AnalyzeImage(bitmap);
                                    UpdateUI(analysis.barcode, analysis.brightness, analysis.dominantColor, analysis.objects, analysis.productType, analysis.productConfidence, analysis.weightGuess);
                                    lastAnalysisTime = DateTime.Now;
                                }
                            });
                        }
                    }
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                    device.StreamGrabber.FreeImageBuffer(frameOut);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка обработки кадра: {ex.Message}");
            }
        }

        private BitmapImage? LoadBitmapFromFile(string path)
        {
            try
            {
                var image = new BitmapImage();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        private (string barcode, string brightness, string dominantColor,
         List<string> objects, string productType, float productConfidence,
         string weightGuess) AnalyzeImage(BitmapSource bitmapSource)
        {
            try
            {
                var bmp = BitmapFromSource(bitmapSource);
                if (bmp == null)
                    return ("Ошибка", "Ошибка", "Ошибка", new(), "Ошибка", 0f, "Не определено");

                var result = barcodeReader.Decode(bmp);
                var bribrightness = AnalyzeBrightnessFast(bmp);
                var color = GetDominantColorFast(bmp);

                var objects = new List<string>();
                string productType = "Тип продукции не определён";
                float confidence = 0f;
                string weightGuess = "Не определено";

                // 1) Детекция упаковок YOLO
                var detections = yoloDetector?.DetectObjectsWithBoxes(bmp) ?? new List<YoloDetection>();

                foreach (var det in detections)
                {
                    objects.Add(det.Label);

                    // === ВЫЧИСЛЕНИЕ ГРАММОВКИ ПО РАЗМЕРУ ===
                    int h = det.Bounds.Height;
                    int w = det.Bounds.Width;

                    //if (h < 300) weightGuess = $"{h}";
                    //else if (h < 450) weightGuess = $"{h}";
                    //else if (h < 600) weightGuess = $"{h}";
                    //else weightGuess = $"{h}";

                    if (h < 300) weightGuess = "50 г";
                    else if (h < 950) weightGuess = "70 г";
                    else if (h < 1300) weightGuess = "100 г";
                    else weightGuess = "250 г";

                    // Классификация CNN (тип продукции)
                    var crop = bmp.Clone(det.Bounds, bmp.PixelFormat);

                    if (cnnClassifier != null)
                    {
                        try
                        {
                            var (label, conf) = cnnClassifier.PredictWithConfidence(crop);
                            productType = label;
                            confidence = conf;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Ошибка классификации CNN: {ex}");
                        }
                    }
                }

                return (result?.Text ?? "Не найден", bribrightness, color, objects, productType, confidence, weightGuess);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"анализ изображения {ex}");
                return ("Ошибка", "Ошибка", "Ошибка", new(), "Ошибка", 0f, "Ошибка");
            }
        }


        private Bitmap? BitmapFromSource(BitmapSource source)
        {
            try
            {
                int width = source.PixelWidth;
                int height = source.PixelHeight;
                int stride = width * ((source.Format.BitsPerPixel + 7) / 8);

                byte[] pixels = new byte[height * stride];
                source.CopyPixels(pixels, stride, 0);

                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, width, height);
                var bmpData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, bitmap.PixelFormat);

                Marshal.Copy(pixels, 0, bmpData.Scan0, pixels.Length);
                bitmap.UnlockBits(bmpData);

                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private string AnalyzeBrightnessFast(Bitmap bmp)
        {
            long total = 0;
            int sampleSize = Math.Max(bmp.Width, bmp.Height) / 50;

            for (int y = 0; y < bmp.Height; y += sampleSize)
            {
                for (int x = 0; x < bmp.Width; x += sampleSize)
                {
                    var c = bmp.GetPixel(x, y);
                    total += (c.R + c.G + c.B) / 3;
                }
            }

            int avg = (int)(total / ((bmp.Width / sampleSize) * (bmp.Height / sampleSize)));
            return GetBrightnessDescription(avg);
        }

        private string GetBrightnessDescription(int avg)
        {
            return avg switch
            {
                < 50 => "Очень темно",
                < 120 => "Темно",
                < 200 => "Нормально",
                _ => "Очень ярко"
            };
        }

        private string GetDominantColorFast(Bitmap bmp)
        {
            long r = 0, g = 0, b = 0;
            int sampleSize = Math.Max(bmp.Width, bmp.Height) / 50;

            for (int y = 0; y < bmp.Height; y += sampleSize)
            {
                for (int x = 0; x < bmp.Width; x += sampleSize)
                {
                    var c = bmp.GetPixel(x, y);
                    r += c.R; g += c.G; b += c.B;
                }
            }

            int count = (bmp.Width / sampleSize) * (bmp.Height / sampleSize);
            r /= count; g /= count; b /= count;

            if (r > g && r > b) return "Доминирует красный";
            if (g > r && g > b) return "Доминирует зелёный";
            if (b > r && b > g) return "Доминирует синий";
            return "Сбалансированный";
        }

        private void UpdateUI(string barcode, string brightness, string dominantColor,
                       List<string> objects, string productType, float productConfidence,
                       string weightGuess)
        {
            string objList = objects.Count > 0 ? string.Join(", ", objects) : "Нет объектов";

            string productText;
            if (productType == "Тип продукции не определён" || productType == "Ошибка классификации")
            {
                productText = productType;
            }
            else
            {
                productText = $"{productType} ({productConfidence:0.##}%)";
            }

            txtAnalysisResult.Text =
                $"Штрих-код: {barcode}\n" +
                $"Яркость: {brightness}\n" +
                $"Цвет: {dominantColor}\n" +
                $"Объекты: {objList}\n" +
                $"Тип продукции: {productText}\n" +
                $"Граммовка: {weightGuess}";

            txtAnalysisResult.Foreground = barcode != "Не найден" ? Brushes.Green : Brushes.Black;
        }

        #endregion

        private void btnApplyROI_Click(object sender, RoutedEventArgs e)
        {
            if (device == null )
            {
                txtStatus.Text = "Ошибка: Камера не подключена.";
                return;
            }

            try
            {
                int width = int.Parse(txtWidth.Text);
                int height = int.Parse(txtHeight.Text);
                int offsetX = int.Parse(txtOffsetX.Text);
                int offsetY = int.Parse(txtOffsetY.Text);

                // ВАЖНО: Проверка диапазонов может потребоваться здесь,
                // особенно суммы (offset + size), чтобы не превысить пределы датчика.
                // Идеально - получить Min/Max значения от камеры через Get*Param методы.
                // Для простоты предположим, что введенные значения корректны или будут проверены камерой.

                // Остановите захват, если он запущен
                bool wasGrabbing = isGrabbing; // Предполагается, что у вас есть такое поле
                if (wasGrabbing)
                {
                    StopGrabbing();
                }

                // Установка ROI
                SetParameter("Width", width);
                SetParameter("Height", height);
                SetParameter("OffsetX", offsetX);
                SetParameter("OffsetY", offsetY);

                txtStatus.Text = $"ROI установлен: {width}x{height}+{offsetX}+{offsetY}";

                // Перезапустите захват, если он был запущен
                if (wasGrabbing)
                {
                    StartGrabbing(); // Предполагается, что этот метод существует и корректно управляет состоянием
                }

            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Ошибка установки ROI: {ex.Message}";
                //MessageBox.Show($"Ошибка установки ROI: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnFullROI_Click(object sender, RoutedEventArgs e)
        {
            if (device == null)
            {
                txtStatus.Text = "Ошибка: Камера не подключена.";
                return;
            }

            try
            {
                int maxWidth = 2448;
                int maxHeight = 2048;
                int offsetX = 0;
                int offsetY = 0;

                // Остановите захват, если он запущен
                bool wasGrabbing = isGrabbing;
                if (wasGrabbing)
                {
                    StopGrabbing();
                }

                // Установка максимального ROI
                SetParameter("Width", maxWidth);
                SetParameter("Height", maxHeight);
                SetParameter("OffsetX", offsetX);
                SetParameter("OffsetY", offsetY);

                // Обновите текстовые поля
                txtWidth.Text = maxWidth.ToString();
                txtHeight.Text = maxHeight.ToString();
                txtOffsetX.Text = offsetX.ToString();
                txtOffsetY.Text = offsetY.ToString();

                txtStatus.Text = $"Полный ROI установлен: {maxWidth}x{maxHeight}+{offsetX}+{offsetY}";

                // Перезапустите захват, если он был запущен
                if (wasGrabbing)
                {
                    // Возможно, потребуется немного времени или проверка состояния
                    Dispatcher.BeginInvoke(new Action(() => {
                        StartGrabbing();
                    }), DispatcherPriority.Background); // Отложенный запуск
                                                        // Или просто StartGrabbing(); если StopGrabbing/StartGrabbing синхронны и корректны
                                                        // StartGrabbing();
                }


            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Ошибка установки полного ROI: {ex.Message}";
                //MessageBox.Show($"Ошибка установки полного ROI: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnRefreshList_Click(object sender, RoutedEventArgs e)
        {
            RefreshDeviceList();
        }

        private void sliderExposure_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            

            if (device != null)
            {
                try
                {
                    /* SetParameter("ExposureTime", (float)sliderExposure.Value);
                     txtExposureValue.Text = $"{(int)sliderExposure.Value} мкс";*/
                    SetParameter("ExposureTime", (float)sliderExposure.Value);
                    // перечитываем фактическую выдержку из камеры
                    RefreshExposureFromCamera();

                }
                catch (Exception ex)
                {
                    txtStatus.Text = $"Ошибка установки экспозиции: {ex.Message}";
                }
            }
        }

        private void btnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Выберите папку для сохранения фотографий";
                dialog.SelectedPath = photoSavePath;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    photoSavePath = dialog.SelectedPath;
                    txtStatus.Text = $"Папка для фото: {photoSavePath}";
                }
            }
        }

        private void btnTakePhoto_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (imgCameraView.Source == null)
                {
                    txtStatus.Text = "Нет изображения для сохранения";
                    return;
                }

                var bitmapSource = imgCameraView.Source as System.Windows.Media.Imaging.BitmapSource;
                if (bitmapSource == null)
                {
                    txtStatus.Text = "Неверный формат изображения";
                    return;
                }

                string fileName = $"Photo_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string fullPath = Path.Combine(photoSavePath, fileName);

                using (var fileStream = new FileStream(fullPath, FileMode.Create))
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmapSource));
                    encoder.Save(fileStream);
                }

                txtStatus.Text = $"Фото сохранено: {fullPath}";
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Ошибка сохранения фото: {ex.Message}";
            }
        }

        //Автонастройка выдержки и усиления 
        private void btnAutoMode_Click(object sender, RoutedEventArgs e)
        {
            if (device == null) { txtStatus.Text = "Камера не подключена"; return; }

            try
            {
                if (!isAutoMode)
                {
                    // Включаем автоэкспозицию и автоусиление
                    SetParameter("ExposureAuto", "Continuous"); // Off/Once/Continuous
                    SetParameter("GainAuto", "Continuous");

                    sliderExposure.IsEnabled = false;
                    sliderGain.IsEnabled = false;

                    btnAutoMode.Content = "Выключить автонастройку";
                    txtStatus.Text = "Автонастройка включена";
                }
                else
                {
                    // Возврат в ручной режим
                    SetParameter("ExposureAuto", "Off");
                    SetParameter("GainAuto", "Off");

                    // Применяем текущие значения слайдеров
                    SetParameter("ExposureTime", (float)sliderExposure.Value);
                    SetParameter("Gain", (float)sliderGain.Value);

                    sliderExposure.IsEnabled = true;
                    sliderGain.IsEnabled = true;

                    btnAutoMode.Content = "Включить автонастройку";
                    txtStatus.Text = "Ручной режим";
                }

                isAutoMode = !isAutoMode;
                RefreshExposureAndGainFromCamera(); // обновим показания
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Ошибка переключения режима: {ex.Message}";
            }
        }

        private void sliderGain_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (device == null) return;

            try
            {
                // В ручном режиме позволяем менять усиление
                SetParameter("Gain", (float)sliderGain.Value);
                RefreshExposureAndGainFromCamera();
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"Ошибка установки усиления: {ex.Message}";
            }
        }

    }



}
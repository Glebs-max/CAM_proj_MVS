using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MvCameraControl;
using ZXing;
using ZXing.Common;
using ZXing.Windows.Compatibility;
using Brushes = System.Windows.Media.Brushes;
using PixelFormat = System.Drawing.Imaging.PixelFormat; // Явное указание

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
                (bitmap) => new ZXing.Windows.Compatibility.BitmapLuminanceSource(bitmap)
            )
            {
                AutoRotate = false,
                Options = new DecodingOptions
                {
                    TryInverted = false,
                    PossibleFormats = new List<BarcodeFormat>
                    {
                        BarcodeFormat.CODE_128,
                        BarcodeFormat.QR_CODE
                    },
                    TryHarder = false
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

        private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                SDKSystem.Initialize();
                RefreshDeviceList();
                yoloDetector = new YoloDetector("Assets/yolov5/yolov5s.onnx");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка инициализации: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            StopGrabbing();
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

        private void btnConnect_Click(object sender, RoutedEventArgs e)
        {
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
                txtStatus.Text = $"Камера {deviceInfoList[cmbCameras.SelectedIndex].ModelName} подключена";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка подключения: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ConfigureCameraParameters()
        {
            SetParameter("AcquisitionMode", "Continuous");
            SetParameter("TriggerMode", "Off");
            SetParameter("ExposureTime", 10000.0f);
            SetParameter("Width", 1920);
            SetParameter("Height", 1080);
            SetParameter("Gain", 10.0f);
            SetParameter("PixelFormat", "BayerRG8");

            //SetParameter("AcquisitionMode", "Continuous");
            //SetParameter("TriggerMode", "Off");
            //SetParameter("ExposureTime", 5000.0f);
            //SetParameter("Width", 1280);
            //SetParameter("Height", 720);
            //SetParameter("Gain", 5.0f);
            //SetParameter("PixelFormat", "BayerRG8");
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

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            StartGrabbing();
            btnStart.IsEnabled = false;
            btnStop.IsEnabled = true;
            txtStatus.Text = "Захват изображения запущен";
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            StopGrabbing();
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
                                    UpdateUI(analysis.barcode, analysis.brightness, analysis.dominantColor, analysis.objects);
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

        private (string barcode, string brightness, string dominantColor, List<string> objects) AnalyzeImage(BitmapSource bitmapSource)
        {
            try
            {
                var bmp = BitmapFromSource(bitmapSource);
                if (bmp == null) return ("Ошибка", "Ошибка", "Ошибка", new());

                var result = barcodeReader.Decode(bmp);
                var brightness = AnalyzeBrightnessFast(bmp);
                var color = GetDominantColorFast(bmp);

                var objects = yoloDetector?.DetectObjects(bmp) ?? new List<string>();

                return (result?.Text ?? "Не найден", brightness, color, objects);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Анализ изображения: {ex}");
                return ("Ошибка", "Ошибка", "Ошибка", new());
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

        private void UpdateUI(string barcode, string brightness, string dominantColor, List<string> objects)
        {
            string objList = objects.Count > 0 ? string.Join(", ", objects) : "Нет объектов";
            txtAnalysisResult.Text = $"Штрих-код: {barcode}\nЯркость: {brightness}\nЦвет: {dominantColor}\nОбъекты: {objList}";
            txtAnalysisResult.Foreground = barcode != "Не найден" ? Brushes.Green : Brushes.Black;
        }
    }
}
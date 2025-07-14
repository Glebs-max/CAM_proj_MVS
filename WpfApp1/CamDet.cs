// Тут вариант с анализом изображения из файла, создаётся ВРЕМЕННЫЙ файл, в который записывается кадр и подружается из файла для анализа
/*using System;
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

namespace WpfApp1
{
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
        private int capacity = 10;
        private int currentCount = 0;
        private bool alarmTriggered = false;

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
                AutoRotate = true,
                Options = new ZXing.Common.DecodingOptions
                {
                    TryInverted = true,
                    PossibleFormats = new List<BarcodeFormat>
            {
                BarcodeFormat.CODE_128,
                BarcodeFormat.EAN_13,
                BarcodeFormat.QR_CODE
            },
                    TryHarder = true
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
                            if (chkAnalyze.IsChecked == true)
                            {
                                var analysis = AnalyzeImage(bitmap);
                                UpdateUI(analysis.barcode, analysis.brightness, analysis.dominantColor);
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

        private (string barcode, string brightness, string dominantColor) AnalyzeImage(BitmapSource bitmapSource)
        {
            try
            {
                using var bmp = new Bitmap(bitmapSource.PixelWidth, bitmapSource.PixelHeight,
                    System.Drawing.Imaging.PixelFormat.Format32bppRgb);

                var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                var bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);

                bitmapSource.CopyPixels(Int32Rect.Empty, bmpData.Scan0, bmpData.Height * bmpData.Stride, bmpData.Stride);
                bmp.UnlockBits(bmpData);

                var result = barcodeReader.Decode(bmp);
                var brightness = AnalyzeBrightness(bmp);
                var color = GetDominantColor(bmp);

                return (result?.Text ?? "Не найден", brightness, color);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Анализ изображения: {ex}");
                return ("Ошибка", "Ошибка", "Ошибка");
            }
        }

        private string AnalyzeBrightness(Bitmap bmp)
        {
            long total = 0;
            int count = 0;

            for (int y = 0; y < bmp.Height; y += 10)
            {
                for (int x = 0; x < bmp.Width; x += 10)
                {
                    var c = bmp.GetPixel(x, y);
                    total += (c.R + c.G + c.B) / 3;
                    count++;
                }
            }

            int avg = (int)(total / count);
            return avg switch
            {
                < 50 => "Очень темно",
                < 120 => "Темно",
                < 200 => "Нормально",
                _ => "Очень ярко"
            };
        }

        private string GetDominantColor(Bitmap bmp)
        {
            long r = 0, g = 0, b = 0;
            int count = 0;

            for (int y = 0; y < bmp.Height; y += 10)
            {
                for (int x = 0; x < bmp.Width; x += 10)
                {
                    var c = bmp.GetPixel(x, y);
                    r += c.R; g += c.G; b += c.B;
                    count++;
                }
            }

            r /= count; g /= count; b /= count;
            if (r > g && r > b) return "Доминирует красный";
            if (g > r && g > b) return "Доминирует зелёный";
            if (b > r && b > g) return "Доминирует синий";
            return "Сбалансированный";
        }

        private void UpdateUI(string barcode, string brightness, string color)
        {
            txtAnalysisResult.Text = $"Штрих-код: {barcode}\nЯркость: {brightness}\nЦвет: {color}";
            txtAnalysisResult.Foreground = barcode != "Не найден" ? Brushes.Green : Brushes.Black;
        }
    }
}*/
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WpfApp1
{
    // Класс для распознания обръектов
    public class YoloDetector
    {
        private readonly InferenceSession _session;

        public YoloDetector(string modelPath)
        {
            _session = new InferenceSession(modelPath);
        }

        public List<string> DetectObjects(Bitmap image)
        {
            var input = Preprocess(image);
            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor("images", input) });

            var output = results.First().AsEnumerable<float>().ToArray();
            return ParseOutput(output); // Упростим до имён объектов
        }
        private DenseTensor<float> Preprocess(Bitmap image)
        {
            var resized = new Bitmap(image, new Size(640, 640));
            var tensor = new DenseTensor<float>(new[] { 1, 3, 640, 640 });

            for (int y = 0; y < 640; y++)
            {
                for (int x = 0; x < 640; x++)
                {
                    Color color = resized.GetPixel(x, y);
                    tensor[0, 0, y, x] = color.R / 255.0f;
                    tensor[0, 1, y, x] = color.G / 255.0f;
                    tensor[0, 2, y, x] = color.B / 255.0f;
                }
            }
            return tensor;
        }

        private List<string> ParseOutput(float[] output)
        {
            List<string> detected = new();
            int numDetections = output.Length / 85;

            for (int i = 0; i < numDetections; i++)
            {
                int offset = i * 85;
                float confidence = output[offset + 4];

                if (confidence > 0.4)
                {
                    float[] classes = output.Skip(offset + 5).Take(80).ToArray();
                    int classId = Array.IndexOf(classes, classes.Max());
                    string labelEn = YoloV5Classes[classId];
                    string labelRu = YoloLabelsRu.TryGetValue(labelEn, out var ru) ? ru : labelEn;
                    detected.Add(labelRu);
                }
            }

            return detected;
        }

        private static readonly string[] YoloV5Classes = new[]
        {
            "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train",
            "truck", "boat", "traffic light", "fire hydrant", "stop sign", "bench",
            "bird", "cat", "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra",
            "giraffe", "backpack", "umbrella", "handbag", "tie", "suitcase", "frisbee",
            "skis", "snowboard", "sports ball", "kite", "baseball bat", "baseball glove",
            "skateboard", "surfboard", "tennis racket", "bottle", "wine glass", "cup",
            "fork", "knife", "spoon", "bowl", "banana", "apple", "sandwich", "orange",
            "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair", "couch",
            "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse",
            "remote", "keyboard", "cell phone", "microwave", "oven", "toaster", "sink",
            "refrigerator", "book", "clock", "vase", "scissors", "teddy bear", "hair drier",
            "toothbrush"
        };

        private static readonly Dictionary<string, string> YoloLabelsRu = new()
        {
            { "person", "человек" }, { "bicycle", "велосипед" }, { "car", "машина" }, { "motorcycle", "мотоцикл" },
            { "airplane", "самолёт" }, { "bus", "автобус" }, { "train", "поезд" }, { "truck", "грузовик" }, { "boat", "лодка" },
            { "traffic light", "светофор" }, { "fire hydrant", "пожарный гидрант" }, { "stop sign", "знак стоп" },
            { "bench", "скамейка" }, { "bird", "птица" }, { "cat", "кот" }, { "dog", "собака" }, { "horse", "лошадь" },
            { "sheep", "овца" }, { "cow", "корова" }, { "elephant", "слон" }, { "bear", "медведь" }, { "zebra", "зебра" },
            { "giraffe", "жираф" }, { "backpack", "рюкзак" }, { "umbrella", "зонт" }, { "handbag", "сумка" },
            { "tie", "галстук" }, { "suitcase", "чемодан" }, { "frisbee", "фрисби" }, { "skis", "лыжи" },
            { "snowboard", "сноуборд" }, { "sports ball", "мяч" }, { "kite", "воздушный змей" },
            { "baseball bat", "бейсбольная бита" }, { "baseball glove", "бейсбольная перчатка" },
            { "skateboard", "скейтборд" }, { "surfboard", "доска для серфинга" }, { "tennis racket", "ракетка" },
            { "bottle", "бутылка" }, { "wine glass", "бокал" }, { "cup", "чашка" }, { "fork", "вилка" }, { "knife", "нож" },
            { "spoon", "ложка" }, { "bowl", "миска" }, { "banana", "банан" }, { "apple", "яблоко" }, { "sandwich", "бутерброд" },
            { "orange", "апельсин" }, { "broccoli", "брокколи" }, { "carrot", "морковь" }, { "hot dog", "хот-дог" },
            { "pizza", "пицца" }, { "donut", "пончик" }, { "cake", "торт" }, { "chair", "стул" }, { "couch", "диван" },
            { "potted plant", "растение в горшке" }, { "bed", "кровать" }, { "dining table", "обеденный стол" },
            { "toilet", "туалет" }, { "tv", "телевизор" }, { "laptop", "ноутбук" }, { "mouse", "мышь" }, { "remote", "пульт" },
            { "keyboard", "клавиатура" }, { "cell phone", "телефон" }, { "microwave", "микроволновка" },
            { "oven", "духовка" }, { "toaster", "тостер" }, { "sink", "раковина" }, { "refrigerator", "холодильник" },
            { "book", "книга" }, { "clock", "часы" }, { "vase", "ваза" }, { "scissors", "ножницы" },
            { "teddy bear", "плюшевый мишка" }, { "hair drier", "фен" }, { "toothbrush", "зубная щётка" }
        };

    }
}

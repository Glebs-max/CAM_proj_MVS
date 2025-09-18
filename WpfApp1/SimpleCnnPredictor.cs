using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WpfApp1
{
    public class SimpleCnnPredictor : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string[] _classes;
        private readonly int _inputWidth;
        private readonly int _inputHeight;

        public SimpleCnnPredictor(string modelPath, string? classesJsonPath = null, int inputWidth = 224, int inputHeight = 224)
        {
            if (!File.Exists(modelPath))
                throw new FileNotFoundException("ONNX model not found", modelPath);

            _session = new InferenceSession(modelPath);
            _inputWidth = inputWidth;
            _inputHeight = inputHeight;

            // попытка найти classes.json рядом с моделью, если не указан явно
            if (string.IsNullOrEmpty(classesJsonPath))
            {
                var dir = Path.GetDirectoryName(modelPath) ?? "";
                var candidate = Path.Combine(dir, "classes.json");
                classesJsonPath = File.Exists(candidate) ? candidate : null;
            }

            if (!string.IsNullOrEmpty(classesJsonPath) && File.Exists(classesJsonPath))
            {
                var txt = File.ReadAllText(classesJsonPath);
                try
                {
                    _classes = JsonSerializer.Deserialize<string[]>(txt) ?? Array.Empty<string>();
                }
                catch
                {
                    _classes = Array.Empty<string>();
                }
            }
            else
            {
                // Если нет classes.json — создаём заглушку по количеству выходных нейронов (если можем)
                // Попытка получить размер выхода из модели:
                try
                {
                    var outShape = _session.OutputMetadata[_session.OutputMetadata.Keys.First()].Dimensions;
                    int n = outShape.Where(d => d > 0).LastOrDefault(); // грубая эвристика
                    if (n <= 0) n = 2;
                    _classes = Enumerable.Range(0, n).Select(i => $"class_{i}").ToArray();
                }
                catch
                {
                    _classes = new[] { "class_0", "class_1" };
                }
            }
        }

        // Возвращает (label, confidencePercent)
        public (string Label, float ConfidencePercent) PredictWithConfidence(Bitmap bitmap)
        {
            var input = PreprocessImage(bitmap, _inputWidth, _inputHeight);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", input)
            };

            using var results = _session.Run(inputs);
            // берем первый выход
            var first = results.First().AsEnumerable<float>().ToArray();

            // softmax
            var probs = Softmax(first);

            int maxIdx = Array.IndexOf(probs, probs.Max());
            string label = (maxIdx >= 0 && maxIdx < _classes.Length) ? _classes[maxIdx] : $"class_{maxIdx}";
            float confidence = probs[maxIdx] * 100f;

            return (label, confidence);
        }

        private static float[] Softmax(float[] logits)
        {
            var max = logits.Max();
            var exps = logits.Select(l => Math.Exp(l - max)).ToArray();
            var sum = exps.Sum();
            return exps.Select(e => (float)(e / sum)).ToArray();
        }

        private static DenseTensor<float> PreprocessImage(Bitmap bitmap, int width, int height)
        {
            // Resize
            var resized = new Bitmap(width, height);
            using (var g = Graphics.FromImage(resized))
            {
                g.DrawImage(bitmap, 0, 0, width, height);
            }

            var tensor = new DenseTensor<float>(new[] { 1, 3, height, width });

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var p = resized.GetPixel(x, y);
                    // Normalize: (x/255 - 0.5)/0.5  <=>  (x/255)*2 -1
                    tensor[0, 0, y, x] = ((p.R / 255f) - 0.5f) / 0.5f;
                    tensor[0, 1, y, x] = ((p.G / 255f) - 0.5f) / 0.5f;
                    tensor[0, 2, y, x] = ((p.B / 255f) - 0.5f) / 0.5f;
                }
            }

            resized.Dispose();
            return tensor;
        }

        public void Dispose()
        {
            _session?.Dispose();
        }
    }
}

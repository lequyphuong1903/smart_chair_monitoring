using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.OnnxRuntime;
using System.IO;

namespace PatientMonitoring.Onnx
{
    public class Runtime : IDisposable
    {
        private InferenceSession _session;
        public Runtime()
        {
            string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Onnx/model_tune1.onnx");
            _session = new InferenceSession(modelPath);
        }

        public float RunInference(float[] inputData, int[] inputDimensions, string inputName = "input")
        {
            var inputTensor = new DenseTensor<float>(inputData, inputDimensions);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            };

            using (var results = _session.Run(inputs))
            {
                var outputTensor = results.First().AsEnumerable<float>().First();
                return outputTensor;
            }
        }

        public void Dispose()
        {
            if (_session != null)
            {
                _session.Dispose();
                _session = null;
            }
        }
    }
}

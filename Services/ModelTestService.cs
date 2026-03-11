using Microsoft.ML.OnnxRuntime;

namespace LocalRagAPI.Services
{
    public class ModelTestService
    {
        public static void TestModel()
        {
            var session = new InferenceSession("AIModels/MiniLM/model.onnx");
            Console.WriteLine("MiniLM ONNX Model Loaded Successfully");
        }
    }
}

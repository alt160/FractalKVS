
namespace FractalKVS
{
    [Serializable]
    internal class DataCorruptionException : Exception
    {
        public DataCorruptionException()
        {
        }

        public DataCorruptionException(string? message) : base(message)
        {
        }

        public DataCorruptionException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}
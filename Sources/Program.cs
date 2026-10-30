namespace WhisperCLI
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            await WhisperApplication.RunAsync(args);
        }
    }
}

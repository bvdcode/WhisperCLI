using System.Net;

namespace WhisperCLI.Tests
{
    internal class ReleaseHttpHandler(string metadata, byte[] payload, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            HttpResponseMessage response = new(status);
            if (request.RequestUri?.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal) == true)
            {
                response.Content = new StringContent(metadata);
            }
            else
            {
                response.Content = new ByteArrayContent(payload);
            }
            return Task.FromResult(response);
        }
    }
}

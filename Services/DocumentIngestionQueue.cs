using System.Threading.Channels;
using LocalRagAPI.Models;
using System.Threading.Tasks;
using System;

namespace LocalRagAPI.Services
{
    public class DocumentIngestionQueue
    {
        private readonly Channel<DocumentIngestionRequest> _channel;

        public DocumentIngestionQueue(int capacity = 50)
        {
            var options = new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait
            };

            _channel = Channel.CreateBounded<DocumentIngestionRequest>(options);
        }

        public ChannelReader<DocumentIngestionRequest> Reader => _channel.Reader;
        public ChannelWriter<DocumentIngestionRequest> Writer => _channel.Writer;

        public async Task<bool> EnqueueAsync(DocumentIngestionRequest request, TimeSpan timeout)
        {
            using var cts = new System.Threading.CancellationTokenSource(timeout);
            try
            {
                await _channel.Writer.WriteAsync(request, cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}

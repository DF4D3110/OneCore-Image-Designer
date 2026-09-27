using System.IO.Pipelines;
using System.Reactive.Disposables;
using BlockCompressor;
using Zafiro.Reactive;

namespace DotnetPackaging.Msix.Core.Compression;

public static class Compressor
{
    public static async Task<byte[]> Uncompress(byte[] compressedData)
    {
        using (MemoryStream ms = new(compressedData))
        {
            await using (DeflateStream deflate = new(ms, CompressionMode.Decompress))
            {
                using (MemoryStream output = new())
                {
                    await deflate.CopyToAsync(output);
                    return output.ToArray();
                }
            }
        }
    }

    public static IObservable<byte[]> Compressed(this IObservable<byte[]> source, CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        return Observable.Create<byte[]>(observer =>
        {
            // Increase buffer size to avoid blocking
            PipeOptions pipeOptions = new(pauseWriterThreshold: 1024 * 1024); // 1MB
            Pipe pipe = new(pipeOptions);

            // First configure the read subscription to ensure data is consumed
            IDisposable readSubscription = pipe.Reader.AsStream().ToObservable().Subscribe(observer);

            // Create DeflateStream after configuring the read
            DeflateStream deflateStream = new(pipe.Writer.AsStream(), compressionLevel, leaveOpen: true);

            // Suscribirse a la fuente
            IDisposable subscription = source.Subscribe(
                block =>
                {
                    try
                    {
                        byte[] array = [.. block];
                        deflateStream.Write(array, 0, array.Length);
                        deflateStream.Flush(); // Hacer flush del DeflateStream
                        pipe.Writer.FlushAsync().GetAwaiter().GetResult(); // Hacer flush del PipeWriter
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            deflateStream.Dispose();
                        }
                        catch
                        {
                        }

                        pipe.Writer.Complete(ex);
                    }
                },
                ex =>
                {
                    try
                    {
                        deflateStream.Dispose();
                    }
                    catch
                    {
                    }

                    pipe.Writer.Complete(ex);
                },
                () =>
                {
                    try
                    {
                        deflateStream.Close();
                        pipe.Writer.Complete();
                    }
                    catch (Exception ex)
                    {
                        pipe.Writer.Complete(ex);
                    }
                });

            return new CompositeDisposable(subscription, readSubscription, Disposable.Create(() =>
            {
                try
                {
                    deflateStream.Dispose();
                }
                catch
                {
                }

                pipe.Writer.Complete();
            }));
        });
    }

    public static IObservable<DeflateBlock> CompressionBlocks(this IObservable<byte[]> bytes)
    {
        return BlockCompressor.Compressed.Blocks(bytes);
    }
}
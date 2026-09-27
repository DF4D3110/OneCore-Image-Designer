using Microsoft.Deployment.Compression.Cab;
using Microsoft.Deployment.Compression;

namespace MobilePackageGen
{
    internal class SafeStream : Stream
    {
        private readonly Stream _inner;
        public SafeStream(Stream inner) { _inner = inner; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                int read = _inner.Read(buffer, offset, count);
                return read < 0 ? 0 : read;
            }
            catch (EndOfStreamException) { return 0; }
            catch (IOException) { return 0; }
            catch { return 0; }
        }
    }

    public class CabinetBuilder
    {
        public static void BuildCab(string cabFile, IEnumerable<CabinetFileInfo> fileMappings, ref string fileStatus)
        {
            double oldPercentage = uint.MaxValue;
            double oldFilePercentage = uint.MaxValue;
            string oldFileName = "";

            string lambdaFileStatus = fileStatus;

            var safeMappings = fileMappings.Select(x =>
            {
                try
                {
                    if (x.FileStream.CanSeek)
                    {
                        x.FileStream.Seek(0, SeekOrigin.Begin);
                    }
                    var ms = new MemoryStream();
                    x.FileStream.CopyTo(ms);
                    ms.Seek(0, SeekOrigin.Begin);
                    x.FileStream = ms;
                }
                catch { }
                return x;
            }).ToList();

            CabInfo cab = new(cabFile);
            cab.PackFiles(null, safeMappings.Select(x => x.GetFileTuple()).ToArray(), safeMappings.Select(x => x.FileName).ToArray(), CompressionLevel.Min, (object? _, ArchiveProgressEventArgs archiveProgressEventArgs) =>
            {
                string fileNameParsed;
                if (string.IsNullOrEmpty(archiveProgressEventArgs.CurrentFileName))
                {
                    fileNameParsed = $"Unknown ({archiveProgressEventArgs.CurrentFileNumber})";
                }
                else
                {
                    fileNameParsed = archiveProgressEventArgs.CurrentFileName;
                }

                double percentage = ((double)archiveProgressEventArgs.CurrentFileNumber * 50 / archiveProgressEventArgs.TotalFiles) + 50;

                if (percentage != oldPercentage)
                {
                    oldPercentage = percentage;
                    string progressBarString = Logging.GetDISMLikeProgressBar(percentage);

                    Logging.Log(progressBarString, returnLine: false);
                }

                if (fileNameParsed != oldFileName)
                {
                    Logging.Log();
                    Logging.Log(new string(' ', lambdaFileStatus.Length));
                    Logging.Log(Logging.GetDISMLikeProgressBar(0), returnLine: false);

                    if (Logging.HasConsole)
                        Console.SetCursorPosition(0, Console.CursorTop - 2);

                    oldFileName = fileNameParsed;

                    oldFilePercentage = uint.MaxValue;

                    lambdaFileStatus = $"Adding file {archiveProgressEventArgs.CurrentFileNumber + 1} of {archiveProgressEventArgs.TotalFiles} - {fileNameParsed}";
                    if (Logging.HasConsole && lambdaFileStatus.Length > Console.BufferWidth - 24 - 1)
                    {
                        lambdaFileStatus = $"{lambdaFileStatus[..(Console.BufferWidth - 24 - 4)]}...";
                    }

                    Logging.Log();
                    Logging.Log(lambdaFileStatus);
                    Logging.Log(Logging.GetDISMLikeProgressBar(0), returnLine: false);

                    if (Logging.HasConsole)
                        Console.SetCursorPosition(0, Console.CursorTop - 2);
                }

                double filePercentage = (double)archiveProgressEventArgs.CurrentFileBytesProcessed * 100 / archiveProgressEventArgs.CurrentFileTotalBytes;

                if (filePercentage != oldFilePercentage)
                {
                    oldFilePercentage = filePercentage;
                    string progressBarString = Logging.GetDISMLikeProgressBar(filePercentage);

                    Logging.Log();
                    Logging.Log();
                    Logging.Log(progressBarString, returnLine: false);

                    if (Logging.HasConsole)
                        Console.SetCursorPosition(0, Console.CursorTop - 2);
                }
            });

            fileStatus = lambdaFileStatus;
        }
    }
}

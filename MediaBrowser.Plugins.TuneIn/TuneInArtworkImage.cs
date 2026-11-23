using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    internal static class TuneInArtworkImage
    {
        internal const int MaxDimension = 4096;
        internal const long MaxPixels = 16L * 1024 * 1024;

        internal static void Validate(string path, string encoderPath, IDictionary<string, string> environment, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(encoderPath))
                throw new InvalidOperationException("The server FFmpeg image decoder is unavailable.");
            token.ThrowIfCancellationRequested();
            using (var stream = File.OpenRead(path))
            {
                var extension = Path.GetExtension(path);
                var dimensions = extension == ".png" ? PngSize(stream)
                    : extension == ".jpg" ? JpegSize(stream)
                    : throw new InvalidDataException("Unsupported artwork image extension.");
                if (dimensions.Width < 1 || dimensions.Height < 1 || dimensions.Width > MaxDimension
                    || dimensions.Height > MaxDimension || (long)dimensions.Width * dimensions.Height > MaxPixels)
                    throw new InvalidDataException("Artwork image dimensions exceed the permitted limit.");
            }
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = encoderPath,
                    Arguments = "-hide_banner -loglevel error -xerror -nostdin -max_alloc 134217728 -threads 1"
                        + " -max_pixels 16777216 -protocol_whitelist pipe -f image2pipe -c:v "
                        + (Path.GetExtension(path) == ".png" ? "png" : "mjpeg")
                        + " -i pipe:0 -frames:v 1 -vf scale=1:1 -pix_fmt rgb24 -f rawvideo pipe:1",
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                if (environment != null)
                    foreach (var item in environment) process.StartInfo.Environment[item.Key] = item.Value;
                if (!process.Start()) throw new InvalidOperationException("The server FFmpeg image decoder could not start.");
                var input = Task.Run(() =>
                {
                    using (var stdin = process.StandardInput.BaseStream)
                    using (var stream = File.OpenRead(path)) stream.CopyTo(stdin);
                });
                var output = Task.Run(() =>
                {
                    var buffer = new byte[4096];
                    long total = 0;
                    int count;
                    while ((count = process.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)) != 0) total += count;
                    return total;
                });
                var errors = Task.Run(() =>
                {
                    var buffer = new char[1024];
                    while (process.StandardError.Read(buffer, 0, buffer.Length) != 0) { }
                });
                var timer = Stopwatch.StartNew();
                while (!process.WaitForExit(250))
                {
                    if (token.IsCancellationRequested || timer.Elapsed >= TimeSpan.FromSeconds(30))
                    {
                        process.Kill();
                        process.WaitForExit();
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException("Artwork image decoding exceeded 30 seconds.");
                    }
                }
                try
                {
                    input.GetAwaiter().GetResult();
                    var bytes = output.GetAwaiter().GetResult();
                    errors.GetAwaiter().GetResult();
                    if (process.ExitCode != 0 || bytes != 3)
                        throw new InvalidDataException("The artwork image could not be decoded.");
                }
                catch (IOException error) { throw new InvalidDataException("The artwork image could not be decoded.", error); }
            }
        }

        private static (int Width, int Height) PngSize(Stream stream)
        {
            var header = new byte[24];
            ReadExactly(stream, header, header.Length);
            var magic = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 };
            for (var i = 0; i < magic.Length; i++)
                if (header[i] != magic[i]) throw new InvalidDataException("Invalid PNG artwork.");
            return (BigEndian(header, 16), BigEndian(header, 20));
        }

        private static (int Width, int Height) JpegSize(Stream stream)
        {
            if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8)
                throw new InvalidDataException("Invalid JPEG artwork.");
            while (true)
            {
                if (stream.ReadByte() != 0xff) throw new InvalidDataException("Invalid JPEG marker.");
                int marker;
                do { marker = stream.ReadByte(); } while (marker == 0xff);
                if (marker < 0 || marker == 0xda || marker == 0xd9 || marker == 0x00
                    || marker == 0xd8 || marker >= 0xd0 && marker <= 0xd7)
                    throw new InvalidDataException("JPEG dimensions are missing or invalid.");
                var length = (ReadByte(stream) << 8) | ReadByte(stream);
                if (length < 2 || length - 2 > stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid JPEG segment.");
                if (marker == 0xc0 || marker == 0xc1 || marker == 0xc2)
                {
                    if (length < 8) throw new InvalidDataException("Invalid JPEG frame.");
                    ReadByte(stream);
                    var height = (ReadByte(stream) << 8) | ReadByte(stream);
                    var width = (ReadByte(stream) << 8) | ReadByte(stream);
                    return (width, height);
                }
                stream.Position += length - 2;
            }
        }

        private static int BigEndian(byte[] bytes, int offset)
        {
            var value = ((long)bytes[offset] << 24) | ((long)bytes[offset + 1] << 16)
                | ((long)bytes[offset + 2] << 8) | bytes[offset + 3];
            return value > int.MaxValue ? -1 : (int)value;
        }

        private static int ReadByte(Stream stream)
        {
            var value = stream.ReadByte();
            if (value < 0) throw new InvalidDataException("Truncated artwork image.");
            return value;
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int length)
        {
            var offset = 0;
            while (offset < length)
            {
                var read = stream.Read(buffer, offset, length - offset);
                if (read == 0) throw new InvalidDataException("Truncated artwork image.");
                offset += read;
            }
        }
    }
}

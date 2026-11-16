using MediaBrowser.Plugins.TuneIn.Security;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    internal abstract class RadioReadStream : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // One streaming WAV header, then fixed-format PCM from separately decoded inputs.
    // Probing and playback therefore see the same codec, even for an MP3 ad followed by AAC.
    internal sealed class PcmSequenceStream : RadioReadStream
    {
        private readonly Queue<Func<CancellationToken, Task<SafeResponse>>> stages;
        private readonly Func<SafeResponse, CancellationToken, Stream> decode;
        private readonly CancellationTokenSource lifetime;
        private readonly MemoryStream header;
        private Stream current;
        private bool disposed;
        private ExceptionDispatchInfo failure;

        public PcmSequenceStream(SafeResponse first, Queue<Func<CancellationToken, Task<SafeResponse>>> stages,
            Func<SafeResponse, CancellationToken, Stream> decode, CancellationToken token)
        {
            this.stages = stages;
            this.decode = decode;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            header = new MemoryStream();
            using (var writer = new BinaryWriter(header, Encoding.ASCII, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(uint.MaxValue);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)2);
                writer.Write(48000);
                writer.Write(192000);
                writer.Write((short)4);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(uint.MaxValue);
            }
            header.Position = 0;
            try { current = decode(first, lifetime.Token); }
            catch { first.Dispose(); header.Dispose(); lifetime.Dispose(); throw; }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PcmSequenceStream));
            failure?.Throw();
            try
            {
                using (var read = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
                {
                    read.Token.ThrowIfCancellationRequested();
                    if (count == 0) return 0;
                    var size = header.Read(buffer, offset, count);
                    if (size != 0) return size;
                    while (current != null)
                    {
                        size = await current.ReadAsync(buffer, offset, count, read.Token).ConfigureAwait(false);
                        read.Token.ThrowIfCancellationRequested();
                        if (size != 0) return size;
                        current.Dispose();
                        current = null;
                        if (stages.Count == 0) return 0;
                        read.Token.ThrowIfCancellationRequested();
                        var response = await stages.Dequeue()(read.Token).ConfigureAwait(false);
                        try { current = decode(response, lifetime.Token); }
                        catch { response.Dispose(); throw; }
                    }
                    return 0;
                }
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
                lifetime.Cancel();
                current?.Dispose();
                current = null;
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                lifetime.Cancel();
                current?.Dispose();
                header.Dispose();
                lifetime.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class PcmDecoderStream : RadioReadStream
    {
        private readonly SafeResponse response;
        private readonly Process process;
        private readonly CancellationTokenSource lifetime;
        private readonly CancellationTokenRegistration cancellation;
        private readonly Task input;
        private readonly Task errors;
        private bool disposed;
        private int stopped;

        public PcmDecoderStream(string encoderPath, SafeResponse response, CancellationToken token,
            IDictionary<string, string> environment = null)
        {
            if (string.IsNullOrWhiteSpace(encoderPath))
                throw new InvalidOperationException("The host FFmpeg executable is unavailable for TuneIn pre-roll playback.");
            this.response = response;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = encoderPath,
                    // FFmpeg receives bytes only. No provider URL or network-capable protocol is passed to it.
                    Arguments = "-hide_banner -loglevel error -nostdin -protocol_whitelist pipe -i pipe:0"
                        + " -map 0:a:0 -vn -sn -dn -ac 2 -ar 48000 -c:a pcm_s16le -f s16le pipe:1",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            try
            {
                if (environment != null)
                    foreach (var pair in environment)
                        process.StartInfo.EnvironmentVariables[pair.Key] = pair.Value;
                if (!process.Start()) throw new IOException("TuneIn audio decoder could not start.");
            }
            catch { process.Dispose(); lifetime.Dispose(); throw; }
            cancellation = lifetime.Token.Register(Stop);
            errors = DrainErrorsAsync();
            input = FeedAsync();
        }

        private async Task FeedAsync()
        {
            try
            {
                await response.Body.CopyToAsync(process.StandardInput.BaseStream, 81920, lifetime.Token).ConfigureAwait(false);
            }
            finally { process.StandardInput.Close(); }
        }

        private async Task DrainErrorsAsync()
        {
            // Drain without retaining provider metadata or allowing unbounded diagnostic buffering.
            var buffer = new char[1024];
            while (await process.StandardError.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) != 0) { }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (disposed) throw new ObjectDisposedException(nameof(PcmDecoderStream));
            using (var read = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
            using (read.Token.Register(Stop))
            {
                read.Token.ThrowIfCancellationRequested();
                if (count == 0) return 0;
                try
                {
                    var size = await process.StandardOutput.BaseStream.ReadAsync(buffer, offset, count, read.Token).ConfigureAwait(false);
                    read.Token.ThrowIfCancellationRequested();
                    if (size != 0) return size;
                    if (!process.WaitForExit(5000))
                        throw new IOException("TuneIn audio decoder did not finish.");
                    if (process.ExitCode != 0) throw new InvalidDataException("TuneIn audio decoding failed.");
                    await input.ConfigureAwait(false);
                    await errors.ConfigureAwait(false);
                    return 0;
                }
                catch { read.Token.ThrowIfCancellationRequested(); throw; }
            }
        }

        private void Stop()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            response.Dispose();
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { /* Process exited between the check and kill. */ }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                lifetime.Cancel();
                cancellation.Dispose();
                try { Task.WhenAll(input, errors).GetAwaiter().GetResult(); }
                catch (Exception error) when (error is IOException || error is OperationCanceledException
                    || error is ObjectDisposedException || error is InvalidOperationException)
                {
                    // ReadAsync reports active playback errors; closing an abandoned probe cancels its pipes.
                }
                process.Dispose();
                lifetime.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

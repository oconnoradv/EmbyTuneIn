using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MediaBrowser.Plugins.TuneIn
{
    internal static class TuneInArtworkScanner
    {
        internal enum Result { Clean, NotScanned }
        private static readonly TimeSpan ScanLimit = TimeSpan.FromMinutes(30);

        internal static Result Scan(string staging, CancellationToken token)
        {
            var files = Directory.GetFiles(staging);
            if (files.Length == 0) throw new InvalidOperationException("No staged artwork files are available to scan.");
            var defender = FindDefender();
            var clam = FindClam();
            if (defender == null && clam == null) return Result.NotScanned;
            var deadline = DateTime.UtcNow + ScanLimit;
            if (defender != null)
            {
                var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(powershell))
                    throw new InvalidOperationException("Cannot verify Microsoft Defender privacy and health settings.");
                var policy = Run(powershell, "-NoProfile -NonInteractive -Command \""
                    + "$ErrorActionPreference='Stop'; $p=Get-MpPreference; $s=Get-MpComputerStatus; "
                    + "Write-Output ('TUNEIN_POLICY:{0}:{1}:{2}:{3}' -f [int]$p.MAPSReporting,[int]$p.SubmitSamplesConsent,$s.AntivirusEnabled,$s.AMRunningMode)\"",
                    deadline, token);
                CheckDefenderPolicy(policy.ExitCode, policy.Output);
                foreach (var file in files)
                {
                    var exclusion = Run(defender, "-CheckExclusion -Path " + Quote(file), deadline, token);
                    CheckDefenderExclusion(exclusion.ExitCode, exclusion.Output, file);
                    var result = Run(defender, "-Scan -ScanType 3 -DisableRemediation -File " + Quote(file), deadline, token);
                    CheckDefender(result.ExitCode, result.Output, file);
                }
            }
            else
            {
                var args = "--recursive=yes --infected --scan-archive=no --alert-exceeds-max --max-filesize=16M "
                    + "--max-scansize=32M --stdout " + Quote(staging);
                var result = Run(clam, args, deadline, token);
                CheckClam(result.ExitCode, result.Output, files.Length);
            }
            return Result.Clean;
        }

        internal static void CheckDefenderExclusion(int exitCode, string output, string file)
        {
            if (exitCode != 1 || output == null
                || output.IndexOf(file + " [", StringComparison.OrdinalIgnoreCase) < 0
                || output.IndexOf("] is not excluded.", StringComparison.OrdinalIgnoreCase) < 0
                || Regex.IsMatch(output, @"(?i)\b(error|warning|failed|skipped)\b"))
                throw new InvalidOperationException("Microsoft Defender exclusion status could not be verified or the staged file is excluded.");
        }

        internal static void CheckDefenderPolicy(int exitCode, string output)
        {
            if (exitCode != 0 || !string.Equals((output ?? "").Trim(), "TUNEIN_POLICY:0:2:True:Normal", StringComparison.Ordinal))
                throw new InvalidOperationException("Microsoft Defender is unavailable or its cloud reporting and sample submission are not both disabled; artwork was not scanned.");
        }

        internal static void CheckDefender(int exitCode, string output, string file)
        {
            if (exitCode != 0 || output == null
                || output.IndexOf("Scan finished.", StringComparison.OrdinalIgnoreCase) < 0
                || output.IndexOf("Scanning " + file + " found no threats.", StringComparison.OrdinalIgnoreCase) < 0
                || output.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("skipped", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("Microsoft Defender reported a threat, error, or incomplete scan.");
        }

        internal static void CheckClam(int exitCode, string output, int expectedFiles)
        {
            var scanned = Regex.Match(output ?? "", @"(?m)^Scanned files:\s*(\d+)\s*$");
            var infected = Regex.Match(output ?? "", @"(?m)^Infected files:\s*0\s*$");
            if (exitCode != 0 || !scanned.Success || !int.TryParse(scanned.Groups[1].Value, out var count)
                || count != expectedFiles || !infected.Success
                || Regex.IsMatch(output, @"(?i)\b(error|warning|skipped|excluded)\b"))
                throw new InvalidOperationException("ClamAV reported a threat, error, or incomplete scan.");
        }

        private static string FindDefender()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var platform = Path.Combine(programData, "Microsoft", "Windows Defender", "Platform");
            if (Directory.Exists(platform))
            {
                var versions = Directory.GetDirectories(platform).OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
                foreach (var directory in versions)
                {
                    var path = Path.Combine(directory, "MpCmdRun.exe");
                    if (File.Exists(path)) return path;
                }
            }
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
            return File.Exists(fallback) ? fallback : null;
        }

        private static string FindClam()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                    .Select(folder => Path.Combine(Environment.GetFolderPath(folder), "ClamAV", "clamscan.exe"))
                    .FirstOrDefault(File.Exists);
            return new[] { "/usr/bin/clamscan", "/usr/local/bin/clamscan", "/opt/homebrew/bin/clamscan" }
                .FirstOrDefault(File.Exists);
        }

        private static ScanOutput Run(string executable, string arguments, DateTime deadline, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Artwork antivirus scanning timed out.");
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = executable, Arguments = arguments, UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                var output = new StringBuilder();
                var gate = new object();
                var truncated = false;
                void Append(string line)
                {
                    if (line == null) return;
                    lock (gate)
                    {
                        if (output.Length + line.Length + 1 > 1024 * 1024) truncated = true;
                        else output.AppendLine(line);
                    }
                }
                process.OutputDataReceived += (_, args) => Append(args.Data);
                process.ErrorDataReceived += (_, args) => Append(args.Data);
                if (!process.Start()) throw new InvalidOperationException("The artwork scanner could not be started.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                while (!process.WaitForExit(250))
                {
                    if (token.IsCancellationRequested || DateTime.UtcNow >= deadline)
                    {
                        process.Kill();
                        process.WaitForExit();
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException("Artwork antivirus scanning timed out.");
                    }
                }
                process.WaitForExit();
                lock (gate)
                {
                    if (truncated) throw new InvalidOperationException("Artwork scanner output was incomplete.");
                    return new ScanOutput { ExitCode = process.ExitCode, Output = output.ToString() };
                }
            }
        }

        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                slashes = 0;
                result.Append(c);
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        private sealed class ScanOutput
        {
            internal int ExitCode;
            internal string Output;
        }
    }
}

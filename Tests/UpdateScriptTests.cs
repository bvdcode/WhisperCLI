using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using WhisperCLI.Updates;

namespace WhisperCLI.Tests
{
    [TestFixture]
    [Platform("Win")]
    public class UpdateScriptTests
    {
        private const string Original = "installed executable";
        private const string Replacement = "new executable";
        private string _directory = string.Empty;
        private string _target = string.Empty;
        private string _staged = string.Empty;
        private string _backup = string.Empty;
        private string _script = string.Empty;
        private string _log = string.Empty;

        [SetUp]
        public async Task SetUp()
        {
            _directory = Directory.CreateDirectory(Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "updater-tests", Guid.NewGuid() + " test's (x64)")).FullName;
            _target = Path.Combine(_directory, "custom name.exe");
            _staged = Path.Combine(_directory, "staged.exe");
            _backup = Path.Combine(_directory, "backup.exe");
            _script = Path.Combine(_directory, "apply update.ps1");
            _log = Path.Combine(_directory, "update.log");
            await File.WriteAllTextAsync(_target, Original);
            await File.WriteAllTextAsync(_staged, Replacement);
            await File.WriteAllTextAsync(_script, UpdateScript.Content, new UTF8Encoding(false));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (string path in Directory.EnumerateFiles(_directory))
            {
                File.Delete(path);
            }
            Directory.Delete(_directory);
        }

        [Test]
        public async Task ReplacementWaitsForParentExitAndPreservesCustomName()
        {
            byte[] hash = await HashReplacementAsync();

            int exitCode = await RunScriptAsync(hash);

            Assert.That(exitCode, Is.Zero);
            Assert.That(await File.ReadAllTextAsync(_target), Is.EqualTo(Replacement));
            Assert.That(await File.ReadAllTextAsync(_log), Does.Contain("Updated successfully to 1.2.3"));
            AssertCleanup();
        }

        [Test]
        public async Task AlteredStagedFileIsRejectedBeforeReplacement()
        {
            int exitCode = await RunScriptAsync(new byte[32]);

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(await File.ReadAllTextAsync(_target), Is.EqualTo(Original));
            Assert.That(await File.ReadAllTextAsync(_log), Does.Contain("SHA-256 verification failed"));
            AssertCleanup();
        }

        [Test]
        public async Task LockedExecutableIsRetainedAndFailureIsLogged()
        {
            byte[] hash = await HashReplacementAsync();
            await using FileStream locked = new(_target, FileMode.Open, FileAccess.Read, FileShare.Read);

            int exitCode = await RunScriptAsync(hash);

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(await File.ReadAllTextAsync(_target), Is.EqualTo(Original));
            Assert.That(await File.ReadAllTextAsync(_log), Does.Contain("Update failed"));
            Assert.That(await File.ReadAllTextAsync(_log), Does.Contain("Replace"));
            AssertCleanup();
        }

        private async Task<int> RunScriptAsync(byte[] hash)
        {
            using Process parent = StartPowerShell(["-Command", "[Console]::In.ReadLine() | Out-Null"]);
            using Process helper = StartPowerShell(
            [
                "-ExecutionPolicy", "Bypass", "-File", _script,
                "-ParentProcessId", parent.Id.ToString(),
                "-TargetPath", _target, "-StagedPath", _staged, "-BackupPath", _backup,
                "-LogPath", _log, "-ExpectedSha256", Convert.ToHexString(hash), "-Version", "1.2.3"
            ]);
            try
            {
                string? ready = await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(ready, Is.EqualTo("ready"));
                Assert.That(parent.HasExited, Is.False);
                Assert.That(await File.ReadAllTextAsync(_target), Is.EqualTo(Original));
                await parent.StandardInput.WriteLineAsync("exit");
                await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                return helper.ExitCode;
            }
            finally
            {
                foreach (Process process in new[] { helper, parent })
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        await process.WaitForExitAsync();
                    }
                }
            }
        }

        private static Process StartPowerShell(string[] arguments)
        {
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            ProcessStartInfo info = new(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            foreach (string argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }
            return Process.Start(info) ?? throw new InvalidOperationException("Could not start PowerShell.");
        }

        private async Task<byte[]> HashReplacementAsync()
        {
            await using FileStream stream = File.OpenRead(_staged);
            return await SHA256.HashDataAsync(stream);
        }

        private void AssertCleanup()
        {
            Assert.That(File.Exists(_staged), Is.False);
            Assert.That(File.Exists(_script), Is.False);
            Assert.That(File.Exists(_backup), Is.False);
        }
    }
}

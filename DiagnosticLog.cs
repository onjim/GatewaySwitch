using System;
using System.IO;
using System.Text;

namespace YuanxinGateway
{
    internal static class DiagnosticLog
    {
        private static readonly object Sync = new object();

        internal static void Write(string message)
        {
            try
            {
                lock (Sync)
                {
                    string folder = Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData), "GatewaySwitch");
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, "gateway.log");
                    if (File.Exists(path) && new FileInfo(path).Length >= 1024 * 1024)
                    {
                        string previous = path + ".1";
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(path, previous);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") +
                        " " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must not prevent network recovery.
            }
        }
    }
}

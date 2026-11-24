using System;
using System.IO;
using System.Text;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class SetupLogReaderTests
    {
        [Fact]
        public void Reads_bounded_utf8_tail_and_allows_concurrent_append()
        {
            string path = Path.GetTempFileName();
            try
            {
                const string latest = "Ultimo stato: aggiornamento riuscito 中文";
                File.WriteAllText(path, "old entry\n" + new string('é', 32 * 1024) + "\n" + latest, Encoding.UTF8);
                var reader = new SetupLogReader(appPath: path);
                using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);

                string text = reader.Read(applicationLog: true);
                Assert.Equal("…\n" + latest, text);

                byte[] appended = Encoding.UTF8.GetBytes("\nNext entry");
                writer.Write(appended, 0, appended.Length);
                writer.Flush();
                Assert.EndsWith("Next entry", reader.Read(applicationLog: true));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Reports_unreadable_log_while_exclusively_locked()
        {
            string path = Path.GetTempFileName();
            try
            {
                var reader = new SetupLogReader(updatePath: path);
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.Equal(I18n.T("update_log_unreadable"), reader.Read(applicationLog: false));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}

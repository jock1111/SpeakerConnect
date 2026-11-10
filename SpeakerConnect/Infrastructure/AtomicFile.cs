using System.IO;
using System.Text;

namespace SpeakerConnect.Infrastructure;

/// <summary>
/// Small helpers for settings files. Writes go to a temporary file in the same
/// directory and are then swapped into place, so a crash or power loss can leave
/// the old file intact but never a half-written JSON document.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";

        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (IOException)
                {
                    // Some redirected/special volumes do not support File.Replace.
                    // File.Move with overwrite still swaps the complete file in.
                    File.Move(tempPath, path, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception)
            {
                // A leftover temp file is harmless; the next write overwrites it.
            }
        }
    }

    /// <summary>
    /// Moves a corrupt settings file aside instead of silently deleting it. The
    /// application starts with defaults and the user can inspect the bad file.
    /// </summary>
    public static void TryBackupCorruptFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var backupPath = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(path, backupPath);
        }
        catch (Exception)
        {
            // Backing up is best-effort; defaults are still safe to use.
        }
    }
}

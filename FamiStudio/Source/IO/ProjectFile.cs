using System;
using System.IO;
using System.IO.Compression;

namespace FamiStudio
{
    public class ProjectFile
    {
        const uint MagicNumber = 0x21534D46; // FMS!

        public Project Load(string filename)
        {
#if !DEBUG
            try
#endif
            {
                using (var stream = File.OpenRead(filename))
                {
                    var data = new byte[4];
                    stream.ReadExactly(data, 0, 4);
                    if (BitConverter.ToUInt32(data, 0) != MagicNumber)
                    {
                        stream.Close();
                        return null;
                    }

                    stream.ReadExactly(data, 0, 4);
                    int loadVersion = BitConverter.ToInt32(data, 0);

                    if (loadVersion > Project.Version || (loadVersion > 19 && loadVersion < Project.TuningFormatVersion))
                    {
                        Log.LogMessage(LogSeverity.Error, $"File version ({loadVersion}) is more recent than this version of FamiStudio ({Project.Version}).");
                        return null;
                    }

                    var buffer = new byte[stream.Length - stream.Position];
                    stream.ReadExactly(buffer, 0, buffer.Length);
                    buffer = Compression.DecompressProjectBytes(buffer, loadVersion);

                    var project = new Project();
                    var serializer = new ProjectLoadBuffer(project, buffer, loadVersion);
#if !DEBUG
                    try
#endif
                    {
                        project.Serialize(serializer);
                    }
#if !DEBUG
                    catch
                    {
                        Log.LogMessage(LogSeverity.Error, $"Project file appears to be corrupted. Files created in development versions or forks are not compatible.");
                        return null;
                    }
#endif
                    project.Filename = filename;
                    project.ValidateIntegrity();
                    return project;
                }
            }
#if !DEBUG
            catch
            {
                return null;
            }
#endif
        }

        public bool Save(Project project, string filename)
        {
            string temporaryFile = null;
            try
            {
                var serializer = new ProjectSaveBuffer(project);
                project.Serialize(serializer);
                var buffer = Compression.CompressProjectBytes(serializer.GetBuffer());

                // Write beside the destination so committing the completed file
                // stays on one filesystem. Failed compression or disk writes do
                // not truncate a previously saved project.
                var destination = Path.GetFullPath(filename);
                var destinationInfo = new FileInfo(destination);
                if (destinationInfo.LinkTarget != null)
                {
                    // Preserve the link itself, matching the previous direct
                    // write behavior, but commit atomically beside its target.
                    destination = destinationInfo.ResolveLinkTarget(true)?.FullName ??
                        throw new IOException("Cannot resolve project file link.");
                    destinationInfo = new FileInfo(destination);
                }
                if (destinationInfo.Exists && destinationInfo.IsReadOnly)
                    throw new UnauthorizedAccessException("Project file is read-only.");
                UnixFileMode? unixMode = !OperatingSystem.IsWindows() && destinationInfo.Exists ?
                    File.GetUnixFileMode(destination) : null;
                temporaryFile = Path.Combine(Path.GetDirectoryName(destination),
                    ".fms-" + Guid.NewGuid().ToString("N") + ".tmp");

                using (var stream = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Unix rename does not inherit the replaced file's mode.
                    // Apply it before writing any project contents.
                    if (!OperatingSystem.IsWindows() && unixMode.HasValue)
                        File.SetUnixFileMode(temporaryFile, unixMode.Value);
                    stream.Write(BitConverter.GetBytes(MagicNumber), 0, 4);
                    stream.Write(BitConverter.GetBytes(Project.Version), 0, 4);
                    stream.Write(buffer, 0, buffer.Length);
                    stream.Flush(true);
                }

                if (File.Exists(destination))
                    File.Replace(temporaryFile, destination, null);
                else
                    File.Move(temporaryFile, destination);

                temporaryFile = null;
                project.Filename = filename;
                return true;
            }
#if !DEBUG
            catch
            {
                return false;
            }
#endif
            finally
            {
                if (temporaryFile != null)
                {
                    try { File.Delete(temporaryFile); }
                    catch { /* Preserve the original save failure. */ }
                }
            }
        }
    }
}

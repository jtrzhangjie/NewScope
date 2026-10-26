using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace NewScope.Services;

internal static class JLinkWorker
{
    public const string Switch = "--jlink-worker";
    private const int MaxPayloadBytes = 4 * 1024 * 1024;

    public static bool IsWorker(string[] args) =>
        args.Any(arg => string.Equals(arg, Switch, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        var parentPid = ParseParentPid(args);
        if (parentPid > 0)
        {
            _ = Task.Run(() => WatchParent(parentPid));
        }

        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: false);
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: false);
        using var session = new JLinkSession();

        while (true)
        {
            byte command;
            try
            {
                command = reader.ReadByte();
            }
            catch (EndOfStreamException)
            {
                return 0;
            }
            catch
            {
                return 1;
            }

            try
            {
                switch (command)
                {
                    case JLinkWorkerProtocol.OpenCommand:
                    {
                        var device = reader.ReadString();
                        var speedKHz = reader.ReadInt32();
                        var resumeAfterAttach = reader.ReadBoolean();
                        session.Open(device, speedKHz, resumeAfterAttach);
                        var payload = new byte[8];
                        BinaryPrimitives.WriteInt32LittleEndian(payload, session.ActualSpeedKHz);
                        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), session.MaxSpeedKHz);
                        WriteResponse(writer, 0, session.Description, payload);
                        break;
                    }
                    case JLinkWorkerProtocol.ReadCommand:
                    {
                        var address = reader.ReadUInt32();
                        var length = reader.ReadInt32();
                        if (length < 0 || length > MaxPayloadBytes)
                        {
                            WriteResponse(writer, 1, "Invalid read length.", []);
                            break;
                        }

                        var buffer = new byte[length];
                        session.ReadMemory(address, buffer, length);
                        WriteResponse(writer, 0, "", buffer);
                        break;
                    }
                    case JLinkWorkerProtocol.CloseCommand:
                    {
                        session.Close();
                        WriteResponse(writer, 0, "", []);
                        break;
                    }
                    case JLinkWorkerProtocol.PingCommand:
                    {
                        WriteResponse(writer, 0, "", []);
                        break;
                    }
                    default:
                        WriteResponse(writer, 1, $"Unknown worker command {command}.", []);
                        break;
                }
            }
            catch (Exception exception)
            {
                WriteResponse(writer, 1, exception.Message, []);
            }
        }
    }

    private static int ParseParentPid(string[] args)
    {
        var index = Array.FindIndex(args, arg =>
            string.Equals(arg, Switch, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
        {
            return 0;
        }

        return int.TryParse(args[index + 1], out var pid) ? pid : 0;
    }

    private static void WatchParent(int parentPid)
    {
        while (true)
        {
            Thread.Sleep(1000);
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                if (parent.HasExited)
                {
                    break;
                }
            }
            catch
            {
                break;
            }
        }

        Environment.Exit(1);
    }

    private static void WriteResponse(BinaryWriter writer, int status, string message, byte[] payload)
    {
        writer.Write(status);
        writer.Write(message);
        writer.Write(payload.Length);
        if (payload.Length > 0)
        {
            writer.Write(payload);
        }

        writer.Flush();
    }
}

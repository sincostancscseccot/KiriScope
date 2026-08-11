using System.Buffers.Binary;
using System.Text;
using KiriScope.Xp3;

namespace KiriScope.Core.Tests;

public sealed class KirikiriRuntimeExecutableProbeTests
{
    [Fact]
    public void TryRead_ReportsVersionImportAndProtectedLauncherHints()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var enginePath = Path.Combine(directory, "engine.exe");
            var launcherPath = Path.Combine(directory, "launcher.exe");
            File.WriteAllBytes(enginePath, CreatePe(".text", "VERSION.dll", extraLength: 0));
            File.WriteAllBytes(launcherPath, CreatePe(".enigma1", importName: null, extraLength: 8_192));

            var engine = Assert.IsType<KirikiriRuntimeExecutableProbe>(KirikiriRuntimeExecutableProbe.TryRead(enginePath));
            var launcher = Assert.IsType<KirikiriRuntimeExecutableProbe>(KirikiriRuntimeExecutableProbe.TryRead(launcherPath));

            Assert.True(engine.IsX86);
            Assert.True(engine.ImportsVersionDll);
            Assert.False(engine.HasProtectedLauncherHint);
            Assert.True(launcher.HasProtectedLauncherHint);
            Assert.False(launcher.ImportsVersionDll);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryRead_RejectsNonPeInput()
    {
        var path = Path.Combine(Path.GetTempPath(), "KiriScope.Tests", Guid.NewGuid().ToString("N"), "not-an-exe.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllBytes(path, "not a PE"u8.ToArray());

            Assert.Null(KirikiriRuntimeExecutableProbe.TryRead(path));
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] CreatePe(string sectionName, string? importName, int extraLength)
    {
        var data = new byte[0x600 + extraLength];
        "MZ"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x3c), 0x80);
        "PE\0\0"u8.CopyTo(data.AsSpan(0x80));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x84), 0x14c);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x86), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x94), 0xe0);

        var optionalHeader = data.AsSpan(0x98, 0xe0);
        BinaryPrimitives.WriteUInt16LittleEndian(optionalHeader, 0x10b);
        BinaryPrimitives.WriteUInt32LittleEndian(optionalHeader[92..], 16);
        if (importName is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(optionalHeader[104..], 0x1100);
            BinaryPrimitives.WriteUInt32LittleEndian(optionalHeader[108..], 40);
        }

        var section = data.AsSpan(0x178, 40);
        Encoding.ASCII.GetBytes(sectionName).CopyTo(section);
        BinaryPrimitives.WriteUInt32LittleEndian(section[8..], 0x400);
        BinaryPrimitives.WriteUInt32LittleEndian(section[12..], 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(section[16..], 0x400);
        BinaryPrimitives.WriteUInt32LittleEndian(section[20..], 0x200);
        if (importName is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x300 + 12), 0x1200);
            Encoding.ASCII.GetBytes(importName + "\0").CopyTo(data.AsSpan(0x400));
        }

        return data;
    }
}

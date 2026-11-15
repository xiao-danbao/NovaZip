using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NovaZip.Engine
{
    [StructLayout(LayoutKind.Sequential)]
    public struct WinFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct PropVariant
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(2)] public ushort Reserved1;
        [FieldOffset(4)] public ushort Reserved2;
        [FieldOffset(6)] public ushort Reserved3;

        [FieldOffset(8)] public IntPtr PointerValue;
        [FieldOffset(8)] public int Int32Value;
        [FieldOffset(8)] public uint UInt32Value;
        [FieldOffset(8)] public long Int64Value;
        [FieldOffset(8)] public ulong UInt64Value;
        [FieldOffset(8)] public short BoolValue;
        [FieldOffset(8)] public WinFileTime FileTime;
        [FieldOffset(16)] public long Pad; // PROPVARIANT 在 x64 下共 24 字节
    }

    public static class VarTypes
    {
        public const ushort VT_EMPTY = 0;
        public const ushort VT_BSTR = 8;
        public const ushort VT_BOOL = 11;
        public const ushort VT_UI4 = 19;
        public const ushort VT_I8 = 20;
        public const ushort VT_UI8 = 21;
        public const ushort VT_FILETIME = 64;
    }

    public static class PropIds
    {
        public const uint Path = 3;
        public const uint Name = 4;
        public const uint IsDir = 6;
        public const uint Size = 7;
        public const uint PackSize = 8;
        public const uint Attrib = 9;
        public const uint MTime = 12;
        public const uint Encrypted = 15;
        public const uint Crc = 19;
        public const uint PhySize = 44;
        public const uint TotalSize = 56;
    }

    public static class HandlerPropIds
    {
        public const uint Name = 0;
        public const uint ClassId = 1;
        public const uint Extension = 2;
        public const uint Update = 4;
    }

    public static class Native
    {
        public const int S_OK = 0;
        public const int S_FALSE = 1;
        public const int E_ABORT = unchecked((int)0x80004004);
        public const int E_FAIL = unchecked((int)0x80004005);
        public const int E_NOTIMPL = unchecked((int)0x80004001);
        public const int E_NOINTERFACE = unchecked((int)0x80004002);
        public const int E_POINTER = unchecked((int)0x80004003);
        public const int E_INVALIDARG = unchecked((int)0x80070057);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibraryW(string fileName);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr GetProcAddress(IntPtr module, string procName);

        /// <summary>在已打开的文件句柄上直接写时间戳，避免为了改时间再打开/关闭一次文件。</summary>
        [DllImport("kernel32", SetLastError = true)]
        public static extern bool SetFileTime(SafeFileHandle file, IntPtr creationTime, IntPtr lastAccessTime,
            ref long lastWriteTime);

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PropVariant pvar);

        [DllImport("ole32.dll", EntryPoint = "PropVariantClear")]
        public static extern int PropVariantClearPtr(IntPtr pvar);

        /// <summary>PROPVARIANT 在 x64 下为 24 字节。</summary>
        public const int PropVariantSize = 24;

        public static IntPtr AllocPropVariant()
        {
            var ptr = Marshal.AllocHGlobal(PropVariantSize);
            for (var i = 0; i < PropVariantSize; i++) Marshal.WriteByte(ptr, i, 0);
            return ptr;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int CreateObjectDelegate(ref Guid classId, ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object outObject);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetNumberOfFormatsDelegate(out uint numFormats);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetHandlerProperty2Delegate(uint formatIndex, uint propId, out PropVariant value);
    }

    public static class InterfaceIds
    {
        public static readonly Guid ISequentialInStream = new Guid("23170F69-40C1-278A-0000-000300010000");
        public static readonly Guid ISequentialOutStream = new Guid("23170F69-40C1-278A-0000-000300020000");
        public static readonly Guid IInStream = new Guid("23170F69-40C1-278A-0000-000300030000");
        public static readonly Guid IOutStream = new Guid("23170F69-40C1-278A-0000-000300040000");
        public static readonly Guid ICryptoGetTextPassword = new Guid("23170F69-40C1-278A-0000-000500100000");
        public static readonly Guid ICryptoGetTextPassword2 = new Guid("23170F69-40C1-278A-0000-000500110000");
        public static readonly Guid IArchiveOpenCallback = new Guid("23170F69-40C1-278A-0000-000600100000");
        public static readonly Guid IArchiveExtractCallback = new Guid("23170F69-40C1-278A-0000-000600200000");
        public static readonly Guid IArchiveUpdateCallback = new Guid("23170F69-40C1-278A-0000-000600800000");
        public static readonly Guid IArchiveUpdateCallback2 = new Guid("23170F69-40C1-278A-0000-000600820000");
        public static readonly Guid IInArchive = new Guid("23170F69-40C1-278A-0000-000600600000");
        public static readonly Guid IOutArchive = new Guid("23170F69-40C1-278A-0000-000600A00000");
        public static readonly Guid ISetProperties = new Guid("23170F69-40C1-278A-0000-000600030000");
    }

    // Interfaces are declared flat (no CLR interface inheritance) so the COM vtable layout is
    // deterministic for both call directions under .NET 8.

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000300010000"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISequentialInStream
    {
        [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000300020000"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISequentialOutStream
    {
        [PreserveSig] int Write(IntPtr data, uint size, IntPtr processedSize);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000300030000"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInStream
    {
        [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
        [PreserveSig] int Seek(long offset, uint seekOrigin, IntPtr newPosition);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000300040000"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOutStream
    {
        [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
        [PreserveSig] int Seek(long offset, uint seekOrigin, IntPtr newPosition);
        [PreserveSig] int SetSize(long newSize);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000500100000"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICryptoGetTextPassword
    {
        [PreserveSig] int CryptoGetTextPassword(out IntPtr password);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000500110000"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICryptoGetTextPassword2
    {
        [PreserveSig] int CryptoGetTextPassword2(out int passwordIsDefined, out IntPtr password);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000600100000"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IArchiveOpenCallback
    {
        [PreserveSig] int SetTotal(IntPtr files, IntPtr bytes);
        [PreserveSig] int SetCompleted(IntPtr files, IntPtr bytes);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000600200000"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IArchiveExtractCallback
    {
        [PreserveSig] int SetTotal(ulong total);
        [PreserveSig] int SetCompleted(IntPtr completeValue);
        [PreserveSig] int GetStream(uint index, [MarshalAs(UnmanagedType.Interface)] out ISequentialOutStream outStream, int askExtractMode);
        [PreserveSig] int PrepareOperation(int askExtractMode);
        [PreserveSig] int SetOperationResult(int opRes);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000600800000"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IArchiveUpdateCallback
    {
        [PreserveSig] int SetTotal(ulong total);
        [PreserveSig] int SetCompleted(IntPtr completeValue);
        [PreserveSig] int GetUpdateItemInfo(uint index, IntPtr newData, IntPtr newProps, IntPtr indexInArchive);
        [PreserveSig] int GetProperty(uint index, uint propId, IntPtr value);
        [PreserveSig] int GetStream(uint index, [MarshalAs(UnmanagedType.Interface)] out ISequentialInStream inStream);
        [PreserveSig] int SetOperationResult(int operationResult);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000600820000"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IArchiveUpdateCallback2
    {
        [PreserveSig] int SetTotal(ulong total);
        [PreserveSig] int SetCompleted(IntPtr completeValue);
        [PreserveSig] int GetUpdateItemInfo(uint index, IntPtr newData, IntPtr newProps, IntPtr indexInArchive);
        [PreserveSig] int GetProperty(uint index, uint propId, IntPtr value);
        [PreserveSig] int GetStream(uint index, [MarshalAs(UnmanagedType.Interface)] out ISequentialInStream inStream);
        [PreserveSig] int SetOperationResult(int operationResult);
        [PreserveSig] int GetVolumeSize(uint index, out ulong size);
        [PreserveSig] int GetVolumeStream(uint index, [MarshalAs(UnmanagedType.Interface)] out ISequentialOutStream volumeStream);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000600030000"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISetProperties
    {
        [PreserveSig] int SetProperties(IntPtr names, IntPtr values, uint numProps);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000600600000"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IInArchive
    {
        [PreserveSig] int Open(IInStream stream, IntPtr maxCheckStartPosition, IArchiveOpenCallback openCallback);
        [PreserveSig] int Close();
        [PreserveSig] int GetNumberOfItems(out uint numItems);
        [PreserveSig] int GetProperty(uint index, uint propId, IntPtr value);
        [PreserveSig] int Extract(IntPtr indices, uint numItems, int testMode, IArchiveExtractCallback extractCallback);
        [PreserveSig] int GetArchiveProperty(uint propId, IntPtr value);
        [PreserveSig] int GetNumberOfProperties(out uint numProps);
        [PreserveSig] int GetPropertyInfo(uint index, IntPtr name, IntPtr propId, IntPtr varType);
        [PreserveSig] int GetNumberOfArchiveProperties(out uint numProps);
        [PreserveSig] int GetArchivePropertyInfo(uint index, IntPtr name, IntPtr propId, IntPtr varType);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000600A00000"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOutArchive
    {
        [PreserveSig] int UpdateItems(ISequentialOutStream outStream, uint numItems, IArchiveUpdateCallback updateCallback);
        [PreserveSig] int GetFileTimeType(out uint type);
    }

    public static class ArchiveResults
    {
        public const int Ok = 0;
        public const int UnsupportedMethod = 1;
        public const int DataError = 2;
        public const int CrcError = 3;
        public const int Unavailable = 4;
        public const int UnexpectedEnd = 5;
        public const int DataAfterEnd = 6;
        public const int IsNotArc = 7;
        public const int HeadersError = 8;
        public const int WrongPassword = 9;

        public static string Describe(int code) => code switch
        {
            Ok => "成功",
            UnsupportedMethod => "不支持的压缩方法",
            DataError => "数据错误",
            CrcError => "CRC 校验失败",
            Unavailable => "数据不可用",
            UnexpectedEnd => "数据意外结束",
            DataAfterEnd => "压缩包尾部存在多余数据",
            IsNotArc => "不是有效的压缩包",
            HeadersError => "压缩包头部损坏",
            WrongPassword => "密码错误",
            _ => "未知错误(" + code + ")"
        };
    }
}

using System.Runtime.InteropServices;

namespace EhViewerForPc.Services;

/// <summary>只通过 Windows 回收操作删除；原生调用放在独立 STA，避免 UI 阻塞和运行时 COM 生成。</summary>
internal static partial class WindowsRecycleBin
{
    internal static void MoveDirectory(string folder)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Recycle(folder); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "Recycle local gallery" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        completion.Task.GetAwaiter().GetResult();
    }

    private static unsafe void Recycle(string folder)
    {
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
        nint operation = 0, item = 0;
        try
        {
            Guid clsid = new("3AD05575-8857-4850-9277-11B85BDB8E09"), iid = new("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8");
            Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, 1, in iid, out operation));
            Guid shellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(folder, 0, in shellItem, out item));
            var methods = *(nint**)operation;
            // RECYCLEONDELETE + ADDUNDORECORD；出错立即停止，不用递归删除作兜底。
            const uint recycleOnDelete = 0x00080000, addUndoRecord = 0x20000000, earlyFailure = 0x00100000;
            const uint noErrorUi = 0x0400, silent = 0x0004, warnBeforePermanentDelete = 0x4000, noConfirmation = 0x0010;
            var setFlags = (delegate* unmanaged[Stdcall]<nint, uint, int>)methods[5];
            var deleteItem = (delegate* unmanaged[Stdcall]<nint, nint, nint, int>)methods[18];
            var perform = (delegate* unmanaged[Stdcall]<nint, int>)methods[21];
            var getAborted = (delegate* unmanaged[Stdcall]<nint, int*, int>)methods[22];
            Marshal.ThrowExceptionForHR(setFlags(operation, recycleOnDelete | addUndoRecord | earlyFailure | noErrorUi | silent | warnBeforePermanentDelete | noConfirmation));
            Marshal.ThrowExceptionForHR(deleteItem(operation, item, 0));
            Marshal.ThrowExceptionForHR(perform(operation));
            int aborted;
            Marshal.ThrowExceptionForHR(getAborted(operation, &aborted));
            if (aborted != 0) throw new OperationCanceledException("Windows 未完成回收操作。");
        }
        finally
        {
            if (item != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)item)[2])(item);
            if (operation != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)operation)[2])(operation);
            CoUninitialize();
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint result);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out nint result);
}

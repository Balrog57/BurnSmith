using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace BurnSmith.Core;

// Typed [ComImport] views of the IMAPI2 calls that matter on a real drive (setting the recorder, writing, erasing). Late binding through
// "dynamic" is reported fragile there (futureburn, MIT, sp00nznet), so the vtable order below was dumped from the installed imapi2.dll
// type library, not written from memory. Members never called keep placeholder signatures: only their position counts.

[ComImport, Guid("27354133-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface IDiscRecorder2
{
    void EjectMedia();
    void CloseTray();
    void AcquireExclusiveAccess([MarshalAs(UnmanagedType.VariantBool)] bool force, [MarshalAs(UnmanagedType.BStr)] string clientName);
    void ReleaseExclusiveAccess();
    void DisableMcn();
    void EnableMcn();
    void InitializeDiscRecorder([MarshalAs(UnmanagedType.BStr)] string recorderUniqueId);
}

[ComImport, Guid("27354153-9F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface IDiscFormat2Data
{
    [return: MarshalAs(UnmanagedType.VariantBool)] bool IsRecorderSupported([MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 recorder);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool IsCurrentMediaSupported([MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 recorder);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_MediaPhysicallyBlank();
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_MediaHeuristicallyBlank();
    void get_SupportedMediaTypes_();
    void put_Recorder([MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 value);
    [return: MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 get_Recorder();
    void put_BufferUnderrunFreeDisabled_();
    void get_BufferUnderrunFreeDisabled_();
    void put_PostgapAlreadyInImage_();
    void get_PostgapAlreadyInImage_();
    void get_CurrentMediaStatus_();
    void get_WriteProtectStatus_();
    int get_TotalSectorsOnMedia();
    int get_FreeSectorsOnMedia();
    void get_NextWritableAddress_();
    void get_StartAddressOfPreviousSession_();
    void get_LastWrittenAddressOfPreviousSession_();
    void put_ForceMediaToBeClosed([MarshalAs(UnmanagedType.VariantBool)] bool value);
    void get_ForceMediaToBeClosed_();
    void put_DisableConsumerDvdCompatibilityMode_();
    void get_DisableConsumerDvdCompatibilityMode_();
    int get_CurrentPhysicalMediaType();
    void put_ClientName([MarshalAs(UnmanagedType.BStr)] string value);
    void get_ClientName_();
    void get_RequestedWriteSpeed_();
    void get_RequestedRotationTypeIsPureCAV_();
    void get_CurrentWriteSpeed_();
    void get_CurrentRotationTypeIsPureCAV_();
    void get_SupportedWriteSpeeds_();
    void get_SupportedWriteSpeedDescriptors_();
    void put_ForceOverwrite_();
    void get_ForceOverwrite_();
    void get_MultisessionInterfaces_();
    void Write([MarshalAs(UnmanagedType.Interface)] IStream data);
    void CancelWrite();
    void SetWriteSpeed(int requestedSectorsPerSecond, [MarshalAs(UnmanagedType.VariantBool)] bool rotationTypeIsPureCAV);
}

[ComImport, Guid("27354156-8F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface IDiscFormat2Erase
{
    [return: MarshalAs(UnmanagedType.VariantBool)] bool IsRecorderSupported([MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 recorder);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool IsCurrentMediaSupported([MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 recorder);
    void get_MediaPhysicallyBlank_();
    void get_MediaHeuristicallyBlank_();
    void get_SupportedMediaTypes_();
    void put_Recorder([MarshalAs(UnmanagedType.Interface)] IDiscRecorder2 value);
    void get_Recorder_();
    void put_FullErase([MarshalAs(UnmanagedType.VariantBool)] bool value);
    void get_FullErase_();
    void get_CurrentPhysicalMediaType_();
    void put_ClientName([MarshalAs(UnmanagedType.BStr)] string value);
    void get_ClientName_();
    void EraseMedia();
}

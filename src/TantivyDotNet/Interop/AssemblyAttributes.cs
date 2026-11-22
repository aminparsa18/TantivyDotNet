using System.Runtime.CompilerServices;

// Every P/Invoke in Native/NativeMethods.g.cs takes only blittable types (pointers, integers and
// the pointer+length TdnString struct). Disabling runtime marshalling makes the compiler enforce
// that and keeps the calls stub-free, so the csbindgen-generated [DllImport]s behave like
// [LibraryImport]s under trimming and NativeAOT.
[assembly: DisableRuntimeMarshalling]

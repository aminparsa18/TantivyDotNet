using System.Text;
using TantivyDotNet.Native;

namespace TantivyDotNet.Interop;

/// <summary>Helpers shared by every call into the native library.</summary>
internal static unsafe class Ffi
{
    /// <summary>Must match <c>ABI_VERSION</c> in native/src/lib.rs.</summary>
    internal const uint ExpectedAbiVersion = 2;

    private static volatile bool s_abiChecked;

    /// <summary>Verifies once per process that the loaded native library speaks our ABI.</summary>
    internal static void EnsureAbi()
    {
        if (s_abiChecked) return;
        uint actual = NativeMethods.tdn_abi_version();
        if (actual != ExpectedAbiVersion)
        {
            throw new TantivyException(
                TantivyErrorCode.Error,
                $"Native library tantivy_dotnet has ABI version {actual}, but this TantivyDotNet build expects {ExpectedAbiVersion}. " +
                "The managed and native parts of the package are out of sync.");
        }
        s_abiChecked = true;
    }

    /// <summary>Throws the exception matching <paramref name="status"/>, consuming <paramref name="error"/>.</summary>
    internal static void Check(int status, TdnString error)
    {
        if (status == 0) return;
        string message = TakeString(error);
        throw (TantivyErrorCode)status switch
        {
            TantivyErrorCode.QueryParse => new QueryParseException(message),
            var code when Enum.IsDefined(code) => new TantivyException(code, message),
            _ => new TantivyException(TantivyErrorCode.Error, $"(unknown native status {status}) {message}"),
        };
    }

    /// <summary>Copies a native UTF-8 buffer into a managed string and frees the buffer.</summary>
    internal static string TakeString(TdnString s)
    {
        try
        {
            return s.ptr == null ? string.Empty : Encoding.UTF8.GetString(s.ptr, checked((int)s.len));
        }
        finally
        {
            NativeMethods.tdn_string_free(s);
        }
    }

    /// <summary>Copies a native buffer into a managed array and frees the buffer.</summary>
    internal static byte[] TakeBytes(TdnString s)
    {
        try
        {
            return s.ptr == null ? [] : new ReadOnlySpan<byte>(s.ptr, checked((int)s.len)).ToArray();
        }
        finally
        {
            NativeMethods.tdn_string_free(s);
        }
    }

    internal static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}

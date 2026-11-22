using TantivyDotNet.Native;

namespace TantivyDotNet.Tests;

public class NativeSmokeTests
{
    [Fact]
    public void AbiVersion_IsOne() => Assert.Equal(1u, NativeMethods.tdn_abi_version());

    [Fact]
    public void SmokeTest_IndexesAndFindsOneDocument() => Assert.Equal(1, NativeMethods.tdn_smoke_test());
}

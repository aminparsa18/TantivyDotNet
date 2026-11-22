fn main() {
    println!("cargo:rerun-if-changed=src/lib.rs");

    csbindgen::Builder::default()
        .input_extern_file("src/lib.rs")
        .csharp_dll_name("tantivy_dotnet")
        .csharp_namespace("TantivyDotNet.Native")
        .csharp_class_name("NativeMethods")
        .csharp_class_accessibility("internal")
        .generate_csharp_file("../src/TantivyDotNet/Native/NativeMethods.g.cs")
        .unwrap();
}

fn main() {
    slint_build::compile_with_config("ui/app.slint", slint_build::CompilerConfiguration::new().with_style("fluent".into())).expect("Failed to compile ui/app.slint");

    #[cfg(target_os = "windows")]
    {
        let mut res = winres::WindowsResource::new();
        let icon_path = "../Assets/FileInventory.ico";
        if std::path::Path::new(icon_path).exists() {
            res.set_icon(icon_path);
        }
        res.set("ProductName", "File Inventory");
        res.set("FileDescription", "File Inventory");
        res.set("FileVersion", "1.6.9.0");
        res.set("ProductVersion", "1.6.9");
        if res.compile().is_ok() && std::env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("gnu") {
            // The GNU linker skips the resource-only object when it is only in a static archive.
            let resource = std::path::PathBuf::from(std::env::var_os("OUT_DIR").unwrap()).join("resource.o");
            println!("cargo:rustc-link-arg-bin=FileInventory={}", resource.display());
        }
    }
}

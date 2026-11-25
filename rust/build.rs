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
        res.set("FileVersion", "1.5.0.0");
        res.set("ProductVersion", "1.5.0");
        let _ = res.compile();
    }
}

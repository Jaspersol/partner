fn main() {
    // The desktop pet is compiled out of distributed builds until Live2D answers the Expandable Application
    // review: the pet bundle carries their Cubism Framework, and shipping it needs a publication licence this
    // project does not hold yet. The release workflow sets `PARTNER_PET=0`; a development build (no variable)
    // includes it, which is what `tauri dev` relies on.
    //
    // This is a build-script cfg rather than a cargo feature on purpose. `tauri dev` builds with
    // `--no-default-features --features <the manifest's default list minus custom-protocol>`, so every feature in
    // `default` is on in development, and a gating feature could not keep the pet in `tauri dev` while taking it
    // out of the release. An env-gated cfg has no such inversion.
    println!("cargo:rerun-if-env-changed=PARTNER_PET");
    println!("cargo:rustc-check-cfg=cfg(partner_pet)");
    if std::env::var("PARTNER_PET").map(|value| value != "0") != Ok(false) {
        println!("cargo:rustc-cfg=partner_pet");
    }

    tauri_build::build()
}
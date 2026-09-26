// Release builds are a GUI application: no console window should appear behind the shell.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    partner_shell::run()
}

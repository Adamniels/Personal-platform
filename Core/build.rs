// Rebuilds when a migration is added or removed. `sqlx::migrate!()` embeds
// Core/migrations at compile time, and Cargo only notices new files there
// because of this line.
fn main() {
    println!("cargo:rerun-if-changed=migrations");
}

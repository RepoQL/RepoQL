/// Example enum used by tests.
pub enum Message {
    /// Unit variant
    Quit,
    /// Tuple variant
    Write(String, usize),
    /// Struct variant
    Move { x: i32, y: i32 },
}
pub trait Storage: Send + Sync {
    type Item;
    const VERSION: usize = 1;

    fn get(&self, key: &str) -> Option<Self::Item>;

    fn set(&mut self, key: &str, value: Self::Item) {
        let _ = (key, value);
    }
}
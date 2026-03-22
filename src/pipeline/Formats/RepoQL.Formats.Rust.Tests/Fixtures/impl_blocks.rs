pub struct Cache;

impl Cache {
    pub fn new() -> Self {
        Self
    }

    pub async fn refresh(&mut self) {
    }
}

impl Storage for Cache {
    type Item = String;
    const VERSION: usize = 2;

    fn get(&self, key: &str) -> Option<Self::Item> {
        let _ = key;
        None
    }

    fn set(&mut self, key: &str, value: Self::Item) {
        let _ = (key, value);
    }
}
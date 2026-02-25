pub async fn fetch_data() -> Result<(), ()> {
    Ok(())
}

pub struct Worker;

impl Worker {
    pub async fn run(&self) -> usize {
        1
    }
}
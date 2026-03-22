macro_rules! make_value {
    () => {
        42
    };
}

make_value!();
lazy_static! {
    static ref VALUE: i32 = 7;
}
println!("value = {}", make_value!());
vec![1, 2, 3];

#[tokio::main]
async fn main() {}

#[async_trait]
pub trait Worker {
    async fn run(&self);
}

#[allow(dead_code)]
#[cfg(test)]
#[inline(always)]
#[must_use]
#[deprecated]
fn helper() -> i32 {
    1
}

#[derive(Debug)]
struct Config;

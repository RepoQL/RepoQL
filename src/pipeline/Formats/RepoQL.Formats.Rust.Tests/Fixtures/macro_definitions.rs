#[macro_export]
macro_rules! make_value {
    () => {
        42
    };
}

make_value!();
println!("value = {}", make_value!());
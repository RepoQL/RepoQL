pub struct Wrapper<'a, T>
where
    T: Clone + 'a,
{
    pub value: &'a T,
}

pub fn build<'a, T>(value: &'a T) -> Wrapper<'a, T>
where
    T: Clone + 'a,
{
    Wrapper { value }
}
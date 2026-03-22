#[cfg(feature = "feat_x")]
#[inline(always)]
#[must_use]
#[deprecated]
pub fn compute() -> i32 {
    1
}

#[test]
fn smoke() {}

#[cfg(test)]
pub mod test_support {}
